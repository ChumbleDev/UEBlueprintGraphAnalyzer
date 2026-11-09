using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.EdGraph;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine.EdGraph;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using UEBlueprintGraphViewer.Assets;
using UEBlueprintGraphViewer.Engine;
using static UEBlueprintGraphViewer.Engine.EngineBPData;
using static UEBlueprintGraphViewer.Engine.Utils;
using CuePinDir = CUE4Parse.UE4.Objects.Engine.EdGraph.EEdGraphPinDirection;
using PinDir = UEBlueprintGraphViewer.Engine.EngineEnums.EEdGraphPinDirection;

namespace UEBlueprintGraphViewer.Nodes
{
    public class AnimPoseNode : BPNode
    {
        public AnimPoseNode(string name, string nodeType) : base(name, null)
        {
            NodeType = nodeType;
            Pure = true;
            if (nodeType == "AnimRoot")
                HeaderCenter = true;
        }
    }

    public static class AnimGraphBuilder
    {
        public static Dictionary<string, BPGraph> BuildAll(AnimGraphSnapshot snap)
        {
            var graphs = new Dictionary<string, BPGraph>(StringComparer.OrdinalIgnoreCase);

            var leftover = snap.LeftoverGraphs.FirstOrDefault(o =>
                o.Name.Equals("AnimGraph", StringComparison.OrdinalIgnoreCase));
            if (leftover.Graph != null)
                graphs["AnimGraph"] = BuildFromEdGraph(leftover.Graph);
            else if (snap.Nodes.Count > 0)
                graphs["AnimGraph"] = BuildFromCooked(snap);

            foreach (var leftoverGraph in snap.LeftoverGraphs)
            {
                if (graphs.ContainsKey(leftoverGraph.Name))
                    continue;
                var built = BuildFromEdGraph(leftoverGraph.Graph);
                if (built.Nodes.Count > 0)
                    graphs[leftoverGraph.Name] = built;
            }

            foreach (var machine in snap.StateMachines)
            {
                if (graphs.ContainsKey(machine.Name))
                    continue;
                graphs[machine.Name] = BuildStateMachine(machine);
            }

            if (graphs.Count == 0)
            {
                var empty = NewAnimGraph();
                empty.AddNode(new AnimPoseNode("No anim graph data", "AnimRoot"));
                graphs["AnimGraph"] = empty;
            }

            return graphs;
        }

        static BPGraph NewAnimGraph() => new() { IsAnimGraph = true, IsDataFlowGraph = true };

        static BPGraph BuildFromCooked(AnimGraphSnapshot snap)
        {
            var graph = NewAnimGraph();
            var nodes = new Dictionary<int, AnimPoseNode>();

            foreach (var record in snap.Nodes)
            {
                var node = new AnimPoseNode(record.Name, ColorKey(record.NodeType));
                foreach (var pose in record.PoseInputs)
                {
                    string pinName = string.IsNullOrEmpty(pose.PinName) ? "Pose" : pose.PinName;
                    node.AddInputPin(new GraphPin(pinName, PinDir.EGPD_Input, MakePinType(PinType.Pose)));
                }

                foreach (var (name, value, type) in record.DataPins)
                {
                    var pin = new GraphPin(name, PinDir.EGPD_Input, DataPinType(type, name));
                    pin.Value = value;
                    node.AddInputPin(pin);
                }

                if (!record.NodeType.Equals("Root", StringComparison.OrdinalIgnoreCase) || node.Output.Count == 0)
                    node.AddOutputPin(new GraphPin("Pose", PinDir.EGPD_Output, MakePinType(PinType.Pose)));

                if (record.NodeType.Equals("Root", StringComparison.OrdinalIgnoreCase))
                {
                    node.HeaderCenter = true;
                    node.NodeType = "AnimRoot";
                }

                node.TintPin = node.Output.FirstOrDefault() ?? node.Input.FirstOrDefault();
                nodes[record.Index] = node;
                graph.AddNode(node);
            }

            foreach (var record in snap.Nodes)
            {
                if (!nodes.TryGetValue(record.Index, out var dest))
                    continue;
                foreach (var pose in record.PoseInputs)
                {
                    if (!nodes.TryGetValue(pose.LinkId, out var source))
                        continue;
                    var input = dest.Input.FirstOrDefault(o =>
                        o.PinName.Equals(pose.PinName, StringComparison.OrdinalIgnoreCase) ||
                        o.PinFriendlyName.Equals(pose.PinName, StringComparison.OrdinalIgnoreCase));
                    var output = source.Output.FirstOrDefault(o => o.PinType.PinCategory == PinType.Pose)
                                 ?? source.Output.FirstOrDefault();
                    if (input != null && output != null)
                        Connect(output, input);
                }
            }

            graph.UseSavedPositions = false;
            return graph;
        }

