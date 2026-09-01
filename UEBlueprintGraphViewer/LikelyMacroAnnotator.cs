using System;
using System.Collections.Generic;
using System.Linq;
using UEBlueprintGraphViewer.Nodes;
using static UEBlueprintGraphViewer.Engine.EngineBPData;

namespace UEBlueprintGraphViewer
{
    internal static class LikelyMacroAnnotator
    {
        const int MinRegionNodes = 2;
        const int MaxGrowNodes = 28;

        public static void Detect(BPGraph graph)
        {
            graph.LikelyMacroRegions.Clear();

            var nodes = graph.Nodes;
            if (nodes.Count == 0)
                return;

            var claimed = new HashSet<BPNode>();
            foreach (var region in CollectMacroLeftoverRegions(nodes))
            {
                if (region.Count < MinRegionNodes || region.Any(claimed.Contains))
                    continue;
                if (!IsMacroLeftoverCluster(region))
                    continue;

                graph.LikelyMacroRegions.Add(region);
                claimed.UnionWith(region);
            }
        }

        public static void PlaceComments(BPGraph graph, string? title = null)
        {
            graph.Nodes.RemoveAll(o => o is K2Node_Comment);

            foreach (var region in graph.LikelyMacroRegions)
            {
                var members = region.Where(graph.Nodes.Contains).ToList();
                if (members.Count < MinRegionNodes)
                    continue;

                graph.AddNode(new K2Node_Comment(members, title));
            }
        }

        public static void Dismiss(BPGraph graph, K2Node_Comment comment)
        {
            graph.LikelyMacroRegions.RemoveAll(region =>
                region.Count == comment.Members.Count && comment.Members.All(region.Contains));
            graph.RemoveNode(comment);
        }

        static List<List<BPNode>> CollectMacroLeftoverRegions(List<BPNode> nodes)
        {
            var seeds = nodes.Where(IsMacroSpecificNode).ToList();
            if (seeds.Count == 0)
                return [];

            var used = new HashSet<BPNode>();
            var regions = new List<List<BPNode>>();

            foreach (var seed in seeds)
            {
                if (!used.Add(seed))
                    continue;

                var region = GrowLeftoverRegion(seed);
                foreach (var node in region)
                    used.Add(node);

                if (region.Any(IsMacroSpecificNode))
                    regions.Add(region);
            }

            return MergeOverlapping(regions);
        }

        static List<BPNode> GrowLeftoverRegion(BPNode start)
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
                    if (!IsCoreMacroLeftover(other, region))
                        continue;
                    if (region.Add(other))
                        queue.Enqueue(other);
                }
            }

            AddInternalControlFlow(region);
            AddFullyInternalHelpers(region);
            return [.. region];
        }

        static bool IsMacroSpecificNode(BPNode node)
        {
            return node is K2Node_AssignmentStatement
                or K2Node_TemporaryVariable
                || IsSetByRefVar(node);
        }

        static bool IsSetByRefVar(BPNode node) => node is K2Node_VariableSet set && set.IsSetByRefVar;

        static bool IsCoreMacroLeftover(BPNode node, HashSet<BPNode> region)
        {
            if (node is K2Node_TemporaryVariable or K2Node_AssignmentStatement)
                return true;

            if (IsSetByRefVar(node))
                return IsDataNeighborOfRegion(node, region);

            if (node is K2Node_GetArrayItem)
                return IsDataNeighborOfRegion(node, region);

            return false;
        }

        static bool IsDataNeighborOfRegion(BPNode node, HashSet<BPNode> region)
        {
            foreach (var pin in node.Input.Concat(node.Output))
            {
                if (IsExec(pin))
                    continue;
                foreach (var link in pin.LinkedTo)
                {
                    if (region.Contains(link.ParentNode))
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

        static void AddFullyInternalHelpers(HashSet<BPNode> region)
        {
            bool changed = true;
            while (changed && region.Count < MaxGrowNodes)
            {
                changed = false;
                var candidates = region
                    .SelectMany(Neighbors)
                    .Distinct()
                    .Where(o => !region.Contains(o) && !IsBoundary(o) && IsHelperNode(o))
                    .ToList();

                foreach (var node in candidates)
                {
                    if (!IsFullyInternalToRegion(node, region))
                        continue;
                    if (region.Add(node))
                        changed = true;
                }
            }
        }

        static bool IsHelperNode(BPNode node)
        {
            return node is K2Node_PromotableOperator
                or K2Node_CallArrayFunction
                or K2Node_GetArrayItem
                or K2Node_AssignmentStatement
                or K2Node_TemporaryVariable
                || IsSetByRefVar(node);
        }

        static bool IsFullyInternalToRegion(BPNode node, HashSet<BPNode> region)
        {
            bool tied = false;
            foreach (var pin in node.Input.Concat(node.Output))
            {
                if (IsExec(pin))
                    continue;
                foreach (var link in pin.LinkedTo)
                {
                    if (IsBoundary(link.ParentNode))
                        return false;
                    if (!region.Contains(link.ParentNode))
                        return false;
                    tied = true;
                }
            }

            return tied;
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
                var connected = node.Output.Where(p => IsExec(p) && p.IsConnected).ToList();
                return connected.Count >= 2 && connected.All(p => p.LinkedTo.All(o => region.Contains(o.ParentNode)));
            }

            return false;
        }

        static bool IsMacroLeftoverCluster(List<BPNode> region)
        {
            if (region.Any(IsBoundary))
                return false;

            bool hasLocal = region.Exists(static o => o is K2Node_TemporaryVariable);
            bool hasAssign = region.Exists(static o => o is K2Node_AssignmentStatement);
            bool hasSetByRef = region.Exists(IsSetByRefVar);

            if (!hasLocal && !hasAssign && !hasSetByRef)
                return false;

            // A lone set-by-ref of an array element is ordinary Blueprint code.
            if (hasSetByRef && !hasLocal && !hasAssign)
                return false;

            return region.Exists(o => o.Input.Concat(o.Output).Any(p => p.IsConnected));
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
                        if (!regions[i].Exists(regions[j].Contains))
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
