using System;
using System.Collections.Generic;
using System.Linq;

namespace UEBlueprintGraphViewer.Nodes
{
    public class K2Node_Comment : BPNode
    {
        public const string LikelyMacroTitle = "Likely a macro.";
        public const string LikelyMaterialFunctionTitle = "Likely a material function.";

        const float PadX = 28f;
        const float PadY = 18f;
        const float TitleHeight = 28f;
        public const float CloseButtonSize = 16f;
        const float CloseButtonInset = 6f;

        public List<BPNode> Members { get; }

        public K2Node_Comment(IEnumerable<BPNode> members, string? title = null) : base(title ?? LikelyMacroTitle, null)
        {
            HeaderHidden = true;
            Members = [.. members];
            FitToMembers();
        }

        public override void CalculateSize()
        {
        }

        public void GetCloseButtonRect(out float x, out float y, out float size)
        {
            size = CloseButtonSize;
            x = Right - CloseButtonInset - size;
            y = Y + CloseButtonInset;
        }

        public bool HitTestClose(float x, float y)
        {
            GetCloseButtonRect(out float left, out float top, out float size);
            return x >= left && x <= left + size && y >= top && y <= top + size;
        }

        public void FitToMembers()
        {
            var live = Members.Where(o => o is not K2Node_Comment).ToList();
            if (live.Count == 0)
                return;

            float minX = live.Min(o => o.X);
            float minY = live.Min(o => o.Y);
            float maxX = live.Max(o => o.Right);
            float maxY = live.Max(o => o.Bottom);

            SetPosition(minX - PadX, minY - PadY - TitleHeight);
            NodeWidth = Math.Max(160, (int)Math.Ceiling(maxX - minX + PadX * 2));
            NodeHeight = Math.Max(48, (int)Math.Ceiling(maxY - minY + PadY * 2 + TitleHeight));
        }
    }
}
