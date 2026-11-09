using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.EdGraph;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using UEBlueprintGraphViewer.Engine;
using UEBlueprintGraphViewer.Nodes;

namespace UEBlueprintGraphViewer.Assets
{
    public sealed class AnimPoseLinkRecord
    {
        public required string PinName { get; init; }
        public int LinkId { get; init; }
    }

    public sealed class AnimNodeRecord
    {
        public required string Name { get; init; }
        public required string NodeType { get; init; }
        public int Index { get; init; }
        public List<AnimPoseLinkRecord> PoseInputs { get; } = [];
        public List<(string Name, string Value, string Type)> DataPins { get; } = [];
        public float EditorX;
        public float EditorY;
        public bool HasEditorPosition;
    }

    public sealed class AnimStateMachineRecord
    {
        public required string Name { get; init; }
        public int InitialState { get; init; }
        public List<(string Name, int RootNodeIndex)> States { get; } = [];
        public List<(int From, int To, string Name, string Duration)> Transitions { get; } = [];
    }

    public sealed class AnimGraphSnapshot
    {
        public string SkeletonName { get; set; } = "";
        public List<AnimNodeRecord> Nodes { get; } = [];
        public List<AnimStateMachineRecord> StateMachines { get; } = [];
        public List<(string Name, UObject Graph)> LeftoverGraphs { get; } = [];
    }

    public static class AnimBlueprintCollector
    {
        public static bool IsAnimBlueprint(UClass? cls)
        {
            if (cls == null)
                return false;
            if (cls is UAnimBlueprintGeneratedClass)
                return true;
            if (ClassLooksLike(cls, "AnimBlueprintGeneratedClass"))
                return true;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            UClass? current = cls;
            while (current != null && seen.Add(current.Name))
            {
                if (IsAnimInstanceName(current.Name) || IsAnimInstanceName(current.SuperStruct?.Name))
                    return true;
                if (!current.SuperStruct.TryLoad(out UClass next) || next == null || ReferenceEquals(next, current))
                    break;
                current = next;
            }

            return false;
        }

        public static void Collect(Asset asset, IPackage package)
        {
            if (asset.GeneratedClass == null || !IsAnimBlueprint(asset.GeneratedClass))
                return;

            asset.IsAnimBlueprint = true;
            var snap = new AnimGraphSnapshot();
            try
            {
                snap.SkeletonName = PackageIndexName(asset.GeneratedClass.GetOrDefault<FPackageIndex?>("TargetSkeleton"));
                CollectCookedNodes(asset, snap);
                CollectStateMachines(asset.GeneratedClass, snap);
                CollectLeftoverGraphs(package, snap);
            }
            catch (Exception ex)
            {
                ErrorLog.Record(ex, $"CollectAnimBlueprint({asset.Name})");
            }

            asset.AnimSnapshot = snap;
            foreach (var graph in AnimGraphBuilder.BuildAll(snap))
                asset.AnimGraphs[graph.Key] = graph.Value;
        }

        static void CollectCookedNodes(Asset asset, AnimGraphSnapshot snap)
        {
            var cls = asset.GeneratedClass!;
            var cdo = asset.ClassDefaultObject;
            var props = cls.ChildProperties?.OfType<FStructProperty>()
                .Where(p => IsAnimNodeStruct(p.Struct?.Name))
                .ToList() ?? [];

            if (props.Count == 0 && cdo != null)
            {
                foreach (var tag in cdo.Properties)
                {
                    if (!IsAnimNodeStruct(tag.PropertyType.Text) && !IsAnimNodeStruct(tag.Name.Text))
                        continue;
                    AddNode(snap, snap.Nodes.Count, tag.Name.Text, ReadStruct(cdo, tag.Name.Text), StructTypeName(tag));
                }
                return;
            }

            for (int i = 0; i < props.Count; i++)
            {
                var prop = props[i];
                string structName = prop.Struct?.Name ?? "AnimNode";
                AddNode(snap, i, prop.Name.Text, cdo != null ? ReadStruct(cdo, prop.Name.Text) : null, structName);
            }
        }

        static void AddNode(AnimGraphSnapshot snap, int index, string name, FStructFallback? data, string structName)
        {
            var node = new AnimNodeRecord
            {
                Name = PrettyNodeName(name, structName, data),
                NodeType = PrettyNodeType(structName),
                Index = index
            };

            if (data != null)
            {
                foreach (var (pin, linkId) in ReadPoseLinks(data))
                {
                    if (linkId >= 0)
                        node.PoseInputs.Add(new AnimPoseLinkRecord { PinName = pin, LinkId = linkId });
                }

                foreach (var (pin, value, type) in ReadDataPins(data))
                    node.DataPins.Add((pin, value, type));
            }

            snap.Nodes.Add(node);
        }

