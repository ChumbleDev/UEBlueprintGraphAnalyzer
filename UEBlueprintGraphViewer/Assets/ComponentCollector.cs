using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using UEBlueprintGraphViewer.Engine;
using JmapData = UEBlueprintGraphViewer.JmapData;
using Settings = UEBlueprintGraphViewer.Settings;
using ErrorLog = UEBlueprintGraphViewer.ErrorLog;

namespace UEBlueprintGraphViewer.Assets
{
    public static class ComponentCollector
    {
        public static bool IsActorOrDerived(UClass? cls)
        {
            while (cls != null)
            {
                if (cls.Name is "Actor" or "AActor")
                    return true;

                string path = cls.GetPathName();
                if (path.EndsWith(".Actor", StringComparison.Ordinal) ||
                    path.EndsWith("/Script/Engine.Actor", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!TryLoadSuper(cls, out var next))
                    break;
                cls = next;
            }

            return false;
        }

        public static List<AssetComponent> Collect(Asset asset, IPackage package)
        {
            if (asset.GeneratedClass == null)
                return [];

            var byName = new Dictionary<string, AssetComponent>(StringComparer.OrdinalIgnoreCase);

            CollectFromBlueprint(asset.GeneratedClass, byName, inherited: false);
            CollectFromCdo(asset.ClassDefaultObject, byName);
            CollectFromExports(package, asset.GeneratedClass, asset.ClassDefaultObject, byName);

            UClass? parentClass = asset.GeneratedClass;
            while (parentClass.SuperStruct.TryLoad(out UClass next) && next != null && !ReferenceEquals(next, parentClass))
            {
                parentClass = next;
                if (parentClass is UBlueprintGeneratedClass parentBp)
                    CollectFromBlueprint(parentBp, byName, inherited: true);
                else
                    break;
            }

            return BuildTree(byName.Values.ToList());
        }

        static void CollectFromBlueprint(UBlueprintGeneratedClass bpClass, Dictionary<string, AssetComponent> byName, bool inherited)
        {
            var scs = bpClass.SimpleConstructionScript?.Load<USimpleConstructionScript>();
            if (scs != null)
                WalkScs(scs.RootNodes, null, inherited, byName);

            var ich = bpClass.InheritableComponentHandler?.Load<UInheritableComponentHandler>();
            if (ich != null)
            {
                foreach (var record in ich.GetRecords())
                {
                    string name = record.ComponentKey.SCSVariableName.Text;
                    if (IsEmptyName(name))
                        name = record.ComponentTemplate?.Name ?? "";
                    var template = record.ComponentTemplate?.Load<UObject>();
                    string className = PrettyClassName(record.ComponentClass.Name) ;
                    if (IsEmptyName(className) || className == "None")
                        className = ClassNameOf(template);
                    Add(byName, name, className, template, inherited, parentName: null);
                }
            }

            foreach (var index in bpClass.ComponentTemplates)
            {
                var template = index?.Load<UObject>();
                if (template == null || !LooksLikeComponent(template))
                    continue;
                Add(byName, CleanName(template.Name), ClassNameOf(template), template, inherited, AttachParentName(template));
            }
        }

        static void WalkScs(FPackageIndex?[] nodes, string? parentName, bool inherited, Dictionary<string, AssetComponent> byName)
        {
            foreach (var index in nodes)
            {
                var node = index?.Load<USCS_Node>();
                if (node == null)
                    continue;

                string name = node.InternalVariableName.Text;
                if (IsEmptyName(name))
                    name = node.GetOrDefault<FName>("VariableName").Text;
                var template = node.ComponentTemplate?.Load<UObject>() ?? node.GetComponentTemplateAsIndex().Load<UObject>();
                if (IsEmptyName(name))
                    name = CleanName(template?.Name);

                string className = ClassNameOf(template);
                if (IsEmptyName(className) || className == "ActorComponent")
                {
                    var componentClass = node.GetOrDefault<FPackageIndex?>("ComponentClass");
                    if (componentClass != null && !componentClass.IsNull)
                        className = PrettyClassName(componentClass.Name);
                }

                Add(byName, name, className, template, inherited, parentName);
                WalkScs(node.ChildNodes, name, inherited, byName);
            }
        }

        static void CollectFromCdo(UObject? cdo, Dictionary<string, AssetComponent> byName)
        {
            if (cdo == null)
                return;

            foreach (var tag in cdo.Properties)
            {
                if (tag.Tag?.GenericValue is not FPackageIndex index || index.IsNull)
                    continue;
                var obj = index.Load<UObject>();
                if (obj == null || !LooksLikeComponent(obj) || IsDefaultObject(obj))
                    continue;
                Add(byName, CleanName(tag.Name.Text), ClassNameOf(obj), obj, inherited: false, AttachParentName(obj));
            }
        }

        static void CollectFromExports(IPackage package, UBlueprintGeneratedClass generated, UObject? cdo, Dictionary<string, AssetComponent> byName)
        {
            foreach (var export in package.GetExports())
            {
                if (export == null || export == generated || export == cdo || !LooksLikeComponent(export) || IsDefaultObject(export))
                    continue;
                if (!IsOwnedByBlueprint(export, generated, cdo))
                    continue;
                Add(byName, CleanName(export.Name), ClassNameOf(export), export, inherited: false, AttachParentName(export));
            }
        }

        static void Add(Dictionary<string, AssetComponent> byName, string name, string className, UObject? template, bool inherited, string? parentName)
        {
            if (IsEmptyName(name))
                return;

            if (byName.TryGetValue(name, out var existing))
            {
                existing.Template ??= template;
                if (existing.ClassName is "ActorComponent" or "" && !IsEmptyName(className))
                    existing.ClassName = className;
                if (string.IsNullOrEmpty(existing.ParentName) && !IsEmptyName(parentName))
                    existing.ParentName = parentName;
                if (template != null)
                    existing.IsSceneComponent |= IsSceneComponent(template);
                if (!inherited)
                    existing.IsInherited = false;
                return;
            }

            byName[name] = new AssetComponent
            {
                Name = name,
                ClassName = IsEmptyName(className) ? "ActorComponent" : className,
                ParentName = IsEmptyName(parentName) ? null : parentName,
                IsInherited = inherited,
                IsSceneComponent = template != null && IsSceneComponent(template),
                Template = template
            };
        }

        static List<AssetComponent> BuildTree(List<AssetComponent> all)
        {
            var lookup = all.ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);
            var roots = new List<AssetComponent>();

            foreach (var component in all)
            {
                if (!string.IsNullOrEmpty(component.ParentName) &&
                    !component.ParentName.Equals(component.Name, StringComparison.OrdinalIgnoreCase) &&
                    lookup.TryGetValue(component.ParentName, out var parent))
                {
                    parent.Children.Add(component);
                }
                else
                {
                    roots.Add(component);
                }
            }

            return roots;
        }

