using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using UEBlueprintGraphViewer.Engine;
using static UEBlueprintGraphViewer.Engine.EngineBPData;
using static UEBlueprintGraphViewer.Engine.Utils;

namespace UEBlueprintGraphViewer.Nodes
{
    public readonly struct MaterialInputLink
    {
        public required string Name { get; init; }
        public FExpressionInput? Input { get; init; }
        public GraphPinType PinType { get; init; }
        public string ConstantText { get; init; }
    }

    public static class MaterialGraphUtil
    {
        static readonly HashSet<string> SkippedProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "Material", "Function", "MaterialExpressionEditorX", "MaterialExpressionEditorY",
            "EditorX", "EditorY", "Desc", "Outputs", "ExpressionGUID", "ParameterName", "Group",
            "SortPriority", "bCollapsed", "bCommentBubblePinned", "bCommentBubbleVisible",
            "bShowOutputNameOnPin", "bHidePreviewWindow", "bShowInputs", "bShowOutputs",
            "bRealtimePreview", "MenuCategories", "Text", "SliderMin", "SliderMax",
            "DefaultValue", "SamplerType", "MipValueMode", "ConstCoordinate",
            "PreviewValue", "Id", "bUsePreviewValueAsDefault", "InputType",
            "Texture", "ChannelNames", "MaterialFunction",
            "ConstA", "ConstB", "R", "G", "B", "A", "Period",
            "SpeedX", "SpeedY", "CoordinateIndex"
        };

        public static readonly string[] TypedInputNames =
        [
            "A", "B", "C", "Input", "Coordinates", "TextureObject", "MipValue",
            "Alpha", "True", "False", "Value", "Default", "Quality", "Preview",
            "Specular", "Roughness", "BaseColor", "Normal", "EmissiveColor",
            "RealTime", "Time", "Period", "Exponent", "Max", "Min", "Clamp",
            "CoordinatesDX", "CoordinatesDY", "AutomaticViewMipBiasValue"
        ];

        public static List<MaterialInputLink> ReadInputs(UObject expression)
        {
            var result = new List<MaterialInputLink>();
            foreach (var tag in expression.Properties)
            {
                string name = tag.Name.Text;
                if (SkippedProperties.Contains(name) && !IsKnownInputName(name))
                    continue;

                foreach (var link in ReadLinks(name, tag.Tag?.GenericValue, tag.PropertyType.Text))
                    result.Add(link);
            }

            return result;
        }

        public static IEnumerable<MaterialInputLink> ReadLinks(string name, object? value, string propertyType)
        {
            foreach (var (linkName, input, constant) in EnumerateInputs(name, value))
            {
                yield return new MaterialInputLink
                {
                    Name = linkName,
                    Input = input,
                    PinType = PinTypeFromProperty(propertyType, linkName),
                    ConstantText = constant
                };
            }
        }

        public static IEnumerable<UObject> PropertySources(UObject? root)
        {
            if (root == null)
                yield break;

            yield return root;
            UObject? editorOnly = null;
            try
            {
                editorOnly = root.GetOrDefault<FPackageIndex?>("EditorOnlyData")?.Load<UObject>();
            }
            catch
            {
                // editor-only object is optional
            }

            if (editorOnly != null)
                yield return editorOnly;
        }

        public static bool TryReadInput(object? value, out FExpressionInput? input, out string constant)
        {
            input = null;
            constant = "";
            switch (value)
            {
                case FExpressionInput expr:
                    input = Normalize(expr);
                    constant = ConstantText(input);
                    return true;
                case FScriptStruct { StructType: FStructFallback fallback }:
                    if (!HasExpressionShape(fallback))
                        return false;
                    input = new FExpressionInput(fallback);
                    constant = ConstantText(input, fallback);
                    return true;
                case FStructFallback fallback:
                    if (!HasExpressionShape(fallback))
                        return false;
                    input = new FExpressionInput(fallback);
                    constant = ConstantText(input, fallback);
                    return true;
                default:
                    return false;
            }
        }

