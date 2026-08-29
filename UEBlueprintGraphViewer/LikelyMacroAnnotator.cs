using System;
using System.Collections.Generic;
using System.Linq;
using UEBlueprintGraphViewer.Nodes;
using static UEBlueprintGraphViewer.Engine.EngineBPData;

namespace UEBlueprintGraphViewer
{
    internal static class LikelyMacroAnnotator
    {
        const int MinTempRegionNodes = 3;
        const int MinRepeatedImpureNodes = 3;
        const int MinRepeatedTotalNodes = 4;
        const int MaxGrowNodes = 40;
        const int MaxSignatureGroupSize = 48;

        public static void Detect(BPGraph graph)
        {
            graph.LikelyMacroRegions.Clear();

            var nodes = graph.Nodes;
            if (nodes.Count == 0)
                return;

            var claimed = new HashSet<BPNode>();
            var regions = new List<List<BPNode>>();

            foreach (var region in CollectTempRegions(nodes))
            {
                if (region.Any(claimed.Contains))
                    continue;
                regions.Add(region);
                claimed.UnionWith(region);
            }

            foreach (var region in CollectRepeatedRegions(nodes, claimed))
            {
                if (region.Any(claimed.Contains))
                    continue;
                regions.Add(region);
                claimed.UnionWith(region);
            }

            graph.LikelyMacroRegions.AddRange(regions);
        }

        public static void PlaceComments(BPGraph graph)
        {
            graph.Nodes.RemoveAll(o => o is K2Node_Comment);

            foreach (var region in graph.LikelyMacroRegions)
            {
                var members = region.Where(graph.Nodes.Contains).ToList();
                if (members.Count < 2)
                    continue;

                graph.AddNode(new K2Node_Comment(members));
            }
        }

        static List<List<BPNode>> CollectTempRegions(List<BPNode> nodes)
        {
            var temps = nodes.OfType<K2Node_TemporaryVariable>().ToList();
            if (temps.Count == 0)
                return [];

            var used = new HashSet<BPNode>();
            var regions = new List<List<BPNode>>();

            foreach (var temp in temps)
            {
                if (!used.Add(temp))
                    continue;

                var region = GrowTempRegion(temp);
                foreach (var node in region)
                    used.Add(node);

                if (region.Count >= MinTempRegionNodes && region.OfType<K2Node_TemporaryVariable>().Any())
                    regions.Add(region);
            }

            return MergeOverlapping(regions);
        }

        static List<BPNode> GrowTempRegion(K2Node_TemporaryVariable start)
        {
            var region = new HashSet<BPNode> { start };
            var queue = new Queue<BPNode>();
            queue.Enqueue(start);

            while (queue.Count > 0 && region.Count < MaxGrowNodes)
            {
                var node = queue.Dequeue();
                foreach (var other in Neighbors(node))
                {
                    if (IsBoundary(other) || region.Contains(other))
                        continue;
                    if (!IsDataTiedToTemps(other, region) && other is not K2Node_TemporaryVariable)
                        continue;
                    if (region.Add(other))
                        queue.Enqueue(other);
                }
            }

            AddInternalControlFlow(region);
            return [.. region];
        }

        static bool IsDataTiedToTemps(BPNode node, HashSet<BPNode> region)
        {
            foreach (var pin in node.Input.Concat(node.Output))
            {
                if (IsExec(pin))
                    continue;
                foreach (var link in pin.LinkedTo)
                {
                    if (region.Contains(link.ParentNode) || link.ParentNode is K2Node_TemporaryVariable)
                        return true;
                }
            }

            return false;
        }

        static void AddInternalControlFlow(HashSet<BPNode> region)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                var candidates = region
                    .SelectMany(Neighbors)
                    .Distinct()
                    .Where(o => !region.Contains(o) && !IsBoundary(o) && IsControlFlow(o))
                    .ToList();