        static bool LooksLikeComponent(UObject obj)
        {
            if (obj is UActorComponent)
                return true;
            if (obj is UBlueprintGeneratedClass or UFunction or USimpleConstructionScript or UInheritableComponentHandler)
                return false;

            string className = obj.Class?.Name.Text ?? obj.ExportType;
            return className.EndsWith("Component", StringComparison.Ordinal) &&
                   !className.Contains("Handler", StringComparison.Ordinal);
        }

        static bool IsSceneComponent(UObject obj)
        {
            if (obj is USceneComponent)
                return true;
            return obj.Properties.Any(o => o.Name.Text is "AttachParent" or "RelativeLocation" or "RelativeRotation");
        }

        static bool IsOwnedByBlueprint(UObject obj, UBlueprintGeneratedClass generated, UObject? cdo)
        {
            var outer = obj.Outer?.Object?.Value;
            return outer == generated || outer == cdo ||
                   (outer != null && (outer.Name == generated.Name || outer.Name == cdo?.Name));
        }

        static bool IsDefaultObject(UObject obj) => obj.Name.StartsWith("Default__", StringComparison.Ordinal);

        static string? AttachParentName(UObject obj)
        {
            var parent = obj.GetOrDefault<FPackageIndex?>("AttachParent");
            if (parent == null || parent.IsNull)
                return null;
            string name = CleanName(parent.Name);
            return IsEmptyName(name) ? null : name;
        }

        static string ClassNameOf(UObject? obj) => PrettyClassName(obj?.Class?.Name.Text ?? obj?.ExportType ?? "ActorComponent");

        static string PrettyClassName(string? name)
        {
            if (string.IsNullOrEmpty(name) || name == "None")
                return "ActorComponent";
            name = name.SubstringAfterLast('.');
            if (name.StartsWith('U') && name.Length > 1 && char.IsUpper(name[1]))
                name = name[1..];
            return name;
        }

