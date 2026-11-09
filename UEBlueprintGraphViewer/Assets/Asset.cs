using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.Utils;
using UEBlueprintGraphViewer.Engine;
using UEBlueprintGraphViewer.Nodes;
using static UEBlueprintGraphViewer.Engine.EngineBPData;
using static UEBlueprintGraphViewer.Engine.EngineEnums;
using static UEBlueprintGraphViewer.Engine.Utils;

namespace UEBlueprintGraphViewer.Assets
{
    public class Asset
    {
        public string ObjectName = null!;
        public string Name = null!;
        public bool IsBP;
        public UBlueprintGeneratedClass? GeneratedClass;
        public UObject? ClassDefaultObject;

        public UFunction? UbergraphFunction;

        private readonly List<UFunction> _events = [];
        public List<UFunction> SortedEvents = [];
        public Dictionary<string, UFunction> Events = [];
        public readonly List<UFunction> Functions = [];
        public readonly Dictionary<string, PropertyData> LoadedProperties = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<PropertyData> ParentProperties = [];

        public readonly List<InputEventData> InputEvents = [];
        public readonly List<TimelineData> Timelines = [];
        public readonly List<AssetMacro> Macros = [];
        public readonly List<AssetComponent> Components = [];
        public bool IsActorBlueprint;

        public bool IsMaterial;
        public MaterialAssetKind MaterialKind;
        public UObject? MaterialObject;
        public readonly List<UObject> MaterialExpressions = [];
        public readonly List<string> MaterialOutputNames = [];
        public readonly List<string> ReferencedMaterialFunctions = [];
        public readonly List<MaterialFunctionRef> MaterialFunctionRefs = [];
        public MaterialSnapshot? MaterialSnapshot;
        public BPGraph? MaterialGraph;

        public bool IsAnimBlueprint;
        public AnimGraphSnapshot? AnimSnapshot;
        public readonly Dictionary<string, BPGraph> AnimGraphs = new(StringComparer.OrdinalIgnoreCase);
        public List<string> AnimGraphNames => [.. AnimGraphs.Keys];

        public EBlueprintType BlueprintType = EBlueprintType.Normal;
        public bool IsMacroLibrary => BlueprintType == EBlueprintType.MacroLibrary;
        
        public string SuperStruct = "None";
        
        public Asset(IPackage package, string name)
        {
            Name = package.Name;
            
            // try to find generated class with the name "{FileName}_C"
            int index = package.GetExportIndex($"{name}_C", StringComparison.OrdinalIgnoreCase);
        
            // if generated class have a different name from filename,
            // trying to find export with generated class
            if (index < 0)
                index = GetBPClassIndex(package);
            
            if (index >= 0 && package.ExportsLazy[index].Value is UBlueprintGeneratedClass BPClass)
            {
                IsBP = true;
                GeneratedClass = BPClass;
                ClassDefaultObject = GeneratedClass.ClassDefaultObject.Load<UObject>();
                SuperStruct = GeneratedClass.SuperStruct.ResolvedObject?.GetPathName() ?? "None";
                ProcessBPClass();
                CollectMacroData(package, name);
                CollectComponents(package);
                CollectAnimBlueprint(package);
                return;
            }

            SuperStruct = "None";
            TryLoadMaterial(package, name);
        }

        public bool IsEvent(UFunction? func) => _events.Contains(func);

