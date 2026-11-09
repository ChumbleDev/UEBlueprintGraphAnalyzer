using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports.Material;

namespace UEBlueprintGraphViewer.Nodes
{
    public enum PreshaderKind
    {
        Constant,
        Parameter,
        Add,
        Subtract,
        Multiply,
        Divide,
        Fmod,
        Min,
        Max,
        Clamp,
        Sin,
        Cos,
        Tan,
        Asin,
        Acos,
        Atan,
        Atan2,
        Dot,
        Cross,
        Sqrt,
        Length,
        Saturate,
        Abs,
        Floor,
        Ceil,
        Round,
        Truncate,
        Sign,
        Frac,
        Log2,
        Log10,
        Swizzle,
        Append,
        TextureSize,
        TexelSize,
        Normalize,
        Unknown
    }

    public sealed class PreshaderExpr
    {
        public required PreshaderKind Kind { get; init; }
        public string Name { get; init; } = "";
        public string Constant { get; init; } = "";
        public string? ParameterName { get; init; }
        public List<PreshaderExpr> Args { get; } = [];
    }

    public sealed class PreshaderProgram
    {
        public required string Label { get; init; }
        public PreshaderExpr? Root { get; init; }
        public HashSet<string> ParameterNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? GuessedAttribute { get; set; }
    }

    public static class MaterialPreshaderDecoder
    {
        public static List<PreshaderProgram> Decode(FUniformExpressionSet set)
        {
            var programs = new List<PreshaderProgram>();
            byte[] data = set.UniformPreshaderData?.Data ?? [];
            if (data.Length == 0)
                return programs;

            bool ue5 = set.UniformPreshaders.Length > 0 || set.UniformNumericParameters.Length > 0;
            var numeric = ParameterNames(set, ue5);
            var scalars = set.UniformScalarParameters.Select(ParameterName).ToList();
            var vectors = set.UniformVectorParameters.Select(ParameterName).ToList();

            void Add(FMaterialUniformPreshaderHeader header, string label, bool scalarSlot)
            {
                if (header.OpcodeSize == 0 || header.OpcodeOffset + header.OpcodeSize > data.Length)
                    return;
                var slice = data.AsSpan((int)header.OpcodeOffset, (int)header.OpcodeSize);
                var program = ReadProgram(slice, label, ue5, numeric, scalars, vectors);
                if (program.Root == null)
                    return;
                program.GuessedAttribute = GuessAttribute(program);
                if (scalarSlot && program.GuessedAttribute == null)
                    program.GuessedAttribute = MaterialGraphUtil.GuessAttribute(label);
                programs.Add(program);
            }

            if (ue5)
            {
                for (int i = 0; i < set.UniformPreshaders.Length; i++)
                    Add(set.UniformPreshaders[i], $"Uniform {i}", false);
            }
            else
            {
                for (int i = 0; i < set.UniformVectorPreshaders.Length; i++)
                    Add(set.UniformVectorPreshaders[i], $"Vector {i}", false);
                for (int i = 0; i < set.UniformScalarPreshaders.Length; i++)
                    Add(set.UniformScalarPreshaders[i], $"Scalar {i}", true);
            }

            return programs;
        }

        static PreshaderProgram ReadProgram(ReadOnlySpan<byte> data, string label, bool ue5, List<string> numeric, List<string> scalars, List<string> vectors)
        {
            var program = new PreshaderProgram { Label = label };
            try
            {
                var reader = new OpcodeReader(data);
                var stack = new Stack<PreshaderExpr>();
                while (!reader.Done)
                {
                    byte opcode = reader.ReadByte();
                    var expr = ReadOpcode(opcode, reader, ue5, numeric, scalars, vectors, stack);
                    if (expr == null)
                        continue;
                    CollectParameters(expr, program.ParameterNames);
                    stack.Push(expr);
                }

                program = new PreshaderProgram { Label = label, Root = stack.Count > 0 ? stack.Pop() : null };
                if (program.Root != null)
                    CollectParameters(program.Root, program.ParameterNames);
            }
            catch
            {
                return new PreshaderProgram { Label = label };
            }

            return program;
        }

