using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports;

namespace UEBlueprintGraphViewer.Assets
{
    public class AssetComponent
    {
        public string Name = "";
        public string ClassName = "ActorComponent";
        public string? ParentName;
        public bool IsInherited;
        public bool IsSceneComponent;
        public UObject? Template;
        public List<AssetComponent> Children { get; } = [];
    }
}
