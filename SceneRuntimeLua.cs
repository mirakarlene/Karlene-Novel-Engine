namespace KNE;

/// <summary>Lua coroutine scheduler. Calls retain a caller frame; jumps replace only the current frame.</summary>
internal static class SceneRuntimeLua
{
    public const string Source = """
        do
            local definitions, cache = {}, {}
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
            function kne_compile_scene(code, name)
                local env = setmetatable({}, {
                    __index = _G,
                    __newindex = function(t, k, v)
                        if k == "main" then rawset(t, k, v) else rawset(_G, k, v) end
                    end
                })
                local chunk, problem = load(code, "@" .. name, "t", env)
                if not chunk then error(problem) end
                local result = chunk()
                local entry = rawget(env, "main") or result
                if type(entry) == "function" then return entry end
                return nil
            end
            function kne_cache_scene(path, entry) cache[path] = entry or false end
            function kne_entry_for_file(path)
                if cache[path] == nil then cache[path] = kne_compile_scene(checked(kne_scene_code_result(path)), path) or false end
                return cache[path] or nil
            end
            local function resolve(target)
                if type(target) ~= "string" or target == "" then error("Укажите имя сцены или путь к файлу") end
                if definitions[target] then return definitions[target] end
                local path = checked(kne_scene_path_result(target))
                local entry = kne_entry_for_file(path)
                if not entry then error("Сцена должна объявлять main() или возвращать функцию: " .. target) end
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