        static BPGraph BuildStateMachine(AnimStateMachineRecord machine)
        {
            var graph = NewAnimGraph();
            var entry = MakeStateBox("Entry", "AnimRoot", outputOnly: true);
            graph.AddNode(entry);

            var states = new List<AnimPoseNode>();
            for (int i = 0; i < machine.States.Count; i++)
            {
                var (name, _) = machine.States[i];
                var node = MakeStateBox(name, "AnimState");
                if (i == machine.InitialState)
                    node.HeaderCenter = true;
                states.Add(node);
                graph.AddNode(node);
            }

            if (machine.InitialState >= 0 && machine.InitialState < states.Count)
                Connect(entry.Output[0], states[machine.InitialState].Input[0]);

            foreach (var (from, to, _, _) in machine.Transitions)
            {
                if (from < 0 || to < 0 || from >= states.Count || to >= states.Count)
                    continue;
                ConnectStates(states[from], states[to]);
            }

            return graph;
        }

        static AnimPoseNode MakeStateBox(string name, string type, bool outputOnly = false)
        {
            var node = new AnimPoseNode(name, type);
            if (!outputOnly)
            {
                var input = new GraphPin("In", PinDir.EGPD_Input, MakePinType(PinType.Pose));
                input.IsNameHidden = true;
                node.AddInputPin(input);
            }
            var output = new GraphPin("Out", PinDir.EGPD_Output, MakePinType(PinType.Pose));
            output.IsNameHidden = true;
            node.AddOutputPin(output);
            node.TintPin = output;
            if (type == "AnimRoot")
                node.HeaderCenter = true;
            return node;
        }

        static void ConnectStates(BPNode from, BPNode to)
        {
            var output = from.Output.FirstOrDefault();
            var input = to.Input.FirstOrDefault();
            if (output == null || input == null || output.LinkedTo.Contains(input))
                return;
            Connect(output, input);
        }

        static BPGraph BuildFromEdGraph(UObject graphExport)
        {
            var graph = NewAnimGraph();
            var package = graphExport.Owner;
            List<UObject> exports = [];
            if (package != null)
            {
                foreach (var lazy in package.ExportsLazy)
                {
                    try
                    {
                        if (lazy.Value is { } obj)
                            exports.Add(obj);
                    }
                    catch
                    {
                        // leftover export
                    }
                }
            }

            var nodeExports = CollectGraphNodes(graphExport, exports);
            bool stateMachine = nodeExports.Any(AnimBlueprintCollector.IsStateMachineEditorNode);
            var nodes = new Dictionary<UObject, BPNode>();
            var pins = new Dictionary<Guid, GraphPin>();
            var transitions = new List<UObject>();

            foreach (var nodeExport in nodeExports)
            {
                if (AnimBlueprintCollector.IsTransitionNode(nodeExport))
                {
                    transitions.Add(nodeExport);
                    foreach (var pin in ReadPins(nodeExport))
                        pins.TryAdd(pin.Guid, pin);
                    continue;
                }

                var node = new AnimPoseNode(DisplayName(nodeExport), ColorKey(NodeTypeName(nodeExport)));
                node.X = nodeExport.GetOrDefault("NodePosX", 0);
                node.Y = nodeExport.GetOrDefault("NodePosY", 0);
                if (node.X != 0 || node.Y != 0)
                    graph.UseSavedPositions = true;

                foreach (var pin in ReadPins(nodeExport))
                {
                    if (stateMachine)
                        pin.IsNameHidden = true;
                    if (pin.IsInput)
                        node.AddInputPin(pin);
                    else
                        node.AddOutputPin(pin);
                    pins.TryAdd(pin.Guid, pin);
                }

                if (node.Output.Count == 0)
                {
                    var output = new GraphPin("Out", PinDir.EGPD_Output, MakePinType(PinType.Pose));
                    output.IsNameHidden = stateMachine;
                    node.AddOutputPin(output);
                }
                if (stateMachine && node.Input.Count == 0 && !AnimBlueprintCollector.IsEntryNode(nodeExport))
                {
                    var input = new GraphPin("In", PinDir.EGPD_Input, MakePinType(PinType.Pose));
                    input.IsNameHidden = true;
                    node.AddInputPin(input);
                }

                node.TintPin = node.Output.FirstOrDefault() ?? node.Input.FirstOrDefault();
                nodes[nodeExport] = node;
                graph.AddNode(node);
            }

            foreach (var nodeExport in nodeExports.OfType<UEdGraphNode>())
            {
                foreach (var pinRef in nodeExport.Pins)
                {
                    if (pinRef is not UEdGraphPin pin)
                        continue;
                    if (!pins.TryGetValue(ToGuid(pin.PinId), out var from))
                        continue;
                    foreach (var linked in pin.LinkedTo)
                    {
                        if (linked == null || !pins.TryGetValue(ToGuid(linked.PinId), out var to))
                            continue;
                        if (from.ParentNode == null || to.ParentNode == null)
                            continue;
                        if (!from.LinkedTo.Contains(to))
                            Connect(from, to);
                    }
                }
            }

            foreach (var transition in transitions)
                WireTransition(transition, nodes, pins);

            return graph;
        }

