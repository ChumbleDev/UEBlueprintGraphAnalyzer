using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using UEBlueprintGraphViewer.Assets;
using UEBlueprintGraphViewer.Engine;
using static UEBlueprintGraphViewer.Engine.EngineBPData;
using static UEBlueprintGraphViewer.Engine.EngineEnums;
using static UEBlueprintGraphViewer.Engine.Utils;

namespace UEBlueprintGraphViewer.Nodes
{
    public static class MaterialGraphBuilder
    {
        public static readonly string[] AttributeNames =
        [
            "BaseColor", "Metallic", "Specular", "Roughness", "Anisotropy", "EmissiveColor",
            "Opacity", "OpacityMask", "Normal", "Tangent", "WorldPositionOffset",
            "WorldDisplacement", "TessellationMultiplier", "SubsurfaceColor", "ClearCoat",
            "ClearCoatRoughness", "AmbientOcclusion", "Refraction", "PixelDepthOffset",
            "ShadingModelFromMaterialExpression", "Displacement"
        ];

        static readonly string[] AlwaysShownAttributes =
        [
            "BaseColor", "Metallic", "Specular", "Roughness", "EmissiveColor",
            "Opacity", "Normal", "AmbientOcclusion"
        ];

        public static BPGraph Build(Asset asset)
        {
            var graph = new BPGraph { IsMaterialGraph = true, IsDataFlowGraph = true };
            var snap = asset.MaterialSnapshot;
            var expressions = snap?.Expressions ?? asset.MaterialExpressions;

            var nodes = new Dictionary<UObject, MaterialExpressionNode>();
            foreach (var expr in expressions)
            {
                try
                {
                    var node = new MaterialExpressionNode(expr);
                    nodes[expr] = node;
                    graph.AddNode(node);
                }
                catch (Exception ex)
                {
                    ErrorLog.Record(ex, $"Material expression {expr.Name}");
                }
            }

            var resolver = new MaterialLinkResolver(nodes);
            foreach (var node in nodes.Values)
                ConnectExpressionWires(node, resolver);

            var resultPins = new Dictionary<string, GraphPin>(StringComparer.OrdinalIgnoreCase);
            MaterialResultNode? result = null;
            if (asset.MaterialKind == MaterialAssetKind.MaterialFunction)
            {
                MarkFunctionOutputs(nodes);
            }
            else
            {
                result = AddMaterialResult(graph, asset.ObjectName, resultPins);
                if (snap != null)
                    ConnectAttributeWires(result, snap, resolver, resultPins);
            }

            var claimedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes.Values)
                claimedNames.Add(node.Name);

            if (snap != null)
            {
                AddParameterNodes(graph, snap, claimedNames, resolver);
                AddTextureNodes(graph, snap, claimedNames, resolver);
                AddFunctionNodes(graph, snap, nodes, claimedNames, resolver);
                if (result != null)
                    WireMaterialNetwork(graph, snap, result, resultPins);
            }
            else
            {
                AddFunctionNodes(graph, asset, nodes, claimedNames, resolver);
            }

            if (graph.Nodes.Count == 0)
            {
                graph.AddNode(new MaterialResultNode(asset.ObjectName) { Name = "No material data" });
            }

            bool hasSynthetic = graph.Nodes.Any(o => o is MaterialParameterNode or MaterialFunctionCallNode);
            var expressionNodes = graph.Nodes.OfType<MaterialExpressionNode>().ToList();
            graph.UseSavedPositions = !hasSynthetic && expressionNodes.Count > 0 && expressionNodes.All(o => o.HasEditorPosition);
            if (graph.UseSavedPositions && result != null)
                PlaceResultBesideExpressions(graph, result);

            if (asset.MaterialKind != MaterialAssetKind.MaterialFunction)
                LikelyMaterialFunctionAnnotator.Detect(graph);

            RefreshOutputNames(asset, result);
            return graph;
        }

