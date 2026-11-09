using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.EdGraph;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.EdGraph;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using UEBlueprintGraphViewer.Engine;
using UEBlueprintGraphViewer.Nodes;
using static UEBlueprintGraphViewer.Engine.EngineBPData;
using static UEBlueprintGraphViewer.Engine.EngineEnums;
using static UEBlueprintGraphViewer.Engine.Utils;
using CuePinDir = CUE4Parse.UE4.Objects.Engine.EdGraph.EEdGraphPinDirection;

namespace UEBlueprintGraphViewer.Assets
{
    // Cooked macro libraries have no UFunction bytecode. Leftover editor graphs, export
    // names, and UBlueprint tags are the only traces still stored on the library itself.
    public static class MacroLeftoverReader
    {
        public static void Collect(Asset asset, IPackage package, string assetName)
        {
            var blueprint = FindBlueprintExport(package, assetName);
            if (blueprint != null && blueprint.Properties.Any(p => p.Name.Text == "BlueprintType"))
                asset.BlueprintType = (EBlueprintType)blueprint.GetOrDefault<byte>("BlueprintType");

            List<UObject> exports = LoadExports(package);

            Dictionary<string, UObject?> graphsByName = new(StringComparer.OrdinalIgnoreCase);
            foreach (var graphName in ReadIndexNames(blueprint, "MacroGraphs"))
            {
                graphsByName.TryAdd(graphName, FindNamedExport(exports, graphName));
            }

            foreach (var export in exports.Where(IsEdGraph))
                graphsByName.TryAdd(export.Name, export);

            foreach (var export in exports.Where(IsEdGraphNode))
            {
                string? graphName = export.Outer?.Name.Text;
                if (string.IsNullOrEmpty(graphName) || !asset.IsLikelyMacroGraphName(graphName))
                    continue;
                graphsByName.TryAdd(graphName, FindNamedExport(exports, graphName));
            }

            if (asset.GeneratedClass?.EditorTags is { Count: > 0 } tags)
            {
                foreach (var tag in tags.Where(o => o.Key.Text.Contains("Macro", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!string.IsNullOrWhiteSpace(tag.Value))
                        graphsByName.TryAdd(tag.Value, null);
                }
            }

            foreach (var (name, graphExport) in graphsByName)
            {
                if (!asset.IsLikelyMacroGraphName(name))
                    continue;
                if (IsAnimLeftoverGraph(name, exports))
                    continue;

                BPGraph? graph = graphExport != null
                    ? TryBuildGraph(graphExport, exports)
                    : EngineMacros.FindSavedGraph(name);

                asset.AddMacro(name, graph, graph is { Nodes.Count: > 0 }
                    ? "Leftover EdGraph in this package"
                    : "Name leftover in this package");
            }
        }

        static BPGraph? TryBuildGraph(UObject graphExport, List<UObject> exports)
        {
            var nodeExports = CollectGraphNodes(graphExport, exports);
            if (nodeExports.Count == 0)
                return EngineMacros.FindSavedGraph(graphExport.Name);

            BPGraph graph = new() { MacroName = graphExport.Name };
            Dictionary<UObject, BPNode> nodes = [];
            Dictionary<Guid, GraphPin> pins = [];

            foreach (var nodeExport in nodeExports)
            {
                var node = new BPNode
                {
                    Name = DisplayName(nodeExport),
                    NodeType = NodeTypeName(nodeExport),
                    X = nodeExport.GetOrDefault("NodePosX", 0),
                    Y = nodeExport.GetOrDefault("NodePosY", 0)
                };

                foreach (var pin in ReadPins(nodeExport))
                {
                    if (pin.IsInput)
                        node.AddInputPin(pin);
                    else
                        node.AddOutputPin(pin);

                    if (pin.PinType.PinCategory == PinType.exec)
                    {
                        if (pin.IsInput)
                            node.ExecPin ??= pin;
                        else
                            node.ExecOutPin ??= pin;
                    }

                    pins.TryAdd(pin.Guid, pin);
                }

                node.CalculateSize();
                nodes[nodeExport] = node;
                graph.AddNode(node);

                if (IsTunnel(nodeExport, node))
                {
                    bool isOutput = nodeExport.GetOrDefault("bCanHaveInputs", node.Name.Contains("Output", StringComparison.OrdinalIgnoreCase));
                    if (isOutput)
                        graph.MacroOutputNode = node;
                    else
                        graph.MacroInputNode = node;
                }
            }

            foreach (var nodeExport in nodeExports)
            {
                if (nodeExport is not UEdGraphNode edNode)
                    continue;

                foreach (var pinRef in edNode.Pins)
                {
                    if (pinRef is not UEdGraphPin pin)
                        continue;
                    if (!pins.TryGetValue(ToGuid(pin.PinId), out var from))
                        continue;

                    foreach (var linked in pin.LinkedTo)
                    {
                        if (linked == null)
                            continue;
                        if (!pins.TryGetValue(ToGuid(linked.PinId), out var to))
                            continue;
                        if (!from.LinkedTo.Contains(to))
                            Connect(from, to);
                    }
                }
            }

            graph.MacroInputNode ??= graph.Nodes.FirstOrDefault(o =>
                o.NodeType.Contains("Tunnel", StringComparison.OrdinalIgnoreCase) &&
                o.Name.Contains("Input", StringComparison.OrdinalIgnoreCase));
            graph.MacroOutputNode ??= graph.Nodes.FirstOrDefault(o =>
                o.NodeType.Contains("Tunnel", StringComparison.OrdinalIgnoreCase) &&
                o.Name.Contains("Output", StringComparison.OrdinalIgnoreCase));

            return graph.Nodes.Count > 0 ? graph : EngineMacros.FindSavedGraph(graphExport.Name);
        }

        static List<UObject> CollectGraphNodes(UObject graphExport, List<UObject> exports)
        {
            HashSet<UObject> nodes = [];

            foreach (var index in ReadPackageIndexes(graphExport, "Nodes"))
            {
                var obj = index.Load<UObject>();
                if (obj != null)
                    nodes.Add(obj);
            }

            foreach (var export in exports.Where(IsEdGraphNode))
            {
                if (export.Outer?.Name.Text.Equals(graphExport.Name, StringComparison.OrdinalIgnoreCase) == true)
                    nodes.Add(export);
            }

            return [.. nodes];
        }

        static IEnumerable<GraphPin> ReadPins(UObject nodeExport)
        {
            if (nodeExport is UEdGraphNode { Pins.Length: > 0 } edNode)
            {
                foreach (var pinRef in edNode.Pins)
                {
                    if (pinRef is UEdGraphPin pin)
                        yield return ConvertPin(pin);
                }
                yield break;
            }

            if (nodeExport is UK2Node_EditablePinBase { SerializedItems: { Length: > 0 } items })
            {
                foreach (var item in items)
                    yield return ConvertUserPin(item);
            }
        }

        static GraphPin ConvertPin(UEdGraphPin pin)
        {
            string name = pin.PinFriendlyName?.Text ?? pin.PinName.Text;
            var graphPin = new GraphPin(pin.PinName.Text, ToDirection(pin.Direction), ConvertPinType(pin.PinType), name)
            {
                Guid = ToGuid(pin.PinId),
                Value = pin.DefaultValue ?? ""
            };
            if (string.Equals(name, "execute", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "then", StringComparison.OrdinalIgnoreCase))
                graphPin.IsNameHidden = true;
            return graphPin;
        }

        static GraphPin ConvertUserPin(FUserPinInfo item)
        {
            return new GraphPin(
                item.PinName.Text,
                ToDirection(item.DesiredPinDirection),
                ConvertPinType(item.PinType),
                item.PinName.Text)
            {
                Value = item.PinDefaultValue ?? ""
            };
        }

        static GraphPinType ConvertPinType(FEdGraphPinType? type)
        {
            if (type == null)
                return MakePinType(PinType.Unknown);

            return new GraphPinType
            {
                PinCategory = FromEdGraphCategory(type.PinCategory.Text),
                PinSubCategory = FromEdGraphCategory(type.PinSubCategory.Text),
                PinSubCategoryObject = PackageIndexToName(type.PinSubCategoryObject),
                ContainerType = (Engine.EngineEnums.EPinContainerType)(byte)type.ContainerType,
                IsReference = type.bIsReference
            };
        }

        static PinType FromEdGraphCategory(string? category)
        {
            return category?.ToLowerInvariant() switch
            {
                "exec" => PinType.exec,
                "bool" => PinType.Bool,
                "byte" => PinType.Byte,
                "enum" => PinType.Enum,
                "int" or "int32" => PinType.Int,
                "int64" => PinType.Int64,
                "float" => PinType.Float,
                "double" or "real" => PinType.Double,
                "name" => PinType.Name,
                "string" => PinType.String,
                "text" => PinType.Text,
                "vector" => PinType.Vector,
                "rotator" => PinType.Rotator,
                "transform" => PinType.Transform,
                "interface" => PinType.Interface,
                "object" => PinType.Object,
                "softobject" or "asset" => PinType.SoftObject,
                "class" => PinType.Class,
                "softclass" or "assetclass" => PinType.SoftClass,
                "delegate" => PinType.Delegate,
                "struct" => PinType.Struct,
                "wildcard" => PinType.Wildcard,
                _ => PinType.Unknown
            };
        }

        static EngineEnums.EEdGraphPinDirection ToDirection(CuePinDir direction) =>
            direction == CuePinDir.EGPD_Output
                ? EngineEnums.EEdGraphPinDirection.EGPD_Output
                : EngineEnums.EEdGraphPinDirection.EGPD_Input;

        static Guid ToGuid(FGuid guid) =>
            Guid.TryParse(guid.ToString(EGuidFormats.DigitsWithHyphens), out var parsed) ? parsed : Guid.NewGuid();

        static string DisplayName(UObject node)
        {
            string comment = node.GetOrDefault("NodeComment", "");
            if (!string.IsNullOrWhiteSpace(comment))
                return comment;

            string typeName = NodeTypeName(node).SubstringAfter("K2Node_");
            return string.IsNullOrEmpty(node.Name) || node.Name.StartsWith("K2Node_", StringComparison.Ordinal)
                ? typeName
                : node.Name;
        }

        static string NodeTypeName(UObject node)
        {
            string className = node.ExportType.SubstringAfterLast('.');
            return className.StartsWith('U') && className.Contains("K2Node", StringComparison.Ordinal)
                ? className[1..]
                : className;
        }

        static bool IsTunnel(UObject nodeExport, BPNode node) =>
            node.NodeType.Contains("Tunnel", StringComparison.OrdinalIgnoreCase) ||
            nodeExport.ExportType.Contains("Tunnel", StringComparison.OrdinalIgnoreCase);

        static bool IsAnimLeftoverGraph(string name, List<UObject> exports)
        {
            if (name.Equals("AnimGraph", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("StateMachine", StringComparison.OrdinalIgnoreCase))
                return true;
            return exports.Any(o =>
                string.Equals(o.Outer?.Name.ToString(), name, StringComparison.OrdinalIgnoreCase) &&
                AnimBlueprintCollector.IsAnimOrStateNode(o));
        }

        static bool IsEdGraph(UObject obj) =>
            obj.ExportType.Ends("EdGraph") && !obj.ExportType.Contains("Node", StringComparison.Ordinal);

        static bool IsEdGraphNode(UObject obj) =>
            obj is UEdGraphNode ||
            obj.ExportType.Contains("EdGraphNode", StringComparison.Ordinal) ||
            obj.ExportType.Contains("K2Node", StringComparison.Ordinal);

        static List<UObject> LoadExports(IPackage package)
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
                    // skip exports that fail to deserialize
                }
            }
            return exports;
        }

