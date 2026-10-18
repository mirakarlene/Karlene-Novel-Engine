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
                if not narration and speaker == "" and (character == nil or character == "") then
                    local inherited = current_speaker()
                    if inherited and inherited ~= "" then character = inherited end
                end
                if narration then speaker, character = "", nil end
                local result = kne_prepare_dialogue(speaker, character, effect)
                if result[3] ~= "" then error(result[3], 2) end
                local event = {kind = "dialogue", speaker = result[0], text = tostring(text),
                    character = result[1], effect = result[2], seconds = auto_seconds, skippable = skippable ~= false}
                if not kne_interactive or not coroutine.isyieldable() then
                    last = event
                    print_to_ui(event.speaker == "" and event.text or event.speaker .. ": " .. event.text)
                    return
                end
                return emit(event)
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
            function console_write(text) print(text); kne_editor_pause() end
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
