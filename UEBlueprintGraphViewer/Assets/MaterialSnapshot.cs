using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Material.Editor;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using UEBlueprintGraphViewer.Engine;
using UEBlueprintGraphViewer.Nodes;

namespace UEBlueprintGraphViewer.Assets
{
    public sealed class MaterialParamRecord
    {
        public required string Name { get; init; }
        public required string Kind { get; init; }
        public string Value { get; init; } = "";
        public string? Attribute { get; init; }
    }

    public sealed class MaterialTextureRecord
    {
        public required string Name { get; init; }
        public string TextureName { get; init; } = "";
        public string Path { get; init; } = "";
        public string? Attribute { get; init; }
    }

    public sealed class MaterialAttributeWire
    {
        public required string Attribute { get; init; }
        public FExpressionInput? Input { get; init; }
        public string Constant { get; init; } = "";
    }

    public sealed class MaterialSnapshot
    {
        public UObject Root { get; init; } = null!;
        public IPackage Package { get; init; } = null!;
        public List<UObject> Expressions { get; } = [];
        public List<MaterialParamRecord> Parameters { get; } = [];
        public List<MaterialTextureRecord> Textures { get; } = [];
        public List<MaterialFunctionRef> Functions { get; } = [];
        public List<MaterialAttributeWire> AttributeWires { get; } = [];
        public List<string> InspectNotes { get; } = [];

        public static MaterialSnapshot Capture(Asset asset, IPackage package, UObject root)
        {
            var snap = new MaterialSnapshot { Root = root, Package = package };
            snap.CollectExpressions(package, root);
            if (asset.MaterialKind == MaterialAssetKind.MaterialInstance)
                snap.CollectParentExpressions(root);
            snap.InspectPackage(package, root);
            snap.ReadAttributeWires(root);
            snap.ReadCachedData(root);
            snap.ReadInstanceOverrides(root);
            snap.ReadGetParams(root);
            snap.ReadFunctions(root);
            snap.ApplyToAsset(asset);
            return snap;
        }

        void CollectExpressions(IPackage package, UObject root)
        {
            var seen = new HashSet<UObject>();
            CollectExpressionsInto(seen, package, root);
            Expressions.AddRange(seen);
        }

        void CollectParentExpressions(UObject root)
        {
            var seen = new HashSet<UObject>(Expressions);
            var current = root;
            var visited = new HashSet<UObject> { root };
            for (int i = 0; i < 8; i++)
            {
                UObject? parent = null;
                try
                {
                    parent = current.GetOrDefault<FPackageIndex?>("Parent")?.Load<UObject>();
                }
                catch
                {
                    break;
                }

                if (parent == null || !visited.Add(parent))
                    break;
                if (parent.Owner is IPackage parentPackage)
                    CollectExpressionsInto(seen, parentPackage, parent);
                current = parent;
            }

            foreach (var expr in seen)
            {
                if (!Expressions.Contains(expr))
                    Expressions.Add(expr);
            }
        }

        static void CollectExpressionsInto(HashSet<UObject> seen, IPackage package, UObject root)
        {
            if (root is UMaterial material)
            {
                foreach (var index in material.Expressions)
                {
                    var expr = index?.Load<UObject>();
                    if (expr != null && MaterialCollector.IsExpression(expr))
                        seen.Add(expr);
                }
            }

            foreach (var tag in root.Properties)
                CollectFromTag(tag.Tag?.GenericValue, seen);

            var editorOnly = LoadEditorOnly(root);
            if (editorOnly != null)
            {
                foreach (var tag in editorOnly.Properties)
                    CollectFromTag(tag.Tag?.GenericValue, seen);
            }

            foreach (var export in package.GetExports())
            {
                if (export == null || export == root || !MaterialCollector.IsExpression(export))
                    continue;
                seen.Add(export);
            }
        }

