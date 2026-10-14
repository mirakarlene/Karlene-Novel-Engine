namespace KNE;

/// <summary>Lua coroutine scheduler. Calls retain a caller frame; jumps replace only the current frame.</summary>
internal static class SceneRuntimeLua
{
    public const string Source = """
        do
            local definitions, loaded = {}, {}
            local owned = setmetatable({}, {__mode = "k"})
            local transfer = {}
            local active_stack = {}
            function kne_scene_owned(thread) return owned[thread] == true end
            function kne_diagnostics_threads()
                local result = {}
                for index = 1, #active_stack do result[#result + 1] = active_stack[index] end
                return result
            end
            local inspect, traceback = debug.getinfo, debug.traceback
            function kne_capture_error(thread, problem)
                if kne_error_snapshot_needed(tostring(problem)) then kne_publish_variables(thread or coroutine.running()) end
                local trace = thread and traceback(thread, tostring(problem), 0) or traceback(tostring(problem), 2)
                for level = 1, 100 do
                    local info = thread and inspect(thread, level, "Sl") or inspect(level + 1, "Sl")
                    if not info then break end
                    if info.source:sub(1, 1) == "@" then
                        kne_record_script_error(info.source:sub(2), info.currentline, trace, tostring(problem))
                        return problem
                    end
                end
                kne_record_script_error("", 0, trace, tostring(problem))
                return problem
            end
            local function handle_error(problem) return kne_capture_error(nil, problem) end
            function kne_invoke_command(command)
                local ok, problem = xpcall(process_command, handle_error, command)
                if not ok then error(problem, 0) end
            end
            function kne_scene_thread(entry)
                local thread = coroutine.create(function(...)
                    local result = table.pack(xpcall(entry, handle_error, ...))
                    if not result[1] then error(result[2], 0) end
                    return table.unpack(result, 2, result.n)
                end)
                local hook, mask, count = debug.gethook()
                if type(hook) == "function" then debug.sethook(thread, hook, mask, count) end
                return thread
            end
            function scene(name, entry)
                if type(name) ~= "string" or name == "" or type(entry) ~= "function" then
                    error("scene(name, function): нужны непустое имя и функция")
                end
                if definitions[name] then error("Сцена уже объявлена: " .. name) end
                definitions[name] = entry
            end
            function kne_load_script(path)
                if loaded[path] then return end
                loaded[path] = true
                local result = kne_scene_code_result(path)
                if result[1] ~= "" then
                    kne_record_script_error(path, 0, result[1], result[1])
                    error(result[1], 0)
                end
                local code = result[0]
                local chunk, problem = load(code, "@" .. path, "t", _G)
                if not chunk then
                    kne_record_script_error(path, tonumber(problem:match(":(%d+):")) or 0, problem, problem)
                    error(problem, 0)
                end
                local ok, result = xpcall(chunk, handle_error)
                if not ok then error(result, 0) end
            end
            function kne_declare_function(name, path)
                if rawget(_G, name) ~= nil then return end
                local placeholder
                placeholder = function(...)
                    kne_load_script(path)
                    local entry = rawget(_G, name)
                    if type(entry) ~= "function" or entry == placeholder then error("Не найдена функция " .. name .. " в " .. path) end
                    return entry(...)
                end
                rawset(_G, name, placeholder)
            end
            local function resolve(target)
                if type(target) == "function" then return target end
                if type(target) ~= "string" or target == "" then error("Цель call/goto должна быть существующей функцией или её именем") end
                if definitions[target] then return definitions[target] end
                if target:find("[/\\.]") then error("Укажите имя функции вместо пути к файлу: " .. target) end
                local entry = rawget(_G, target)
                if type(entry) ~= "function" then error("Не найдена функция: " .. target) end
                return entry
            end
            function call_scene(target, ...)
                if not owned[coroutine.running()] then error("call_scene доступен только внутри игровой сцены") end
                kne_editor_pause()
                return coroutine.yield({token = transfer, kind = "call", target = target, args = table.pack(...)})
            end
            function goto_scene(target, ...)
                if not owned[coroutine.running()] then error("goto_scene доступен только внутри игровой сцены") end
                kne_editor_pause()
                return coroutine.yield({token = transfer, kind = "goto", target = target, args = table.pack(...)})
            end
            call = call_scene
            jump = goto_scene
            function kne_run_scene(entry)
                local stack, input, transitions = {kne_scene_thread(entry)}, table.pack(), 0
                active_stack = stack
                while #stack > 0 do
                    local thread = stack[#stack]
                    owned[thread] = true
                    local values = table.pack(coroutine.resume(thread, table.unpack(input, 1, input.n)))
                    if not values[1] then kne_capture_error(thread, values[2]); error(values[2], 0) end
                    if coroutine.status(thread) == "dead" then
                        kne_transition_pending(#stack > 1)
                        owned[thread] = nil
                        table.remove(stack)
                        input = table.pack(table.unpack(values, 2, values.n))
                    else
                        local event = values[2]
                        if type(event) ~= "table" then error("Сцена приостановлена без команды") end
                        if event.token == transfer then
                            kne_transition_pending(true)
                            transitions = transitions + 1
                            if transitions > 10000 then error(kne_capture_error(thread, "Больше 10000 переходов между репликами"), 0) end
                            if event.kind == "call" and #stack >= 64 then error(kne_capture_error(thread, "Глубина вызовов сцен больше 64"), 0) end
                            local ok, next_entry = pcall(resolve, event.target)
                            if not ok then error(kne_capture_error(thread, next_entry), 0) end
                            if event.kind == "goto" then
                                owned[thread] = nil
                                table.remove(stack)
                                local ok, problem = coroutine.close(thread)
                                if not ok then error(problem) end
                            end
                            stack[#stack + 1] = kne_scene_thread(next_entry)
                            input = event.args
                        elseif event.kind == "dialogue" or event.kind == "choice" or (kne_editor and event.kind == "preview") then
                            if event.kind ~= "preview" then kne_transition_pending(false) end
                            transitions = 0
                            input = table.pack(coroutine.yield(event))
                        else error("Неизвестная команда сцены") end
                    end
                end
            end
        end
        """;
}