        private void ProcessBPClass()
        {
            ObjectName = GeneratedClass!.Name;
            var functions = GeneratedClass.FuncMap.Select(o => o.Value.ResolvedObject?.Object?.Value).OfType<UFunction>().ToArray();
            
            // finding ubergraph
            UbergraphFunction = GeneratedClass.UberGraphFunction?.Load<UFunction>();

            // finding events/functions
            foreach (UFunction func in functions)
            {
                // skip delegate signatures and ubergraph itself
                if (func.FunctionFlags.HasFlag(EFunctionFlags.FUNC_Delegate) || func == UbergraphFunction)
                    continue;

                if (IsUbergraphEvent(func.ScriptBytecode))
                    _events.Add(func);
                else
                    Functions.Add(func);
            }

            SortedEvents = Settings.Instance.ReorderEvents ? _events.OrderBy(o => GetUbergraphEntryPoint(o.ScriptBytecode)).ToList() : _events;
            Events = SortedEvents.ToDictionary(o => o.Name, o => o);

            foreach (var index in GeneratedClass.DynamicBindingObjects)
            {
                if (index.ResolvedObject?.Object?.Value.Class?.Name.ToString() == "ComponentDelegateBinding")
                {
                    // TODO
                }
                else
                {
                    InputEvents.AddRange(GetInputEvents(index.ResolvedObject?.Object?.Value));
                }
            }
            foreach (var inputEvent in InputEvents.Where(o => o.FunctionName != "None"))
            {
                Events.Remove(inputEvent.FunctionName);
                if (!Events.ContainsKey(inputEvent.Name))
                {
                    Events[inputEvent.Name] = _events.Find(o => o.Name == inputEvent.FunctionName)!;
                }
            }

            foreach (var index in GeneratedClass.Timelines)
            {
                Timelines.Add(GetTimelineData(index.ResolvedObject?.Object?.Value));
            }

            foreach (var timeline in Timelines)
            {
                Events.Remove(timeline.UpdateFunctionName);
                Events.Remove(timeline.FinishedFunctionName);
            }

            T? GetPropValue<T>(AbstractPropertyHolder? obj, string name) where T : class
            {
                return obj?.Properties.FirstOrDefault(o => o.Name.ToString() == name)?.Tag?.GetValue(typeof(T)) as T;
            }
            
            string GetPropValueName(AbstractPropertyHolder? obj, string name)
            {
                return GetPropValue<object>(obj, name)?.ToString() ?? "None";
            }
            
            IEnumerable<T> GetPropsValues<T>(UScriptArray? obj) where T : class
            {
                return obj?.Properties.Select(o => o.GenericValue).OfType<T>() ?? [];
            }

            List<InputEventData> GetInputEvents(UObject? obj)
            {
                List<InputEventData> result = [];
                InputEventType eventType = obj?.Class?.Name.ToString() switch
                {
                    "InputKeyDelegateBinding" => InputEventType.Key,
                    "InputAxisKeyDelegateBinding" => InputEventType.InputAxisKey,
                    "InputActionDelegateBinding" => InputEventType.InputAction,
                    "InputAxisDelegateBinding" => InputEventType.InputAxisAction,
                    "EnhancedInputActionDelegateBinding" => InputEventType.EnhancedInputAction,
                    _ => InputEventType.Key,
                };
                
                if (obj?.Properties.FirstOrDefault()?.Tag?.GenericValue is UScriptArray eventsArray)
                {
                    foreach (var eventInfo in GetPropsValues<FScriptStruct>(eventsArray).Select(o => o.StructType).OfType<FStructFallback>())
                    {
                        string funcName = GetPropValueName(eventInfo, "FunctionNameToBind");
                        
                        string name = eventType switch
                        {
                            InputEventType.Key => funcName.SubstringBefore("_K2Node_").SubstringAfter("InpActEvt_").Replace('_', ' '),
                            InputEventType.InputAxisKey => funcName.SubstringBefore("_K2Node_").SubstringAfter("InpAxisKeyEvt_").Replace('_', ' '),
                            InputEventType.InputAction => $"InputAction {GetPropValueName(eventInfo, "InputActionName")}",
                            InputEventType.InputAxisAction => $"InputAxis {GetPropValueName(eventInfo, "InputAxisName")}",
                            InputEventType.EnhancedInputAction => $"EnhancedInputAction {GetPropValue<FPackageIndex>(eventInfo, "InputAction")?.Name ?? "None"}",
                            _ => throw new ArgumentOutOfRangeException(),
                        };

                        InputEventPinType pinType;
                        
                        switch (eventType)
                        {
                            case InputEventType.Key:
                            case InputEventType.InputAction:
                            {
                                string type = GetPropValueName(eventInfo, "InputKeyEvent");
                                pinType = type switch
                                {
                                    "EInputEvent::IE_Pressed" or "IE_Pressed" => InputEventPinType.Pressed,
                                    "EInputEvent::IE_Released" or "IE_Released" => InputEventPinType.Released,
                                    _ => throw new ArgumentOutOfRangeException(),
                                };
                                break;
                            }
                            case InputEventType.InputAxisAction:
                            case InputEventType.InputAxisKey:
                            {
                                pinType = InputEventPinType.AxisExec;
                                break;
                            }
                            case InputEventType.EnhancedInputAction:
                            {
                                string type = GetPropValueName(eventInfo, "TriggerEvent");
                                pinType = type switch
                                {
                                    "ETriggerEvent::Triggered" => InputEventPinType.Triggered,
                                    "ETriggerEvent::Started" => InputEventPinType.Started,
                                    "ETriggerEvent::Ongoing" => InputEventPinType.Ongoing,
                                    "ETriggerEvent::Canceled" => InputEventPinType.Canceled,
                                    "ETriggerEvent::Completed" => InputEventPinType.Completed,
                                    _ => throw new ArgumentOutOfRangeException(),
                                };
                                break;
                            }
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                        
                        result.Add(new() { Name = name, FunctionName = funcName, PinType = pinType, Type = eventType});
                    }
                }

                return result;
            }

            TimelineData GetTimelineData(UObject? obj)
            {
                List<FloatTrack> floatTracks = [];
                if (GetPropValue<UScriptArray>(obj, "FloatTracks") is { } array)
                {
                    floatTracks.AddRange(GetPropsValues<FScriptStruct>(array)
                        .Select(o => o.StructType)
                        .OfType<FStructFallback>()
                        .Select(track => new FloatTrack()
                        {
                            //CurveFloat = GetPropValue<FPackageIndex>(track, "CurveFloat").ResolvedObject.Object.Value,
                            PropertyName = GetPropValueName(track, "PropertyName"),
                            TrackName = GetPropValueName(track, "TrackName"),
                            IsExternalCurve = bool.Parse(GetPropValue<object>(track, "bIsExternalCurve")?.ToString() ?? "false")
                        }));
                }

                return new()
                {
                    FloatTracks = floatTracks,
                    TimelineGuid = GetPropValueName(obj, "TimelineGuid"),
                    VariableName = GetPropValueName(obj, "VariableName"),
                    DirectionPropertyName = GetPropValueName(obj, "DirectionPropertyName"),
                    UpdateFunctionName = GetPropValueName(obj, "UpdateFunctionName"),
                    FinishedFunctionName = GetPropValueName(obj, "FinishedFunctionName"),
                };
            }
        }

        bool IsUbergraphEvent(KismetExpression[] script)
        {
            if (script.Length < 3 || UbergraphFunction == null)
                return false;
            
            return script[^3] switch
            {
                EX_LocalFinalFunction ex => ex.StackNode.IsExport && ex.StackNode.Load() == UbergraphFunction,
                EX_VirtualFunction ex => ex.VirtualFunctionName == UbergraphFunction.Name,
                _ => false,
            };
        }

        // Method to find index of generated class export index.
        // We don't want to load all exports to check their classes
        // so instead we resolve class names from ExportMap
        // (this will not load the exports themselves)
        static int GetBPClassIndex(IPackage package)
        {
            switch (package)
            {
                case Package pack:
                    for (int i = 0; i < pack.ExportMap.Length; i++)
                    {
                        if (pack.ExportMap[i].ClassName.Ends("BlueprintGeneratedClass"))
                            return i;
                    }
                    break;
                case IoPackage io:
                    for (int i = 0; i < io.ExportMap.Length; i++)
                    {
                        var classIndex = io.ResolveObjectIndex(io.ExportMap[i].ClassIndex)?.Object?.Value as UStruct;
                        if (classIndex?.Name.Ends("BlueprintGeneratedClass") == true)
                            return i;
                    }
                    break;
            }
            return -1;
        }

        public void LoadAllProperties()
        {
            if (GeneratedClass == null) return;
            foreach (var fp in GeneratedClass.ChildProperties.OfType<FProperty>())
            {
                if (IsAnimBlueprint && AnimBlueprintCollector.IsAnimGraphProperty(fp))
                    continue;
                var prop = new PropertyData(fp, GeneratedClass);
                if (ClassDefaultObject != null)
                    prop.SetPropertyDefaults(ClassDefaultObject);
                LoadedProperties.Add(prop.Name, prop);
            }

            UClass? superStruct = GeneratedClass;
            FPackageIndex currentPackageIndex = GeneratedClass.SuperStruct;
            while (superStruct.SuperStruct.TryLoad<UClass>(out superStruct) && superStruct != null)
            {
                if (superStruct is UScriptClass)
                {
                    if (Settings.Instance.Game != null! &&
                        Settings.Instance.Game.Jmap.TryFindProperties(currentPackageIndex, out var properties))
                    {
                        foreach (var prop in properties!)
                        {
                            if (ClassDefaultObject != null)
                                prop.SetPropertyDefaults(ClassDefaultObject);
                            ParentProperties.Add(prop);
                        }
                        
                    }

                    return;
                }
                
                currentPackageIndex = superStruct.SuperStruct;
                
                foreach (var fp in superStruct.ChildProperties.OfType<FProperty>())
                {
                    if (IsAnimBlueprint && AnimBlueprintCollector.IsAnimGraphProperty(fp))
                        continue;
                    var prop = new PropertyData(fp, superStruct);
                    if (ClassDefaultObject != null)
                        prop.SetPropertyDefaults(ClassDefaultObject);
                    ParentProperties.Add(prop);
                }
            }
            
        }

        void CollectAnimBlueprint(IPackage package)
        {
            try
            {
                AnimBlueprintCollector.Collect(this, package);
            }
            catch (Exception ex)
            {
                ErrorLog.Record(ex, $"CollectAnimBlueprint({Name})");
            }
        }

        void CollectComponents(IPackage package)
        {
            try
            {
                IsActorBlueprint = ComponentCollector.IsActorOrDerived(GeneratedClass);
                if (!IsActorBlueprint)
                    return;
                Components.AddRange(ComponentCollector.Collect(this, package));
            }
            catch
            {
                // cooked component data is optional
            }
        }

        void TryLoadMaterial(IPackage package, string name)
        {
            try
            {
                if (!MaterialCollector.TryCollect(this, package, name))
                    return;
                SuperStruct = MaterialObject is UMaterialInstance inst
                    ? inst.Parent?.GetPathName() ?? "Material Instance"
                    : MaterialKind == MaterialAssetKind.MaterialFunction
                        ? "Material Function"
                        : "Material";
                MaterialGraph = MaterialGraphBuilder.Build(this);
            }
            catch (Exception ex)
            {
                ErrorLog.Record(ex, $"TryLoadMaterial({name})");
            }
        }

        private void CollectMacroData(IPackage package, string assetName)
        {
            try
            {
                MacroLeftoverReader.Collect(this, package, assetName);
            }
            catch
            {
                // leftover editor data is optional; keep the asset usable without it
            }
        }

        public void AddMacro(string name, BPGraph? graph = null, string? leftoverSource = null)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                Macros.Any(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                var existing = Macros.Find(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                    return;
                existing.Graph ??= graph ?? EngineMacros.FindSavedGraph(name);
                if (string.IsNullOrEmpty(existing.LeftoverSource))
                    existing.LeftoverSource = leftoverSource;
                return;
            }

            Macros.Add(new AssetMacro
            {
                Name = name,
                Graph = graph ?? EngineMacros.FindSavedGraph(name),
                LeftoverSource = leftoverSource
            });
        }

        public void AddOrUpdateMacro(string name, BPGraph? graph, string? foundIn, int instances = 1)
        {
            if (EngineMacros.IsBuiltIn(name))
                return;

            var existing = Macros.Find(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                Macros.Add(new AssetMacro
                {
                    Name = name,
                    Graph = graph ?? EngineMacros.FindSavedGraph(name),
                    InstanceCount = instances,
                    FoundIn = foundIn
                });
                return;
            }

            existing.InstanceCount = Math.Max(existing.InstanceCount, instances);
            existing.Graph ??= graph ?? EngineMacros.FindSavedGraph(name);
            if (string.IsNullOrEmpty(existing.FoundIn))
                existing.FoundIn = foundIn;
        }

        public bool IsLikelyMacroGraphName(string name)
        {
            if (name.Contains("EventGraph", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("UserConstructionScript", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("ExecuteUbergraph", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Default__", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("DelegateSignature", StringComparison.OrdinalIgnoreCase))
                return false;

            if (Functions.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (Events.ContainsKey(name))
                return false;

            return true;
        }
    }

    public class AssetIsNotBlueprintException : Exception
    {
        public AssetIsNotBlueprintException() : base("Asset is not a blueprint or material") { }
    }
}
