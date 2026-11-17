using System.Globalization;
using System.Text.Json;

namespace KNE;

/// <summary>Compiles typed logic operands; arbitrary Lua expressions are never interpolated.</summary>
public static class LogicCompiler
{
    public static bool IsLogic(ScriptNodeKind kind) => kind >= ScriptNodeKind.CreateList && kind <= ScriptNodeKind.ResetVariable;
    public static string Label(ScriptNodeKind kind) => kind switch
    {
        ScriptNodeKind.CreateList => "Создать массив", ScriptNodeKind.DeleteList => "Удалить массив", ScriptNodeKind.ClearList => "Очистить массив",
        ScriptNodeKind.ListAdd => "Добавить элемент", ScriptNodeKind.ListRemove => "Удалить элемент", ScriptNodeKind.ListRemoveAt => "Удалить по индексу",
        ScriptNodeKind.ListInsert => "Вставить по индексу", ScriptNodeKind.ListSet => "Заменить по индексу", ScriptNodeKind.ListLength => "Длина массива",
        ScriptNodeKind.ListGet => "Получить по индексу", ScriptNodeKind.ListContains => "Проверить наличие", ScriptNodeKind.ListIndexOf => "Найти индекс",
        ScriptNodeKind.ListIsEmpty => "Проверить пустоту", ScriptNodeKind.ListShuffle => "Перемешать", ScriptNodeKind.ListSort => "Отсортировать",
        ScriptNodeKind.ListMerge => "Объединить массивы", ScriptNodeKind.ListCopy => "Скопировать массив", ScriptNodeKind.ListExists => "Существует ли массив",
        ScriptNodeKind.ListRandom => "Случайный выбор из списка", ScriptNodeKind.CompoundCondition => "Составные условия",
        ScriptNodeKind.StringConcat => "Конкатенация", ScriptNodeKind.StringLength => "Длина строки", ScriptNodeKind.StringContains => "Поиск подстроки",
        ScriptNodeKind.StringReplace => "Замена значения", ScriptNodeKind.StringUpper => "Верхний регистр", ScriptNodeKind.StringLower => "Нижний регистр",
        ScriptNodeKind.ConvertType => "Приведение типов", ScriptNodeKind.CopyVariable => "Копировать переменную", ScriptNodeKind.ResetVariable => "Очистить переменную", _ => throw new ArgumentException("Неизвестный блок логики.")
    };
    private static string Quote(string value) => VisualScript.Quote(value);
    private static string Key(string value) => LuaSource.IsIdentifier(value) && value.Length <= 80 ? value : throw new ArgumentException("Имя: латинские буквы, цифры и _, без цифры в начале.");
    public static string Operand(LogicOperand operand)
    {
        if (operand == null || operand.Value == null) throw new ArgumentException("Не задано значение операнда.");
        return operand.Kind switch
        {
            "string" => Quote(operand.Value), "null" => "nil", "variable" => "state[" + Quote(Key(operand.Value)) + "]",
            "list" => Key(operand.Value),
            "boolean" => operand.Value is "true" or "false" ? operand.Value : throw new ArgumentException("Bool: true или false."),
            "number" => double.TryParse(operand.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number.ToString("R", CultureInfo.InvariantCulture) : throw new ArgumentException("Требуется конечное число."),
            _ => throw new ArgumentException("Неизвестный тип операнда.")
        };
    }
    public static string CompileCondition(LogicCondition condition) => Condition(condition, 0);
    private static string Condition(LogicCondition condition, int depth)
    {
        if (condition == null || condition.Children == null || depth > 24) throw new ArgumentException("Некорректная группа условий или слишком глубокая вложенность.");
        if (condition.Operator == "compare")
        {
            if (condition.Comparison is not ("==" or "~=" or "<" or ">" or "<=" or ">=")) throw new ArgumentException("Неизвестное сравнение.");
            return "kne_logic_compare(" + Quote(condition.Comparison) + "," + Operand(condition.A) + "," + Operand(condition.B) + ")";
        }
        if (condition.Operator == "not" && condition.Children.Count == 1) return "(not " + Condition(condition.Children[0], depth + 1) + ")";
        if (condition.Operator is not ("and" or "or") || condition.Children.Count == 0) throw new ArgumentException("AND/OR требуют условия, NOT — ровно одно условие.");
        return "(" + string.Join(" " + condition.Operator + " ", condition.Children.Select(child => Condition(child, depth + 1))) + ")";
    }
    private static string Initial(string source)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(source);
            if (json.ValueKind != JsonValueKind.Array) throw new ArgumentException("Начальные значения — JSON-массив.");
            EngineSettings.ValidateState(json, 0);
            return Literal(json);
        }
        catch (Exception e) when (e is JsonException or System.IO.InvalidDataException) { throw new ArgumentException("Начальные значения: " + e.Message, e); }
    }
    private static string Literal(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Array => "{" + string.Join(",", e.EnumerateArray().Select(Literal)) + "}",
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().Select(p => "[" + Quote(p.Name) + "]=" + Literal(p.Value))) + "}",
        JsonValueKind.String => Quote(e.GetString()!), JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Number => e.GetRawText(), _ => throw new ArgumentException("Неизвестное значение.")
    };
    public static string Compile(ScriptNodeKind kind, LogicSettings s, string resultVariable)
    {
        if (s == null) throw new ArgumentException("Не заданы настройки логики.");
        if (kind == ScriptNodeKind.CompoundCondition) return CompileCondition(s.Condition);
        string Destination() => "state[" + Quote(Key(resultVariable)) + "] = ";
        string List() => Quote(Key(s.List)); string Other() => Quote(Key(s.OtherList));
        string A() => Operand(s.A); string B() => Operand(s.B); string C() => Operand(s.C); string I() => Operand(s.Index);
        return kind switch
        {
            ScriptNodeKind.CreateList => "create_list(" + List() + "," + Initial(s.InitialValues) + ")",
            ScriptNodeKind.DeleteList => "delete_list(" + List() + ")", ScriptNodeKind.ClearList => "clear_list(" + List() + ")",
            ScriptNodeKind.ListAdd => "list_add(" + List() + "," + A() + ")", ScriptNodeKind.ListRemove => "list_remove(" + List() + "," + A() + ")",
            ScriptNodeKind.ListRemoveAt => "list_remove_at(" + List() + "," + I() + ")", ScriptNodeKind.ListInsert => "list_insert(" + List() + "," + I() + "," + A() + ")",
            ScriptNodeKind.ListSet => "list_set(" + List() + "," + I() + "," + A() + ")",
            ScriptNodeKind.ListLength => Destination() + "list_length(" + List() + ")", ScriptNodeKind.ListGet => Destination() + "list_get(" + List() + "," + I() + ")",
            ScriptNodeKind.ListContains => Destination() + "list_contains(" + List() + "," + A() + ")", ScriptNodeKind.ListIndexOf => Destination() + "list_index_of(" + List() + "," + A() + ")",
            ScriptNodeKind.ListIsEmpty => Destination() + "list_is_empty(" + List() + ")", ScriptNodeKind.ListExists => Destination() + "list_exists(" + List() + ")",
            ScriptNodeKind.ListShuffle => "list_shuffle(" + List() + ")", ScriptNodeKind.ListSort => "list_sort(" + List() + ")",
            ScriptNodeKind.ListMerge => "list_merge(" + List() + "," + Other() + ")", ScriptNodeKind.ListCopy => "list_copy(" + List() + "," + Other() + ")",
            ScriptNodeKind.ListRandom => Destination() + "list_random(" + List() + ")",
            ScriptNodeKind.StringConcat => Destination() + "string_concat(" + A() + "," + B() + ")", ScriptNodeKind.StringLength => Destination() + "string_length(" + A() + ")",
            ScriptNodeKind.StringContains => Destination() + "string_contains(" + A() + "," + B() + ")", ScriptNodeKind.StringReplace => Destination() + "string_replace(" + A() + "," + B() + "," + C() + ")",
            ScriptNodeKind.StringUpper => Destination() + "string_upper(" + A() + ")", ScriptNodeKind.StringLower => Destination() + "string_lower(" + A() + ")",
            ScriptNodeKind.ConvertType => Destination() + (s.Conversion switch { "number" => "to_number", "string" => "to_string", "boolean" => "to_bool", _ => throw new ArgumentException("Выберите число, строку или bool.") }) + "(" + A() + ")",
            ScriptNodeKind.CopyVariable => s.A is { Kind: "variable" } ? "copy_variable(" + Quote(Key(resultVariable)) + "," + Quote(Key(s.A.Value)) + ")" : throw new ArgumentException("Источник копирования — переменная state."),
            ScriptNodeKind.ResetVariable => "reset_variable(" + Quote(Key(resultVariable)) + ")", _ => throw new ArgumentException("Неизвестный блок логики.")
        };
    }
}
