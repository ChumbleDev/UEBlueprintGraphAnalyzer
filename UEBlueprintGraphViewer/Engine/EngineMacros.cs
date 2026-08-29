using System;
using System.Collections.Frozen;
using System.Linq;

namespace UEBlueprintGraphViewer.Engine
{
    public static class EngineMacros
    {
        // Shipped StandardMacros / EnumMacros patterns. Custom game macros are not in this set.
        public static readonly FrozenSet<string> BuiltInNames = new[]
        {
            "Do N",
            "Do Once",
            "Do Once2 (reset is unused)",
            "DoOnce MultiInput",
            "DoOnce MultiInput2 (reset is unused)",
            "Flip Flop",
            "For Each Loop",
            "For Each Loop with Break",
            "For Each Loop with Break2 (break is unused)",
            "For Each Loop with Break3 (no loop body)",
            "For Each Loop with Break4 (break is unused no loop body)",
            "For Loop",
            "For Loop with Break",
            "For Loop with Break2 (break is unused)",
            "Foreach Enum",
            "Is Valid",
            "Reverse for Each Loop",
            "While Loop",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static bool IsBuiltIn(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (BuiltInNames.Contains(name))
                return true;

            return Settings.Instance.Macros.TryGetValue(name, out var graph) &&
                   BuiltInNames.Contains(graph.MacroName);
        }

        public static BPGraph? FindSavedGraph(string name)
        {
            if (Settings.Instance.Macros.TryGetValue(name, out var graph))
                return graph;

            return Settings.Instance.Macros.Values.FirstOrDefault(o =>
                o.MacroName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