        static void CollectStateMachines(UObject cls, AnimGraphSnapshot snap)
        {
            foreach (var machine in EnumerateStructs(cls, "BakedStateMachines"))
            {
                string name = machine.GetOrDefault<FName>("MachineName").Text;
                if (string.IsNullOrEmpty(name) || name == "None")
                    name = $"State Machine {snap.StateMachines.Count + 1}";

                var record = new AnimStateMachineRecord
                {
                    Name = name,
                    InitialState = machine.GetOrDefault("InitialState", 0)
                };

                foreach (var state in EnumerateStructs(machine, "States"))
                {
                    string stateName = state.GetOrDefault<FName>("StateName").Text;
                    if (string.IsNullOrEmpty(stateName) || stateName == "None")
                        stateName = $"State {record.States.Count}";
                    record.States.Add((stateName, state.GetOrDefault("StateRootNodeIndex", -1)));
                }

                var transitions = EnumerateStructs(machine, "Transitions").ToList();
                for (int i = 0; i < transitions.Count; i++)
                {
                    var tr = transitions[i];
                    int from = tr.GetOrDefault("PreviousState", -1);
                    int to = tr.GetOrDefault("NextState", -1);
                    string trName = tr.GetOrDefault<FName>("Name").Text;
                    if (string.IsNullOrEmpty(trName) || trName == "None")
                        trName = $"Transition {i}";
                    string duration = FormatNumber(tr.GetOrDefault<object>("CrossfadeDuration"));
                    record.Transitions.Add((from, to, trName, duration));
                }

                snap.StateMachines.Add(record);
            }
        }