                foreach (var node in candidates)
                {
                    if (!ControlFlowBelongsToRegion(node, region))
                        continue;
                    if (region.Add(node))
                        changed = true;
                }
            }
        }

        static bool ControlFlowBelongsToRegion(BPNode node, HashSet<BPNode> region)
        {
            if (node is K2Node_IfThenElse ifNode)
                return ifNode.ConditionPin.LinkedTo.Any(o => region.Contains(o.ParentNode));

            if (node is K2Node_Switch)
            {
                return node.Input.Any(p => !IsExec(p) && p.LinkedTo.Any(o => region.Contains(o.ParentNode)));
            }

            if (node is K2Node_ExecutionSequence)
            {
                int hits = node.Output.Count(p => IsExec(p) && p.LinkedTo.Any(o => region.Contains(o.ParentNode)));
                return hits >= 2;
            }

            return false;
        }

        static List<List<BPNode>> CollectRepeatedRegions(List<BPNode> nodes, HashSet<BPNode> claimed)
        {
            var impure = nodes.Where(o => o.ExecPin != null && !IsBoundary(o) && !claimed.Contains(o)).ToList();
            var bySig = impure
                .GroupBy(NodeSignature, StringComparer.Ordinal)
                .Where(g => g.Count() is >= 2 and <= MaxSignatureGroupSize)
                .ToList();

            var found = new List<HashSet<BPNode>>();

            foreach (var group in bySig)
            {
                var starts = group.ToList();
                if (!IsDistinctiveStart(starts[0]))
                    continue;

                for (int i = 0; i < starts.Count; i++)
                {
                    for (int j = i + 1; j < starts.Count; j++)
                    {
                        if (!TryGrowMatch(starts[i], starts[j], claimed, out var regionA, out var regionB))
                            continue;
                        found.Add(regionA);
                        found.Add(regionB);
                    }
                }
            }

            var unique = new List<HashSet<BPNode>>();
            foreach (var region in found.OrderByDescending(o => o.Count))
            {
                if (unique.Any(o => o.SetEquals(region)))
                    continue;
                unique.Add(region);
            }

            var chosen = new List<List<BPNode>>();
            var used = new HashSet<BPNode>(claimed);
            int impureTotal = Math.Max(1, impure.Count);

            foreach (var region in unique.OrderByDescending(o => o.Count))
            {
                if (region.Overlaps(used))
                    continue;
                if (!LooksLikeInlinedMacro(region, impureTotal))
                    continue;

                var list = region.ToList();
                chosen.Add(list);
                used.UnionWith(region);
            }

            return chosen;
        }

        static bool TryGrowMatch(BPNode a, BPNode b, HashSet<BPNode> claimed, out HashSet<BPNode> regionA, out HashSet<BPNode> regionB)
        {
            regionA = [];
            regionB = [];
            if (a == b || claimed.Contains(a) || claimed.Contains(b) || !SignaturesEqual(a, b))
                return false;

            var map = new Dictionary<BPNode, BPNode> { [a] = b };
            var reverse = new Dictionary<BPNode, BPNode> { [b] = a };
            var queue = new Queue<BPNode>();
            queue.Enqueue(a);

            while (queue.Count > 0 && map.Count < MaxGrowNodes)
            {
                var na = queue.Dequeue();
                var nb = map[na];
                MatchPins(na.Output, nb.Output);
                MatchPins(na.Input, nb.Input);
            }

            if (map.Keys.Count(o => o.ExecPin != null) < MinRepeatedImpureNodes)
                return false;
            if (map.Count < MinRepeatedTotalNodes)
                return false;

            regionA = [.. map.Keys];
            regionB = [.. map.Values];
            return true;

            void MatchPins(List<GraphPin> pinsA, List<GraphPin> pinsB)
            {
                int count = Math.Min(pinsA.Count, pinsB.Count);
                for (int i = 0; i < count; i++)
                {
                    var linksB = pinsB[i].LinkedTo.Select(o => o.ParentNode).ToList();
                    foreach (var childA in pinsA[i].LinkedTo.Select(o => o.ParentNode))
                    {
                        if (childA == null || IsBoundary(childA) || map.ContainsKey(childA) || claimed.Contains(childA))
                            continue;

                        var childB = linksB.FirstOrDefault(c =>
                            c != null &&
                            c != childA &&
                            !reverse.ContainsKey(c) &&
                            !IsBoundary(c) &&
                            !claimed.Contains(c) &&
                            SignaturesEqual(childA, c));
                        if (childB == null)
                            continue;

                        map[childA] = childB;
                        reverse[childB] = childA;
                        queue.Enqueue(childA);
                    }
                }
            }
        }

        static bool LooksLikeInlinedMacro(HashSet<BPNode> region, int impureTotal)
        {
            if (region.Any(IsBoundary))
                return false;

            var outsidePreds = region
                .Where(o => o.ExecPin != null)
                .SelectMany(o => o.ExecPin!.LinkedTo.Select(p => p.ParentNode))
                .Where(o => o != null && !region.Contains(o))
                .Distinct()
                .ToList();

            if (outsidePreds.Count == 0)
                return false;

            int impureInRegion = region.Count(o => o.ExecPin != null);
            bool onlyFromEntry = outsidePreds.All(o => o is K2Node_FunctionEntry);
            if (onlyFromEntry && impureInRegion * 5 > impureTotal * 3)
                return false;

            return true;
        }

        static bool IsDistinctiveStart(BPNode node)
        {
            return node is K2Node_CallFunction
                or K2Node_IfThenElse
                or K2Node_Switch
                or K2Node_DynamicCast
                or K2Node_ClassDynamicCast
                or K2Node_PromotableOperator;
        }

        static bool SignaturesEqual(BPNode a, BPNode b) => NodeSignature(a) == NodeSignature(b);

        static string NodeSignature(BPNode node)
        {
            switch (node)
            {
                case K2Node_CallFunction call:
                    return $"Call|{call.FunctionName}|{call.OuterName}|{call.Input.Count}|{call.Output.Count}";
                case K2Node_TemporaryVariable temp:
                    return $"Temp|{temp.VarPin.PinType.PinCategory}|{temp.VarPin.PinType.ContainerType}";
                case K2Node_AssignmentStatement:
                    return "Assign";
                case K2Node_VariableGet get:
                    return VariableSignature("Get", get.Property, get.VarPin);
                case K2Node_VariableSet set:
                    return VariableSignature("Set", set.Property, set.ValuePin);
                case K2Node_ExecutionSequence:
                    return $"Seq|{node.Output.Count}";
                case K2Node_IfThenElse:
                    return "If";
                default:
                    return $"{node.NodeType}|{node.Name}|{node.Input.Count}|{node.Output.Count}";
            }
        }

        static string VariableSignature(string kind, Engine.PropertyData property, GraphPin pin)
        {
            string id = property.IsTempVar() ? pin.PinType.PinCategory.ToString() : property.Name;
            return $"{kind}|{id}|{pin.PinType.PinCategory}|{pin.PinType.ContainerType}";
        }

        static bool IsBoundary(BPNode node)
        {
            return node is K2Node_FunctionEntry
                or K2Node_FunctionResult
                or K2Node_MacroInstance
                or K2Node_Comment
                or K2Node_Tunnel;
        }

        static bool IsControlFlow(BPNode node)
        {
            return node is K2Node_IfThenElse or K2Node_ExecutionSequence or K2Node_Switch;
        }

        static bool IsExec(GraphPin pin) => pin.PinType.PinCategory == PinType.exec;

        static IEnumerable<BPNode> Neighbors(BPNode node)
        {
            foreach (var pin in node.Input.Concat(node.Output))
            {
                foreach (var link in pin.LinkedTo)
                {
                    if (link.ParentNode != null)
                        yield return link.ParentNode;
                }
            }
        }

        static List<List<BPNode>> MergeOverlapping(List<List<BPNode>> regions)
        {
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < regions.Count; i++)
                {
                    for (int j = i + 1; j < regions.Count; j++)
                    {
                        if (!regions[i].Intersect(regions[j]).Any())
                            continue;
                        regions[i] = [.. regions[i].Union(regions[j])];
                        regions.RemoveAt(j);
                        merged = true;
                        break;
                    }
                    if (merged)
                        break;
                }
            }

            return regions;
        }
    }
}