        static PreshaderExpr? ReadOpcode(byte opcode, OpcodeReader reader, bool ue5, List<string> numeric, List<string> scalars, List<string> vectors, Stack<PreshaderExpr> stack)
        {
            if (opcode == 0)
                return null;
            if (opcode == 1)
                return new PreshaderExpr { Kind = PreshaderKind.Constant, Name = "0", Constant = "0" };
            if (opcode == 2)
                return ReadConstant(reader, ue5);
            if (opcode == 3)
            {
                ushort index = reader.ReadUInt16();
                string name = ue5 ? NameAt(numeric, index) : NameAt(scalars, index);
                return new PreshaderExpr { Kind = PreshaderKind.Parameter, Name = name, ParameterName = name };
            }
            if (!ue5 && opcode == 4)
            {
                ushort index = reader.ReadUInt16();
                string name = NameAt(vectors, index);
                return new PreshaderExpr { Kind = PreshaderKind.Parameter, Name = name, ParameterName = name };
            }

            int mapped = ue5 ? opcode + 1 : opcode;
            return mapped switch
            {
                5 => Binary(stack, PreshaderKind.Add, "+"),
                6 => Binary(stack, PreshaderKind.Subtract, "-"),
                7 => Binary(stack, PreshaderKind.Multiply, "×"),
                8 => Binary(stack, PreshaderKind.Divide, "÷"),
                9 => Binary(stack, PreshaderKind.Fmod, "Fmod"),
                10 => Binary(stack, PreshaderKind.Min, "Min"),
                11 => Binary(stack, PreshaderKind.Max, "Max"),
                12 => Ternary(stack, PreshaderKind.Clamp, "Clamp"),
                13 => Unary(stack, PreshaderKind.Sin, "Sine"),
                14 => Unary(stack, PreshaderKind.Cos, "Cosine"),
                15 => Unary(stack, PreshaderKind.Tan, "Tangent"),
                16 => Unary(stack, PreshaderKind.Asin, "Arcsine"),
                17 => Unary(stack, PreshaderKind.Acos, "Arccosine"),
                18 => Unary(stack, PreshaderKind.Atan, "Arctangent"),
                19 => Binary(stack, PreshaderKind.Atan2, "Atan2"),
                20 => Binary(stack, PreshaderKind.Dot, "Dot"),
                21 => Binary(stack, PreshaderKind.Cross, "Cross"),
                22 => Unary(stack, PreshaderKind.Sqrt, "Sqrt"),
                23 => Unary(stack, PreshaderKind.Length, "Length"),
                24 => Unary(stack, PreshaderKind.Saturate, "Saturate"),
                25 => Unary(stack, PreshaderKind.Abs, "Abs"),
                26 => Unary(stack, PreshaderKind.Floor, "Floor"),
                27 => Unary(stack, PreshaderKind.Ceil, "Ceil"),
                28 => Unary(stack, PreshaderKind.Round, "Round"),
                29 => Unary(stack, PreshaderKind.Truncate, "Truncate"),
                30 => Unary(stack, PreshaderKind.Sign, "Sign"),
                31 or 32 => Unary(stack, PreshaderKind.Frac, "Frac"),
                33 => Unary(stack, PreshaderKind.Log2, "Log2"),
                34 => Unary(stack, PreshaderKind.Log10, "Log10"),
                35 => ReadSwizzle(reader, stack),
                36 => ReadAppend(reader, stack),
                37 => ReadTextureQuery(reader, PreshaderKind.TextureSize, "TextureSize"),
                38 => ReadTextureQuery(reader, PreshaderKind.TexelSize, "TexelSize"),
                39 => SkipExternal(reader, 28, "External UV Scale"),
                40 => SkipExternal(reader, 28, "External UV Offset"),
                41 => SkipExternal(reader, 16, "Runtime VT"),
                _ => throw new InvalidDataException($"Unknown preshader opcode {opcode}")
            };
        }

        static PreshaderExpr ReadConstant(OpcodeReader reader, bool ue5)
        {
            if (!ue5)
            {
                float r = reader.ReadFloat();
                float g = reader.ReadFloat();
                float b = reader.ReadFloat();
                float a = reader.ReadFloat();
                return ConstantExpr(FormatVector(r, g, b, a));
            }

            byte type = reader.ReadByte();
            int count = type switch
            {
                1 or 5 or 9 or 13 or 17 => 1,
                2 or 6 or 10 or 14 or 18 => 2,
                3 or 7 or 11 or 15 or 19 => 3,
                4 or 8 or 12 or 16 or 20 => 4,
                _ => 0
            };
            bool isDouble = type is >= 5 and <= 8 or >= 17 and <= 20;
            var values = new float[Math.Max(count, 1)];
            for (int i = 0; i < count; i++)
                values[i] = isDouble ? (float)reader.ReadDouble() : reader.ReadFloat();
            if (count == 0)
                return ConstantExpr("0");
            if (count == 1)
                return ConstantExpr(values[0].ToString("0.###"));
            return ConstantExpr("(" + string.Join(", ", values.Select(o => o.ToString("0.##"))) + ")");
        }

        static PreshaderExpr ReadSwizzle(OpcodeReader reader, Stack<PreshaderExpr> stack)
        {
            int num = reader.ReadByte();
            var mask = new char[4];
            int filled = 0;
            for (int i = 0; i < 4; i++)
            {
                byte component = reader.ReadByte();
                if (i < num && component < 4)
                    mask[filled++] = "xyzw"[component];
            }

            string name = filled == 0 ? "Mask" : "Mask " + new string(mask, 0, filled).ToUpperInvariant();
            return Unary(stack, PreshaderKind.Swizzle, name);
        }

        static PreshaderExpr ReadAppend(OpcodeReader reader, Stack<PreshaderExpr> stack)
        {
            reader.ReadByte();
            return Binary(stack, PreshaderKind.Append, "Append");
        }