        static UObject? FindNamedExport(List<UObject> exports, string name) =>
            exports.Find(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        static UObject? FindBlueprintExport(IPackage package, string assetName)
        {
            int index = package.GetExportIndex(assetName, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var obj = package.GetExport(index);
                if (obj != null && IsBlueprintAssetObject(obj))
                    return obj;
            }

            try
            {
                return LoadExports(package).FirstOrDefault(IsBlueprintAssetObject);
            }
            catch
            {
                return null;
            }
        }

        static bool IsBlueprintAssetObject(UObject obj)
        {
            string className = obj.ExportType;
            return className.Ends("Blueprint") &&
                   !className.Contains("GeneratedClass") &&
                   !className.Contains("Core");
        }

        static IEnumerable<string> ReadIndexNames(UObject? obj, string propName)
        {
            if (obj == null)
                yield break;

            foreach (var index in ReadPackageIndexes(obj, propName))
            {
                string name = PackageIndexToName(index);
                if (!string.IsNullOrEmpty(name) && name != "None")
                    yield return name;
            }
        }

        static IEnumerable<FPackageIndex> ReadPackageIndexes(UObject obj, string propName)
        {
            if (obj.TryGetValue(out FPackageIndex[] indexes, propName) && indexes != null)
                return indexes.Where(i => i is { IsNull: false });

            if (obj.GetOrDefault<object>(propName) is UScriptArray array)
            {
                return array.Properties
                    .Select(o => o.GenericValue)
                    .OfType<FPackageIndex>()
                    .Where(i => !i.IsNull);
            }

            return [];
        }
    }
}
