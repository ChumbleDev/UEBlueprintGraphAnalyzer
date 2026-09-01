using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Misc;
using UEBlueprintGraphViewer.Decompiler;
using static UEBlueprintGraphViewer.Engine.EngineBPData;
using static UEBlueprintGraphViewer.Engine.EngineEnums;

namespace UEBlueprintGraphViewer.Engine
{
    // Object containing property info
    public class PropertyData
    {
        public string Name;

        public EPropertyFlags Flags;

        public UObject? OwnerObject;
        public string Owner;

        public GraphPinType PinType;
        
        public FPackageIndex? PropertyClassPackageIndex;
        public string DelegateSignatureFunction;
        public string DelegateSignatureObjectPath;
        public UObject? DelegateSignatureObject;

        public string DefaultValue;
        public List<PropertyValueNode> ValueChildren = [];

        public override string ToString()
        {
            return $"{PinType.ContainerType} {PinType.PinCategory} {Name}";
        }

        public PropertyData Clone()
        {
            return (PropertyData)MemberwiseClone();
        }

        public bool IsFunctionParam()
        {
            return Flags.HasFlag(EPropertyFlags.Parm);
        }

        // ReferenceParm always requires OutParam to be set, even if it's not output parameter
        // flag check is taken from https://github.com/EpicGames/UnrealEngine/blob/5.4/Engine/Source/Editor/BlueprintGraph/Private/K2Node_CallFunction.cpp#L1189
        public bool IsOutParam()
        {
            return Flags.HasFlag(EPropertyFlags.ReturnParm) || Flags.HasFlag(EPropertyFlags.OutParm) && !Flags.HasFlag(EPropertyFlags.ReferenceParm);
        }

        public bool IsInputParam()
        {
            return IsFunctionParam() && !IsOutParam();
        }

        public bool IsTempVar()
        {
            return Name.Starts("Temp_");
        }

        public PropertyData(FProperty? prop, UObject owner) : this(new PropertyContainer(prop), owner) { }
        public PropertyData(UProperty? prop, UObject owner) : this(new PropertyContainer(prop), owner) { }

        public PropertyData(PropertyContainer? prop, UObject? owner)
        {
            if (prop == null || owner == null)
                return;

            Name = prop.GetName();
            Owner = owner.GetPathName();
            OwnerObject = owner;
            Flags = prop.GetFlags();
            MakePinType(prop);
            if (prop.New is FMulticastInlineDelegateProperty d)
            {
                DelegateSignatureFunction = d.SignatureFunction.Name;
                DelegateSignatureObjectPath = d.SignatureFunction.ResolvedObject?.Outer?.GetPathName() ??
                                              throw new DecompilerException("Delegate signature function object is null");
                DelegateSignatureObject = d.SignatureFunction.ResolvedObject.Outer.Load();
            }
            prop.Clear();
        }

        public PropertyData(
            string name,
            string owner,
            string type,
            EPropertyFlags flags,
            string className,
            string innerProp,
            string keyProp,
            string valueProp,
            string delegateSignatureFunction,
            string delegateSignatureObject)
        {
            Name = name;
            Owner = owner;
            Flags = flags;
            DelegateSignatureFunction = delegateSignatureFunction;
            DelegateSignatureObjectPath = delegateSignatureObject;
            MakePinType(type, innerProp, keyProp, valueProp, className);
        }