        static PreshaderExpr ReadTextureQuery(OpcodeReader reader, PreshaderKind kind, string name)
        {
            int index = reader.ReadInt32();
            return new PreshaderExpr { Kind = kind, Name = $"{name} {index}" };
        }

        static PreshaderExpr SkipExternal(OpcodeReader reader, int extraBytes, string name)
        {
            reader.Skip(extraBytes);
            return new PreshaderExpr { Kind = PreshaderKind.Unknown, Name = name };
        }

        static PreshaderExpr Unary(Stack<PreshaderExpr> stack, PreshaderKind kind, string name)
        {
            var node = new PreshaderExpr { Kind = kind, Name = name };
            if (stack.Count > 0)
                node.Args.Add(stack.Pop());
            return node;
        }

        static PreshaderExpr Binary(Stack<PreshaderExpr> stack, PreshaderKind kind, string name)
        {
            var node = new PreshaderExpr { Kind = kind, Name = name };
            var b = stack.Count > 0 ? stack.Pop() : ConstantExpr("0");
            var a = stack.Count > 0 ? stack.Pop() : ConstantExpr("0");
            node.Args.Add(a);
            node.Args.Add(b);
            return node;
        }

        static PreshaderExpr Ternary(Stack<PreshaderExpr> stack, PreshaderKind kind, string name)
        {
            var node = new PreshaderExpr { Kind = kind, Name = name };
            var c = stack.Count > 0 ? stack.Pop() : ConstantExpr("1");
            var b = stack.Count > 0 ? stack.Pop() : ConstantExpr("0");
            var a = stack.Count > 0 ? stack.Pop() : ConstantExpr("0");
            node.Args.Add(a);
            node.Args.Add(b);
            node.Args.Add(c);
            return node;
        }

        static PreshaderExpr ConstantExpr(string value) =>
            new() { Kind = PreshaderKind.Constant, Name = value, Constant = value };

        static string FormatVector(float r, float g, float b, float a)
        {
            if (g == 0 && b == 0 && a is 0 or 1 && r != 0 && r == g)
                return r.ToString("0.###");
            if (a is 0 or 1 && g == 0 && b == 0)
                return r.ToString("0.###");
            if (a is 0 or 1)
                return $"({r:0.##}, {g:0.##}, {b:0.##})";
            return $"({r:0.##}, {g:0.##}, {b:0.##}, {a:0.##})";
        }

        static List<string> ParameterNames(FUniformExpressionSet set, bool ue5)
        {
            if (!ue5)
                return [];
            return set.UniformNumericParameters.Select(o =>
            {
                string name = o.ParameterInfo?.Name.Text ?? "";
                return string.IsNullOrEmpty(name) || name == "None" ? $"Parameter" : name;
            }).ToList();
        }

        static string ParameterName(FMaterialBaseParameterInfo info)
        {
            string name = info.ParameterInfo?.Name.Text ?? info.ParameterName ?? "";
            return string.IsNullOrEmpty(name) || name == "None" ? "Parameter" : name;
        }

        static string NameAt(List<string> names, int index)
        {
            if (index >= 0 && index < names.Count && !string.IsNullOrEmpty(names[index]))
                return names[index];
            return $"Parameter {index}";
        }

        static void CollectParameters(PreshaderExpr expr, HashSet<string> names)
        {
            if (!string.IsNullOrEmpty(expr.ParameterName))
                names.Add(expr.ParameterName);
            foreach (var arg in expr.Args)
                CollectParameters(arg, names);
        }

        static string? GuessAttribute(PreshaderProgram program)
        {
            var guessed = program.ParameterNames
                .Select(MaterialGraphUtil.GuessAttribute)
                .Where(o => o != null)
                .Distinct()
                .ToList();
            return guessed.Count == 1 ? guessed[0] : null;
        }

        sealed class OpcodeReader(ReadOnlySpan<byte> data)
        {
            readonly byte[] _data = data.ToArray();
            int _pos;

            public bool Done => _pos >= _data.Length;

            public byte ReadByte()
            {
                Ensure(1);
                return _data[_pos++];
            }

            public ushort ReadUInt16()
            {
                Ensure(2);
                ushort value = BitConverter.ToUInt16(_data, _pos);
                _pos += 2;
                return value;
            }

            public int ReadInt32()
            {
                Ensure(4);
                int value = BitConverter.ToInt32(_data, _pos);
                _pos += 4;
                return value;
            }

            public float ReadFloat()
            {
                Ensure(4);
                float value = BitConverter.ToSingle(_data, _pos);
                _pos += 4;
                return value;
            }

            public double ReadDouble()
            {
                Ensure(8);
                double value = BitConverter.ToDouble(_data, _pos);
                _pos += 8;
                return value;
            }

            public void Skip(int count)
            {
                Ensure(count);
                _pos += count;
            }

            void Ensure(int count)
            {
                if (_pos + count > _data.Length)
                    throw new EndOfStreamException();
            }
        }
    }
}