        static void ConnectExpressionWires(MaterialExpressionNode node, MaterialLinkResolver resolver)
        {
            foreach (var (pin, input) in node.MaterialWires)
            {
                var source = resolver.Resolve(input);
                if (source == null || source == node)
                    continue;
                MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(source, input.OutputIndex), pin);
            }
        }

        static MaterialResultNode AddMaterialResult(BPGraph graph, string name, Dictionary<string, GraphPin> resultPins)
        {
            var result = new MaterialResultNode(string.IsNullOrEmpty(name) ? "Material" : name);
            foreach (var attr in AlwaysShownAttributes)
            {
                var pin = new GraphPin(PrettyAttribute(attr), EEdGraphPinDirection.EGPD_Input, MakePinType(PinType.Float));
                result.AddInputPin(pin);
                resultPins[attr] = pin;
            }
            graph.AddNode(result);
            return result;
        }

        static void ConnectAttributeWires(MaterialResultNode result, MaterialSnapshot snap, MaterialLinkResolver resolver, Dictionary<string, GraphPin> resultPins)
        {
            foreach (var wire in snap.AttributeWires)
            {
                if (!resultPins.TryGetValue(wire.Attribute, out var pin))
                {
                    pin = new GraphPin(PrettyAttribute(wire.Attribute), EEdGraphPinDirection.EGPD_Input, MakePinType(PinType.Float));
                    result.AddInputPin(pin);
                    resultPins[wire.Attribute] = pin;
                }
                if (!string.IsNullOrEmpty(wire.Constant) && string.IsNullOrEmpty(pin.Value))
                    pin.Value = wire.Constant;
                var resolved = resolver.Resolve(wire.Input);
                if (resolved == null)
                    continue;
                MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(resolved, wire.Input?.OutputIndex ?? 0), pin);
            }
        }

        static void AddParameterNodes(BPGraph graph, MaterialSnapshot snap, HashSet<string> claimed, MaterialLinkResolver resolver)
        {
            foreach (var param in snap.Parameters)
            {
                if (!claimed.Add(param.Name))
                    continue;
                var pinType = param.Kind switch
                {
                    "Vector" => MakePinType(PinType.Vector),
                    "Switch" => MakePinType(PinType.Bool),
                    _ => MakePinType(PinType.Float)
                };
                var node = new MaterialParameterNode(param.Name, param.Value, pinType, param.Attribute);
                graph.AddNode(node);
                resolver.Register(param.Name, node);
            }
        }

        static void AddTextureNodes(BPGraph graph, MaterialSnapshot snap, HashSet<string> claimed, MaterialLinkResolver resolver)
        {
            foreach (var tex in snap.Textures)
            {
                if (!claimed.Add(tex.Name))
                    continue;
                string value = string.IsNullOrEmpty(tex.TextureName) ? tex.Path : tex.TextureName;
                var node = new MaterialParameterNode(tex.Name, value, MakePinType(PinType.Object), tex.Attribute);
                graph.AddNode(node);
                resolver.Register(tex.Name, node);
                resolver.Register(tex.TextureName, node);
            }
        }

        static void AddFunctionNodes(BPGraph graph, MaterialSnapshot snap, Dictionary<UObject, MaterialExpressionNode> expressionNodes, HashSet<string> claimed, MaterialLinkResolver resolver)
        {
            foreach (var expr in expressionNodes.Keys)
            {
                if (!IsFunctionCall(expr))
                    continue;
                string name = FunctionAssetName(expr);
                if (!string.IsNullOrEmpty(name))
                    claimed.Add(name);
            }

            foreach (var funcRef in snap.Functions)
            {
                if (string.IsNullOrEmpty(funcRef.Name) || !claimed.Add(funcRef.Name))
                    continue;
                var node = CreateFunctionNode(funcRef);
                graph.AddNode(node);
                resolver.Register(funcRef.Name, node);
            }
        }

        static void AddFunctionNodes(BPGraph graph, Asset asset, Dictionary<UObject, MaterialExpressionNode> expressionNodes, HashSet<string> claimed, MaterialLinkResolver resolver)
        {
            foreach (var funcRef in asset.MaterialFunctionRefs)
            {
                if (string.IsNullOrEmpty(funcRef.Name) || !claimed.Add(funcRef.Name))
                    continue;
                var node = CreateFunctionNode(funcRef);
                graph.AddNode(node);
                resolver.Register(funcRef.Name, node);
            }
        }

        static MaterialFunctionCallNode CreateFunctionNode(MaterialFunctionRef funcRef)
        {
            if (funcRef.Inputs.Count == 0 && funcRef.Outputs.Count == 0)
                MaterialFunctionInterface.Fill(funcRef);

            var node = new MaterialFunctionCallNode(funcRef.Name, funcRef.Role);
            foreach (var input in funcRef.Inputs)
            {
                var pinType = input.Type switch
                {
                    "Texture" => MakePinType(PinType.Object),
                    "Bool" => MakePinType(PinType.Bool),
                    "Vector" => MakePinType(PinType.Vector),
                    _ => MakePinType(PinType.Float)
                };
                node.AddInputPin(new GraphPin(input.Name, EEdGraphPinDirection.EGPD_Input, pinType));
            }
            foreach (var output in funcRef.Outputs)
                node.AddOutputPin(new GraphPin(output.Name, EEdGraphPinDirection.EGPD_Output, MakePinType(PinType.Float)));
            if (node.Output.Count == 0)
                node.AddOutputPin(new GraphPin("Output", EEdGraphPinDirection.EGPD_Output, MakePinType(PinType.Float)));
            node.TintPin = node.Output.FirstOrDefault() ?? node.Input.FirstOrDefault();
            return node;
        }

        static void WireMaterialNetwork(BPGraph graph, MaterialSnapshot snap, MaterialResultNode result, Dictionary<string, GraphPin> resultPins)
        {
            var functions = graph.Nodes.OfType<MaterialFunctionCallNode>().ToList();
            var sources = graph.Nodes
                .Where(o => o is MaterialParameterNode or MaterialExpressionNode)
                .Where(o => UnusedOutput(o) != null)
                .ToList();

            AssignBestMatches(functions, sources, 35);

            var uvFunctions = functions.Where(o => FunctionRole(o, snap) == "UV").ToList();
            foreach (var uv in uvFunctions)
            {
                var uvOut = MaterialLinkResolver.GetOutputPin(uv, 0);
                if (uvOut == null)
                    continue;
                foreach (var func in functions)
                {
                    if (func == uv)
                        continue;
                    foreach (var pin in func.Input.Where(IsUvPin))
                    {
                        if (pin.LinkedTo.Count > 0)
                            continue;
                        MaterialLinkResolver.ConnectPins(uvOut, pin);
                    }
                }
            }

            AssignBestMatches(functions, functions.Where(o => FunctionRole(o, snap) != "UV").Cast<BPNode>().ToList(), 55);

            foreach (var func in functions)
                WireFunctionToResult(func, snap, result, resultPins);

            foreach (var source in sources)
            {
                if (HasOutgoing(source) || source is MaterialFunctionCallNode)
                    continue;
                if (IsTextureSource(source))
                {
                    WireLeftoverTexture(source, functions, resultPins);
                    continue;
                }

                string? attr = AttributeOf(source);
                if (attr == null || MaterialGraphUtil.GuessRole(source.Name) is "UV" or "Adjust")
                    continue;
                if (!resultPins.TryGetValue(attr, out var pin) || pin.LinkedTo.Count > 0)
                    continue;
                MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(source, 0), pin);
            }
        }

        static void AssignBestMatches(List<MaterialFunctionCallNode> functions, List<BPNode> sources, int minScore)
        {
            var candidates = new List<(BPNode Source, GraphPin Input, int Score, bool Reusable)>();
            foreach (var func in functions)
            {
                foreach (var input in func.Input)
                {
                    if (input.LinkedTo.Count > 0)
                        continue;
                    foreach (var source in sources)
                    {
                        if (source == func)
                            continue;
                        int score = ScoreMatch(source, input, func);
                        if (score < minScore)
                            continue;
                        candidates.Add((source, input, score, IsReusableSource(source)));
                    }
                }
            }

            foreach (var (source, input, _, reusable) in candidates.OrderByDescending(o => o.Score))
            {
                if (input.LinkedTo.Count > 0)
                    continue;
                if (!reusable && HasOutgoing(source) && !IsUvSource(source))
                    continue;
                MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(source, 0), input);
            }
        }

        static int ScoreMatch(BPNode source, GraphPin input, MaterialFunctionCallNode func)
        {
            string sourceName = source.Name;
            string pinName = input.PinFriendlyName;
            if (sourceName.Equals(pinName, StringComparison.OrdinalIgnoreCase))
                return 100;
            if (NamesOverlap(sourceName, pinName))
                return 88;

            int tokens = TokenOverlap(sourceName, pinName);
            if (tokens >= 50)
                return tokens;

            string? sourceAttr = AttributeOf(source);
            string? pinAttr = MaterialGraphUtil.GuessAttribute(pinName);
            if (sourceAttr != null && sourceAttr == pinAttr)
                return 80;

            string? sourceRole = MaterialGraphUtil.GuessRole(sourceName);
            string? pinRole = MaterialGraphUtil.GuessRole(pinName);
            string funcRole = func.Role;
            if (string.IsNullOrEmpty(funcRole))
                funcRole = MaterialGraphUtil.GuessRole(func.Name) ?? "";

            bool texturePin = IsTexturePin(input);
            bool sourceTex = IsTextureSource(source);

            if (sourceTex && texturePin && RolesAlign(sourceRole, sourceAttr, funcRole, pinRole, pinAttr))
                return 76;
            if (sourceRole == "UV" && (pinRole == "UV" || IsUvPin(input)))
                return 70;
            if (sourceRole == "Adjust" && IsAdjustPin(pinName))
                return 66;
            if (sourceRole != null && sourceRole == pinRole)
                return 60;
            if (sourceRole != null && sourceRole == funcRole && !texturePin)
                return 52;
            if (sourceTex && texturePin && string.IsNullOrEmpty(sourceRole) && string.IsNullOrEmpty(sourceAttr) && !string.IsNullOrEmpty(funcRole))
                return 38;
            return tokens >= 35 ? tokens : 0;
        }

        static bool RolesAlign(string? sourceRole, string? sourceAttr, string funcRole, string? pinRole, string? pinAttr)
        {
            if (!string.IsNullOrEmpty(sourceRole) && sourceRole == funcRole)
                return true;
            if (!string.IsNullOrEmpty(sourceRole) && sourceRole == pinRole)
                return true;
            if (!string.IsNullOrEmpty(sourceAttr) && sourceAttr == pinAttr)
                return true;
            if (sourceAttr != null && RoleToAttribute(funcRole) == sourceAttr)
                return true;
            if (sourceAttr == "BaseColor" && funcRole == "Diffuse")
                return true;
            if (sourceAttr == "OpacityMask" && funcRole == "Mask")
                return true;
            if (sourceAttr == "Specular" && funcRole == "Mask")
                return true;
            return false;
        }

        static void WireFunctionToResult(MaterialFunctionCallNode func, MaterialSnapshot snap, MaterialResultNode result, Dictionary<string, GraphPin> resultPins)
        {
            string role = FunctionRole(func, snap);
            if (role == "UV")
                return;

            foreach (var output in func.Output)
            {
                string? attr = MaterialGraphUtil.GuessAttribute(output.PinFriendlyName)
                               ?? MaterialGraphUtil.GuessAttribute(func.Name)
                               ?? RoleToAttribute(role);
                if (attr == null)
                    continue;
                if (!resultPins.TryGetValue(attr, out var pin))
                {
                    pin = new GraphPin(PrettyAttribute(attr), EEdGraphPinDirection.EGPD_Input, output.PinType);
                    result.AddInputPin(pin);
                    resultPins[attr] = pin;
                }
                if (pin.LinkedTo.Count > 0)
                    continue;
                MaterialLinkResolver.ConnectPins(output, pin);
            }
        }

        static void WireLeftoverTexture(BPNode source, List<MaterialFunctionCallNode> functions, Dictionary<string, GraphPin> resultPins)
        {
            string? role = MaterialGraphUtil.GuessRole(source.Name);
            string? attr = AttributeOf(source);
            if (functions.Any(o =>
                    o.Role == role ||
                    (!string.IsNullOrEmpty(role) && MaterialGraphUtil.GuessRole(o.Name) == role)))
                return;

            string? target = attr ?? role switch
            {
                "Mask" => resultPins.ContainsKey("Specular") && resultPins["Specular"].LinkedTo.Count == 0 ? "Specular" : "Roughness",
                "Diffuse" => "BaseColor",
                "Normal" => "Normal",
                "Emissive" => "EmissiveColor",
                _ => null
            };
            if (target == null || !resultPins.TryGetValue(target, out var pin) || pin.LinkedTo.Count > 0)
                return;
            MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(source, 0), pin);
        }

        static string FunctionRole(MaterialFunctionCallNode func, MaterialSnapshot snap) =>
            !string.IsNullOrEmpty(func.Role)
                ? func.Role
                : snap.Functions.FirstOrDefault(f => f.Name.Equals(func.Name, StringComparison.OrdinalIgnoreCase))?.Role
                  ?? MaterialGraphUtil.GuessRole(func.Name)
                  ?? "";

        static string? RoleToAttribute(string role) =>
            role switch
            {
                "Diffuse" => "BaseColor",
                "Normal" => "Normal",
                "Emissive" => "EmissiveColor",
                "Mask" => "Roughness",
                _ => null
            };

        static string? AttributeOf(BPNode source) =>
            source is MaterialParameterNode param && !string.IsNullOrEmpty(param.Attribute)
                ? param.Attribute
                : MaterialGraphUtil.GuessAttribute(source.Name);

        static bool NamesOverlap(string a, string b)
        {
            string na = MaterialGraphUtil.CleanName(a);
            string nb = MaterialGraphUtil.CleanName(b);
            if (na.Length < 3 || nb.Length < 3)
                return false;
            return na.Contains(nb, StringComparison.OrdinalIgnoreCase) ||
                   nb.Contains(na, StringComparison.OrdinalIgnoreCase);
        }

        static int TokenOverlap(string a, string b)
        {
            var left = Tokens(a);
            var right = Tokens(b);
            if (left.Count == 0 || right.Count == 0)
                return 0;
            int shared = left.Intersect(right, StringComparer.OrdinalIgnoreCase).Count();
            if (shared == 0)
                return 0;
            return Math.Min(80, 30 + shared * 20);
        }

        static HashSet<string> Tokens(string name)
        {
            var raw = new List<string>();
            var current = new System.Text.StringBuilder();
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(char.ToLowerInvariant(c));
                    continue;
                }
                if (current.Length > 0)
                {
                    raw.Add(current.ToString());
                    current.Clear();
                }
            }
            if (current.Length > 0)
                raw.Add(current.ToString());

            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in raw)
            {
                if (token.Length < 2 || token is "mf" or "pm" or "tex" or "map" or "the")
                    continue;
                tokens.Add(token);
                foreach (var alias in Aliases(token))
                    tokens.Add(alias);
            }
            return tokens;
        }

        static IEnumerable<string> Aliases(string token) =>
            token switch
            {
                "uv" or "uvs" or "texcoord" or "texcoords" or "coordinate" or "coordinates" => ["uv", "uvs", "texcoord"],
                "bright" or "brightness" or "br" => ["brightness", "bright"],
                "diff" or "diffuse" or "albedo" or "basecolor" or "color" => ["diffuse", "albedo", "basecolor"],
                "nrm" or "nm" or "normal" or "normals" => ["normal"],
                "spec" or "specular" or "sp" => ["specular", "mask"],
                "mask" or "orm" or "mra" => ["mask"],
                "emissive" or "emission" or "emit" => ["emissive"],
                "tile" or "tiling" => ["tiling"],
                _ => []
            };

        static bool IsUvPin(GraphPin pin)
        {
            string n = pin.PinFriendlyName;
            return n.Equals("UVs", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("UV", StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("TexCoord", StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("Coordinate", StringComparison.OrdinalIgnoreCase) ||
                   MaterialGraphUtil.GuessRole(n) == "UV";
        }

        static bool IsAdjustPin(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("bright") || n.Contains("contrast") || n.Contains("tint") ||
                   n.Contains("intensity") || n.Contains("saturation");
        }

        static bool IsTexturePin(GraphPin pin) =>
            pin.PinType.PinCategory == PinType.Object ||
            pin.PinFriendlyName.Contains("texture", StringComparison.OrdinalIgnoreCase) ||
            pin.PinFriendlyName.Contains("map", StringComparison.OrdinalIgnoreCase);

        static bool IsTextureSource(BPNode node) =>
            node is MaterialParameterNode && node.Output.Any(o => o.PinType.PinCategory == PinType.Object);

        static bool IsUvSource(BPNode node) =>
            node is MaterialFunctionCallNode func
                ? func.Role == "UV" || MaterialGraphUtil.GuessRole(func.Name) == "UV"
                : MaterialGraphUtil.GuessRole(node.Name) == "UV";

        static bool IsReusableSource(BPNode node) => IsUvSource(node);

        static bool HasOutgoing(BPNode node) =>
            node.Output.Any(o => o.LinkedTo.Count > 0);

        static GraphPin? UnusedOutput(BPNode node) =>
            node.Output.FirstOrDefault(o => !o.IsHidden && o.LinkedTo.Count == 0)
            ?? node.Output.FirstOrDefault(o => o.LinkedTo.Count == 0);

        static bool IsFunctionCall(UObject expr)
        {
            string cls = expr.Class?.Name.Text ?? expr.ExportType;
            return cls.Contains("FunctionCall", StringComparison.OrdinalIgnoreCase);
        }

        static string FunctionAssetName(UObject expr)
        {
            var func = expr.GetOrDefault<FPackageIndex?>("MaterialFunction") ?? expr.GetOrDefault<FPackageIndex?>("Function");
            return MaterialGraphUtil.CleanName(func?.Name);
        }

        static void MarkFunctionOutputs(Dictionary<UObject, MaterialExpressionNode> nodes)
        {
            foreach (var (expr, node) in nodes)
            {
                string cls = expr.Class?.Name.Text ?? expr.ExportType;
                if (cls.Contains("FunctionOutput", StringComparison.OrdinalIgnoreCase))
                    node.HeaderCenter = true;
            }
        }

        static void PlaceResultBesideExpressions(BPGraph graph, MaterialResultNode result)
        {
            var expressions = graph.Nodes.OfType<MaterialExpressionNode>().Where(o => o.HasEditorPosition).ToList();
            if (expressions.Count == 0)
                return;
            result.X = expressions.Max(o => o.X) + 420;
            result.Y = expressions.Average(o => o.Y);
        }

        static void RefreshOutputNames(Asset asset, MaterialResultNode? result)
        {
            if (result == null)
                return;
            var connected = result.Input.Where(o => o.LinkedTo.Count > 0).Select(o => o.PinFriendlyName).ToList();
            if (connected.Count == 0)
                return;
            asset.MaterialOutputNames.Clear();
            asset.MaterialOutputNames.AddRange(connected);
        }

        public static string PrettyAttribute(string name) =>
            name switch
            {
                "EmissiveColor" => "Emissive Color",
                "BaseColor" => "Base Color",
                "WorldPositionOffset" => "World Position Offset",
                "SubsurfaceColor" => "Subsurface Color",
                "AmbientOcclusion" => "Ambient Occlusion",
                "PixelDepthOffset" => "Pixel Depth Offset",
                "OpacityMask" => "Opacity Mask",
                _ => name
            };
    }
}
