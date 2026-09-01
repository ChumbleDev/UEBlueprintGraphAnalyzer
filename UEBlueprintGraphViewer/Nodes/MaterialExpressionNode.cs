using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using UEBlueprintGraphViewer.Engine;
using static UEBlueprintGraphViewer.Engine.EngineBPData;
using static UEBlueprintGraphViewer.Engine.EngineEnums;
using static UEBlueprintGraphViewer.Engine.Utils;

namespace UEBlueprintGraphViewer.Nodes
{
    public class MaterialExpressionNode : BPNode
    {
        public UObject Expression { get; }
        public string ExpressionClass { get; }
        public bool HasEditorPosition { get; }

        public MaterialExpressionNode(UObject expression) : base(PrettyName(expression), null)
        {
            Expression = expression;
            ExpressionClass = expression.Class?.Name.Text ?? expression.ExportType;
            NodeType = ColorKey(ExpressionClass);
            Pure = true;

            if (IsCompactMath(ExpressionClass))
            {
                HeaderHidden = true;
                ShowNameAsBody = true;
            }

            if (TryReadEditorPosition(expression, out float x, out float y))
            {
                HasEditorPosition = true;
                X = x;
                Y = y;
            }

            ApplyParameterTitle(expression);
            MakeOutputPins();
            MakeInputPins();
            AddTypedInputPins();
            TintPin = Output.FirstOrDefault() ?? Input.FirstOrDefault();
        }

        public readonly List<(GraphPin Pin, FExpressionInput Input)> MaterialWires = [];

        public static string PrettyName(UObject expression)
        {
            string type = expression.Class?.Name.Text ?? expression.ExportType;
            type = type.SubstringAfterLast('.');
            if (type.StartsWith("MaterialExpression", StringComparison.Ordinal))
                type = type["MaterialExpression".Length..];
            if (string.IsNullOrEmpty(type))
                type = expression.Name;
            return type;
        }

