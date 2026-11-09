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
                AddTextureNodes(graph, snap, claimedNames, resolver, snap.HasShaderMap);
                AddFunctionNodes(graph, snap, nodes, claimedNames, resolver, snap.HasShaderMap);
                if (result != null)
                {
                    var preshaderRoots = snap.Preshaders.Count > 0
                        ? EmitPreshaderGraphs(graph, snap)
                        : [];
                    WireMaterialNetwork(graph, snap, resultPins);
                    ConnectPreshaderRoots(preshaderRoots, resultPins);
                    CombineTexturesWithUniforms(graph, resultPins);
                    AttachOrphanOps(graph, resultPins);
                    HideUnusedMaterialPins(graph);
                }
            }
            else
            {
                AddFunctionNodes(graph, asset, nodes, claimedNames, resolver);
            }

            if (graph.Nodes.Count == 0)
            {
                graph.AddNode(new MaterialResultNode(asset.ObjectName) { Name = "No material data" });
            }

            bool hasSynthetic = graph.Nodes.Any(o => o is MaterialParameterNode or MaterialFunctionCallNode or MaterialOpNode);
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
                var pin = new GraphPin(PrettyAttribute(attr), EEdGraphPinDirection.EGPD_Input, AttributePinType(attr));
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
                    pin = new GraphPin(PrettyAttribute(wire.Attribute), EEdGraphPinDirection.EGPD_Input, AttributePinType(wire.Attribute));
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

        static void AddTextureNodes(BPGraph graph, MaterialSnapshot snap, HashSet<string> claimed, MaterialLinkResolver resolver, bool asSamples)
        {
            foreach (var tex in snap.Textures)
            {
                if (!claimed.Add(tex.Name))
                    continue;
                string value = string.IsNullOrEmpty(tex.TextureName) ? tex.Path : tex.TextureName;
                BPNode node = asSamples
                    ? MaterialOpNode.TextureSample(tex.Name, value, tex.Attribute)
                    : new MaterialParameterNode(tex.Name, value, MakePinType(PinType.Object), tex.Attribute);
                graph.AddNode(node);
                resolver.Register(tex.Name, node);
                resolver.Register(tex.TextureName, node);
            }
        }

        static void AddFunctionNodes(BPGraph graph, MaterialSnapshot snap, Dictionary<UObject, MaterialExpressionNode> expressionNodes, HashSet<string> claimed, MaterialLinkResolver resolver, bool sampledTextures)
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
                var node = CreateFunctionNode(funcRef, sampledTextures);
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
                var node = CreateFunctionNode(funcRef, false);
                graph.AddNode(node);
                resolver.Register(funcRef.Name, node);
            }
        }

        static MaterialFunctionCallNode CreateFunctionNode(MaterialFunctionRef funcRef, bool sampledTextures)
        {
            if (funcRef.Inputs.Count == 0 && funcRef.Outputs.Count == 0)
                MaterialFunctionInterface.Fill(funcRef);

            var node = new MaterialFunctionCallNode(funcRef.Name, funcRef.Role, funcRef.Index, funcRef.UsedFallback);
            foreach (var input in funcRef.Inputs)
                node.AddInputPin(new GraphPin(input.Name, EEdGraphPinDirection.EGPD_Input, FunctionPinType(input.Type, input.Name, sampledTextures)));
            foreach (var output in funcRef.Outputs)
                node.AddOutputPin(new GraphPin(output.Name, EEdGraphPinDirection.EGPD_Output, FunctionPinType(output.Type, output.Name, sampledTextures)));
            if (node.Output.Count == 0)
                node.AddOutputPin(new GraphPin("Output", EEdGraphPinDirection.EGPD_Output, MakePinType(PinType.Float)));
            node.TintPin = node.Output.FirstOrDefault() ?? node.Input.FirstOrDefault();
            return node;
        }

        static GraphPinType FunctionPinType(string type, string name, bool sampledTextures)
        {
            if (MaterialGraphUtil.GuessRole(name) == "UV" || type.Equals("UV", StringComparison.OrdinalIgnoreCase))
                return MakePinType(PinType.Vector);
            if (sampledTextures && type.Equals("Texture", StringComparison.OrdinalIgnoreCase))
                return MakePinType(PinType.Vector);
            return type switch
            {
                "Texture" => MakePinType(PinType.Object),
                "Bool" => MakePinType(PinType.Bool),
                "Vector" => MakePinType(PinType.Vector),
                _ => IsColorName(name) ? MakePinType(PinType.Vector) : MakePinType(PinType.Float)
            };
        }

        static GraphPinType AttributePinType(string attr) =>
            attr is "BaseColor" or "EmissiveColor" or "Normal" or "SubsurfaceColor" or "Tangent"
                ? MakePinType(PinType.Vector)
                : MakePinType(PinType.Float);

        static bool IsColorName(string name) =>
            MaterialGraphUtil.GuessAttribute(name) is "BaseColor" or "EmissiveColor" or "Normal" or "SubsurfaceColor";

        static List<(BPNode Root, string? Attribute)> EmitPreshaderGraphs(BPGraph graph, MaterialSnapshot snap)
        {
            var existing = graph.Nodes
                .Where(o => o is MaterialParameterNode or MaterialOpNode)
                .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(o => o.Key, o => o.First(), StringComparer.OrdinalIgnoreCase);
            var roots = new List<(BPNode Root, string? Attribute)>();

            foreach (var program in snap.Preshaders)
            {
                if (program.Root == null)
                    continue;
                var root = EmitPreshader(graph, program.Root, existing);
                if (root != null)
                    roots.Add((root, program.GuessedAttribute));
            }

            return roots;
        }

        static void ConnectPreshaderRoots(List<(BPNode Root, string? Attribute)> roots, Dictionary<string, GraphPin> resultPins)
        {
            foreach (var (root, guessed) in roots)
            {
                if (HasOutgoing(root))
                    continue;
                string? attr = guessed;
                if (attr == null || !resultPins.TryGetValue(attr, out var pin) || pin.LinkedTo.Count > 0)
                    continue;
                var output = MaterialLinkResolver.GetOutputPin(root, 0);
                if (!MaterialLinkResolver.PinsCompatible(output, pin))
                    continue;
                MaterialLinkResolver.ConnectPins(output, pin);
            }
        }

        static BPNode? EmitPreshader(BPGraph graph, PreshaderExpr expr, Dictionary<string, BPNode> existing)
        {
            if (expr.Kind == PreshaderKind.Parameter && !string.IsNullOrEmpty(expr.ParameterName))
            {
                if (existing.TryGetValue(expr.ParameterName, out var param))
                    return param;
                var created = new MaterialParameterNode(expr.ParameterName, "", MakePinType(PinType.Float), MaterialGraphUtil.GuessAttribute(expr.ParameterName));
                graph.AddNode(created);
                existing[expr.ParameterName] = created;
                return created;
            }

            if (expr.Kind == PreshaderKind.Constant)
            {
                string key = "const:" + expr.Constant;
                if (existing.TryGetValue(key, out var constant))
                    return constant;
                var node = MaterialOpNode.Constant(string.IsNullOrEmpty(expr.Constant) ? "0" : expr.Constant);
                graph.AddNode(node);
                existing[key] = node;
                return node;
            }

            var op = MaterialOpNode.Math(expr.Name, Math.Max(expr.Args.Count, 1));
            graph.AddNode(op);
            for (int i = 0; i < expr.Args.Count && i < op.Input.Count; i++)
            {
                if (expr.Args[i].Kind == PreshaderKind.Constant)
                {
                    op.Input[i].Value = string.IsNullOrEmpty(expr.Args[i].Constant) ? "0" : expr.Args[i].Constant;
                    continue;
                }

                var child = EmitPreshader(graph, expr.Args[i], existing);
                MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(child, 0), op.Input[i]);
            }
            return op;
        }

        static void WireMaterialNetwork(BPGraph graph, MaterialSnapshot snap, Dictionary<string, GraphPin> resultPins)
        {
            var functions = graph.Nodes.OfType<MaterialFunctionCallNode>().ToList();
            var sources = graph.Nodes
                .Where(o => o is MaterialParameterNode or MaterialExpressionNode or MaterialOpNode)
                .Where(o => UnusedOutput(o) != null)
                .ToList();

            FanUvOutputs(functions.Where(o => FunctionRole(o, snap) == "UV").ToList(), graph);
            AssignBestMatches(functions, sources, 50);
            AssignBestMatches(functions, functions.Where(o => FunctionRole(o, snap) != "UV").Cast<BPNode>().ToList(), 70);

            foreach (var func in functions)
                WireFunctionToResult(func, snap, resultPins);

            foreach (var source in sources)
            {
                if (HasOutgoing(source) || source is MaterialFunctionCallNode)
                    continue;
                if (source is MaterialOpNode { IsTextureSample: false })
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
                var output = MaterialLinkResolver.GetOutputPin(source, 0);
                if (!MaterialLinkResolver.PinsCompatible(output, pin))
                    continue;
                MaterialLinkResolver.ConnectPins(output, pin);
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
                if (!reusable && HasOutgoing(source) && !MaterialGraphUtil.IsUvNode(source))
                    continue;
                var output = MaterialLinkResolver.GetOutputPin(source, 0);
                if (!MaterialGraphUtil.CanConnectMaterial(source, output, input))
                    continue;
                MaterialLinkResolver.ConnectPins(output, input);
            }
        }

        static void FanUvOutputs(List<MaterialFunctionCallNode> uvFunctions, BPGraph graph)
        {
            foreach (var uv in uvFunctions)
            {
                var uvOut = MaterialLinkResolver.GetOutputPin(uv, 0);
                if (uvOut == null)
                    continue;
                foreach (var node in graph.Nodes)
                {
                    if (node == uv)
                        continue;
                    foreach (var pin in node.Input.Where(o => MaterialGraphUtil.IsUvPinName(o.PinFriendlyName)))
                    {
                        if (pin.LinkedTo.Count > 0)
                            continue;
                        MaterialLinkResolver.ConnectPins(uvOut, pin);
                    }
                }
            }
        }

        static int ScoreMatch(BPNode source, GraphPin input, MaterialFunctionCallNode func)
        {
            var output = MaterialLinkResolver.GetOutputPin(source, 0);
            if (!MaterialLinkResolver.PinsCompatible(output, input))
                return 0;

            if (MaterialGraphUtil.IsUvPinName(input.PinFriendlyName))
                return MaterialGraphUtil.IsUvNode(source) || MaterialGraphUtil.IsAppendOp(source) ? 90 : 0;
            if (MaterialGraphUtil.IsUvNode(source) && source is not MaterialOpNode)
                return 0;

            if (source is MaterialOpNode { IsTextureSample: false } op)
            {
                if (IsAdjustPin(input.PinFriendlyName))
                    return 74;
                if (op.Name.Equals("Append", StringComparison.OrdinalIgnoreCase) &&
                    MaterialGraphUtil.IsUvPinName(input.PinFriendlyName))
                    return 88;
                if (op.Name.Equals("Append", StringComparison.OrdinalIgnoreCase) &&
                    (input.PinFriendlyName.Contains("Tile", StringComparison.OrdinalIgnoreCase) ||
                     input.PinFriendlyName.Contains("Offset", StringComparison.OrdinalIgnoreCase)))
                    return 80;
                return 0;
            }

            string sourceName = source.Name;
            string pinName = input.PinFriendlyName;
            if (sourceName.Equals(pinName, StringComparison.OrdinalIgnoreCase))
                return 100;
            if (MaterialGraphUtil.NamesMatch(sourceName, pinName))
                return 96;
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

            bool colorPin = IsColorPin(input);
            bool sourceTex = IsTextureSource(source);

            if (sourceTex && colorPin && RolesAlign(sourceRole, sourceAttr, funcRole, pinRole, pinAttr))
                return 76;
            if (sourceRole == "Adjust" && IsAdjustPin(pinName))
                return 72;
            if (sourceRole == "Adjust")
                return 0;
            if (sourceRole != null && sourceRole == pinRole)
                return 60;
            if (sourceTex && colorPin && string.IsNullOrEmpty(sourceRole) && string.IsNullOrEmpty(sourceAttr) && !string.IsNullOrEmpty(funcRole))
                return 45;
            return 0;
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

        static void WireFunctionToResult(MaterialFunctionCallNode func, MaterialSnapshot snap, Dictionary<string, GraphPin> resultPins)
        {
            string role = FunctionRole(func, snap);
            if (role == "UV")
                return;

            foreach (var output in func.Output)
            {
                if (output.LinkedTo.Count > 0)
                    continue;
                string? attr = MaterialGraphUtil.GuessAttribute(output.PinFriendlyName)
                               ?? (MaterialGraphUtil.CanonicalName(output.PinFriendlyName) is "output" or "result"
                                   ? RoleToAttribute(role)
                                   : null)
                               ?? MaterialGraphUtil.GuessAttribute(func.Name)
                               ?? RoleToAttribute(role);
                if (attr == null)
                    continue;
                if (!resultPins.TryGetValue(attr, out var pin) || pin.LinkedTo.Count > 0)
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

        static void CombineTexturesWithUniforms(BPGraph graph, Dictionary<string, GraphPin> resultPins)
        {
            foreach (var (attr, pin) in resultPins.ToList())
            {
                var texture = graph.Nodes
                    .OfType<MaterialOpNode>()
                    .FirstOrDefault(o => o.IsTextureSample && !HasOutgoing(o) &&
                                         (o.Attribute == attr || MaterialGraphUtil.GuessAttribute(o.Name) == attr));
                if (texture == null)
                    continue;

                if (pin.LinkedTo.Count == 1)
                {
                    var uniform = pin.LinkedTo[0];
                    if (uniform.ParentNode is MaterialFunctionCallNode)
                        continue;
                    Disconnect(uniform, pin);
                    var multiply = ExistingOrNewMultiply(graph, texture, uniform.ParentNode);
                    MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(multiply, 0), pin);
                    continue;
                }

                if (pin.LinkedTo.Count == 0)
                    MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(texture, 0), pin);
            }
        }

        static MaterialOpNode ExistingOrNewMultiply(BPGraph graph, BPNode texture, BPNode? uniform)
        {
            var existing = graph.Nodes.OfType<MaterialOpNode>()
                .FirstOrDefault(o => !o.IsTextureSample && !HasOutgoing(o) &&
                                     (o.Name is "×" or "*" or "Multiply") &&
                                     (uniform == null || o.Input.Any(p => p.LinkedTo.Any(l => l.ParentNode == uniform))));
            if (existing != null)
            {
                var empty = existing.Input.FirstOrDefault(p => p.LinkedTo.Count == 0 && string.IsNullOrEmpty(p.Value));
                if (empty != null)
                    MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(texture, 0), empty);
                return existing;
            }

            var multiply = MaterialOpNode.Math("×", 2);
            graph.AddNode(multiply);
            MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(texture, 0), multiply.Input[0]);
            if (uniform != null)
                MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(uniform, 0), multiply.Input[1]);
            return multiply;
        }

        static void Disconnect(GraphPin a, GraphPin b)
        {
            a.LinkedTo.Remove(b);
            b.LinkedTo.Remove(a);
        }

        static void AttachOrphanOps(BPGraph graph, Dictionary<string, GraphPin> resultPins)
        {
            var functions = graph.Nodes.OfType<MaterialFunctionCallNode>().ToList();
            var orphans = graph.Nodes.OfType<MaterialOpNode>()
                .Where(o => !o.IsTextureSample && !HasOutgoing(o))
                .OrderByDescending(OpCompleteness)
                .ToList();

            foreach (var op in orphans)
            {
                if (HasOutgoing(op))
                    continue;

                GraphPin? best = null;
                int bestScore = 0;
                foreach (var func in functions)
                {
                    foreach (var input in func.Input.Where(p => p.LinkedTo.Count == 0))
                    {
                        int score = ScoreMatch(op, input, func);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = input;
                        }
                    }
                }

                if (best != null && bestScore >= 50)
                {
                    MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(op, 0), best);
                    continue;
                }

                string? attr = AttributeOf(op) ?? GuessAttributeFromInputs(op);
                if (attr != null && resultPins.TryGetValue(attr, out var pin) && pin.LinkedTo.Count == 0)
                {
                    MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(op, 0), pin);
                    continue;
                }

                var emptyAdjust = functions
                    .SelectMany(f => f.Input)
                    .FirstOrDefault(p => p.LinkedTo.Count == 0 && IsAdjustPin(p.PinFriendlyName) &&
                                         MaterialLinkResolver.PinsCompatible(MaterialLinkResolver.GetOutputPin(op, 0), p));
                if (emptyAdjust != null)
                    MaterialLinkResolver.ConnectPins(MaterialLinkResolver.GetOutputPin(op, 0), emptyAdjust);
            }
        }

        static int OpCompleteness(MaterialOpNode op) =>
            op.Input.Count(p => p.LinkedTo.Count > 0 || !string.IsNullOrEmpty(p.Value));

        static string? GuessAttributeFromInputs(BPNode node)
        {
            var names = node.Input
                .SelectMany(p => p.LinkedTo.Select(l => l.ParentNode?.Name))
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => MaterialGraphUtil.GuessAttribute(n))
                .Where(a => a != null)
                .Distinct()
                .ToList();
            return names.Count == 1 ? names[0] : null;
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
            source switch
            {
                MaterialParameterNode param when !string.IsNullOrEmpty(param.Attribute) => param.Attribute,
                MaterialOpNode op when !string.IsNullOrEmpty(op.Attribute) => op.Attribute,
                _ => MaterialGraphUtil.GuessAttribute(source.Name)
            };

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

        static bool IsAdjustPin(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("bright") || n.Contains("contrast") || n.Contains("tint") ||
                   n.Contains("intensity") || n.Contains("saturation");
        }

        static bool IsColorPin(GraphPin pin) =>
            pin.PinType.PinCategory == PinType.Vector ||
            pin.PinType.PinCategory == PinType.Object ||
            IsColorName(pin.PinFriendlyName);

        static bool IsTextureSource(BPNode node) =>
            node is MaterialOpNode { IsTextureSample: true } ||
            (node is MaterialParameterNode && node.Output.Any(o => o.PinType.PinCategory == PinType.Object));

        static bool IsReusableSource(BPNode node) => MaterialGraphUtil.IsUvNode(node);

        static void HideUnusedMaterialPins(BPGraph graph)
        {
            foreach (var node in graph.Nodes)
            {
                if (node is MaterialResultNode or K2Node_Comment)
                    continue;

                if (node is MaterialOpNode { IsTextureSample: true })
                {
                    foreach (var pin in node.Input)
                    {
                        if (pin.LinkedTo.Count > 0 || !string.IsNullOrEmpty(pin.Value) || pin.IsHidden)
                            continue;
                        pin.HidePin();
                    }
                }

                foreach (var pin in node.Input.Concat(node.Output))
                    pin.UpdateAnchor();
            }
        }

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