        public static bool HasSource(FExpressionInput? input)
        {
            if (input == null)
                return false;
            if (input.Expression is { IsNull: false })
                return true;
            return !IsNone(input.ExpressionName);
        }

        public static string CleanName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "None")
                return "";
            name = name.SubstringAfterLast('/');
            name = name.SubstringAfterLast('.');
            return name;
        }

        public static FExpressionInput Normalize(FExpressionInput input)
        {
            if (input.FallbackStruct == null)
                return input;
            if (input.Expression is { IsNull: false } || !IsNone(input.ExpressionName))
                return input;
            return new FExpressionInput(input.FallbackStruct);
        }

        static IEnumerable<(string Name, FExpressionInput Input, string Constant)> EnumerateInputs(string name, object? value, int depth = 0)
        {
            if (depth > 8 || value == null)
                yield break;

            if (TryReadInput(value, out var input, out string constant) && input != null)
            {
                yield return (DisplayInputName(name, input), input, constant);
                yield break;
            }

            switch (value)
            {
                case FScriptStruct { StructType: FStructFallback fb }:
                    foreach (var nested in EnumerateFromFallback(name, fb, depth))
                        yield return nested;
                    break;
                case FStructFallback fb:
                    foreach (var nested in EnumerateFromFallback(name, fb, depth))
                        yield return nested;
                    break;
                case UScriptArray array:
                    int i = 0;
                    foreach (var item in array.Properties)
                    {
                        foreach (var nested in EnumerateInputs($"{name}[{i}]", item.GenericValue, depth + 1))
                            yield return nested;
                        i++;
                    }
                    break;
            }
        }

        static IEnumerable<(string Name, FExpressionInput Input, string Constant)> EnumerateFromFallback(string name, FStructFallback fallback, int depth)
        {
            foreach (var property in fallback.Properties)
            {
                string nestedName = property.Name.Text;
                if (nestedName is "Input" or "ExpressionInput" or "Expression")
                {
                    foreach (var nested in EnumerateInputs(name, property.Tag?.GenericValue, depth + 1))
                        yield return nested;
                    continue;
                }

                if (nestedName is "OutputIndex" or "InputName" or "ExpressionName" or "Mask" or "MaskR" or "MaskG" or "MaskB" or "MaskA" or "UseConstant" or "Constant" or "ExpressionInputId")
                    continue;

                foreach (var nested in EnumerateInputs(DisplayInputName(nestedName, null), property.Tag?.GenericValue, depth + 1))
                    yield return nested;
            }
        }

        static bool HasExpressionShape(FStructFallback fallback) =>
            fallback.Properties.Any(o => o.Name.Text is "Expression" or "OutputIndex" or "InputName" or "ExpressionName");

        static bool IsKnownInputName(string name) =>
            name is "A" or "B" or "C" or "Input" or "Coordinates" or "TextureObject" or "MipValue"
                or "Alpha" or "True" or "False" or "Value" or "Default" or "Quality" or "Preview"
                or "Specular" or "Roughness" or "BaseColor" or "Normal" or "EmissiveColor"
                or "FunctionInputs" or "FunctionOutputs";

        static string DisplayInputName(string propertyName, FExpressionInput? input)
        {
            string fromInput = input?.InputName.Text ?? "";
            if (!string.IsNullOrEmpty(fromInput) && fromInput != "None")
                return fromInput;
            return propertyName;
        }

        static GraphPinType PinTypeFromProperty(string propertyType, string name)
        {
            if (propertyType.Contains("Color", StringComparison.OrdinalIgnoreCase) ||
                propertyType.Contains("Vector", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Color", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Normal", StringComparison.OrdinalIgnoreCase))
                return MakePinType(PinType.Vector);
            if (propertyType.Contains("Bool", StringComparison.OrdinalIgnoreCase))
                return MakePinType(PinType.Bool);
            return MakePinType(PinType.Float);
        }

        static string ConstantText(FExpressionInput input, FStructFallback? fallback = null)
        {
            if (input is FMaterialInputVector v && v.UseConstant)
                return $"({v.Constant.X:0.##}, {v.Constant.Y:0.##}, {v.Constant.Z:0.##})";
            if (input is FMaterialInputVector2D v2 && v2.UseConstant)
                return $"({v2.Constant.X:0.##}, {v2.Constant.Y:0.##})";
            if (input is FMaterialInput<float> f && f.UseConstant)
                return f.Constant.ToString("0.###");
            if (input is FMaterialInput<FLinearColor> c && c.UseConstant)
                return $"({c.Constant.R:0.##}, {c.Constant.G:0.##}, {c.Constant.B:0.##})";

            if (fallback == null)
                return "";
            if (!fallback.GetOrDefault("UseConstant", false))
                return "";
            if (fallback.TryGetValue(out float sf, "Constant"))
                return sf.ToString("0.###");
            if (fallback.TryGetValue(out FLinearColor col, "Constant"))
                return $"({col.R:0.##}, {col.G:0.##}, {col.B:0.##})";
            if (fallback.TryGetValue(out FVector vec, "Constant"))
                return $"({vec.X:0.##}, {vec.Y:0.##}, {vec.Z:0.##})";
            return fallback.GetOrDefault<object>("Constant")?.ToString() ?? "";
        }

        static bool IsNone(FName name) =>
            name.IsNone || string.IsNullOrEmpty(name.Text);

        public static string? GuessAttribute(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "None")
                return null;
            if (name.Equals("PM_Diffuse", StringComparison.OrdinalIgnoreCase))
                return "BaseColor";
            if (name.Equals("PM_Normals", StringComparison.OrdinalIgnoreCase))
                return "Normal";
            if (name.Equals("PM_SpecularMasks", StringComparison.OrdinalIgnoreCase))
                return "Specular";
            if (name.Equals("PM_Emissive", StringComparison.OrdinalIgnoreCase))
                return "EmissiveColor";

            string n = NormalizeToken(name);
            if (n.Length == 0)
                return null;

            if (ContainsToken(n, "worldpositionoffset", "wpo"))
                return "WorldPositionOffset";
            if (ContainsToken(n, "pixeldepthoffset", "pdo"))
                return "PixelDepthOffset";
            if (ContainsToken(n, "opacitymask", "clipmask", "alphaclip"))
                return "OpacityMask";
            if (ContainsToken(n, "ambientocclusion", "occlusion") || n is "ao" or "aomap")
                return "AmbientOcclusion";
            if (ContainsToken(n, "emissive", "emission"))
                return "EmissiveColor";
            if (ContainsToken(n, "roughness", "rough"))
                return "Roughness";
            if (ContainsToken(n, "metallic", "metalness") || (n.Contains("metal") && !n.Contains("normal")))
                return "Metallic";
            if (ContainsToken(n, "specular") && !n.Contains("mask"))
                return "Specular";
            if (ContainsToken(n, "normalmap", "normal") || n.EndsWith("nrm") || n.EndsWith("nm"))
                return "Normal";
            if (ContainsToken(n, "opacity", "translucen") && !n.Contains("mask"))
                return "Opacity";
            if (ContainsToken(n, "basecolor", "basecolour", "albedo", "diffuse") || n is "color" or "bc" or "bca" or "base" or "colormap")
                return "BaseColor";
            return null;
        }

        public static string? GuessRole(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            string n = NormalizeToken(name);
            if (n.Length == 0)
                return null;
            if ((ContainsToken(n, "uv", "texcoord", "tiling") || n.Contains("offset")) &&
                !ContainsToken(n, "worldpositionoffset", "pixeldepthoffset", "wpo", "pdo"))
                return "UV";
            if (ContainsToken(n, "brightness", "contrast", "tint", "intensity", "saturation"))
                return "Adjust";
            if (ContainsToken(n, "normal") || n.EndsWith("nrm") || n.EndsWith("nm"))
                return "Normal";
            if (ContainsToken(n, "emissive", "emission"))
                return "Emissive";
            if (ContainsToken(n, "roughness", "metallic", "specular", "occlusion", "orm", "mask"))
                return "Mask";
            if (ContainsToken(n, "diffuse", "albedo", "basecolor", "basecolour", "color"))
                return "Diffuse";
            return null;
        }

        static string NormalizeToken(string name)
        {
            var chars = name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant);
            return string.Concat(chars);
        }

        static bool ContainsToken(string normalized, params string[] tokens) =>
            tokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));

        public static string CanonicalName(string? name)
        {
            string n = NormalizeToken(CleanName(name));
            if (n.Length == 0)
                return "";
            if (n.StartsWith("pm") && n.Length > 4)
                n = n[2..];
            else if (n.StartsWith("mf") && n.Length > 4)
                n = n[2..];
            else if (n.StartsWith("texture") && n.Length > 10)
                n = n[7..];
            return n switch
            {
                "uvs" or "texcoord" or "texcoords" or "coordinate" or "coordinates" => "uv",
                "basecolour" => "basecolor",
                "normals" => "normal",
                _ => n
            };
        }

        public static bool NamesMatch(string? a, string? b)
        {
            string ca = CanonicalName(a);
            string cb = CanonicalName(b);
            return ca.Length >= 2 && ca == cb;
        }

        public static bool IsUvPinName(string? name)
        {
            string n = CanonicalName(name);
            return n is "uv" or "texcoord";
        }

        public static bool IsUvNode(BPNode node)
        {
            if (node is MaterialFunctionCallNode func)
                return func.Role == "UV" || GuessRole(func.Name) == "UV";
            string n = CanonicalName(node.Name);
            return n is "uv" or "texcoord";
        }

        public static bool IsAppendOp(BPNode node) =>
            node is MaterialOpNode { IsTextureSample: false } op &&
            op.Name.Equals("Append", StringComparison.OrdinalIgnoreCase);

        public static bool IsTextureLike(BPNode node) =>
            node is MaterialOpNode { IsTextureSample: true } ||
            (node is MaterialParameterNode && node.Output.Any(o => o.PinType.PinCategory == PinType.Object));

        public static bool IsColorOrTexturePin(GraphPin pin)
        {
            if (pin.PinType.PinCategory == PinType.Object)
                return true;
            if (GuessRole(pin.PinFriendlyName) is "Diffuse" or "Normal" or "Mask" or "Emissive")
                return true;
            if (GuessAttribute(pin.PinFriendlyName) is "BaseColor" or "EmissiveColor" or "Normal" or "SubsurfaceColor")
                return true;
            string n = CanonicalName(pin.PinFriendlyName);
            return n is "texture" or "tex" or "map" or "color" or "rgb";
        }

        public static bool CanConnectMaterial(BPNode source, GraphPin? from, GraphPin? to)
        {
            if (!MaterialLinkResolver.PinsCompatible(from, to) || to == null)
                return false;
            if (IsUvPinName(to.PinFriendlyName))
                return IsUvNode(source) || IsAppendOp(source);
            if (IsUvNode(source))
                return IsUvPinName(to.PinFriendlyName);
            if (IsTextureLike(source))
                return IsColorOrTexturePin(to);
            return true;
        }
    }

    public sealed class MaterialLinkResolver
    {
        readonly Dictionary<UObject, MaterialExpressionNode> _byObject;
        readonly Dictionary<string, BPNode> _byName;
        readonly List<MaterialExpressionNode> _expressions;

        public MaterialLinkResolver(Dictionary<UObject, MaterialExpressionNode> byObject)
        {
            _byObject = byObject;
            _expressions = [.. byObject.Values];
            _byName = new Dictionary<string, BPNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in byObject.Values)
            {
                Register(node.Expression.Name, node);
                Register(node.Name, node);
                string pretty = MaterialExpressionNode.PrettyName(node.Expression);
                if (_expressions.Count(o => MaterialExpressionNode.PrettyName(o.Expression) == pretty) == 1)
                    Register(pretty, node);
            }
        }

        public void Register(string? name, BPNode node)
        {
            string clean = MaterialGraphUtil.CleanName(name);
            if (string.IsNullOrEmpty(clean))
                return;
            _byName.TryAdd(clean, node);
        }

        public BPNode? Resolve(FExpressionInput? input)
        {
            if (input == null)
                return null;
            input = MaterialGraphUtil.Normalize(input);

            if (input.Expression is { IsNull: false } index)
            {
                try
                {
                    if (index.Load<UObject>() is { } sourceExpr)
                    {
                        if (_byObject.TryGetValue(sourceExpr, out var fromObject))
                            return fromObject;
                        if (TryGet(sourceExpr.Name, out var fromLoadedName))
                            return fromLoadedName;
                    }
                }
                catch
                {
                    // broken package index; fall through to name lookup
                }

                if (TryGet(index.Name, out var fromIndexName))
                    return fromIndexName;
            }

            string exprName = input.ExpressionName.Text;
            if (TryGet(exprName, out var fromExprName))
                return fromExprName;

            return MatchExpressionName(exprName);
        }

        BPNode? MatchExpressionName(string? name)
        {
            string clean = MaterialGraphUtil.CleanName(name);
            if (string.IsNullOrEmpty(clean))
                return null;

            var exact = _expressions.Where(o =>
                o.Expression.Name.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
                o.Name.Equals(clean, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1)
                return exact[0];

            var ends = _expressions.Where(o =>
                o.Expression.Name.EndsWith(clean, StringComparison.OrdinalIgnoreCase) ||
                clean.EndsWith(o.Expression.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ends.Count == 1)
                return ends[0];

            return null;
        }

        public static GraphPin? GetOutputPin(BPNode source, int outputIndex)
        {
            var visible = source.Output.Where(o => !o.IsHidden).ToList();
            if (visible.Count == 0)
                visible = source.Output.ToList();
            if (visible.Count == 0)
                return null;
            if (outputIndex >= 0 && outputIndex < visible.Count)
                return visible[outputIndex];
            return visible[0];
        }

        public static GraphPin? FindInputPin(BPNode node, string name)
        {
            var match = node.Input.FirstOrDefault(o =>
                o.PinName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                o.PinFriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
            var visible = node.Input.Where(o => !o.IsHidden).ToList();
            return visible.Count == 1 ? visible[0] : null;
        }

        public static bool PinsCompatible(GraphPin? from, GraphPin? to)
        {
            if (from == null || to == null)
                return false;
            if (from.IsInput && to.IsOutput)
                (from, to) = (to, from);
            if (!from.IsOutput || !to.IsInput)
                return false;

            var a = from.PinType.PinCategory;
            var b = to.PinType.PinCategory;
            if (a == b)
                return true;
            if (IsNumeric(a) && IsNumeric(b))
                return true;
            return false;
        }

        static bool IsNumeric(PinType type) =>
            type is PinType.Float or PinType.Double or PinType.Vector or PinType.Int or PinType.Byte;

        public static void ConnectPins(GraphPin? from, GraphPin? to)
        {
            if (from == null || to == null || from == to)
                return;
            if (from.ParentNode != null && from.ParentNode == to.ParentNode)
                return;
            if (from.LinkedTo.Contains(to) || to.LinkedTo.Contains(from))
                return;
            if (from.IsInput && to.IsOutput)
                (from, to) = (to, from);
            if (!from.IsOutput || !to.IsInput)
                return;
            if (!PinsCompatible(from, to))
                return;
            Utils.Connect(from, to);
        }

        bool TryGet(string? name, out BPNode node)
        {
            node = null!;
            string clean = MaterialGraphUtil.CleanName(name);
            return !string.IsNullOrEmpty(clean) && _byName.TryGetValue(clean, out node!);
        }
    }
}
