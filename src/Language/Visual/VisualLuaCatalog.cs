namespace KNE;

internal enum LuaBlockShape { Statement, If, Body, Loop, Function, Continue }
internal sealed record LuaBlockSpec(string Id, BlockGroup Group, string Title, string Code, LuaBlockShape Shape, string? Symbol)
{
    internal bool HasBody => Shape is LuaBlockShape.If or LuaBlockShape.Body or LuaBlockShape.Loop or LuaBlockShape.Function;
    internal BlockDefinition Entry => new(ScriptNodeKind.LuaBlock, Group.Id, Title,
        Shape switch
        {
            LuaBlockShape.Continue => "goto continue_label",
            LuaBlockShape.If => Code + "\n    -- условие истинно\nelse\n    -- условие ложно\nend",
            _ when HasBody => Code + "\n    -- тело\nend",
            _ => Code
        }, Group.Color, LuaOperation: Id);
}

internal static class VisualLuaCatalog
{
    internal static readonly IReadOnlyList<BlockGroup> Groups =
    [
        new("lua_flow", "Поток выполнения", "#FFFFFF"),
        new("lua_raw", "Исходный код", "#000000"),
        new("lua_variables", "Переменные", "#2563A6"),
        new("lua_tables", "Таблицы", "#228052"),
        new("lua_functions", "Функции и корутины", "#E3B52D"),
        new("lua_operators", "Операторы", "#C65C16"),
        new("lua_types", "Типы данных", "#8246B3"),
        new("lua_libraries", "Встроенные функции", "#BA3846"),
        new("lua_meta", "Метаметоды", "#8B5A35"),
    ];

    internal static readonly IReadOnlyDictionary<string, LuaBlockSpec> Blocks = Build();
    internal static readonly IReadOnlyDictionary<string, string> Symbols = Blocks.Values.Where(block => block.Symbol != null)
        .ToDictionary(block => block.Symbol!, block => block.Id, StringComparer.Ordinal);
    internal static IEnumerable<BlockDefinition> Definitions => Blocks.Values.Select(block => block.Entry)
        .Append(new(ScriptNodeKind.Comment, "lua_raw", "Комментарий", "-- Комментарий", "#000000"))
        .Prepend(new(ScriptNodeKind.Start, "lua_flow", "Начало", "-- Начало Lua-файла", "#FFFFFF"));