        private string GetClassName(PropertyContainer prop)
        {
            FPackageIndex? index;
            if (prop.IsNew)
            {
                index = prop.New switch
                {
                    FInterfaceProperty p => p.InterfaceClass,
                    FSoftClassProperty p => p.MetaClass,
                    FClassProperty p => p.MetaClass,
                    FObjectProperty p => p.PropertyClass,
                    FStructProperty p => p.Struct,
                    FByteProperty p => p.Enum,
                    FEnumProperty p => p.Enum,
                    _ => null,
                };
            }
            else
            {
                index = prop.Old switch
                {
                    UInterfaceProperty p => p.InterfaceClass,
                    USoftClassProperty p => p.MetaClass,
                    UClassProperty p => p.MetaClass,
                    UObjectProperty p => p.PropertyClass,
                    UStructProperty p => p.Struct,
                    UByteProperty p => p.Enum,
                    UEnumProperty p => p.Enum,
                    _ => null,
                };
            }

            if (index == null || index.IsNull) { return ""; };
            PropertyClassPackageIndex = index;
            return index.ResolvedObject?.GetPathName() ?? "";
        }

        // Make pin type for this property
        private void MakePinType(PropertyContainer prop)
        {
            PinType = new GraphPinType();

            PropertyContainer propContainer;

            if (prop.IsNew)
            {
                propContainer = prop.New switch
                {
                    FMapProperty p => new PropertyContainer(p.KeyProp!),
                    FSetProperty p => new PropertyContainer(p.ElementProp!),
                    FArrayProperty p => new PropertyContainer(p.Inner!),
                    _ => prop,
                };

                PinType.ContainerType = prop.New switch
                {
                    FMapProperty => EPinContainerType.Map,
                    FSetProperty => EPinContainerType.Set,
                    FArrayProperty => EPinContainerType.Array,
                    _ => EPinContainerType.None,
                };

                if (prop.New is FMapProperty map)
                {
                    PinType.PinSubCategory = PropTypeToPinType(new PropertyContainer(map.ValueProp!).GetPropType());
                }
            }
            else
            {
                throw new NotImplementedException();
            }

            PinType.PinCategory = PropTypeToPinType(propContainer.GetPropType());
            PinType.PinSubCategoryObject = GetClassName(propContainer);
            PinType.IsReference = Flags.HasFlag(EPropertyFlags.OutParm) && Flags.HasFlag(EPropertyFlags.ReferenceParm);
        }

        // Make pin type from types string
        private void MakePinType(string type, string inner, string key, string value, string className)
        {
            PinType = new GraphPinType();

            PinType.ContainerType = type switch
            {
                "MapProperty" => EPinContainerType.Map,
                "SetProperty" => EPinContainerType.Set,
                "ArrayProperty" => EPinContainerType.Array,
                _ => EPinContainerType.None,
            };

            if (PinType.ContainerType == EPinContainerType.Map)
            {
                PinType.PinCategory = PropTypeToPinType(key);
                PinType.PinSubCategory = PropTypeToPinType(value);
            }
            else
            {
                PinType.PinCategory = PropTypeToPinType(inner == None || type == "EnumProperty" ? type : inner);
            }
            PinType.PinSubCategoryObject = className == None ? None : className;
            PinType.IsReference = Flags.HasFlag(EPropertyFlags.OutParm) && Flags.HasFlag(EPropertyFlags.ReferenceParm);
        }
        
        public void SetPropertyDefaults(UObject source)
        {
            ValueChildren = [];
            if (source?.Properties?.FirstOrDefault(o => o.Name == Name) is { } value)
            {
                var parsed = ParseValue(value.Tag?.GenericValue);
                DefaultValue = parsed.Text;
                ValueChildren = parsed.Children;
            }
            else
                DefaultValue = "[ENGINE DEFAULT]";
        }

        public static string FormatTagValue(FPropertyTag tag) => ParseValue(tag.Tag?.GenericValue).Text;