        static void CollectFromTag(object? value, HashSet<UObject> seen)
        {
            switch (value)
            {
                case FPackageIndex index:
                {
                    UObject? obj = null;
                    try { obj = index.Load<UObject>(); }
                    catch { return; }
                    if (obj == null)
                        return;
                    if (MaterialCollector.IsExpression(obj))
                    {
                        seen.Add(obj);
                        return;
                    }

                    string cls = obj.Class?.Name.Text ?? obj.ExportType;
                    if (cls.Contains("EditorOnlyData", StringComparison.OrdinalIgnoreCase) ||
                        cls.Contains("ExpressionCollection", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var tag in obj.Properties)
                            CollectFromTag(tag.Tag?.GenericValue, seen);
                    }
                    break;
                }
                case FScriptStruct { StructType: FStructFallback fallback }:
                    foreach (var property in fallback.Properties)
                        CollectFromTag(property.Tag?.GenericValue, seen);
                    break;
                case FStructFallback fallback:
                    foreach (var property in fallback.Properties)
                        CollectFromTag(property.Tag?.GenericValue, seen);
                    break;
                case UScriptArray array:
                    foreach (var item in array.Properties)
                        CollectFromTag(item.GenericValue, seen);
                    break;
            }
        }

        static UObject? LoadEditorOnly(UObject root)
        {
            try
            {
                return root.GetOrDefault<FPackageIndex?>("EditorOnlyData")?.Load<UObject>();
            }
            catch
            {
                return null;
            }
        }

        void InspectPackage(IPackage package, UObject root)
        {
            var exports = package.GetExports().Where(o => o != null).ToList();
            var byClass = exports
                .GroupBy(o => o!.Class?.Name.Text ?? o.ExportType)
                .OrderByDescending(o => o.Count())
                .Select(o => $"{o.Key} x{o.Count()}");
            InspectNotes.Add($"Exports: {exports.Count} ({string.Join(", ", byClass.Take(12))})");
            InspectNotes.Add($"Root class: {root.Class?.Name.Text ?? root.ExportType}");
            InspectNotes.Add($"Root properties ({root.Properties.Count}): {SummarizeNames(root.Properties.Select(o => o.Name.Text))}");

            var editorOnly = LoadEditorOnly(root);
            if (editorOnly != null)
            {
                InspectNotes.Add($"EditorOnlyData properties ({editorOnly.Properties.Count}): {SummarizeNames(editorOnly.Properties.Select(o => o.Name.Text))}");
            }
            else
            {
                InspectNotes.Add("EditorOnlyData: none");
            }

            InspectNotes.Add($"Expression objects: {Expressions.Count}");
        }