    private static Dictionary<string, LuaBlockSpec> Build()
    {
        var result = new Dictionary<string, LuaBlockSpec>(StringComparer.Ordinal);
        void Add(int group, string id, string title, string code, LuaBlockShape shape = LuaBlockShape.Statement, string? symbol = null) =>
            result.Add(id, new(id, Groups[group], title, code, shape, symbol));

        Add(0, "sequence", "Последовательность", "do", LuaBlockShape.Body);
        Add(0, "if", "Условие If / Else", "if true then", LuaBlockShape.If);
        Add(0, "while", "Цикл While", "while false do", LuaBlockShape.Loop);
        Add(0, "for_numeric", "Числовой For", "for i = 1, 3 do", LuaBlockShape.Loop);
        Add(0, "for_in", "Цикл For In", "for k, v in pairs({}) do", LuaBlockShape.Loop);
        Add(0, "break", "Прервать цикл", "break");
        Add(0, "continue", "Следующая итерация", "", LuaBlockShape.Continue);
        Add(0, "return", "Вернуть значение", "return");
        Add(0, "call", "Вызвать функцию", "print('Lua')");
        Add(0, "pcall", "Защищённый вызов", "local ok, result = pcall(print, 'Lua')", symbol: "pcall");
        Add(0, "xpcall", "Вызов с обработчиком ошибки", "local ok, result = xpcall(print, debug.traceback, 'Lua')", symbol: "xpcall");
        Add(0, "krl_sync", "Вызвать KRL синхронно", "call_scene('chapter')", symbol: "call_scene");
        Add(0, "krl_async", "Вызвать KRL асинхронно", "local task = kne_async({function() call_scene('chapter') end})");
        Add(1, "raw", "Исходный код", "-- Lua 5.4");
        Add(2, "local", "Объявить локальную переменную", "local value = 0");
        Add(2, "assign", "Присвоить значение", "value = 0");
        Add(2, "multi", "Множественное присваивание", "a, b = b, a");
        Add(2, "global", "Доступ к глобальной переменной", "local value = _ENV['global_name']");
        Add(2, "index", "Прочитать поле таблицы", "local value = items['key']");
        Add(3, "table", "Создать таблицу", "local items = { key = 'value' }");
        Add(3, "array", "Создать массив", "local items = { 'a', 'b' }");
        Add(3, "insert", "Вставить элемент", "table.insert(items, 'value')", symbol: "table.insert");
        Add(3, "remove", "Удалить элемент", "local removed = table.remove(items, 1)", symbol: "table.remove");
        Add(3, "table_length", "Длина таблицы", "local count = #items");
        Add(3, "pairs", "Перебрать ключи", "for k, v in pairs(items) do", LuaBlockShape.Loop);
        Add(3, "ipairs", "Перебрать массив", "for i, v in ipairs(items) do", LuaBlockShape.Loop);
        Add(3, "setmetatable", "Установить метатаблицу", "setmetatable(items, {})", symbol: "setmetatable");
        Add(3, "getmetatable", "Получить метатаблицу", "local meta = getmetatable(items)", symbol: "getmetatable");
        Add(4, "function", "Объявить функцию", "function example(value)", LuaBlockShape.Function);
        Add(4, "anonymous", "Создать анонимную функцию", "local callback = function(value)", LuaBlockShape.Function);
        Add(4, "co_create", "Создать корутину", "local thread = coroutine.create(callback)", symbol: "coroutine.create");
        Add(4, "co_yield", "Приостановить корутину", "coroutine.yield(value)", symbol: "coroutine.yield");
        Add(4, "co_resume", "Продолжить корутину", "local ok, result = coroutine.resume(thread, value)", symbol: "coroutine.resume");
        Add(4, "require", "Подключить модуль", "local module = require('module')", symbol: "require");
        Add(4, "varargs", "Получить аргументы ...", "local arguments = table.pack(...)");
        Add(5, "math", "Арифметика (+ − * / // % ^)", "local value = a + b");
        Add(5, "logical", "Логика (and / or / not)", "local value = a and not b");
        Add(5, "comparison", "Сравнение (== ~= < > <= >=)", "local equal = a == b");
        Add(5, "concat", "Конкатенация (..)", "local value = a .. b");
        Add(5, "length", "Длина (#)", "local count = #text");
        Add(5, "negative", "Унарный минус", "local value = -a");
        Add(5, "bitwise", "Побитовая операция", "local value = a & b");
        Add(6, "number", "Число", "local value = 42");
        Add(6, "string", "Строка", "local value = 'текст'");
        Add(6, "boolean", "Логическое значение", "local value = true");
        Add(6, "nil", "Пустое значение (nil)", "local value = nil");
        Add(6, "table_ref", "Ссылка на таблицу", "local alias = items");
        Add(6, "function_ref", "Ссылка на функцию", "local alias = print");
        Add(8, "set_meta", "Установить метаметод", "getmetatable(items).__tostring = callback");
        Add(8, "get_meta", "Получить метаметод", "local method = getmetatable(items).__tostring");
        Add(8, "get_env", "Получить окружение _ENV", "local environment = _ENV");
        Add(8, "set_env", "Установить окружение _ENV", "local _ENV = environment");
        Add(8, "get_info", "Сведения об отладке", "local info = debug.getinfo(1)", symbol: "debug.getinfo");
        Add(7, "print", "print", "print('Lua')", symbol: "print");
        Add(7, "type", "type", "local result = type(value)", symbol: "type");
        Add(7, "tostring", "tostring", "local result = tostring(value)", symbol: "tostring");
        Add(7, "tonumber", "tonumber", "local result = tonumber('42')", symbol: "tonumber");
        Add(7, "select", "select", "local result = select('#', ...)", symbol: "select");
        Add(7, "table.unpack", "table.unpack", "local result = table.unpack(items)", symbol: "table.unpack");
        Add(7, "rawget", "rawget", "local result = rawget(items, 'key')", symbol: "rawget");
        Add(7, "rawset", "rawset", "rawset(items, 'key', value)", symbol: "rawset");
        Add(7, "error", "error", "error('Lua error')", symbol: "error");
        Add(7, "assert", "assert", "assert(true)", symbol: "assert");
        Add(7, "math.random", "math.random", "local result = math.random(1, 10)", symbol: "math.random");
        Add(7, "math.floor", "math.floor", "local result = math.floor(3.5)", symbol: "math.floor");
        Add(7, "math.ceil", "math.ceil", "local result = math.ceil(3.5)", symbol: "math.ceil");
        Add(7, "math.abs", "math.abs", "local result = math.abs(-3)", symbol: "math.abs");
        Add(7, "math.sin", "math.sin", "local result = math.sin(0)", symbol: "math.sin");
        Add(7, "math.cos", "math.cos", "local result = math.cos(0)", symbol: "math.cos");
        Add(7, "math.tan", "math.tan", "local result = math.tan(0)", symbol: "math.tan");
        Add(7, "math.log", "math.log", "local result = math.log(10)", symbol: "math.log");
        Add(7, "math.sqrt", "math.sqrt", "local result = math.sqrt(9)", symbol: "math.sqrt");
        Add(7, "math.min", "math.min", "local result = math.min(1, 2)", symbol: "math.min");
        Add(7, "math.max", "math.max", "local result = math.max(1, 2)", symbol: "math.max");
        Add(7, "math.exp", "math.exp", "local result = math.exp(1)", symbol: "math.exp");
        Add(7, "math.fmod", "math.fmod", "local result = math.fmod(5, 2)", symbol: "math.fmod");
        Add(7, "math.modf", "math.modf", "local result = math.modf(5.2)", symbol: "math.modf");
        Add(7, "math.atan", "math.atan", "local result = math.atan(1)", symbol: "math.atan");
        Add(7, "math.deg", "math.deg", "local result = math.deg(math.pi)", symbol: "math.deg");
        Add(7, "math.rad", "math.rad", "local result = math.rad(180)", symbol: "math.rad");
        Add(7, "math.tointeger", "math.tointeger", "local result = math.tointeger(3)", symbol: "math.tointeger");
        Add(7, "math.type", "math.type", "local result = math.type(3)", symbol: "math.type");
        Add(7, "math.ult", "math.ult", "local result = math.ult(1, 2)", symbol: "math.ult");
        Add(7, "string.find", "string.find", "local result = string.find('Lua', 'ua')", symbol: "string.find");
        Add(7, "string.gsub", "string.gsub", "local result = string.gsub('Lua', 'ua', 'UA')", symbol: "string.gsub");
        Add(7, "string.format", "string.format", "local result = string.format('%s', 'Lua')", symbol: "string.format");
        Add(7, "string.sub", "string.sub", "local result = string.sub('Lua', 1, 2)", symbol: "string.sub");
        Add(7, "string.rep", "string.rep", "local result = string.rep('Lua', 2)", symbol: "string.rep");
        Add(7, "string.byte", "string.byte", "local result = string.byte('A')", symbol: "string.byte");
        Add(7, "string.char", "string.char", "local result = string.char(65)", symbol: "string.char");
        Add(7, "string.match", "string.match", "local result = string.match('Lua', '%a+')", symbol: "string.match");
        Add(7, "string.gmatch", "string.gmatch", "local result = string.gmatch('Lua', '%a+')", symbol: "string.gmatch");
        Add(7, "io.open", "io.open", "local result = io.open('text.txt', 'r')", symbol: "io.open");
        Add(7, "io.read", "io.read", "local result = io.read('*l')", symbol: "io.read");
        Add(7, "io.write", "io.write", "io.write('Lua')", symbol: "io.write");
        Add(7, "io.close", "io.close", "io.close(file)", symbol: "io.close");
        Add(7, "io.lines", "io.lines", "local result = io.lines('text.txt')", symbol: "io.lines");
        Add(7, "os.time", "os.time", "local result = os.time()", symbol: "os.time");
        Add(7, "os.date", "os.date", "local result = os.date('%Y-%m-%d')", symbol: "os.date");
        Add(7, "os.clock", "os.clock", "local result = os.clock()", symbol: "os.clock");
        Add(7, "os.execute", "os.execute", "local result = os.execute()", symbol: "os.execute");
        Add(7, "os.getenv", "os.getenv", "local result = os.getenv('LANG')", symbol: "os.getenv");
        Add(7, "utf8.len", "utf8.len", "local result = utf8.len('Lua')", symbol: "utf8.len");
        Add(7, "utf8.codes", "utf8.codes", "local result = utf8.codes('Lua')", symbol: "utf8.codes");
        Add(7, "utf8.char", "utf8.char", "local result = utf8.char(65)", symbol: "utf8.char");
        Add(7, "collectgarbage", "collectgarbage", "local result = collectgarbage('count')", symbol: "collectgarbage");
        Add(7, "math.huge", "math.huge", "local value = math.huge", symbol: "math.huge");
        Add(7, "math.pi", "math.pi", "local value = math.pi", symbol: "math.pi");
        Add(7, "math.maxinteger", "math.maxinteger", "local value = math.maxinteger", symbol: "math.maxinteger");
        Add(7, "math.mininteger", "math.mininteger", "local value = math.mininteger", symbol: "math.mininteger");
        Add(7, "utf8.charpattern", "utf8.charpattern", "local value = utf8.charpattern", symbol: "utf8.charpattern");
        Add(8, "__add", "__add", "getmetatable(items).__add = handler");
        Add(8, "__sub", "__sub", "getmetatable(items).__sub = handler");
        Add(8, "__mul", "__mul", "getmetatable(items).__mul = handler");
        Add(8, "__div", "__div", "getmetatable(items).__div = handler");
        Add(8, "__mod", "__mod", "getmetatable(items).__mod = handler");
        Add(8, "__pow", "__pow", "getmetatable(items).__pow = handler");
        Add(8, "__unm", "__unm", "getmetatable(items).__unm = handler");
        Add(8, "__idiv", "__idiv", "getmetatable(items).__idiv = handler");
        Add(8, "__band", "__band", "getmetatable(items).__band = handler");
        Add(8, "__bor", "__bor", "getmetatable(items).__bor = handler");
        Add(8, "__bxor", "__bxor", "getmetatable(items).__bxor = handler");
        Add(8, "__bnot", "__bnot", "getmetatable(items).__bnot = handler");
        Add(8, "__shl", "__shl", "getmetatable(items).__shl = handler");
        Add(8, "__shr", "__shr", "getmetatable(items).__shr = handler");
        Add(8, "__eq", "__eq", "getmetatable(items).__eq = handler");
        Add(8, "__lt", "__lt", "getmetatable(items).__lt = handler");
        Add(8, "__le", "__le", "getmetatable(items).__le = handler");
        Add(8, "__call", "__call", "getmetatable(items).__call = handler");
        Add(8, "__gc", "__gc", "getmetatable(items).__gc = handler");
        Add(8, "__close", "__close", "getmetatable(items).__close = handler");
        Add(8, "__tostring", "__tostring", "getmetatable(items).__tostring = handler");
        Add(8, "__len", "__len", "getmetatable(items).__len = handler");
        Add(8, "__index", "__index", "getmetatable(items).__index = handler");
        Add(8, "__newindex", "__newindex", "getmetatable(items).__newindex = handler");
        Add(8, "__concat", "__concat", "getmetatable(items).__concat = handler");
        Add(8, "__pairs", "__pairs", "getmetatable(items).__pairs = handler");
        Add(8, "__metatable", "__metatable", "getmetatable(items).__metatable = handler");
        Add(8, "__mode", "__mode", "getmetatable(items).__mode = 'kv'");
        Add(8, "__name", "__name", "getmetatable(items).__name = 'Items'");
        return result;
    }

    internal static ScriptNode Create(string operation)
    {
        var spec = Blocks[operation];
        return new ScriptNode
        {
            Kind = ScriptNodeKind.LuaBlock, Category = spec.Group.Id,
            Lua = new() { Operation = operation, Code = "\n" + spec.Code + "\n", Close = spec.HasBody ? "\nend\n" : "", Otherwise = spec.Shape == LuaBlockShape.If ? "\nelse\n" : "" }
        };
    }
}

/// <summary>Source fragments and operation metadata for an editable Lua node.</summary>
public sealed record LuaBlockSettings
{
    public string Operation { get; init; } = "raw";
    public string Code { get; init; } = "";
    public string Otherwise { get; init; } = "";
    public string Close { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ElseIf { get; init; }
}
