using System.Collections.Generic;
using System.Linq;
using UEBlueprintGraphViewer.Nodes;

namespace UEBlueprintGraphViewer
{
    internal static class LikelyMaterialFunctionAnnotator
    {
        const int MinRegionNodes = 2;
        const int MaxGrowNodes = 36;

        public static void Detect(BPGraph graph)
        {
            graph.LikelyMacroRegions.Clear();

            var nodes = graph.Nodes.Where(o => o is not MaterialResultNode and not K2Node_Comment).ToList();
            if (nodes.Count == 0)
                return;

            var claimed = new HashSet<BPNode>();
            foreach (var region in CollectRegions(nodes))
            {
                if (region.Count < MinRegionNodes || region.Any(claimed.Contains))
                    continue;
                if (region.Count >= nodes.Count - 1)
                    continue;
                if (!region.Any(MaterialExpressionNode.IsFunctionLeftover))
                    continue;

                graph.LikelyMacroRegions.Add(region);
                claimed.UnionWith(region);
            }
        }

        public static void Dismiss(BPGraph graph, K2Node_Comment comment)
        {
            graph.LikelyMacroRegions.RemoveAll(region =>
                region.Count == comment.Members.Count && comment.Members.All(region.Contains));
            graph.RemoveNode(comment);
        }

        static List<List<BPNode>> CollectRegions(List<BPNode> nodes)
        {
            var seeds = nodes.Where(MaterialExpressionNode.IsFunctionLeftover).ToList();
            if (seeds.Count == 0)
                return [];

            var used = new HashSet<BPNode>();
            var regions = new List<List<BPNode>>();

            foreach (var seed in seeds)
            {
                if (!used.Add(seed))
                    continue;

                var region = Grow(seed);
                foreach (var node in region)
                    used.Add(node);

                if (region.Any(MaterialExpressionNode.IsFunctionLeftover))
                    regions.Add(region);
            }

            return MergeOverlapping(regions);
        }

        static List<BPNode> Grow(BPNode start)
        {
            var region = new List<BPNode>();
            var queue = new Queue<BPNode>();
            queue.Enqueue(start);

            while (queue.Count > 0 && region.Count < MaxGrowNodes)
            {
                var node = queue.Dequeue();
                if (node is MaterialResultNode or K2Node_Comment)
                    continue;
                if (region.Contains(node))
                    continue;

                region.Add(node);
                foreach (var pin in node.Input.Concat(node.Output))
                {
                    foreach (var linked in pin.LinkedTo)
                    {
                        var other = linked.ParentNode;
                        if (other == null || other is MaterialResultNode or K2Node_Comment)
                            continue;
                        if (!region.Contains(other) && !queue.Contains(other))
                            queue.Enqueue(other);
                    }
                }
            }

            return region;
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
                        regions[i] = regions[i].Union(regions[j]).ToList();
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
