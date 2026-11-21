using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NLua;

namespace KNE;

public sealed partial class LuaRuntime
{
    private readonly List<SystemReplayEntry> systemHistory = [];
    private IReadOnlyList<SystemReplayEntry> systemReplay = [];
    private int systemReplayIndex;
    private int systemHistoryCharacters;
    private bool systemReplayMismatch;
    private GameSystemServices? localSystem;
    internal Func<SystemCall, CancellationToken, Task<SystemReply>>? SystemHost { get; init; }
    internal double PlayedSeconds { get; set; }

    private void InitializeSystem()
    {
        Register(nameof(InvokeSystemForLua), "kne_system_call");
        string globalFunctions = string.Join(",", SystemBlocks.All.Values
            .Where(block => block.Function is not ("save_named" or "restart_game" or "exit_game" or "return_to_menu") && !SystemBlocks.IsPresentation(block.Function))
            .Select(block => LuaQuote(block.Function)));
        lua!.DoString("local functions = {" + globalFunctions + "}\n" + """
            local host, compile = kne_system_call, load
            local function request(operation, ...)
                local result = host(operation, table.pack(...))
                if result[1] ~= '' then error(result[1], 3) end
                return assert(compile('return ' .. result[0], 'KNE system result', 't', {}))()
            end
            for _, operation in ipairs(functions) do
                _G[operation] = function(...) return request(operation, ...) end
            end
            persistent = setmetatable({}, {
                __index = function(_, key) return persistent_get(key) end,
                __newindex = function(_, key, value)
                    if value == nil then persistent_delete(key) else persistent_set(key, value) end
                end,
                __metatable = false
            })
            function restart_game() kne_end('restart') end
            function exit_game() kne_end('exit') end
            function return_to_menu() kne_end('menu_reset') end
            function global_timer(action, format)
                local now = action == 'show' or action == 'reset'
                local problem = kne_script_api('global_timer', {action, format, now and request('play_time') * 60 or 0})
                if problem ~= '' then error(problem, 2) end
                kne_editor_pause()
            end
            function save_named(slot)
                if type(slot) ~= 'string' then error('Нужно имя слота сохранения', 2) end
                if not kne_interactive or not kne_scene_owned(coroutine.running()) then
                    error('Сохранение доступно только внутри игровой сцены', 2)
                end
                coroutine.yield(kne_event_location({kind = 'system_save', text = slot}))
            end
            """, "KNE system API");
    }

    /// <summary>Returns a typed Lua literal and an error separately across the Lua coroutine boundary.</summary>
    public string[] InvokeSystemForLua(string operation, LuaTable arguments)
    {
        using (arguments)
        {
            try
            {
                if (!SystemBlocks.FunctionNames.Contains(operation) || operation == "save_named" || SystemBlocks.IsPresentation(operation))
                    throw new InvalidOperationException("Неизвестная системная команда.");
                int count = Convert.ToInt32(arguments["n"]);
                if (count < 0 || count > (operation == "show_video" ? 6 : 3))
                    throw new ArgumentException("Слишком много аргументов системной команды.");
                var values = new object?[count];
                for (int i = 0; i < count; i++)
                {
                    if (arguments[i + 1] is LuaTable table)
                    {
                        using (table) values[i] = ReadState(table, 0, allowArray: true);
                    }
                    else values[i] = arguments[i + 1] switch
                    {
                        null => null,
                        string or bool or long or int or double => arguments[i + 1],
                        _ => throw new ArgumentException("Неподдерживаемый тип аргумента.")
                    };
                }
                var args = JsonSerializer.SerializeToElement(values);
                SystemReplayEntry entry;
                if (replaying)
                {
                    if (systemReplayIndex >= systemReplay.Count ||
                        systemReplay[systemReplayIndex] is not { } saved || saved.Operation != operation ||
                        !JsonNode.DeepEquals(JsonNode.Parse(saved.Arguments.GetRawText()), JsonNode.Parse(args.GetRawText())))
                    {
                        systemReplayMismatch = true;
                        throw new InvalidDataException("Последовательность системных команд не совпадает с сохранением.");
                    }
                    entry = saved;
                    systemReplayIndex++;
                }
                else
                {
                    if (systemHistory.Count >= 4096 || systemHistoryCharacters + args.GetRawText().Length > 4_000_000)
                        throw new InvalidDataException("Журнал системных команд заполнен.");
                    SystemReply result = CallSystem(new(operation, args));
                    entry = new(operation, args, result.Value, result.Error);
                    systemHistoryCharacters += args.GetRawText().Length + result.Value.GetRawText().Length;
                    systemHistory.Add(entry);
                }
                return entry.Error == null ? [StateLiteral(entry.Value), ""] : ["", entry.Error];
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return ["", "Мета и система: " + error.Message];
            }
        }
    }

    private SystemReply CallSystem(SystemCall call)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (SystemHost != null) return SystemHost(call, executionToken).GetAwaiter().GetResult();
            localSystem ??= new(project);
            localSystem.PlayedSeconds = PlayedSeconds;
            return localSystem.InvokeAsync(call, executionToken).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return SystemReply.Failure("Мета и система: " + error.Message);
        }
        finally
        {
            // Waiting for a player decision or for a host operation does not consume Lua's execution budget.
            deadline += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        }
    }

    private void DrainNamedSaves(bool write)
    {
        while (Story.Kind == "system_save")
        {
            if (history.Count >= 10_000) throw new InvalidDataException("История игры слишком длинная для сохранения.");
            if (write)
            {
                var checkpoint = CaptureCheckpoint(saving: true);
                var arguments = JsonSerializer.SerializeToElement(new object[] { Story.Text, checkpoint });
                var result = CallSystem(new("save_checkpoint", arguments));
                if (result.Error != null) throw new InvalidOperationException(result.Error);
                if (!string.IsNullOrWhiteSpace(project.Configuration.EntryPoints?.SaveScript))
                    RecordProjectEvent(new("save", Story.Text));
            }
            string before = FrameSignature(Story);
            ResumeStory(null);
            history.Add(new("system_continue", before));
            write = true;
        }
    }

    private void PrepareSystemReplay(GameCheckpoint checkpoint)
    {
        if (!double.IsFinite(checkpoint.PlayedSeconds) || checkpoint.PlayedSeconds < 0 || checkpoint.SystemResults is { Count: > 4096 })
            throw new InvalidDataException("Некорректный журнал или время игры.");
        var entries = checkpoint.SystemResults ?? [];
        foreach (var entry in entries)
        {
            if (entry is null || entry.Operation == null || !SystemBlocks.FunctionNames.Contains(entry.Operation) ||
                entry.Arguments.ValueKind != JsonValueKind.Array || entry.Arguments.GetArrayLength() > 3 ||
                entry.Value.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("Повреждена запись журнала системных команд.");
            EngineSettings.ValidateState(entry.Value, 0, allowNull: true);
        }
        systemReplay = entries;
        systemReplayIndex = 0;
        systemReplayMismatch = false;
        systemHistory.Clear();
        systemHistoryCharacters = 0;
    }
}