        void ReadAttributeWires(UObject root)
        {
            foreach (var source in MaterialGraphUtil.PropertySources(root))
            {
                foreach (var attr in MaterialGraphBuilder.AttributeNames)
                {
                    var tag = source.Properties.FirstOrDefault(o => o.Name.Text.Equals(attr, StringComparison.OrdinalIgnoreCase));
                    if (tag == null)
                        continue;
                    foreach (var link in MaterialGraphUtil.ReadLinks(attr, tag.Tag?.GenericValue, tag.PropertyType.Text))
                    {
                        if (!MaterialGraphUtil.HasSource(link.Input) && string.IsNullOrEmpty(link.ConstantText))
                            continue;
                        if (AttributeWires.Any(o => o.Attribute.Equals(attr, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        AttributeWires.Add(new MaterialAttributeWire
                        {
                            Attribute = attr,
                            Input = link.Input,
                            Constant = link.ConstantText
                        });
                    }
                }
            }

            InspectNotes.Add($"Attribute wires: {AttributeWires.Count}" +
                             (AttributeWires.Count == 0 ? "" : " (" + string.Join(", ", AttributeWires.Select(o => o.Attribute)) + ")"));
        }

        void ReadCachedData(UObject root)
        {
            var cached = (root as UMaterialInterface)?.CachedExpressionData ??
                         root.GetOrDefault<FStructFallback?>("CachedExpressionData");
            ReadCached(cached, "CachedExpressionData");

            var editorOnly = LoadEditorOnly(root);
            if (editorOnly != null)
            {
                ReadCached(editorOnly.GetOrDefault<FStructFallback?>("CachedExpressionData"), "EditorOnly CachedExpressionData");
                if (editorOnly is UMaterialInterfaceEditorOnlyData editorData)
                    ReadCached(editorData.CachedExpressionData, "EditorOnly serialized cache");
            }

            if (root is UMaterial material)
            {
                for (int i = 0; i < material.ReferencedTextures.Count; i++)
                {
                    var tex = material.ReferencedTextures[i];
                    if (tex == null)
                        continue;
                    AddTexture(tex.Name, tex.Name, tex.GetPathName(), MaterialGraphUtil.GuessAttribute(tex.Name));
                }
            }
        }

        void ReadCached(FStructFallback? cached, string label)
        {
            if (cached == null)
            {
                InspectNotes.Add($"{label}: none");
                return;
            }

            InspectNotes.Add($"{label} fields: {SummarizeNames(cached.Properties.Select(o => o.Name.Text))}");

            if (!cached.TryGetValue(out FStructFallback parameters, "Parameters"))
                parameters = cached;

            AddNamedScalars(parameters);
            AddNamedVectors(parameters);
            AddNamedSwitches(parameters);
            AddNamedTextures(parameters);
            CollectFunctionsFromCached(cached);

            if (cached.TryGetValue(out FPackageIndex[] referenced, "ReferencedTextures") ||
                parameters.TryGetValue(out referenced, "ReferencedTextures"))
            {
                foreach (var index in referenced)
                    AddTextureFromIndex(index, index.Name);
            }
        }

        void AddNamedScalars(FStructFallback data)
        {
            if (!data.TryGetValue(out float[] values, "ScalarValues"))
                return;
            var names = ParameterNames(data, 0);
            for (int i = 0; i < values.Length; i++)
            {
                string name = i < names.Count ? names[i] : $"Scalar_{i}";
                AddParam(name, "Scalar", values[i].ToString("0.###"));
            }
        }

        void AddNamedVectors(FStructFallback data)
        {
            if (!data.TryGetValue(out FLinearColor[] values, "VectorValues"))
                return;
            var names = ParameterNames(data, 1);
            for (int i = 0; i < values.Length; i++)
            {
                string name = i < names.Count ? names[i] : $"Vector_{i}";
                AddParam(name, "Vector", FormatColor(values[i]));
            }
        }

        void AddNamedSwitches(FStructFallback data)
        {
            if (!data.TryGetValue(out bool[] values, "StaticSwitchValues"))
                return;
            var names = ParameterNames(data, 2);
            if (names.Count == 0)
                names = ParameterNames(data, 6);
            for (int i = 0; i < values.Length; i++)
            {
                string name = i < names.Count ? names[i] : $"Switch_{i}";
                AddParam(name, "Switch", values[i].ToString());
            }
        }

        void AddNamedTextures(FStructFallback data)
        {
            var names = ParameterNames(data, 3);
            if (names.Count == 0)
                names = ParameterNames(data, 2);

            if (data.TryGetValue(out FSoftObjectPath[] soft, "TextureValues"))
            {
                for (int i = 0; i < soft.Length; i++)
                {
                    string name = i < names.Count ? names[i] : TextureName(soft[i]);
                    AddTextureFromSoft(name, soft[i]);
                }
                return;
            }

            if (data.TryGetValue(out FPackageIndex[] indices, "TextureValues"))
            {
                for (int i = 0; i < indices.Length; i++)
                {
                    string name = i < names.Count ? names[i] : indices[i].Name;
                    AddTextureFromIndex(indices[i], name);
                }
                return;
            }

            if (data.TryGetValue(out UTexture[] textures, "TextureValues"))
            {
                for (int i = 0; i < textures.Length; i++)
                {
                    var tex = textures[i];
                    if (tex == null)
                        continue;
                    string name = i < names.Count ? names[i] : tex.Name;
                    AddTexture(name, tex.Name, tex.GetPathName(), MaterialGraphUtil.GuessAttribute(name) ?? MaterialGraphUtil.GuessAttribute(tex.Name));
                }
            }
        }

        static List<string> ParameterNames(FStructFallback data, int entryIndex)
        {
            var names = new List<string>();
            if (!data.TryGetAllValues(out FStructFallback[] entries, "RuntimeEntries") || entryIndex >= entries.Length)
                return names;

            var entry = entries[entryIndex];
            if (entry.TryGetValue(out FMaterialParameterInfo[] infos, "ParameterInfos") ||
                entry.TryGetValue(out infos, "ParameterInfoSet"))
            {
                names.AddRange(infos.Select(o => o.Name.Text));
            }

            return names;
        }

        void ReadInstanceOverrides(UObject root)
        {
            if (root is not UMaterialInstanceConstant mic)
                return;
            foreach (var p in mic.ScalarParameterValues)
                AddParam(p.Name, "Scalar", p.ParameterValue.ToString("0.###"));
            foreach (var p in mic.VectorParameterValues)
                AddParam(p.Name, "Vector", p.ParameterValue?.ToString() ?? "");
            foreach (var p in mic.TextureParameterValues)
            {
                string path = p.ParameterValue?.ResolvedObject?.GetPathName() ?? p.ParameterValue?.ToString() ?? "";
                AddTexture(p.Name, p.ParameterValue?.Name ?? p.Name, path, MaterialGraphUtil.GuessAttribute(p.Name));
            }
        }

        void ReadGetParams(UObject root)
        {
            if (root is not UUnrealMaterial material)
                return;
            try
            {
                var p2 = new CMaterialParams2();
                material.GetParams(p2, EMaterialFormat.AllLayers);
                foreach (var (name, value) in p2.Scalars)
                    AddParam(name, "Scalar", value.ToString("0.###"));
                foreach (var (name, color) in p2.Colors)
                    AddParam(name, "Vector", FormatColor(color));
                foreach (var (name, on) in p2.Switches)
                    AddParam(name, "Switch", on.ToString());
                foreach (var (name, tex) in p2.Textures)
                {
                    if (tex is not UTexture texture)
                        continue;
                    if (name.StartsWith("PM_", StringComparison.Ordinal))
                    {
                        AddTexture(texture.Name, texture.Name, texture.GetPathName(), MaterialGraphUtil.GuessAttribute(name));
                        continue;
                    }
                    AddTexture(name, texture.Name, texture.GetPathName(), MaterialGraphUtil.GuessAttribute(name) ?? MaterialGraphUtil.GuessAttribute(texture.Name));
                }

                var p1 = new CMaterialParams();
                material.GetParams(p1);
                AddResolved(p1.Diffuse, "BaseColor");
                AddResolved(p1.Normal, "Normal");
                AddResolved(p1.Specular, "Specular");
                AddResolved(p1.Emissive, "EmissiveColor");
                AddResolved(p1.Opacity, "Opacity");
                AddResolved(p1.Mask, "OpacityMask");
            }
            catch (Exception ex)
            {
                InspectNotes.Add($"GetParams failed: {ex.Message}");
            }
        }

        void AddResolved(UUnrealMaterial? tex, string attribute)
        {
            if (tex is not UTexture texture)
                return;
            AddTexture(texture.Name, texture.Name, texture.GetPathName(), attribute);
        }

        void ReadFunctions(UObject root)
        {
            foreach (var expr in Expressions)
            {
                var func = expr.GetOrDefault<FPackageIndex?>("MaterialFunction") ?? expr.GetOrDefault<FPackageIndex?>("Function");
                AddFunction(func, func?.Name);
            }

            InspectNotes.Add($"Material functions: {Functions.Count}" +
                             (Functions.Count == 0 ? "" : " (" + string.Join(", ", Functions.Select(o => o.Name)) + ")"));
        }

        void CollectFunctionsFromCached(FStructFallback cached)
        {
            if (cached.TryGetValue(out FStructFallback[] functionInfos, "FunctionInfos"))
            {
                foreach (var info in functionInfos)
                {
                    var index = info.GetOrDefault<FPackageIndex?>("Function");
                    string name = index?.Name ?? info.GetOrDefault<FName>("Name").Text;
                    AddFunction(index, name);
                }
            }

            if (cached.TryGetValue(out FPackageIndex[] functions, "ReferencedFunctions"))
            {
                foreach (var index in functions)
                    AddFunction(index, index.Name);
            }
        }

        void AddFunction(FPackageIndex? index, string? name)
        {
            string clean = MaterialGraphUtil.CleanName(name);
            if (string.IsNullOrEmpty(clean))
                return;
            if (Functions.Any(o => o.Name.Equals(clean, StringComparison.OrdinalIgnoreCase)))
                return;
            Functions.Add(new MaterialFunctionRef { Name = clean, Index = index });
            var added = Functions[^1];
            added.Role = MaterialGraphUtil.GuessRole(clean) ?? "";
            MaterialFunctionInterface.Fill(added);
        }

        void AddParam(string name, string kind, string value)
        {
            string clean = MaterialGraphUtil.CleanName(name);
            if (string.IsNullOrEmpty(clean))
                return;
            var existing = Parameters.FirstOrDefault(o => o.Name.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return;
            Parameters.Add(new MaterialParamRecord
            {
                Name = clean,
                Kind = kind,
                Value = value,
                Attribute = MaterialGraphUtil.GuessAttribute(clean)
            });
        }

        void AddTextureFromIndex(FPackageIndex? index, string name)
        {
            if (index is not { IsNull: false })
                return;
            string path = "";
            string texName = index.Name;
            try
            {
                if (index.Load<UTexture>() is { } tex)
                {
                    texName = tex.Name;
                    path = tex.GetPathName();
                }
            }
            catch
            {
                // texture package may be missing
            }

            AddTexture(name, texName, path, MaterialGraphUtil.GuessAttribute(name) ?? MaterialGraphUtil.GuessAttribute(texName));
        }

        void AddTextureFromSoft(string name, FSoftObjectPath path)
        {
            string texName = TextureName(path);
            string pathText = path.ToString();
            try
            {
                if (path.TryLoad(out UTexture tex))
                {
                    texName = tex.Name;
                    pathText = tex.GetPathName();
                }
            }
            catch
            {
                // texture package may be missing
            }

            AddTexture(name, texName, pathText, MaterialGraphUtil.GuessAttribute(name) ?? MaterialGraphUtil.GuessAttribute(texName));
        }

        void AddTexture(string name, string textureName, string path, string? attribute)
        {
            string clean = MaterialGraphUtil.CleanName(name);
            if (string.IsNullOrEmpty(clean))
                clean = MaterialGraphUtil.CleanName(textureName);
            if (string.IsNullOrEmpty(clean))
                return;
            var existing = Textures.FirstOrDefault(o => o.Name.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (string.IsNullOrEmpty(existing.Attribute) && !string.IsNullOrEmpty(attribute))
                {
                    Textures.Remove(existing);
                    Textures.Add(new MaterialTextureRecord
                    {
                        Name = existing.Name,
                        TextureName = string.IsNullOrEmpty(existing.TextureName) ? textureName : existing.TextureName,
                        Path = string.IsNullOrEmpty(existing.Path) ? path : existing.Path,
                        Attribute = attribute
                    });
                }
                return;
            }

            Textures.Add(new MaterialTextureRecord
            {
                Name = clean,
                TextureName = MaterialGraphUtil.CleanName(textureName),
                Path = path,
                Attribute = attribute
            });
        }

        static string TextureName(FSoftObjectPath path)
        {
            string text = path.AssetPathName.Text;
            if (string.IsNullOrEmpty(text) || text == "None")
                text = path.ToString();
            return MaterialGraphUtil.CleanName(text);
        }

        static string FormatColor(FLinearColor c) => $"({c.R:0.##}, {c.G:0.##}, {c.B:0.##}, {c.A:0.##})";

        static string SummarizeNames(IEnumerable<string> names)
        {
            var list = names.Where(o => !string.IsNullOrEmpty(o) && o != "None").Distinct().ToList();
            if (list.Count == 0)
                return "(none)";
            if (list.Count <= 24)
                return string.Join(", ", list);
            return string.Join(", ", list.Take(24)) + $", +{list.Count - 24} more";
        }

        void ApplyToAsset(Asset asset)
        {
            asset.MaterialExpressions.Clear();
            asset.MaterialExpressions.AddRange(Expressions);
            asset.ReferencedMaterialFunctions.Clear();
            asset.MaterialFunctionRefs.Clear();
            foreach (var func in Functions)
            {
                asset.ReferencedMaterialFunctions.Add(func.Name);
                asset.MaterialFunctionRefs.Add(func);
            }

            AddInfo(asset, "Asset type", asset.MaterialKind.ToString());
            if (Root is UMaterial mat)
            {
                AddInfo(asset, "Blend Mode", mat.BlendMode.ToString());
                AddInfo(asset, "Shading Model", mat.ShadingModel.ToString());
                AddInfo(asset, "Two Sided", mat.TwoSided.ToString());
                AddInfo(asset, "Masked", mat.bIsMasked.ToString());
            }

            if (Root is UMaterialInstance instance)
                AddInfo(asset, "Parent", instance.Parent?.Name ?? Root.GetOrDefault<FPackageIndex?>("Parent")?.Name ?? "None");

            foreach (var note in InspectNotes)
                AddInfo(asset, note.Contains(':') ? note[..note.IndexOf(':')] : note, note.Contains(':') ? note[(note.IndexOf(':') + 1)..].Trim() : note);

            foreach (var param in Parameters)
                AddParamToAsset(asset, param.Name, param.Kind == "Vector" ? "StructProperty" : param.Kind == "Switch" ? "BoolProperty" : "FloatProperty", param.Value);
            foreach (var tex in Textures)
                AddParamToAsset(asset, tex.Name, "ObjectProperty", string.IsNullOrEmpty(tex.Path) ? tex.TextureName : tex.Path);

            asset.MaterialOutputNames.Clear();
            foreach (var wire in AttributeWires)
                asset.MaterialOutputNames.Add(MaterialGraphBuilder.PrettyAttribute(wire.Attribute));
            foreach (var tex in Textures)
            {
                if (tex.Attribute == null)
                    continue;
                string pretty = MaterialGraphBuilder.PrettyAttribute(tex.Attribute);
                if (!asset.MaterialOutputNames.Contains(pretty, StringComparer.OrdinalIgnoreCase))
                    asset.MaterialOutputNames.Add(pretty);
            }
            foreach (var param in Parameters)
            {
                if (param.Attribute == null)
                    continue;
                string pretty = MaterialGraphBuilder.PrettyAttribute(param.Attribute);
                if (!asset.MaterialOutputNames.Contains(pretty, StringComparer.OrdinalIgnoreCase))
                    asset.MaterialOutputNames.Add(pretty);
            }

            if (asset.MaterialKind == MaterialAssetKind.MaterialFunction)
            {
                foreach (var expr in Expressions)
                {
                    string cls = expr.Class?.Name.Text ?? expr.ExportType;
                    if (!cls.Contains("FunctionOutput", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var name = expr.GetOrDefault<FName>("OutputName");
                    asset.MaterialOutputNames.Add(name.IsNone ? expr.Name : name.Text);
                }
            }

            if (asset.MaterialOutputNames.Count == 0)
                asset.MaterialOutputNames.Add(asset.MaterialKind == MaterialAssetKind.MaterialFunction ? "Output" : "Material");
        }

        static void AddInfo(Asset asset, string name, string value) =>
            AddParamToAsset(asset, name, "StrProperty", value);

        static void AddParamToAsset(Asset asset, string name, string type, string value)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "None")
                return;
            if (asset.LoadedProperties.ContainsKey(name))
            {
                if (!string.IsNullOrEmpty(value))
                    asset.LoadedProperties[name].DefaultValue = value;
                return;
            }

            var data = new PropertyData(name, asset.ObjectName, type, default, "", "", "", "", "", "");
            data.DefaultValue = value;
            asset.LoadedProperties.Add(name, data);
        }
    }
}
