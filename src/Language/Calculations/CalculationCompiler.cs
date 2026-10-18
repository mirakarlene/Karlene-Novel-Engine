using System.Globalization;

namespace KNE;

/// <summary>Compiles a bounded arithmetic language into checked Lua operations, never arbitrary Lua code.</summary>
public static class CalculationCompiler
{
    public static bool IsCalculation(ScriptNodeKind kind) => kind is >= ScriptNodeKind.Arithmetic and <= ScriptNodeKind.Formula;
    public static IReadOnlyList<string> Constants { get; } = ["pi", "tau", "e", "phi", "half_pi", "sqrt2"];
    private static readonly Dictionary<string, int> Functions = new(StringComparer.Ordinal)
    { ["abs"] = 1, ["sqrt"] = 1, ["sin"] = 1, ["cos"] = 1, ["tan"] = 1, ["floor"] = 1, ["ceil"] = 1, ["round"] = 1,
      ["pow"] = 2, ["mod"] = 2, ["min"] = 2, ["max"] = 2, ["clamp"] = 3, ["lerp"] = 3, ["remap"] = 5 };

    /// <summary>Generates one assignment and validates all author-controlled tokens and dimensions.</summary>
    public static string Compile(ScriptNodeKind kind, CalculationSettings settings, string result)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsCalculation(kind) || !LuaSource.IsIdentifier(result)) throw new ArgumentException("Укажите переменную результата: латиница, цифры и _, без цифры в начале.");
        string E(string value) => Expression(value);
        string Op(string name, params string[] values) => $"kne_math_op('{name}', {string.Join(", ", values)})";
        string value;
        switch (kind)
        {
            case ScriptNodeKind.Arithmetic:
                string operation = settings.Operator switch { "+" => "add", "-" => "sub", "*" => "mul", "/" => "div", _ => throw new ArgumentException("Арифметика: выберите +, -, * или /.") };
                value = Op(operation, E(settings.A), E(settings.B)); break;
            case ScriptNodeKind.Modulo: value = Op("mod", E(settings.A), E(settings.B)); break;
            case ScriptNodeKind.Increment: value = Op("add", E(settings.A), "1"); break;
            case ScriptNodeKind.Decrement: value = Op("sub", E(settings.A), "1"); break;
            case ScriptNodeKind.Pow: value = Op("pow", E(settings.A), E(settings.B)); break;
            case ScriptNodeKind.Sqrt: value = Op("sqrt", E(settings.A)); break;
            case ScriptNodeKind.Trigonometry:
                if (settings.Function is not ("sin" or "cos" or "tan")) throw new ArgumentException("Тригонометрия: sin, cos или tan.");
                value = Op(settings.Function, E(settings.A)); break;
            case ScriptNodeKind.Absolute: value = Op("abs", E(settings.A)); break;
            case ScriptNodeKind.Clamp: value = Op("clamp", E(settings.A), E(settings.B), E(settings.C)); break;
            case ScriptNodeKind.Round: value = Op("round", E(settings.A)); break;
            case ScriptNodeKind.Floor: value = Op("floor", E(settings.A)); break;
            case ScriptNodeKind.Ceil: value = Op("ceil", E(settings.A)); break;
            case ScriptNodeKind.Lerp: value = Op("lerp", E(settings.A), E(settings.B), E(settings.T)); break;
            case ScriptNodeKind.Remap: value = Op("remap", E(settings.A), E(settings.B), E(settings.C), E(settings.D), E(settings.T)); break;
            case ScriptNodeKind.WeightedRandom:
                if (settings.Weights == null || settings.Weights.Count is < 1 or > 100 || settings.Weights.Any(row => row == null || !double.IsFinite(row.Weight) || row.Weight < 0) || !settings.Weights.Any(row => row.Weight > 0))
                    throw new ArgumentException("Таблица случайности: 1–100 строк, конечные неотрицательные веса, хотя бы один положительный.");
                value = "kne_weighted_number({" + string.Join(",", settings.Weights.Select(row => "{" + E(row.Value) + "," + row.Weight.ToString("R", CultureInfo.InvariantCulture) + "}")) + "})"; break;
            case ScriptNodeKind.Constant:
                if (!Constants.Contains(settings.Constant)) throw new ArgumentException("Неизвестная математическая константа.");
                value = "kne_math_constant('" + settings.Constant + "')"; break;
            case ScriptNodeKind.Formula:
                if (settings.FormulaName == null || settings.FormulaName.Length is < 1 or > 120 || settings.Steps == null || settings.Steps.Count is < 1 or > 64)
                    throw new ArgumentException("Формула: имя до 120 символов и от 1 до 64 шагов.");
                var locals = new Dictionary<string, string>(StringComparer.Ordinal); var lines = new List<string>();
                foreach (var step in settings.Steps)
                {
                    if (step == null || !LuaSource.IsIdentifier(step.Name) || Constants.Contains(step.Name) || step.Name == "state" || locals.ContainsKey(step.Name))
                        throw new ArgumentException("Шагам формулы нужны уникальные имена переменных, отличные от state и констант.");
                    string expression = new Parser(step.Expression, locals).Parse();
                    string variable = "__kne_f" + locals.Count; locals.Add(step.Name, variable); lines.Add("local " + variable + " = " + expression);
                }
                return "do " + string.Join("; ", lines) + "; state[\"" + result + "\"] = __kne_f" + (locals.Count - 1) + " end";
            default: throw new ArgumentException("Неизвестный блок вычислений.");
        }
        return "state[\"" + result + "\"] = " + value;
    }

    public static string Expression(string expression) => new Parser(expression, new Dictionary<string, string>()).Parse();

    private sealed class Parser(string text, IReadOnlyDictionary<string, string> locals)
    {
        private int position, depth, tokens;
        private ArgumentException Invalid() => new("Числовое выражение: ошибка около символа " + (position + 1) + ". Разрешены числа, переменные state, + − * / % ^, скобки и математические функции.");
        private void Space() { while (position < text.Length && char.IsWhiteSpace(text[position])) position++; }
        private bool Take(char value) { Space(); if (position >= text.Length || text[position] != value) return false; position++; if (++tokens > 512) throw Invalid(); return true; }
        public string Parse()
        {
            if (text == null || text.Length is < 1 or > 2048) throw new ArgumentException("Выражение: от 1 до 2048 символов.");
            string value = Add(); Space(); if (position != text.Length) throw Invalid(); return value;
        }
        private string Add()
        {
            string value = Multiply();
            while (true) { if (Take('+')) value = Op("add", value, Multiply()); else if (Take('-')) value = Op("sub", value, Multiply()); else return value; }
        }
        private string Multiply()
        {
            string value = Unary();
            while (true) { if (Take('*')) value = Op("mul", value, Unary()); else if (Take('/')) value = Op("div", value, Unary()); else if (Take('%')) value = Op("mod", value, Unary()); else return value; }
        }
        private string Unary()
        {
            if (++depth > 32) throw Invalid();
            try { if (Take('+')) return Unary(); if (Take('-')) return Op("sub", "0", Unary()); string value = Primary(); return Take('^') ? Op("pow", value, Unary()) : value; }
            finally { depth--; }
        }
        private string Primary()
        {
            if (Take('(')) { string value = Add(); if (!Take(')')) throw Invalid(); return value; }
            Space(); int start = position;
            if (++tokens > 512 || position >= text.Length) throw Invalid();
            if (char.IsAsciiDigit(text[position]) || text[position] == '.')
            {
                while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] == '.')) position++;
                if (position < text.Length && text[position] is 'e' or 'E') { position++; if (position < text.Length && text[position] is '+' or '-') position++; while (position < text.Length && char.IsAsciiDigit(text[position])) position++; }
                if (!double.TryParse(text[start..position], NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) throw Invalid();
                return number.ToString("R", CultureInfo.InvariantCulture);
            }
            while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_')) position++;
            if (start == position) throw Invalid();
            string name = text[start..position];
            if (Take('('))
            {
                if (!Functions.TryGetValue(name, out int count)) throw Invalid();
                var args = new List<string>(); for (int i = 0; i < count; i++) { if (i > 0 && !Take(',')) throw Invalid(); args.Add(Add()); }
                if (!Take(')')) throw Invalid(); return Op(name, args.ToArray());
            }
            if (name == "state")
            {
                if (!Take('.')) throw Invalid(); Space(); start = position;
                while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_')) position++;
                name = text[start..position];
                if (!LuaSource.IsIdentifier(name)) throw Invalid();
                return "kne_math_value(state[\"" + name + "\"], \"" + name + "\")";
            }
            if (!LuaSource.IsIdentifier(name)) throw Invalid();
            if (locals.TryGetValue(name, out var local)) return local;
            if (Constants.Contains(name)) return "kne_math_constant('" + name + "')";
            return "kne_math_value(state[\"" + name + "\"], \"" + name + "\")";
        }
        private static string Op(string name, params string[] args) => "kne_math_op('" + name + "', " + string.Join(", ", args) + ")";
    }
}