        static string CleanName(string? name)
        {
            if (string.IsNullOrEmpty(name) || name == "None")
                return "";
            name = name.SubstringAfterLast('.');
            if (name.EndsWith("_GEN_VARIABLE", StringComparison.Ordinal))
                name = name[..^"_GEN_VARIABLE".Length];
            return name;
        }

        static bool IsEmptyName(string? name) => string.IsNullOrWhiteSpace(name) || name == "None";

        static bool TryLoadSuper(UClass cls, out UClass? next)
        {
            next = null;
            var super = cls.SuperStruct;
            if (super == null || super.IsNull)
                return false;
            return super.TryLoad(out next) && next != null && !ReferenceEquals(next, cls);
        }

        public static List<PropertyData> ReadAllProperties(UObject? template)
        {
            if (template == null)
                return [];

            try
            {
                return ReadAllPropertiesCore(template);
            }
            catch (Exception ex)
            {
                ErrorLog.Record(ex, $"ReadAllProperties({template.Name})");
                return [];
            }
        }

        static List<PropertyData> ReadAllPropertiesCore(UObject template)
        {
            var collected = new List<PropertyData>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddProperty(PropertyData prop, bool clone)
            {
                if (string.IsNullOrEmpty(prop.Name) || !seen.Add(prop.Name))
                    return;
                var data = clone ? prop.Clone() : prop;
                data.SetPropertyDefaults(template);
                collected.Add(data);
            }

            // Same as Asset.LoadAllProperties: only build PropertyData from blueprint
            // ChildProperties. Native UScriptClass fields go through jmap — the FProperty
            // constructor NREs on engine delegate/array members.
            UClass? current = null;
            try
            {
                current = template.Class?.Load<UClass>();
            }
            catch (Exception ex)
            {
                ErrorLog.Record(ex, $"Load class {template.Name}");
            }

            FPackageIndex? nativeIndex = null;
            while (current != null && current is not UScriptClass)
            {
                foreach (var fp in current.ChildProperties?.OfType<FProperty>() ?? [])
                {
                    try
                    {
                        AddProperty(new PropertyData(fp, current), clone: false);
                    }
                    catch (Exception ex)
                    {
                        ErrorLog.Record(ex, $"Property {fp.Name} on {current.Name}");
                    }
                }

                nativeIndex = current.SuperStruct;
                if (!TryLoadSuper(current, out var next))
                    break;
                current = next;
            }

            var jmap = Settings.Instance.Game?.Jmap;
            if (jmap != null)
            {
                List<PropertyData>? nativeProps = null;
                bool found = TryJmapByPath(jmap, SafePath(template.Class), out nativeProps);

                if (!found)
                    found = TryJmapByPath(jmap, current?.GetPathName(), out nativeProps);

                if (!found && CanQueryJmap(nativeIndex))
                    found = jmap.TryFindProperties(nativeIndex!, out nativeProps) && nativeProps is { Count: > 0 };

                if (found && nativeProps != null)
                {
                    foreach (var prop in nativeProps)
                        AddProperty(prop, clone: true);
                }
            }

            foreach (var tag in template.Properties ?? [])
            {
                if (tag?.Name == null || !seen.Add(tag.Name.Text))
                    continue;
                var extra = new PropertyData(
                    tag.Name.Text,
                    template.Name,
                    tag.PropertyType.Text,
                    default,
                    "",
                    "",
                    "",
                    "",
                    "",
                    "");
                extra.SetPropertyDefaults(template);
                collected.Add(extra);
            }

            return collected;
        }

        static bool CanQueryJmap(FPackageIndex? index) =>
            index != null && !index.IsNull && index.ResolvedObject != null;

        static bool TryJmapByPath(JmapData jmap, string? path, out List<PropertyData>? props)
        {
            props = null;
            return !string.IsNullOrEmpty(path) &&
                   jmap.TryFindPropertiesByPath(path, out props) &&
                   props is { Count: > 0 };
        }

        static string? SafePath(CUE4Parse.UE4.Assets.ResolvedObject? obj)
        {
            if (obj == null)
                return null;
            try
            {
                return obj.GetPathName();
            }
            catch
            {
                return obj.Name.ToString();
            }
        }
    }
}