        public static bool IsAnimGraphProperty(FProperty prop)
        {
            string name = prop.Name.Text;
            if (name.Contains("AnimGraphNode", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("AnimNode_", StringComparison.OrdinalIgnoreCase))
                return true;
            return prop switch
            {
                FStructProperty structProp => IsAnimNodeStruct(structProp.Struct?.Name),
                FObjectProperty objectProp => IsAnimClassName(objectProp.PropertyClass?.Name),
                FArrayProperty { Inner: FStructProperty innerStruct } => IsAnimNodeStruct(innerStruct.Struct?.Name),
                FArrayProperty { Inner: FObjectProperty innerObject } => IsAnimClassName(innerObject.PropertyClass?.Name),
                _ => false
            };
        }

        static bool IsAnimClassName(string? name)
        {
            if (string.IsNullOrEmpty(name) || name == "None")
                return false;
            string n = name.SubstringAfterLast('.');
            return n.Contains("AnimGraphNode", StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("AnimStateNode", StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("AnimStateTransition", StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("AnimStateEntry", StringComparison.OrdinalIgnoreCase);
        }

        static void CollectLeftoverGraphs(IPackage package, AnimGraphSnapshot snap)
        {
            List<UObject> exports = [];
            foreach (var lazy in package.ExportsLazy)
            {
                try
                {
                    if (lazy.Value is { } obj)
                        exports.Add(obj);
                }
                catch
                {
                    // skip broken leftover exports
                }
            }

            var animNodes = exports.Where(IsAnimOrStateNode).ToList();
            var graphNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var export in exports.Where(IsEdGraph))
            {
                bool named = IsAnimGraphName(export.Name);
                bool hasNodes = animNodes.Any(o =>
                    string.Equals(o.Outer?.Name.ToString(), export.Name, StringComparison.OrdinalIgnoreCase));
                bool listed = ListedNodes(export).Any(IsAnimOrStateNode);
                if (named || hasNodes || listed)
                {
                    if (graphNames.Add(export.Name))
                        snap.LeftoverGraphs.Add((export.Name, export));
                }
            }

            foreach (var group in animNodes.GroupBy(o => o.Outer?.Name.ToString() ?? "AnimGraph"))
            {
                if (graphNames.Contains(group.Key))
                    continue;
                var graph = exports.FirstOrDefault(o => IsEdGraph(o) && o.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
                if (graph != null && graphNames.Add(graph.Name))
                    snap.LeftoverGraphs.Add((graph.Name, graph));
            }
        }

        static FStructFallback? ReadStruct(UObject obj, string name)
        {
            var value = obj.GetOrDefault<object>(name);
            return value switch
            {
                FStructFallback fb => fb,
                FScriptStruct { StructType: FStructFallback fb } => fb,
                _ => obj.GetOrDefault<FStructFallback?>(name)
            };
        }

        static IEnumerable<(string Pin, int LinkId)> ReadPoseLinks(FStructFallback data)
        {
            foreach (var tag in data.Properties)
            {
                string pin = tag.Name.Text;
                if (IsSkippedField(pin))
                    continue;

                foreach (var link in EnumeratePoseLinks(tag.Tag?.GenericValue, pin))
                    yield return link;
            }
        }

        static IEnumerable<(string Pin, int LinkId)> EnumeratePoseLinks(object? value, string pin)
        {
            switch (value)
            {
                case FStructFallback fb when IsPoseLink(fb):
                    int id = fb.GetOrDefault("LinkID", fb.GetOrDefault("LinkId", -1));
                    yield return (pin, id);
                    break;
                case FScriptStruct { StructType: FStructFallback inner } when IsPoseLink(inner):
                    yield return (pin, inner.GetOrDefault("LinkID", inner.GetOrDefault("LinkId", -1)));
                    break;
                case UScriptArray array:
                    int index = 0;
                    foreach (var item in array.Properties)
                    {
                        foreach (var link in EnumeratePoseLinks(item.GenericValue, $"{pin} {index}"))
                            yield return link;
                        index++;
                    }
                    break;
                case FStructFallback[] items:
                    for (int i = 0; i < items.Length; i++)
                    {
                        foreach (var link in EnumeratePoseLinks(items[i], $"{pin} {i}"))
                            yield return link;
                    }
                    break;
            }
        }

        static IEnumerable<(string Name, string Value, string Type)> ReadDataPins(FStructFallback data)
        {
            foreach (var tag in data.Properties)
            {
                string name = tag.Name.Text;
                if (IsSkippedField(name) || IsPoseLinkField(tag.Tag?.GenericValue))
                    continue;
                string value = FormatValue(tag.Tag?.GenericValue);
                if (string.IsNullOrEmpty(value) || value == "None")
                    continue;
                yield return (name, value, tag.PropertyType.Text);
            }
        }

        static IEnumerable<FStructFallback> EnumerateStructs(UObject obj, string name)
        {
            if (obj.TryGetValue(out FStructFallback[] structs, name) && structs != null)
                return structs;
            return EnumerateStructsFromValue(obj.GetOrDefault<object>(name));
        }

        static IEnumerable<FStructFallback> EnumerateStructs(FStructFallback obj, string name)
        {
            if (obj.TryGetValue(out FStructFallback[] structs, name) && structs != null)
                return structs;
            return EnumerateStructsFromValue(obj.GetOrDefault<object>(name));
        }

        static IEnumerable<FStructFallback> EnumerateStructsFromValue(object? value)
        {
            switch (value)
            {
                case FStructFallback[] structs:
                    return structs;
                case UScriptArray array:
                    return array.Properties.Select(o => o.GenericValue).Select(ToFallback).OfType<FStructFallback>();
                case FStructFallback fb:
                    return [fb];
                default:
                    return [];
            }
        }

        static FStructFallback? ToFallback(object? value) =>
            value switch
            {
                FStructFallback fb => fb,
                FScriptStruct { StructType: FStructFallback fb } => fb,
                _ => null
            };

        static bool IsPoseLink(FStructFallback fb) =>
            fb.Properties.Any(o => o.Name.Text is "LinkID" or "LinkId" or "SourceLinkID");

        static bool IsPoseLinkField(object? value) =>
            value switch
            {
                FStructFallback fb => IsPoseLink(fb),
                FScriptStruct { StructType: FStructFallback fb } => IsPoseLink(fb),
                UScriptArray array => array.Properties.Any(o => IsPoseLinkField(o.GenericValue)),
                _ => false
            };

        static bool IsAnimNodeStruct(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            string n = name.SubstringAfterLast('.');
            return n.Contains("AnimNode", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsAnimInstanceName(string? name)
        {
            if (string.IsNullOrEmpty(name) || name == "None")
                return false;
            string n = name.SubstringAfterLast('.');
            return n.Contains("AnimInstance", StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("AnimBlueprint", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsAnimGraphName(string name) =>
            name.Contains("AnimGraph", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("StateMachine", StringComparison.OrdinalIgnoreCase);

        static IEnumerable<UObject> ListedNodes(UObject graph)
        {
            if (graph.GetOrDefault<FPackageIndex[]>("Nodes") is not { Length: > 0 } indexes)
                yield break;
            foreach (var index in indexes)
            {
                UObject? obj = null;
                try
                {
                    obj = index?.Load<UObject>();
                }
                catch
                {
                    // leftover node
                }
                if (obj != null)
                    yield return obj;
            }
        }

        static bool IsEdGraph(UObject obj) =>
            obj.ExportType.Ends("EdGraph") && !obj.ExportType.Contains("Node", StringComparison.Ordinal);

        static bool IsAnimGraphNode(UObject obj) =>
            obj is UAnimGraphNode_Base ||
            obj.ExportType.Contains("AnimGraphNode", StringComparison.OrdinalIgnoreCase);

        public static bool IsAnimOrStateNode(UObject obj) =>
            IsAnimGraphNode(obj) || IsStateMachineEditorNode(obj);

        public static bool IsStateMachineEditorNode(UObject obj)
        {
            if (obj is UAnimStateNode or UAnimStateTransitionNode or UAnimStateEntryNode)
                return true;
            string type = obj.ExportType;
            return type.Contains("AnimStateNode", StringComparison.OrdinalIgnoreCase) ||
                   type.Contains("AnimStateTransition", StringComparison.OrdinalIgnoreCase) ||
                   type.Contains("AnimStateEntry", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsTransitionNode(UObject obj) =>
            obj is UAnimStateTransitionNode ||
            obj.ExportType.Contains("AnimStateTransition", StringComparison.OrdinalIgnoreCase);

        public static bool IsEntryNode(UObject obj) =>
            obj is UAnimStateEntryNode ||
            obj.ExportType.Contains("AnimStateEntry", StringComparison.OrdinalIgnoreCase);

        public static bool IsStateNode(UObject obj) =>
            obj is UAnimStateNode ||
            (obj.ExportType.Contains("AnimStateNode", StringComparison.OrdinalIgnoreCase) &&
             !IsTransitionNode(obj) && !IsEntryNode(obj));

        static bool ClassLooksLike(UObject obj, string token)
        {
            string cls = obj.Class?.Name.ToString() ?? obj.ExportType;
            return cls.Contains(token, StringComparison.OrdinalIgnoreCase);
        }

        static bool IsSkippedField(string name) =>
            name is "EvaluateGraphExposedInputs" or "NodeIndex" or "NodeId" or "Tag" or
                "SourceLinkID" or "LinkID" or "LinkId" or "bHasBeenInitialized";

        static string PrettyNodeType(string structName)
        {
            string n = structName.SubstringAfterLast('.');
            if (n.StartsWith("FAnimNode_", StringComparison.Ordinal))
                return n["FAnimNode_".Length..];
            if (n.StartsWith("AnimNode_", StringComparison.Ordinal))
                return n["AnimNode_".Length..];
            if (n.StartsWith("AnimGraphNode_", StringComparison.Ordinal))
                return n["AnimGraphNode_".Length..];
            return n;
        }

        static string PrettyNodeName(string propName, string structName, FStructFallback? data)
        {
            string type = PrettyNodeType(structName);
            string asset = data == null ? "" : FirstAssetName(data);
            if (!string.IsNullOrEmpty(asset))
                return $"{type}\n{asset}";
            string clean = propName.SubstringAfterLast('.');
            if (clean.StartsWith("AnimGraphNode_", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("AnimNode_", StringComparison.OrdinalIgnoreCase))
                return type;
            return string.IsNullOrEmpty(clean) ? type : clean;
        }

        static string FirstAssetName(FStructFallback data)
        {
            foreach (var key in new[] { "Sequence", "BlendSpace", "AnimationAsset", "SlotName", "CachePoseName", "PoseName" })
            {
                string value = FormatValue(data.GetOrDefault<object>(key));
                if (!string.IsNullOrEmpty(value) && value != "None")
                    return value;
            }
            return "";
        }

        static string StructTypeName(FPropertyTag tag)
        {
            string type = tag.PropertyType.Text;
            if (type.Contains("AnimNode", StringComparison.OrdinalIgnoreCase))
                return type;
            return tag.Name.Text;
        }

        static string FormatValue(object? value) =>
            value switch
            {
                null => "",
                FPackageIndex index => PackageIndexName(index),
                FName name => name.IsNone ? "" : name.Text,
                bool b => b ? "true" : "false",
                float f => f.ToString("0.###"),
                double d => d.ToString("0.###"),
                FStructFallback fb => FormatNumber(fb.GetOrDefault<object>("X")) is { Length: > 0 } ? "" : "",
                _ => value.ToString() ?? ""
            };

        static string FormatNumber(object? value) =>
            value switch
            {
                float f => f.ToString("0.###"),
                double d => d.ToString("0.###"),
                int i => i.ToString(),
                _ => value?.ToString() ?? ""
            };

        static string PackageIndexName(FPackageIndex? index)
        {
            if (index == null || index.IsNull)
                return "";
            string name = index.Name;
            return string.IsNullOrEmpty(name) || name == "None" ? "" : name.SubstringAfterLast('.');
        }
    }
}
