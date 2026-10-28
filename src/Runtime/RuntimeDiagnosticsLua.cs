namespace KNE;

internal static class RuntimeDiagnosticsLua
{
    public const string Source = """
        do
            local baseline, nodes, calls = {}, setmetatable({}, {__mode = "k"}), setmetatable({}, {__mode = "k"})
            local inspect, getlocal, getupvalue, next_value, value_type = debug.getinfo, debug.getlocal, debug.getupvalue, next, type
            local text_value, each, running, infinity = tostring, ipairs, coroutine.running, math.huge
            function kne_diagnostics_baseline()
                for key, value in next_value, _G do baseline[key] = value end
            end
            local function copy(value, seen, depth, budget)
                local kind = value_type(value)
                if kind == "string" then return value:sub(1, 4096) end
                if kind == "number" then
                    if value ~= value or value == infinity or value == -infinity then return "<non-finite>" end
                    return value
                end
                if kind == "nil" or kind == "boolean" then return value end
                if kind ~= "table" then return "<" .. kind .. ">" end
                if seen[value] then return "<cycle>" end
                if depth >= 5 then return "<depth limit>" end
                seen[value] = true
                local result, count = {}, 0
                for key, item in next_value, value do
                    if budget.count >= 500 or count >= 100 then result["…"] = "<size limit>"; break end
                    local key_kind = value_type(key)
                    if key_kind == "string" or key_kind == "number" then
                        budget.count, count = budget.count + 1, count + 1
                        result[text_value(key):sub(1, 256)] = copy(item, seen, depth + 1, budget)
                    end
                end
                seen[value] = nil
                return result
            end
            function kne_publish_variables(thread)
                local snapshot, budget = {Globals = {}, Frames = {}}, {count = 0}
                for key, value in next_value, _G do
                    if value_type(key) == "string" and (key == "state" or baseline[key] ~= value) and
                       key:sub(1, 4) ~= "kne_" and key:sub(1, 5) ~= "__kne" then
                        if budget.count >= 500 then snapshot.Globals["…"] = "<size limit>"; break end
                        budget.count = budget.count + 1
                        snapshot.Globals[key:sub(1, 256)] = copy(value, {}, 0, budget)
                    end
                end
                local threads = kne_diagnostics_threads and kne_diagnostics_threads() or {}
                if thread then threads[#threads + 1] = thread end
                local seen_threads = {}
                for thread_index, current in each(threads) do
                    if not seen_threads[current] then
                        seen_threads[current] = true
                        for level = 1, 32 do
                            if budget.count >= 500 then snapshot.Frames["…"] = {limit = "<size limit>"}; break end
                            local info = inspect(current, level, "Slf")
                            if not info then break end
                            if info.source:sub(1, 1) == "@" then
                                budget.count = budget.count + 1
                                local values = {}
                                for index = 1, 64 do
                                    if budget.count >= 500 then values["…"] = "<size limit>"; break end
                                    local name, value = getlocal(current, level, index)
                                    if not name then break end
                                    if name:sub(1, 1) ~= "(" and name:sub(1, 5) ~= "__kne" then
                                        budget.count = budget.count + 1
                                        values[name:sub(1, 256)] = copy(value, {}, 0, budget)
                                    end
                                end
                                for index = 1, 64 do
                                    if budget.count >= 500 then values["…"] = "<size limit>"; break end
                                    local name, value = getupvalue(info.func, index)
                                    if not name then break end
                                    if name ~= "_ENV" and name:sub(1, 4) ~= "kne_" and name:sub(1, 5) ~= "__kne" then
                                        budget.count = budget.count + 1
                                        values[name:sub(1, 256) .. " (замыкание)"] = copy(value, {}, 0, budget)
                                    end
                                end
                                snapshot.Frames[info.source:sub(2, 1025) .. ":" .. info.currentline .. " (#" .. thread_index .. "/" .. level .. ")"] = values
                            end
                        end
                    end
                end
                kne_receive_variables(snapshot)
            end
            local function location()
                for level = 2, 32 do
                    local info = inspect(level, "Sl")
                    if not info then break end
                    if info.source:sub(1, 1) == "@" then
                        return info.source:sub(2), info.currentline, info.linedefined
                    end
                end
                return "", 0, 0
            end
            function kne_runtime_node(id)
                if kne_scene_owned and kne_scene_owned(running()) then
                    local file, line, functionline = location()
                    nodes[running()] = {id = text_value(id), file = file, functionline = functionline}
                    if kne_debug_enabled and kne_debug_should_pause(file, line, text_value(id)) then
                        kne_publish_variables(running())
                        kne_debug_wait(file, line, text_value(id))
                    end
                end
            end
            function kne_editor_node(id)
                if kne_editor and kne_scene_owned and kne_scene_owned(running()) then
                    kne_runtime_node(id)
                end
            end
            function kne_frame_call(key, entry, ...)
                local thread, previous = running(), calls[running()]
                calls[thread] = key
                local result = table.pack(entry(...))
                calls[thread] = previous
                return table.unpack(result, 1, result.n)
            end
            function kne_error_context(thread, info)
                local current = thread or running()
                local node = nodes[current]
                local file = info.source:sub(2)
                if node and (node.file ~= file or node.functionline ~= info.linedefined) then node = nil end
                kne_record_script_context(file, info.currentline, info.linedefined, node and node.id or "", calls[current] or "")
            end
            function kne_event_location(event)
                local file, line, functionline = location()
                local node = nodes[running()]
                if node and (node.file ~= file or node.functionline ~= functionline) then node = nil end
                event.file, event.line, event.functionline, event.node, event.key = file, line, functionline, node and node.id, calls[running()]
                return event
            end
            function kne_editor_pause()
                if kne_editor and kne_scene_owned and kne_scene_owned(running()) then
                    coroutine.yield(kne_event_location({kind = "preview"}))
                end
            end
        end
        """;
}