        public static PropertyValueNode ParseValue(object? rawValue)
        {
            const int maxItems = 256;
            switch (rawValue)
            {
                case UScriptArray a:
                    return FromSequence(
                        CountLabel(a.Properties.Count, "element", "elements"),
                        a.Properties, maxItems);
                case UScriptSet s:
                    return FromSequence(
                        CountLabel(s.Properties.Count, "element", "elements"),
                        s.Properties, maxItems);
                case UScriptMap m:
                {
                    var node = new PropertyValueNode { Text = CountLabel(m.Properties.Count, "pair", "pairs") };
                    int i = 0;
                    foreach (var kv in m.Properties)
                    {
                        if (i >= maxItems)
                        {
                            node.Children.Add(new PropertyValueNode { Name = "…", Text = $"{m.Properties.Count - maxItems} more" });
                            break;
                        }

                        var key = ParseValue(kv.Key?.GenericValue);
                        var val = ParseValue(kv.Value?.GenericValue);
                        node.Children.Add(new PropertyValueNode
                        {
                            Name = string.IsNullOrEmpty(key.Text) ? $"[{i}]" : key.Text,
                            Text = val.Text,
                            Children = val.Children
                        });
                        i++;
                    }
                    return node;
                }
                case FScriptStruct { StructType: FStructFallback f }:
                {
                    var children = f.Properties.Select(p =>
                    {
                        var child = ParseValue(p.Tag?.GenericValue);
                        child.Name = p.Name.Text;
                        return child;
                    }).ToList();
                    return new PropertyValueNode
                    {
                        Text = children.Count == 0
                            ? "()"
                            : $"({string.Join("; ", children.Select(o => $"{o.Name}={o.Text}"))})",
                        Children = children
                    };
                }
                case FScriptStruct a:
                    return new PropertyValueNode { Text = a.ToString() };
                case FMulticastScriptDelegate a:
                {
                    var children = (a.InvocationList ?? []).Select((o, i) => new PropertyValueNode
                    {
                        Name = $"[{i}]",
                        Text = $"{o.Object.ResolvedObject?.GetPathName()}:{o.FunctionName}"
                    }).ToList();
                    return new PropertyValueNode
                    {
                        Text = children.Count == 0
                            ? "()"
                            : $"({string.Join("; ", children.Select(o => o.Text))})",
                        Children = children
                    };
                }
                default:
                    return new PropertyValueNode { Text = FormatLeaf(rawValue) };
            }
        }

        static PropertyValueNode FromSequence(string summary, IReadOnlyList<FPropertyTagType> items, int maxItems)
        {
            var node = new PropertyValueNode { Text = summary };
            int take = Math.Min(items.Count, maxItems);
            for (int i = 0; i < take; i++)
            {
                var child = ParseValue(items[i]?.GenericValue);
                child.Name = $"[{i}]";
                node.Children.Add(child);
            }

            if (items.Count > maxItems)
                node.Children.Add(new PropertyValueNode { Name = "…", Text = $"{items.Count - maxItems} more" });
            return node;
        }

        static string CountLabel(int count, string singular, string plural) =>
            $"{count} {(count == 1 ? singular : plural)}";

        static string FormatLeaf(object? rawValue) => rawValue switch
        {
            int a => a.ToString(),
            float a => a.ToString(),
            double a => a.ToString(),
            ulong a => a.ToString(),
            long a => a.ToString(),
            byte a => a.ToString(),
            bool a => a.ToString(),
            string a => a,
            FText a => a.ToString(),
            FName a => a.ToString(),
            FGuid a => a.ToString(),
            FPackageIndex a => a.ResolvedObject?.GetFullName() ?? a.ToString(),
            _ => rawValue?.ToString() ?? "",
        };

        private static PinType PropTypeToPinType(string type)
        {
            if (type == None)
                return EngineBPData.PinType.Unknown;

            if (type == "StrProperty")
            {
                return EngineBPData.PinType.String;
            }
            if (type.Ends("DelegateProperty"))
            {
                return EngineBPData.PinType.Delegate;
            }
            if (type.Ends("Property"))
            {
                type = type[..^"Property".Length];
            }

            return Enum.GetValues<PinType>().FirstOrDefault(v => v.ToString() == type);
        }

    }

    public class PropertyValueNode
    {
        public string Name = "";
        public string Text = "";
        public List<PropertyValueNode> Children = [];
    }
}
