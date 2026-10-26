namespace KNE;

/// <summary>Validated narrative commands sharing the scene scheduler's logical clock.</summary>
internal static class NarrativeRuntimeLua
{
    public const string Source = """
        do
            local last = {speaker = "", text = ""}
            local speakers = setmetatable({}, {__mode = "k"})
            local main_speaker = ""
            local function current_speaker()
                local thread = coroutine.running()
                return thread and speakers[thread] or main_speaker
            end
            function kne_inherit_speaker(parent, child)
                speakers[child] = (parent and speakers[parent]) or main_speaker
            end
            function kne_remember_dialogue(event) last = event end
            local function seconds(value)
                if type(value) ~= "number" or value ~= value or value < 0 or value > 3600 then error("Время: от 0 до 3600 секунд", 3) end
                return value
            end
            local function emit(event)
                if not kne_scene_owned(coroutine.running()) then error("Команда доступна только внутри игровой сцены", 3) end
                return coroutine.yield(kne_event_location(event))
            end
            function register_character(json)
                if type(json) ~= "string" then error("register_character требует JSON-строку", 2) end
                local problem = kne_register_character(json)
                if problem ~= "" then error(problem, 2) end
                kne_editor_pause()
            end
            function update_character(json)
                if type(json) ~= "string" then error("update_character требует JSON-строку", 2) end
                local problem = kne_update_character(json)
                if problem ~= "" then error(problem, 2) end
                kne_editor_pause()
            end
            function set_speaker(id)
                if type(id) ~= "string" then error("set_speaker требует ID персонажа", 2) end
                local problem = kne_check_character(id)
                if problem ~= "" then error(problem, 2) end
                local thread = coroutine.running()
                if thread then speakers[thread] = id else main_speaker = id end
                kne_editor_pause()
            end
            function reset_speakers()
                local thread = coroutine.running()
                if thread then speakers[thread] = "" else main_speaker = "" end
                kne_editor_pause()
            end
            function kne_say(speaker, text, character, effect, auto_seconds, skippable, narration)
                if character ~= nil and type(character) ~= "string" then error("ID персонажа должен быть строкой", 2) end
                if effect ~= nil and type(effect) ~= "string" then error("Эффект должен быть JSON-строкой", 2) end
                if skippable ~= nil and type(skippable) ~= "boolean" then error("Возможность пропуска должна быть логическим значением", 2) end
                if narration ~= nil and type(narration) ~= "boolean" then error("Рассказчик должен быть логическим значением", 2) end
                if auto_seconds ~= nil then seconds(auto_seconds) end
                if skippable == false and auto_seconds == nil then error("Непропускаемая реплика требует время автоматического перехода", 2) end
                speaker = speaker == nil and "" or tostring(speaker)
                if kne_plugin_dialogue and kne_sdk_dialogue_hooks then
                    for level = 2, 32 do
                        local info = debug.getinfo(level, "Sl")
                        if not info then break end
                        if info.source:sub(1, 1) == "@" then kne_error_context(nil, info); break end
                    end
                    local result = kne_plugin_dialogue(tostring(text), speaker)
                    if result[2] ~= "" then error(result[2], 2) end
                    text, speaker = result[0], result[1]
                end
                if not narration and speaker == "" and (character == nil or character == "") then
                    local inherited = current_speaker()
                    if inherited and inherited ~= "" then character = inherited end
                end
                if narration then speaker, character = "", nil end
                local result = kne_prepare_dialogue(speaker, character, effect)
                if result[3] ~= "" then error(result[3], 2) end
                local parts = kne_prepare_inline_dialogue(tostring(text))
                if parts[0] ~= "" then error(parts[0], 2) end
                local name = kne_inline_text(result[0])
                local count, presentation = tonumber(parts[1]), nil
                for index = 1, count do
                    local duration = parts[index * 2 + 1]
                    local final = duration == "final"
                    local wait = final and auto_seconds or tonumber(duration)
                    if wait and wait < 0 then wait = nil end
                    local event = {kind = "dialogue", speaker = name, text = parts[index * 2],
                        character = result[1], effect = result[2], seconds = wait,
                        skippable = final and skippable ~= false or not final and wait == nil, presentation_id = presentation}
                    if not kne_interactive or not coroutine.isyieldable() then
                        if index == count then
                            last = event
                            print_to_ui(kne_inline_plain(event.speaker == "" and event.text or event.speaker .. ": " .. event.text))
                        end
                    else
                        emit(event)
                        presentation = event.presentation_id
                    end
                end
            end
            function d(speaker, text) return kne_say(speaker, text) end
            function text_pause(duration, skippable)
                seconds(duration)
                if skippable ~= nil and type(skippable) ~= "boolean" then error("Возможность пропуска должна быть логическим значением", 2) end
                if duration == 0 then return end
                return emit({kind = "text_pause", speaker = last.speaker, text = last.text, character = last.character, effect = last.effect,
                    seconds = duration, skippable = skippable ~= false, presentation_id = last.presentation_id})
            end
            function clear_text(animated, duration)
                if animated ~= nil and type(animated) ~= "boolean" then error("Анимация стирания должна быть логическим значением", 2) end
                duration = seconds(duration == nil and 0.4 or duration)
                local previous = last
                last = {speaker = "", text = "", presentation_id = previous.presentation_id}
                if animated and duration > 0 and previous.text ~= "" then
                    emit({kind = "clear_text", speaker = previous.speaker, text = previous.text, character = previous.character,
                        effect = previous.effect, seconds = duration, skippable = false, presentation_id = previous.presentation_id})
                end
                kne_clear_remembered_text()
                kne_notify_text_closed()
                kne_editor_pause()
            end
            function console_write(text) print(kne_inline_plain(kne_inline_text(text))); kne_editor_pause() end
            function kne_random_branch(weights)
                if type(weights) ~= "table" or #weights < 2 or #weights > 20 then error("Случайная ветка требует от 2 до 20 весов", 2) end
                local total = 0
                for index = 1, #weights do
                    local weight = weights[index]
                    if type(weight) ~= "number" or weight ~= weight or weight < 0 or weight > 1000000 then error("Вес ветки: от 0 до 1000000", 2) end
                    total = total + weight
                end
                if total == 0 then error("Хотя бы одна случайная ветка должна иметь положительный вес", 2) end
                local value, accumulated = math.random() * total, 0
                for index = 1, #weights do
                    accumulated = accumulated + weights[index]
                    if value < accumulated then return index end
                end
                return #weights
            end
        end
        """;
}
