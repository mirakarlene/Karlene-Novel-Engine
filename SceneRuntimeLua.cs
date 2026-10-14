namespace KNE;

/// <summary>Lua coroutine scheduler. Calls retain a caller frame; jumps replace only the current frame.</summary>
internal static class SceneRuntimeLua
{
    public const string Source = """
        do
            local definitions, loaded = {}, {}
            local owned = setmetatable({}, {__mode = "k"})
            local transfer = {}
            local function checked(result)
                if result[1] ~= "" then error(result[1]) end
                return result[0]
            end
            function kne_scene_thread(entry)
                local thread = coroutine.create(entry)
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
                local code = checked(kne_scene_code_result(path))
                local chunk, problem = load(code, "@" .. path, "t", _G)
                if not chunk then error(problem) end
                chunk()
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
                return coroutine.yield({token = transfer, kind = "call", target = target, args = table.pack(...)})
            end
            function goto_scene(target, ...)
                if not owned[coroutine.running()] then error("goto_scene доступен только внутри игровой сцены") end
                return coroutine.yield({token = transfer, kind = "goto", target = target, args = table.pack(...)})
            end
            call = call_scene
            jump = goto_scene
            function kne_run_scene(entry)
                local stack, input, transitions = {kne_scene_thread(entry)}, table.pack(), 0
                while #stack > 0 do
                    local thread = stack[#stack]
                    owned[thread] = true
                    local values = table.pack(coroutine.resume(thread, table.unpack(input, 1, input.n)))
                    if not values[1] then error(values[2]) end
                    if coroutine.status(thread) == "dead" then
                        owned[thread] = nil
                        table.remove(stack)
                        input = table.pack(table.unpack(values, 2, values.n))
                    else
                        local event = values[2]
                        if type(event) ~= "table" then error("Сцена приостановлена без команды") end
                        if event.token == transfer then
                            transitions = transitions + 1
                            if transitions > 10000 then error("Больше 10000 переходов между репликами") end
                            if event.kind == "call" and #stack >= 64 then error("Глубина вызовов сцен больше 64") end
                            local next_entry = resolve(event.target)
                            if event.kind == "goto" then
                                owned[thread] = nil
                                table.remove(stack)
                                local ok, problem = coroutine.close(thread)
                                if not ok then error(problem) end
                            end
                            stack[#stack + 1] = kne_scene_thread(next_entry)
                            input = event.args
                        elseif event.kind == "dialogue" or event.kind == "choice" then
                            transitions = 0
                            input = table.pack(coroutine.yield(event))
                        else error("Неизвестная команда сцены") end
                    end
                end
            end
        end
        """;
}