        static string ColorKey(string className)
        {
            string type = className.SubstringAfterLast('.');
            if (type.Contains("FunctionCall", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("MaterialFunction", StringComparison.OrdinalIgnoreCase))
                return "MaterialFunctionCall";
            if (type.Contains("FunctionInput", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("FunctionOutput", StringComparison.OrdinalIgnoreCase))
                return "MaterialFunctionPort";
            if (type.Contains("Parameter", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("TextureSample", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("Texture", StringComparison.OrdinalIgnoreCase))
                return "MaterialParameter";
            return "MaterialExpression";
        }

        static bool IsCompactMath(string className)
        {
            string type = PrettyClass(className);
            return type is "Add" or "Subtract" or "Multiply" or "Divide" or "Power" or "Lerp" or "LinearInterpolate"
                or "Clamp" or "Min" or "Max" or "Abs" or "Sine" or "Cosine" or "Floor" or "Ceil" or "Frac"
                or "OneMinus" or "Saturate" or "DotProduct" or "CrossProduct" or "Normalize" or "AppendVector"
                or "ComponentMask" or "Desaturation" or "CheapContrast" or "Fresnel" or "If" or "StaticSwitch";
        }

        static string PrettyClass(string className)
        {
            string type = className.SubstringAfterLast('.');
            if (type.StartsWith("MaterialExpression", StringComparison.Ordinal))
                type = type["MaterialExpression".Length..];
            return type;
        }

        void ApplyParameterTitle(UObject expression)
        {
            if (expression is UMaterialExpressionParameter p && !p.ParameterName.IsNone)
            {
                Name = p.ParameterName.Text;
                return;
            }

            var parameterName = expression.GetOrDefault<FName>("ParameterName");
            if (!parameterName.IsNone)
                Name = parameterName.Text;

            var inputName = expression.GetOrDefault<FName>("InputName");
            if (!inputName.IsNone)
                Name = $"{PrettyName(expression)} {inputName.Text}";

            var outputName = expression.GetOrDefault<FName>("OutputName");
            if (!outputName.IsNone)
                Name = $"{PrettyName(expression)} {outputName.Text}";

            if (expression is UMaterialExpressionTextureBase tex && tex.Texture != null)
                Name = string.IsNullOrEmpty(Name) || Name == PrettyName(expression)
                    ? tex.Texture.Name
                    : Name;
        }

        void MakeOutputPins()
        {
            var outputs = ReadNamedOutputs(Expression);
            if (outputs.Count == 0)
            {
                string cls = PrettyClass(ExpressionClass);
                outputs = cls.Contains("TextureSample", StringComparison.OrdinalIgnoreCase)
                    ? ["RGB", "R", "G", "B", "A"]
                    : ["Output"];
            }

            var pinType = InferOutputType(ExpressionClass);
            foreach (var output in outputs)
            {
                var pin = new GraphPin(output, EEdGraphPinDirection.EGPD_Output, pinType);
                if (outputs.Count == 1)
                    pin.IsNameHidden = HeaderHidden;
                AddOutputPin(pin);
            }

            ApplyConstantPreview();
        }

        void MakeInputPins()
        {
            foreach (var input in MaterialGraphUtil.ReadInputs(Expression))
                AddMaterialInputPin(input.Name, input.PinType, input.Input, input.ConstantText);
        }

        void AddTypedInputPins()
        {
            foreach (var name in MaterialGraphUtil.TypedInputNames)
            {
                if (Input.Any(o => o.PinName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                FExpressionInput? input = null;
                try
                {
                    input = Expression.GetOrDefault<FExpressionInput?>(name);
                }
                catch
                {
                    continue;
                }
                if (input == null || !MaterialGraphUtil.HasSource(input))
                    continue;
                AddMaterialInputPin(name, MakePinType(PinType.Float), MaterialGraphUtil.Normalize(input), "");
            }

            foreach (var tag in Expression.Properties)
            {
                if (tag.Tag?.GenericValue is not FPackageIndex index || index.IsNull)
                    continue;
                string name = tag.Name.Text;
                if (name is "Material" or "Function" or "Texture" or "MaterialFunction" or "Resource" or "Font")
                    continue;
                if (Input.Any(o => o.PinName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var synthetic = new FExpressionInput { Expression = index, ExpressionName = new FName(index.Name) };
                AddMaterialInputPin(name, MakePinType(PinType.Float), synthetic, "");
            }
        }

        void AddMaterialInputPin(string name, GraphPinType pinType, FExpressionInput? input, string constant)
        {
            var pin = new GraphPin(name, EEdGraphPinDirection.EGPD_Input, pinType);
            if (!string.IsNullOrEmpty(constant) && !MaterialGraphUtil.HasSource(input))
                pin.Value = constant;
            if (HeaderHidden)
                pin.IsNameHidden = true;
            AddInputPin(pin);
            if (input != null && MaterialGraphUtil.HasSource(input))
                MaterialWires.Add((pin, MaterialGraphUtil.Normalize(input)));
        }

        void ApplyConstantPreview()
        {
            string cls = PrettyClass(ExpressionClass);
            if (cls is "Constant" or "ScalarParameter")
            {
                float value = Expression is UMaterialExpressionScalarParameter s
                    ? s.DefaultValue
                    : Expression.GetOrDefault<float>("R");
                if (Output.Count > 0 && string.IsNullOrEmpty(Output[0].Value))
                    Output[0].Value = value.ToString("0.###");
            }
            else if (cls is "Constant3Vector" or "Constant4Vector" or "VectorParameter")
            {
                var color = Expression is UMaterialExpressionVectorParameter v
                    ? v.DefaultValue
                    : Expression.GetOrDefault<FLinearColor>("Constant");
                if (Output.Count > 0 && string.IsNullOrEmpty(Output[0].Value))
                    Output[0].Value = FormatColor(color);
            }
        }

        static string FormatColor(FLinearColor c) => $"({c.R:0.##}, {c.G:0.##}, {c.B:0.##})";

        static List<string> ReadNamedOutputs(UObject expression)
        {
            var names = new List<string>();
            var tag = expression.Properties.FirstOrDefault(o => o.Name.Text is "Outputs");
            if (tag?.Tag?.GenericValue is not UScriptArray array)
                return names;

            foreach (var item in array.Properties)
            {
                string? name = item.GenericValue switch
                {
                    FScriptStruct { StructType: FStructFallback fb } => fb.GetOrDefault<FName>("OutputName").Text,
                    FStructFallback fb => fb.GetOrDefault<FName>("OutputName").Text,
                    _ => null
                };
                if (!string.IsNullOrEmpty(name) && name != "None")
                    names.Add(name);
            }

            return names;
        }

        static GraphPinType InferOutputType(string className)
        {
            string type = PrettyClass(className);
            if (type.Contains("Bool", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("StaticBool", StringComparison.OrdinalIgnoreCase))
                return MakePinType(PinType.Bool);
            if (type.Contains("Texture", StringComparison.OrdinalIgnoreCase) &&
                !type.Contains("Sample", StringComparison.OrdinalIgnoreCase))
                return MakePinType(PinType.Object);
            if (type.Contains("Scalar", StringComparison.OrdinalIgnoreCase) || type == "Constant")
                return MakePinType(PinType.Float);
            return MakePinType(PinType.Float);
        }

        static bool TryReadEditorPosition(UObject expression, out float x, out float y)
        {
            x = expression.GetOrDefault("MaterialExpressionEditorX", expression.GetOrDefault("EditorX", int.MinValue));
            y = expression.GetOrDefault("MaterialExpressionEditorY", expression.GetOrDefault("EditorY", int.MinValue));
            if (x == int.MinValue || y == int.MinValue)
            {
                x = 0;
                y = 0;
                return false;
            }

            return x != 0 || y != 0;
        }

        public static bool IsFunctionLeftover(BPNode node)
        {
            if (node is not MaterialExpressionNode expr)
                return false;
            string type = PrettyClass(expr.ExpressionClass);
            return type.Contains("FunctionInput", StringComparison.OrdinalIgnoreCase) ||
                   type.Contains("FunctionOutput", StringComparison.OrdinalIgnoreCase) ||
                   type.Contains("FunctionCall", StringComparison.OrdinalIgnoreCase) ||
                   type.Contains("MaterialFunction", StringComparison.OrdinalIgnoreCase);
        }
    }

    public class MaterialResultNode : BPNode
    {
        public MaterialResultNode(string name) : base(name, null)
        {
            NodeType = "MaterialResult";
            HeaderCenter = true;
            Pure = true;
        }
    }

    public class MaterialFunctionCallNode : BPNode
    {
        public string Role { get; }

        public MaterialFunctionCallNode(string name, string role = "") : base(name, null)
        {
            Role = role;
            NodeType = "MaterialFunctionCall";
            Pure = true;
        }
    }

    public class MaterialParameterNode : BPNode
    {
        public string? Attribute { get; }

        public MaterialParameterNode(string name, string value, GraphPinType pinType, string? attribute, string nodeType = "MaterialParameter")
            : base(name, null)
        {
            Attribute = attribute;
            NodeType = nodeType;
            Pure = true;
            var pin = new GraphPin("Output", EEdGraphPinDirection.EGPD_Output, pinType);
            if (!string.IsNullOrEmpty(value))
                pin.Value = value;
            AddOutputPin(pin);
            TintPin = pin;
        }
    }
}
