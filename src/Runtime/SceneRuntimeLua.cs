namespace KNE;

/// <summary>Lua coroutine scheduler. Calls retain a caller frame; jumps replace only the current frame.</summary>
internal static class SceneRuntimeLua
{
    public const string Source = """
        do
            local definitions, loaded = {}, {}
            local subsystem_entries = {}
            local owned = setmetatable({}, {__mode = "k"})
            local transfer = {}
            local flow_failure = {}
            local active_stack = {}
            local scene_test = kne_scene_test == true
            local task_limit = kne_max_async_tasks or 128
            function kne_scene_owned(thread) return owned[thread] == true end
            function kne_diagnostics_threads()
                local result = {}
                for index = 1, #active_stack do result[#result + 1] = active_stack[index] end
                return result
            end
            local inspect, traceback = debug.getinfo, debug.traceback
            function kne_capture_error(thread, problem)
                if type(problem) == "table" and problem.token == flow_failure then
                    kne_restore_flow_error(problem.context)
                    return problem
                end
                if kne_error_snapshot_needed(tostring(problem)) then kne_publish_variables(thread or coroutine.running()) end
                local trace = thread and traceback(thread, tostring(problem), 0) or traceback(tostring(problem), 2)
                for level = 1, 100 do
                    local info = thread and inspect(thread, level, "Sl") or inspect(level + 1, "Sl")
                    if not info then break end
                    if info.source:sub(1, 1) == "@" then
                        kne_error_context(thread, info)
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
            function kne_scene_thread(entry, parent)
                parent = parent or coroutine.running()
                local thread = coroutine.create(function(...)
                    local result = table.pack(xpcall(entry, handle_error, ...))
                    if not result[1] then error(result[2], 0) end
                    return table.unpack(result, 2, result.n)
                end)
                if kne_inherit_speaker then kne_inherit_speaker(parent, thread) end
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
            local function duration(value)
                if type(value) ~= "number" or value ~= value or value < 0 or value > 3600 then
                    error("Время должно быть числом от 0 до 3600 секунд", 3)
                end
                return value
            end
            local function flow(event)
                if not owned[coroutine.running()] then error("Команда доступна только внутри игровой сцены", 3) end
                return coroutine.yield(kne_event_location(event))
            end
            local function branches(entries)
                if type(entries) ~= "table" or #entries < 1 or #entries > 64 then error("Нужно от 1 до 64 параллельных веток", 3) end
                for index = 1, #entries do if type(entries[index]) ~= "function" then error("Ветка должна быть функцией", 3) end end
                return entries
            end
            function kne_pause(seconds) return flow({kind = "pause", seconds = duration(seconds)}) end
            kne_timer = kne_pause
            function kne_wait_condition(condition, timeout)
                if type(condition) ~= "function" then error("Ожидание требует функцию условия", 2) end
                timeout = duration(timeout or 0)
                local wait_id = {}
                while not condition() do
                    local elapsed = flow({kind = "poll", timeout = timeout, wait_id = wait_id})
                    if elapsed == false then error("Истекло время ожидания условия", 2) end
                end
            end
            function kne_parallel(entries)
                local ok, problem = flow({kind = "spawn", entries = branches(entries), detached = false})
                if ok == false then error(problem, 0) end
            end
            function kne_async(entries) return flow({kind = "spawn", entries = branches(entries), detached = true}) end
            function kne_subsystem(path, wait)
                if type(path) ~= "string" then error("Укажите путь к подсистеме", 2) end
                local key = path:gsub("\\", "/"):upper()
                local entry = subsystem_entries[key]
                if not entry then
                    local result = kne_subsystem_code_result(path)
                    if result[1] ~= "" then error(result[1], 2) end
                    local chunk, problem = load(result[0], "@" .. path, "t", _G)
                    if not chunk then error(problem, 2) end
                    entry = chunk(); subsystem_entries[key] = entry
                end
                if wait ~= false then return call_scene(entry) end
                return kne_async({entry})
            end
            function kne_on_event(specification, entry)
                if type(specification) ~= "string" or type(entry) ~= "function" then error("Триггер требует параметры события и функцию", 2) end
                local problem = kne_validate_trigger(specification)
                if problem ~= "" then error(problem, 2) end
                return flow({kind = "subscribe", specification = specification, entry = entry})
            end
            function kne_notify_text_closed()
                if owned[coroutine.running()] then return flow({kind = "signal", signal = '{"kind":"text_closed"}'}) end
            end
            function kne_await_event(specification)
                local problem = kne_validate_trigger(specification)
                if problem ~= "" then error(problem, 2) end
                return flow({kind = "await_event", specification = specification})
            end
            function kne_join(handle)
                if type(handle) ~= "number" or handle % 1 ~= 0 then error("Join требует идентификатор асинхронной ветки", 2) end
                local ok, problem = flow({kind = "join", handle = handle})
                if ok == false then error(problem, 0) end
            end
            function kne_end(action, screen)
                if action ~= "menu" and action ~= "screen" and action ~= "exit" then error("Неизвестное действие конца", 2) end
                if action == "screen" and (type(screen) ~= "string" or screen == "") then error("Укажите экран конца", 2) end
                local problem = kne_validate_end(action, screen)
                if problem ~= "" then error(problem, 2) end
                return flow({kind = "end", action = action, screen = screen})
            end
            function kne_run_scene(entry)
                local tasks, groups, clock, group_id, visible = {}, {}, 0, 0, nil
                local presentation_id = 0
                local subscriptions = {}
                local function create(fn, detached, group, parent)
                    local task = {stack = {kne_scene_thread(fn, parent)}, input = table.pack(), detached = detached, group = group, transitions = 0}
                    tasks[#tasks + 1] = task
                    return task
                end
                local root = create(entry, false)
                local function close_task(task)
                    for _, group in pairs(groups) do
                        if group.owner == task then for _, child in ipairs(group) do close_task(child) end end
                    end
                    if task.done then return end
                    task.done = true
                    for _, thread in ipairs(task.stack) do
                        owned[thread] = nil
                        if coroutine.status(thread) ~= "dead" then pcall(coroutine.close, thread) end
                    end
                    task.stack, task.done = {}, true
                end
                local function close_others()
                    for _, task in ipairs(tasks) do if task ~= root then close_task(task) end end
                end
                local function group_done(handle)
                    local group = groups[handle]
                    if not group then error("Не найдена асинхронная ветка: " .. tostring(handle)) end
                    for _, task in ipairs(group) do if not task.done then return false end end
                    return true
                end
                local function refresh_threads()
                    active_stack = {}
                    for _, task in ipairs(tasks) do
                        for _, thread in ipairs(task.stack) do active_stack[#active_stack + 1] = thread end
                    end
                end
                local function fail(task, problem)
                    local thread = task.stack[#task.stack]
                    kne_capture_error(thread, problem)
                    if task.group and (not task.detached or groups[task.group].awaited) then
                        local group = groups[task.group]
                        group.failure = setmetatable({token = flow_failure, context = kne_suspend_flow_error(), message = tostring(problem)},
                            {__tostring = function(value) return value.message end})
                        for _, child in ipairs(group) do close_task(child) end
                        return
                    end
                    for _, current in ipairs(tasks) do close_task(current) end
                    error(problem, 0)
                end
                local function prune_subscriptions()
                    for index = #subscriptions, 1, -1 do
                        local subscription = subscriptions[index]
                        if subscription.owner.done or coroutine.status(subscription.thread) == "dead" or subscription.used then
                            table.remove(subscriptions, index)
                        end
                    end
                end
                local function dispatch(signal)
                    prune_subscriptions()
                    for _, subscription in ipairs(subscriptions) do
                        if not subscription.owner.done and coroutine.status(subscription.thread) ~= "dead" and not subscription.used and
                            (not subscription.active or subscription.active.done) and kne_trigger_matches(subscription.specification, signal) then
                            if subscription.waiter then
                                subscription.waiter.wait_event = nil
                                subscription.used = true
                            else
                            local count = 0; for _, current in ipairs(tasks) do if not current.done then count = count + 1 end end
                            if count >= task_limit then fail(subscription.owner, "Одновременно разрешено не более " .. task_limit .. " веток") end
                            local child = create(subscription.entry, false, nil, subscription.thread)
                            child.trigger = true; subscription.active = child
                            subscription.used = kne_trigger_once(subscription.specification)
                            end
                        end
                    end
                    visible = nil
                end
                local function step(task)
                    local stack, thread = task.stack, task.stack[#task.stack]
                    owned[thread] = true
                    local values = table.pack(coroutine.resume(thread, table.unpack(task.input, 1, task.input.n)))
                    task.input = table.pack()
                    if not values[1] then return fail(task, values[2]) end
                    if coroutine.status(thread) == "dead" then
                        kne_transition_pending(#stack > 1)
                        owned[thread] = nil
                        table.remove(stack)
                        task.input = table.pack(table.unpack(values, 2, values.n))
                        task.done = #stack == 0
                        return
                    end
                    local event = values[2]
                    if type(event) ~= "table" then return fail(task, "Сцена приостановлена без команды") end
                    if event.token == transfer then
                        kne_transition_pending(true)
                        task.transitions = task.transitions + 1
                        if task.transitions > 10000 then return fail(task, "Больше 10000 переходов между репликами") end
                        if event.kind == "call" and #stack >= 64 then return fail(task, "Глубина вызовов сцен больше 64") end
                        local ok, next_entry = pcall(resolve, event.target)
                        if not ok then return fail(task, next_entry) end
                        if task == root and #stack == 1 and event.kind == "goto" then
                            close_others()
                            if scene_test then close_task(root); return end
                        end
                        if event.kind == "goto" then owned[thread] = nil; table.remove(stack); pcall(coroutine.close, thread) end
                        stack[#stack + 1], task.input = kne_scene_thread(next_entry, thread), event.args
                    elseif event.kind == "spawn" then
                        local count = 0
                        for _, current in ipairs(tasks) do if not current.done then count = count + 1 end end
                        if count + #event.entries > task_limit then return fail(task, "Одновременно разрешено не более " .. task_limit .. " веток") end
                        group_id = group_id + 1
                        if group_id > 10000 then return fail(task, "Больше 10000 групп параллельных веток") end
                        local group = {owner = task, awaited = not event.detached}; groups[group_id] = group
                        for _, fn in ipairs(event.entries) do group[#group + 1] = create(fn, task.detached or event.detached, group_id, thread) end
                        if event.detached then task.input = table.pack(group_id) else task.join = group_id end
                    elseif event.kind == "join" then
                        if task.group == event.handle then return fail(task, "Ветка не может ожидать завершения собственной группы") end
                        local ok, completed = pcall(group_done, event.handle)
                        if not ok then return fail(task, completed) end
                        groups[event.handle].awaited = true
                        if completed and groups[event.handle].failure then task.input = table.pack(false, groups[event.handle].failure)
                        elseif not completed then task.join = event.handle end
                    elseif event.kind == "subscribe" or event.kind == "await_event" then
                        prune_subscriptions()
                        if #subscriptions >= 512 then return fail(task, "Не более 512 триггеров в активной сцене") end
                        subscriptions[#subscriptions + 1] = {specification = event.specification, entry = event.entry, thread = thread, owner = task, waiter = event.kind == "await_event" and task or nil}
                        if event.kind == "await_event" then task.wait_event = true end
                    elseif event.kind == "signal" then
                        dispatch(event.signal)
                    elseif event.kind == "pause" then
                        if event.seconds > 0 then task.wake = clock + event.seconds end
                    elseif event.kind == "poll" then
                        if task.poll_id ~= event.wait_id then task.poll_id, task.poll_started = event.wait_id, clock end
                        task.poll_timeout = event.timeout
                        task.wake = clock + 0.01
                    elseif event.kind == "end" then
                        root.ending = {kind = "completed", action = event.action, screen = event.screen,
                            file = event.file, line = event.line, node = event.node, key = event.key, functionline = event.functionline}
                        for _, current in ipairs(tasks) do close_task(current) end
                    elseif event.kind == "dialogue" or event.kind == "choice" or event.kind == "input" or event.kind == "text_pause" or event.kind == "clear_text" or (kne_editor and event.kind == "preview") then
                        task.transitions = 0
                        if task.detached then
                            if event.kind == "choice" then return fail(task, "Выбор недоступен в фоновой асинхронной ветке; используйте Параллель") end
                            if event.kind == "input" then return fail(task, "Ввод текста недоступен в фоновой асинхронной ветке; используйте Параллель") end
                            if event.kind == "dialogue" then print_to_ui(event.speaker == "" and event.text or event.speaker .. ": " .. event.text) end
                            if event.seconds and event.seconds > 0 then task.wake = clock + event.seconds end
                        else
                            if event.kind ~= "preview" then kne_transition_pending(false) end
                            if event.seconds ~= nil then
                                event.remaining = event.seconds
                                event.erase_progress = event.kind == "clear_text" and 0 or nil
                            end
                            if event.seconds ~= 0 then task.event = event end
                        end
                    else return fail(task, "Неизвестная команда сцены: " .. tostring(event.kind)) end
                end
                while true do
                    local progressed, passes = true, 0
                    while progressed and not root.done do
                        progressed = false
                        for _, task in ipairs(tasks) do
                            if task.event and task.event_deadline then
                                task.event.remaining = math.max(0, task.event_deadline - clock)
                                if task.event.kind == "clear_text" then task.event.erase_progress = math.min(1, (clock - task.event_started) / task.event.seconds) end
                                if clock + 0.0000001 >= task.event_deadline then
                                    if task.event.kind == "choice" then task.input = table.pack(task.event.default_option) end
                                    task.event, task.event_deadline = nil, nil
                                end
                            end
                            if not task.done and task.join and group_done(task.join) then
                                if groups[task.join].failure then task.input = table.pack(false, groups[task.join].failure) end
                                task.join = nil
                            end
                            if not task.done and task.wake and clock + 0.0000001 >= task.wake then
                                task.wake = nil
                                if task.poll_started then
                                    task.input = table.pack(not (task.poll_timeout > 0 and clock - task.poll_started + 0.0000001 >= task.poll_timeout))
                                end
                            end
                            if not task.done and not task.event and not task.join and not task.wake and not task.wait_event then
                                step(task); progressed = true; passes = passes + 1
                                if passes > 10000 then fail(task, "Больше 10000 команд без остановки; добавьте паузу") end
                            end
                            if root.done then break end
                        end
                    end
                    prune_subscriptions()
                    refresh_threads()
                    if root.done then close_others(); return root.ending end
                    for index = #tasks, 2, -1 do if tasks[index].done then table.remove(tasks, index) end end
                    if not visible or visible.done or not visible.event then
                        visible = nil
                        for _, task in ipairs(tasks) do if task.trigger and not task.done and task.event then visible = task; break end end
                        if not visible then for _, task in ipairs(tasks) do if not task.done and task.event then visible = task; break end end end
                    end
                    local background = false
                    for _, task in ipairs(tasks) do if not task.done and (task.detached or task.wake or task.join) then background = true; break end end
                    local event = visible and visible.event or {kind = "wait"}
                    event.triggers = {}
                    for _, subscription in ipairs(subscriptions) do
                        if not subscription.owner.done and coroutine.status(subscription.thread) ~= "dead" and not subscription.used and
                            (not subscription.active or subscription.active.done) then event.triggers[#event.triggers + 1] = subscription.specification end
                    end
                    if visible and event.seconds ~= nil and not visible.event_deadline then
                        visible.event_started, visible.event_deadline = clock, clock + event.seconds
                    end
                    if event.kind == "dialogue" or event.kind == "choice" or event.kind == "input" then
                        if event.presentation_id == nil then
                            presentation_id = presentation_id + 1
                            event.presentation_id = presentation_id
                        end
                        if event.kind ~= "input" then kne_remember_dialogue(event) end
                    end
                    event.background = background
                    if not kne_interactive then
                        clock = clock + 0.1
                    else
                        local answer, seconds, tick, signal = coroutine.yield(event)
                        if signal then
                            dispatch(signal)
                        elseif tick then clock = clock + (seconds or 0)
                        elseif visible then visible.event, visible.event_deadline, visible.input = nil, nil, table.pack(answer); visible = nil end
                    end
                end
            end
        end
        """;
}