        static void WireTransition(UObject transition, Dictionary<UObject, BPNode> nodes, Dictionary<Guid, GraphPin> pins)
        {
            BPNode? from = LoadBoundState(transition, "PreviousState", nodes);
            BPNode? to = LoadBoundState(transition, "NextState", nodes);

            if (transition is UEdGraphNode edNode)
            {
                foreach (var pinRef in edNode.Pins)
                {
                    if (pinRef is not UEdGraphPin pin)
                        continue;
                    foreach (var linked in pin.LinkedTo)
                    {
                        if (linked == null || !pins.TryGetValue(ToGuid(linked.PinId), out var other))
                            continue;
                        var owner = other.ParentNode;
                        if (owner == null)
                            continue;
                        if (pin.Direction == CuePinDir.EGPD_Input)
                            from ??= owner;
                        else
                            to ??= owner;
                    }
                }
            }

            if (from != null && to != null)
                ConnectStates(from, to);
        }

        static BPNode? LoadBoundState(UObject transition, string property, Dictionary<UObject, BPNode> nodes)
        {
            var index = transition.GetOrDefault<FPackageIndex?>(property);
            if (index is not { IsNull: false })
                return null;
            try
            {
                if (index.Load<UObject>() is { } obj && nodes.TryGetValue(obj, out var node))
                    return node;
            }
            catch
            {
                // missing leftover state
            }

            string name = index.Name;
            return nodes.FirstOrDefault(o =>
                o.Key.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                o.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        }

        static List<UObject> CollectGraphNodes(UObject graphExport, List<UObject> exports)
        {
            HashSet<UObject> nodes = [];
            if (graphExport.TryGetValue(out FPackageIndex[] indexes, "Nodes") && indexes != null)
            {
                foreach (var index in indexes)
                {
                    var obj = index?.Load<UObject>();
                    if (obj != null)
                        nodes.Add(obj);
                }
            }

            foreach (var export in exports)
            {
                if (string.Equals(export.Outer?.Name.ToString(), graphExport.Name, StringComparison.OrdinalIgnoreCase) &&
                    (export is UEdGraphNode || export.ExportType.Contains("AnimGraphNode", StringComparison.OrdinalIgnoreCase) ||
                     export.ExportType.Contains("AnimState", StringComparison.OrdinalIgnoreCase)))
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
            }
        }

        static GraphPin ConvertPin(UEdGraphPin pin)
        {
            string name = pin.PinFriendlyName?.Text ?? pin.PinName.Text;
            var type = ConvertPinType(pin.PinType);
            if (IsPoseCategory(pin.PinType?.PinCategory.Text) || IsPoseCategory(name))
                type = MakePinType(PinType.Pose);
            var graphPin = new GraphPin(pin.PinName.Text, ToDirection(pin.Direction), type, name)
            {
                Guid = ToGuid(pin.PinId),
                Value = pin.DefaultValue ?? ""
            };
            return graphPin;
        }

        static GraphPinType ConvertPinType(FEdGraphPinType? type)
        {
            if (type == null)
                return MakePinType(PinType.Unknown);
            string category = type.PinCategory.Text;
            if (IsPoseCategory(category))
                return MakePinType(PinType.Pose);
            return new GraphPinType
            {
                PinCategory = category?.ToLowerInvariant() switch
                {
                    "exec" => PinType.exec,
                    "bool" => PinType.Bool,
                    "byte" => PinType.Byte,
                    "enum" => PinType.Enum,
                    "int" or "int32" => PinType.Int,
                    "float" or "double" or "real" => PinType.Float,
                    "name" => PinType.Name,
                    "string" => PinType.String,
                    "object" => PinType.Object,
                    "struct" => PinType.Struct,
                    "wildcard" => PinType.Wildcard,
                    _ => PinType.Unknown
                }
            };
        }

        static bool IsPoseCategory(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            return name.Contains("pose", StringComparison.OrdinalIgnoreCase);
        }

        static PinDir ToDirection(CuePinDir direction) =>
            direction == CuePinDir.EGPD_Output
                ? PinDir.EGPD_Output
                : PinDir.EGPD_Input;

        static Guid ToGuid(FGuid guid) =>
            Guid.TryParse(guid.ToString(EGuidFormats.DigitsWithHyphens), out var parsed) ? parsed : Guid.NewGuid();

        static string DisplayName(UObject node)
        {
            if (AnimBlueprintCollector.IsEntryNode(node))
                return "Entry";

            string bound = PackageIndexName(node.GetOrDefault<FPackageIndex?>("BoundGraph"));
            if (!string.IsNullOrEmpty(bound) &&
                (AnimBlueprintCollector.IsStateNode(node) ||
                 NodeTypeName(node).Contains("StateMachine", StringComparison.OrdinalIgnoreCase)))
                return bound;

            string stateName = node.GetOrDefault<FName>("StateName").Text;
            if (string.IsNullOrEmpty(stateName) || stateName == "None")
                stateName = node.GetOrDefault<FName>("State").Text;
            if (!string.IsNullOrWhiteSpace(stateName) && stateName != "None")
                return stateName;

            string comment = node.GetOrDefault("NodeComment", "");
            if (!string.IsNullOrWhiteSpace(comment))
                return comment;
            string type = NodeTypeName(node);
            if (type.StartsWith("AnimGraphNode_", StringComparison.Ordinal))
                type = type["AnimGraphNode_".Length..];
            if (type.StartsWith("AnimState", StringComparison.OrdinalIgnoreCase))
                type = string.IsNullOrEmpty(node.Name) || node.Name.StartsWith("AnimState", StringComparison.OrdinalIgnoreCase)
                    ? type
                    : node.Name;
            return type;
        }

        static string PackageIndexName(FPackageIndex? index)
        {
            if (index == null || index.IsNull)
                return "";
            string name = index.Name;
            return string.IsNullOrEmpty(name) || name == "None" ? "" : name.SubstringAfterLast('.');
        }

        static string NodeTypeName(UObject node)
        {
            string className = node.ExportType.SubstringAfterLast('.');
            return className.StartsWith('U') && className.Length > 1 && char.IsUpper(className[1])
                ? className[1..]
                : className;
        }

        static string ColorKey(string type)
        {
            if (type.Equals("Root", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("StateResult", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("StateEntry", StringComparison.OrdinalIgnoreCase))
                return "AnimRoot";
            if (type.Contains("StateMachine", StringComparison.OrdinalIgnoreCase))
                return "AnimStateMachine";
            if (type.Contains("Transition", StringComparison.OrdinalIgnoreCase))
                return "AnimTransition";
            if (type.Contains("State", StringComparison.OrdinalIgnoreCase) &&
                !type.Contains("StateMachine", StringComparison.OrdinalIgnoreCase))
                return "AnimState";
            return "AnimGraphNode";
        }

        static GraphPinType DataPinType(string type, string name)
        {
            if (type.Contains("Object", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Sequence", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("BlendSpace", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Asset", StringComparison.OrdinalIgnoreCase))
                return MakePinType(PinType.Object);
            if (type.Contains("Bool", StringComparison.OrdinalIgnoreCase) || name.StartsWith('b'))
                return MakePinType(PinType.Bool);
            if (type.Contains("Name", StringComparison.OrdinalIgnoreCase))
                return MakePinType(PinType.Name);
            if (type.Contains("Int", StringComparison.OrdinalIgnoreCase))
                return MakePinType(PinType.Int);
            return MakePinType(PinType.Float);
        }
    }
}
