using System;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using System.Collections.Generic;
using System.Linq;
using UEBlueprintGraphViewer.Nodes;

namespace UEBlueprintGraphViewer.Assets
{
    public enum MaterialAssetKind
    {
        Material,
        MaterialInstance,
        MaterialFunction
    }

    public sealed class MaterialFunctionPin
    {
        public required string Name { get; init; }
        public string Type { get; init; } = "Float";
    }

    public sealed class MaterialFunctionRef
    {
        public required string Name { get; init; }
        public FPackageIndex? Index { get; init; }
        public List<MaterialFunctionPin> Inputs { get; } = [];
        public List<MaterialFunctionPin> Outputs { get; } = [];
        public string Role { get; set; } = "";
        public bool UsedFallback { get; set; }
    }

    public static class MaterialCollector
    {
        public static bool TryCollect(Asset asset, IPackage package, string name)
        {
            var root = FindMaterialExport(package, name);
            if (root == null)
                return false;

            asset.IsMaterial = true;
            asset.MaterialKind = KindOf(root);
            asset.MaterialObject = root;
            asset.ObjectName = root.Name;
            asset.MaterialSnapshot = MaterialSnapshot.Capture(asset, package, root);
            return true;
        }

        static UObject? FindMaterialExport(IPackage package, string name)
        {
            int index = package.GetExportIndex(name, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && package.ExportsLazy[index].Value is UObject named && IsMaterialRoot(named))
                return named;

            foreach (var export in package.GetExports())
            {
                if (export != null && IsMaterialRoot(export))
                    return export;
            }

            return null;
        }

        static bool IsMaterialRoot(UObject obj) =>
            obj is UMaterial or UMaterialInstance or UMaterialFunction ||
            ClassLooksLike(obj, "MaterialFunction") ||
            (ClassLooksLike(obj, "Material") && !ClassLooksLike(obj, "MaterialExpression") &&
             !ClassLooksLike(obj, "MaterialInstanceEditor") && !ClassLooksLike(obj, "PhysicalMaterial"));

        static MaterialAssetKind KindOf(UObject obj)
        {
            if (obj is UMaterialFunction || ClassLooksLike(obj, "MaterialFunction"))
                return MaterialAssetKind.MaterialFunction;
            if (obj is UMaterialInstance || ClassLooksLike(obj, "MaterialInstance"))
                return MaterialAssetKind.MaterialInstance;
            return MaterialAssetKind.Material;
        }

        static bool ClassLooksLike(UObject obj, string token)
        {
            string cls = obj.Class?.Name.Text ?? obj.ExportType;
            return cls.Contains(token, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsExpression(UObject obj)
        {
            if (obj is UMaterialExpression)
                return true;
            string cls = obj.Class?.Name.Text ?? obj.ExportType;
            return cls.Contains("MaterialExpression", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class MaterialFunctionInterface
    {
        public static void Fill(MaterialFunctionRef func)
        {
            try
            {
                var obj = func.Index?.Load<UObject>();
                if (obj != null)
                {
                    CollectPorts(obj, func);
                    if (obj.Owner is IPackage package)
                    {
                        foreach (var export in package.GetExports())
                        {
                            if (export == null || export == obj || !MaterialCollector.IsExpression(export))
                                continue;
                            CollectPorts(export, func);
                        }
                    }
                }
            }
            catch
            {
                // function package may be missing from the provider
            }

            if (func.Inputs.Count == 0)
            {
                AddFallbackPorts(func, inputsOnly: func.Outputs.Count > 0);
                func.UsedFallback = true;
            }
            else if (func.Inputs.Count <= 2)
                EnsureCommonInputs(func);
            if (func.Outputs.Count == 0)
                func.Outputs.Add(new MaterialFunctionPin { Name = DefaultOutputName(func), Type = DefaultOutputType(func) });
        }

        static void CollectPorts(UObject obj, MaterialFunctionRef func)
        {
            string cls = obj.Class?.Name.Text ?? obj.ExportType;
            if (cls.Contains("FunctionInput", StringComparison.OrdinalIgnoreCase))
            {
                var name = obj.GetOrDefault<FName>("InputName");
                string pin = name.IsNone ? obj.Name : name.Text;
                pin = MaterialGraphUtil.CleanName(pin);
                if (!IsUsefulPortName(pin))
                    return;
                if (func.Inputs.Any(o => o.Name.Equals(pin, StringComparison.OrdinalIgnoreCase)))
                    return;
                func.Inputs.Add(new MaterialFunctionPin { Name = pin, Type = PinTypeName(obj) });
            }
            else if (cls.Contains("FunctionOutput", StringComparison.OrdinalIgnoreCase))
            {
                var name = obj.GetOrDefault<FName>("OutputName");
                string pin = name.IsNone ? obj.Name : name.Text;
                pin = MaterialGraphUtil.CleanName(pin);
                if (!IsUsefulPortName(pin))
                    return;
                if (func.Outputs.Any(o => o.Name.Equals(pin, StringComparison.OrdinalIgnoreCase)))
                    return;
                func.Outputs.Add(new MaterialFunctionPin { Name = pin, Type = OutputTypeName(pin) });
            }
        }

        static string PinTypeName(UObject obj)
        {
            string type = obj.GetOrDefault<object>("InputType")?.ToString() ?? "";
            if (type.Contains("Texture", StringComparison.OrdinalIgnoreCase))
                return "Texture";
            if (type.Contains("Bool", StringComparison.OrdinalIgnoreCase) || type.Contains("StaticBool", StringComparison.OrdinalIgnoreCase))
                return "Bool";
            if (type.Contains("Vector", StringComparison.OrdinalIgnoreCase))
                return "Vector";
            var name = obj.GetOrDefault<FName>("InputName").Text;
            if (MaterialGraphUtil.GuessRole(name) == "UV")
                return "UV";
            if (MaterialGraphUtil.GuessAttribute(name) is "BaseColor" or "EmissiveColor" or "Normal" or "SubsurfaceColor")
                return "Vector";
            return "Float";
        }

        static string OutputTypeName(string pin)
        {
            if (MaterialGraphUtil.GuessRole(pin) == "UV")
                return "UV";
            if (MaterialGraphUtil.GuessAttribute(pin) is "BaseColor" or "EmissiveColor" or "Normal" or "SubsurfaceColor")
                return "Vector";
            return "Float";
        }

        static string DefaultOutputName(MaterialFunctionRef func) =>
            func.Role switch
            {
                "UV" => "UVs",
                "Diffuse" => "Base Color",
                "Normal" => "Normal",
                "Emissive" => "Emissive Color",
                _ => "Output"
            };

        static string DefaultOutputType(MaterialFunctionRef func) =>
            func.Role switch
            {
                "UV" => "UV",
                "Diffuse" or "Normal" or "Emissive" => "Vector",
                _ => "Float"
            };

        static bool IsUsefulPortName(string pin)
        {
            if (string.IsNullOrEmpty(pin))
                return false;
            if (pin.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase))
                return false;
            if (pin.StartsWith("FunctionInput", StringComparison.OrdinalIgnoreCase) ||
                pin.StartsWith("FunctionOutput", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        static void EnsureCommonInputs(MaterialFunctionRef func)
        {
            void AddIfMissing(string name, string type)
            {
                if (func.Inputs.Any(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return;
                func.Inputs.Add(new MaterialFunctionPin { Name = name, Type = type });
            }

            if (!func.Inputs.Any(o => MaterialGraphUtil.IsUvPinName(o.Name)))
                AddIfMissing("UVs", "UV");

            switch (func.Role)
            {
                case "Diffuse":
                    AddIfMissing("Base Color", "Texture");
                    AddIfMissing("Tint", "Vector");
                    AddIfMissing("Brightness", "Float");
                    AddIfMissing("Contrast", "Float");
                    break;
                case "Normal":
                    AddIfMissing("Normal", "Texture");
                    AddIfMissing("Intensity", "Float");
                    break;
                case "Mask":
                    AddIfMissing("Mask", "Texture");
                    break;
                case "Emissive":
                    AddIfMissing("Emissive", "Texture");
                    AddIfMissing("Intensity", "Float");
                    break;
                case "UV":
                    AddIfMissing("Tiling", "Float");
                    AddIfMissing("Offset", "Float");
                    break;
            }
        }

        static void AddFallbackPorts(MaterialFunctionRef func, bool inputsOnly = false)
        {
            void Out(string name, string type)
            {
                if (!inputsOnly)
                    func.Outputs.Add(new MaterialFunctionPin { Name = name, Type = type });
            }

            switch (func.Role)
            {
                case "UV":
                    func.Inputs.Add(new MaterialFunctionPin { Name = "UVs", Type = "UV" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Tiling", Type = "Float" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Offset", Type = "Float" });
                    Out("UVs", "UV");
                    break;
                case "Diffuse":
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Base Color", Type = "Texture" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Mask", Type = "Texture" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "UVs", Type = "UV" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Tint", Type = "Vector" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Brightness", Type = "Float" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Contrast", Type = "Float" });
                    Out("Base Color", "Vector");
                    break;
                case "Normal":
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Normal", Type = "Texture" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "UVs", Type = "UV" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Intensity", Type = "Float" });
                    Out("Normal", "Vector");
                    break;
                case "Mask":
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Mask", Type = "Texture" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "UVs", Type = "UV" });
                    Out("Roughness", "Float");
                    Out("Metallic", "Float");
                    Out("Ambient Occlusion", "Float");
                    break;
                case "Emissive":
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Emissive", Type = "Texture" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "UVs", Type = "UV" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Intensity", Type = "Float" });
                    Out("Emissive Color", "Vector");
                    break;
                default:
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Texture", Type = "Texture" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "UVs", Type = "UV" });
                    func.Inputs.Add(new MaterialFunctionPin { Name = "Value", Type = "Float" });
                    Out("Output", "Float");
                    break;
            }
        }
    }
}
