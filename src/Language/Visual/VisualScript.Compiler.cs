using System.Globalization;
using System.IO;
using System.Text;

namespace KNE;

public sealed partial class VisualScript
{
    private string CompileCore(bool editorPreview, bool subsystem)
    {
        ValidateStructure();
        var starts = Nodes.Where(n => n.Kind == ScriptNodeKind.Start).ToArray();
        if (starts.Length != 1) throw new InvalidDataException("Нужен ровно один блок «Начало».");
        var map = Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (!(subsystem && node.Kind == ScriptNodeKind.End)) ValidateCommand(node);
            int portIndex = 0;
            foreach (var (_, target) in node.Outputs())
            {
                int outputIndex = portIndex++;
                if (node.Kind == ScriptNodeKind.Comment && target == null) continue;
                if (string.IsNullOrEmpty(target) || !map.ContainsKey(target))
                    Fail(node, "соедините все выходы с существующими блоками");
                if (map[target!].Kind == ScriptNodeKind.Start) Fail(node, "нельзя соединять выход с блоком «Начало»");
                if (map[target!].Kind == ScriptNodeKind.Comment) Fail(node, "комментарии не имеют портов и не подключаются к схеме");
                if (node.TargetInput(outputIndex) >= (map[target!].Plugin?.Inputs.Length ?? 1)) Fail(node, "целевой входной порт отсутствует");
            }
        }
        var connectedTriggers = Nodes.SelectMany(node => node.Outputs()).Where(port => port.Target != null)
            .Select(port => port.Target!).ToHashSet(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        void Visit(ScriptNode node)
        {
            if (active.Contains(node.Id)) Fail(node, "произвольные циклы не поддерживаются; используйте блок While или For Each");
            if (!visited.Add(node.Id)) return;
            active.Add(node.Id);
            foreach (var (_, target) in node.Outputs())
                if (target != null) Visit(map[target]);
            active.Remove(node.Id);
        }
        Visit(starts[0]);
        foreach (var trigger in Nodes.Where(node => node.IsTrigger)) Visit(trigger);
        bool anchorTransfers = Nodes.Any(node => node.Kind == ScriptNodeKind.SendFlow);
        bool added;
        do
        {
            added = false;
            foreach (var send in Nodes.Where(node => node.Kind == ScriptNodeKind.SendFlow && visited.Contains(node.Id)))
            {
                if (!map.TryGetValue(send.Target, out var anchor) || anchor.Kind != ScriptNodeKind.Anchor) Fail(send, "выберите существующий якорь этой сцены");
                if (!visited.Contains(send.Target)) { Visit(map[send.Target]); added = true; }
            }
        } while (added);
        if (Nodes.Any(n => !visited.Contains(n.Id) && n.Kind != ScriptNodeKind.Comment))
            throw new InvalidDataException("Есть блоки, недостижимые из «Начала». Соедините или удалите их.");
        if (anchorTransfers) ValidateAnchorScopes(map, starts[0].Id, connectedTriggers);

        var code = new StringBuilder("-- Сгенерировано из блок-схемы KNE.\nfunction ").Append(FunctionName).Append("()\n");
        code.Append(anchorTransfers ? "    local __kne_run, __kne_flow\n    __kne_run = function(__kne_node, __kne_stop)\n" : "    local __kne_run\n    __kne_run = function(__kne_node, __kne_stop)\n")
            .Append("        while __kne_node and __kne_node ~= __kne_stop do\n");
        bool first = true;
        foreach (var node in Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            code.Append(first ? "            if " : "            elseif ").Append("__kne_node == ").Append(Quote(node.Id)).Append(" then\n");
            first = false;
            void Line(string value) => code.Append("                ").Append(value).Append('\n');
            Line($"if kne_runtime_node then kne_runtime_node({Quote(node.Id)}) end");
            Line($"if kne_plugin_node then kne_plugin_node({Quote(node.Kind.ToString())}, {Quote(node.Id)}) end");
            if (editorPreview) Line($"kne_editor_node({Quote(node.Id)})");
            // Keep the generated line spans stable so subsystem failures use the same frame IDs.
            string command = subsystem && node.Kind == ScriptNodeKind.End && node.EndAction != ScriptEndAction.Return ? "do end" : Command(node);
            if (command.Length > 0) Line(command);
            if (editorPreview && node.Kind is ScriptNodeKind.Start or ScriptNodeKind.End or ScriptNodeKind.SetVariable or ScriptNodeKind.Condition or ScriptNodeKind.Comment or ScriptNodeKind.SetFlag)
                Line("kne_editor_pause()");
            switch (node.Kind)
            {
                case ScriptNodeKind.Plugin:
                    Line($"local output = kne_execute_plugin_block({Quote(node.Plugin!.BlockId)}, {Quote(node.Plugin.Parameters.ToJsonString())})");
                    Line($"__kne_node = ({{{string.Join(", ", node.Options.Select(o => Quote(o.Target!)))}}})[output]");
                    break;
                case ScriptNodeKind.SendFlow:
                    Line($"return {Quote(node.Target)}");
                    break;
                case ScriptNodeKind.TriggerKey:
                case ScriptNodeKind.TriggerButton:
                case ScriptNodeKind.TriggerHotspot:
                case ScriptNodeKind.TriggerTextClosed:
                case ScriptNodeKind.TriggerCombination:
                    Line($"kne_await_event({Quote(System.Text.Json.JsonSerializer.Serialize(node.Trigger, EngineSettings.JsonOptions))})");
                    Line($"__kne_node = {Quote(node.Next!)}");
                    break;
                case ScriptNodeKind.TextInput:
                    Line($"local answer = text_input({Quote(System.Text.Json.JsonSerializer.Serialize(node.Input ?? new TextInputSettings(), EngineSettings.JsonOptions))})");
                    Line($"if answer == 'error' then __kne_node = {(node.Input is { BranchOnResult: true, HasErrorOutput: true } ? Quote(node.True!) : Quote(node.Next!))} elseif answer == 'cancel' then __kne_node = {(node.Input is { BranchOnResult: true, HasCancelOutput: true } ? Quote(node.False!) : Quote(node.Next!))} else __kne_node = {Quote(node.Next!)} end");
                    break;
                case ScriptNodeKind.RandomBranch:
                    Line($"local answer = kne_random_branch({{{string.Join(", ", node.Options.Select(option => Number(option.Weight)))}}})");
                    Line($"__kne_node = ({{{string.Join(", ", node.Options.Select(option => Quote(option.Target!)))}}})[answer]");
                    break;
                case ScriptNodeKind.Choice:
                case ScriptNodeKind.TimedChoice:
                    if (node.Kind == ScriptNodeKind.Choice && node.Options.All(o => o.Lock == null))
                        Line($"local answer = choice({Quote(node.Text)}, {{{string.Join(", ", node.Options.Select(o => Quote(o.Text)))}}})");
                    else
                    {
                        var request = new ChoiceRequest { Prompt = node.Text, Options = node.Options.Select(o => new ChoiceRequestOption(o.Text, o.Lock)).ToArray(),
                            Timer = node.Kind == ScriptNodeKind.TimedChoice ? node.ChoiceSettings ?? new ChoiceSettings() : null };
                        Line($"local answer = kne_choice({Quote(System.Text.Json.JsonSerializer.Serialize(request, EngineSettings.JsonOptions))})");
                    }
                    Line($"__kne_node = ({{{string.Join(", ", node.Options.Select(o => Quote(o.Target!)))}}})[answer]");
                    break;
                case ScriptNodeKind.Condition:
                case ScriptNodeKind.Branching:
                case ScriptNodeKind.CompareVariables:
                case ScriptNodeKind.CheckFlag:
                    Line($"if {Comparison(node)} then __kne_node = {Quote(node.True!)} else __kne_node = {Quote(node.False!)} end");
                    break;
                case ScriptNodeKind.Switch:
                    for (int i = 0; i < node.Options.Count; i++)
                    {
                        var option = node.Options[i];
                        Line($"{(i == 0 ? "if" : "elseif")} state[{Quote(node.Variable)}] == {CaseLiteral(node, option.Text)} then");
                        Line($"    {RunBody(option.Target!, node.Next!)}");
                    }
                    Line($"else {RunBody(node.False!, node.Next!)} end");
                    Line($"__kne_node = {Quote(node.Next!)}");
                    break;
                case ScriptNodeKind.While:
                    Line("local __kne_iterations = 0");
                    Line("while true do");
                    Line($"    if kne_runtime_node then kne_runtime_node({Quote(node.Id)}) end");
                    Line($"    if not ({Comparison(node)}) then break end");
                    Line("    __kne_iterations = __kne_iterations + 1");
                    Line("    if __kne_iterations > 10000 then error('Цикл While превысил 10000 итераций') end");
                    Line($"    {RunBody(node.True!, node.Next!)}");
                    Line("end");
                    Line($"__kne_node = {Quote(node.Next!)}");
                    break;
                case ScriptNodeKind.ForEach:
                    Line($"local __kne_collection = state[{Quote(node.Path)}]");
                    Line("if type(__kne_collection) ~= 'table' then error('For Each требует массив в state') end");
                    Line("local __kne_size = 0");
                    Line("for __kne_key in pairs(__kne_collection) do");
                    Line("    __kne_size = __kne_size + 1");
                    Line("    if __kne_size > 10000 or type(__kne_key) ~= 'number' or __kne_key % 1 ~= 0 or __kne_key < 1 or __kne_key > 10000 then error('For Each требует массив с индексами от 1 до 10000') end");
                    Line("end");
                    Line("for __kne_index = 1, __kne_size do if __kne_collection[__kne_index] == nil then error('For Each не допускает пропущенных индексов') end end");
                    Line("for __kne_index, __kne_value in ipairs(__kne_collection) do");
                    Line($"    state[{Quote(node.Variable)}] = __kne_value; state[{Quote(node.IterationKey)}] = __kne_index");
                    Line($"    {RunBody(node.True!, node.Next!)}");
                    Line("end");
                    Line($"__kne_node = {Quote(node.Next!)}");
                    break;
                case ScriptNodeKind.TryCatch:
                    Line($"local __kne_ok, __kne_error = pcall(function() {(anchorTransfers ? "return " : "")}__kne_run({Quote(node.True!)}, {Quote(node.Next!)}) end)");
                    if (anchorTransfers)
                        Line("if __kne_ok and __kne_error then return __kne_error end");
                    Line("if not __kne_ok then");
                    Line($"    state[{Quote(node.Variable)}] = tostring(__kne_error)");
                    Line($"    {RunBody(node.False!, node.Next!)}");
                    Line("end");
                    Line($"__kne_node = {Quote(node.Next!)}");
                    break;
                case ScriptNodeKind.Parallel:
                case ScriptNodeKind.Async:
                    string branches = string.Join(", ", node.Options.Select(o => anchorTransfers ? $"function() __kne_flow({Quote(o.Target!)}, {Quote(node.Next!)}) end" : $"function() __kne_run({Quote(o.Target!)}, {Quote(node.Next!)}) end"));
                    Line(node.Kind == ScriptNodeKind.Parallel ? $"kne_parallel({{{branches}}})" : $"state[{Quote(node.Variable)}] = kne_async({{{branches}}})");
                    Line($"__kne_node = {Quote(node.Next!)}");
                    break;
                default:
                    Line("__kne_node = " + (node.Kind is ScriptNodeKind.End or ScriptNodeKind.GotoScene || node.Next == null ? "nil" : Quote(node.Next)));
                    break;
            }
        }
        code.Append("            else\n                error('Неизвестный блок')\n            end\n        end\n    end\n");
        if (anchorTransfers) code.Append("    __kne_flow = function(entry, stop)\n        while entry do entry = __kne_run(entry, stop) end\n    end\n");
        foreach (var trigger in Nodes.Where(node => node.IsTrigger && !connectedTriggers.Contains(node.Id)))
            code.Append("    kne_on_event(").Append(Quote(System.Text.Json.JsonSerializer.Serialize(trigger.Trigger, EngineSettings.JsonOptions)))
                .Append(anchorTransfers ? ", function() __kne_flow(" : ", function() __kne_run(").Append(Quote(trigger.Next!)).Append(", nil) end)\n");
        code.Append(anchorTransfers ? "    __kne_flow(" : "    __kne_run(").Append(Quote(starts[0].Id)).Append(", nil)\nend\n");
        return code.ToString();

        string RunBody(string entry, string stop) => anchorTransfers
            ? $"local __kne_jump = __kne_run({Quote(entry)}, {Quote(stop)}); if __kne_jump then return __kne_jump end"
            : $"__kne_run({Quote(entry)}, {Quote(stop)})";
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Command(ScriptNode node) => node.Kind switch
    {
        _ when CalculationCompiler.IsCalculation(node.Kind) => CalculationCompiler.Compile(node.Kind, node.Calculation ?? new CalculationSettings(), node.Variable),
        ScriptNodeKind.Dialogue or ScriptNodeKind.AutoDialogue or ScriptNodeKind.EffectDialogue => DialogueCommand(node),
        ScriptNodeKind.RegisterCharacter => $"register_character({Quote(System.Text.Json.JsonSerializer.Serialize(node.Character ?? new CharacterProfile(), EngineSettings.JsonOptions))})",
        ScriptNodeKind.UpdateCharacter => $"update_character({Quote(System.Text.Json.JsonSerializer.Serialize(node.Character ?? new CharacterProfile(), EngineSettings.JsonOptions))})",
        ScriptNodeKind.SetSpeaker => $"set_speaker({Quote(node.SpeakerId)})",
        ScriptNodeKind.ResetSpeakers => "reset_speakers()",
        ScriptNodeKind.TextPause => $"text_pause({Number(node.Duration)}, {(node.Skippable ? "true" : "false")})",
        ScriptNodeKind.ClearText => $"clear_text({(node.Animated ? "true" : "false")}, {Number(node.Duration)})",
        ScriptNodeKind.ConsoleWrite => $"console_write({Quote(node.Text)})",
        ScriptNodeKind.SetVariable => $"state[{Quote(node.Variable)}] = {Literal(node)}",
        ScriptNodeKind.SetFlag => $"state[{Quote(node.Variable)}] = {FlagLiteral(node)}",
        ScriptNodeKind.Random => RandomCommand(node),
        ScriptNodeKind.Background => $"background({(node.Path.Length == 0 ? "nil" : Quote(node.Path))})",
        ScriptNodeKind.HideBackground => "hide_background()",
        ScriptNodeKind.Character => $"show_character({Quote(node.Path)}, {Quote(node.Position)})",
        ScriptNodeKind.HideCharacter => "hide_character()",
        ScriptNodeKind.Music => $"play_audio({Quote(node.Channel)}, {Quote(node.Path)}, {(node.Loop ? "true" : "false")})",
        ScriptNodeKind.StopMusic => "stop_music()",
        ScriptNodeKind.Sound => $"play_audio({Quote(node.Channel == "music" && node.Category.Length == 0 ? "sfx" : node.Channel)}, {Quote(node.Path)}, false)",
        ScriptNodeKind.StopSound => $"stop_audio({Quote(node.Channel)})",
        ScriptNodeKind.Voice => $"play_audio('voice', {Quote(node.Path)}, false)",
        ScriptNodeKind.AudioChannel => $"set_audio_channel({Quote(node.Channel)}, {Number(node.Amount)}, {(node.Loop ? "true" : "false")})",
        ScriptNodeKind.MasterVolume => $"set_master_volume({Number(node.Amount)})",
        ScriptNodeKind.GotoScene => $"goto_scene({Quote(node.Path)})",
        ScriptNodeKind.CallScene => $"call_scene({Quote(node.Path)})",
        ScriptNodeKind.ConditionalGoto => $"if {Comparison(node)} then goto_scene({Quote(node.Path)}) end",
        ScriptNodeKind.Anchor => "",
        ScriptNodeKind.Subsystem => $"kne_subsystem({Quote(node.Path)}, {(node.WaitForSubsystem ? "true" : "false")})",
        ScriptNodeKind.Title => $"show_title({Quote(node.Text)})",
        ScriptNodeKind.MoveBackground => $"move_visual('background', {Number(node.OffsetX)}, {Number(node.OffsetY)}, {Number(node.Duration)})",
        ScriptNodeKind.MoveCharacter => $"move_visual('sprite', {Number(node.OffsetX)}, {Number(node.OffsetY)}, {Number(node.Duration)})",
        ScriptNodeKind.Animation => $"animate_visual({Quote(node.Target)}, {Quote(node.Text)}, {Number(node.Duration)}, {Number(node.Amount)})",
        ScriptNodeKind.Shake => $"screen_shake({Number(node.Amount)}, {Number(node.Duration)})",
        ScriptNodeKind.Flash => $"screen_flash({Quote(node.Value)}, {Number(node.Duration)})",
        ScriptNodeKind.Camera => $"camera({Number(node.Amount)}, {Number(node.OffsetX)}, {Number(node.OffsetY)}, {Number(node.Duration)})",
        ScriptNodeKind.ShowScreen => $"show_screen({Quote(node.Path)}, {Number(node.Opacity)}, {Quote(node.ScreenLayer)})",
        ScriptNodeKind.ClearBackgrounds => "clear_backgrounds()",
        ScriptNodeKind.ClearSprites => "clear_sprites()",
        ScriptNodeKind.IncludeLua => $"include_lua({Quote(node.Path)})",
        ScriptNodeKind.IncludeCs => $"include_cs({Quote(node.Path)})",
        ScriptNodeKind.ShaderContainer => $"apply_shader({Quote(node.Path)}, {(node.Shader?.Enabled == false ? "false" : "true")})",
        ScriptNodeKind.RemoveLayerShaders => $"clear_layer_shaders({Quote(node.Target)}, {Quote(node.Path)}, {(node.SuppressShadersUntilContainer ? "true" : "false")})",
        ScriptNodeKind.RemoveAllShaders => $"clear_shaders({(node.SuppressShadersUntilContainer ? "true" : "false")})",
        ScriptNodeKind.Script => $"run_script({Quote(node.Path)})",
        ScriptNodeKind.ConfigureDialogue or ScriptNodeKind.ConfigureButton => $"configure_ui({Quote(node.Kind == ScriptNodeKind.ConfigureDialogue ? "dialogue" : node.UiTarget)}, {Quote(System.Text.Json.JsonSerializer.Serialize(node.Ui ?? GameplayUiStyle.ForTarget(node.Kind == ScriptNodeKind.ConfigureDialogue ? "dialogue" : node.UiTarget), EngineSettings.JsonOptions))})",
        ScriptNodeKind.Join => $"kne_join(state[{Quote(node.Variable)}])",
        ScriptNodeKind.Timer or ScriptNodeKind.Pause => $"kne_pause({Number(node.Duration)})",
        ScriptNodeKind.WaitCondition => $"kne_wait_condition(function() return {Comparison(node)} end, {Number(node.Duration)})",
        ScriptNodeKind.End when node.EndAction != ScriptEndAction.Return => $"kne_end({Quote(node.EndAction.ToString().ToLowerInvariant())}, {Quote(node.Path)})",
        _ => ""
    };

    private static string DialogueCommand(ScriptNode node)
    {
        string speaker = node.Speaker.Length == 0 ? "nil" : Quote(node.Speaker);
        if (node.Kind == ScriptNodeKind.Dialogue && node.SpeakerId.Length == 0 && !node.Narration) return $"d({speaker}, {Quote(node.Text)})";
        string profile = node.SpeakerId.Length == 0 ? "nil" : Quote(node.SpeakerId);
        string effect = node.Kind == ScriptNodeKind.EffectDialogue
            ? Quote(System.Text.Json.JsonSerializer.Serialize(node.TextEffect ?? new TextEffectSettings(), EngineSettings.JsonOptions)) : "nil";
        return $"kne_say({speaker}, {Quote(node.Text)}, {profile}, {effect}, {(node.Kind == ScriptNodeKind.AutoDialogue ? Number(node.Duration) : "nil")}, {(node.Skippable ? "true" : "false")}{(node.Narration ? ", true" : "")})";
    }

    private static string Comparison(ScriptNode node)
    {
        string op = node.Comparison switch
        {
            ScriptComparison.Equal => "==", ScriptComparison.NotEqual => "~=", ScriptComparison.Greater => ">",
            ScriptComparison.Less => "<", ScriptComparison.GreaterOrEqual => ">=", _ => "<="
        };
        return $"state[{Quote(node.Variable)}] {op} {(node.Kind == ScriptNodeKind.CompareVariables ? $"state[{Quote(node.CompareVariable)}]" : node.Kind == ScriptNodeKind.CheckFlag ? FlagLiteral(node) : Literal(node))}";
    }

    private static string RandomCommand(ScriptNode node)
    {
        string low = Number(node.Minimum), high = Number(node.Maximum);
        if (node.Minimum == node.Maximum) return $"state[{Quote(node.Variable)}] = {low}";
        if (Math.Truncate(node.Minimum) == node.Minimum && Math.Truncate(node.Maximum) == node.Maximum &&
            Math.Abs(node.Minimum) <= 9007199254740991 && Math.Abs(node.Maximum) <= 9007199254740991)
            return $"state[{Quote(node.Variable)}] = math.random({low}, {high})";
        // Weighted interpolation stays finite even when high-low overflows a double.
        return $"local __kne_random = math.random(); state[{Quote(node.Variable)}] = {low} * (1 - __kne_random) + {high} * __kne_random";
    }

    private static string FlagLiteral(ScriptNode node)
    {
        if (bool.TryParse(node.Value, out bool value)) return value ? "true" : "false";
        Fail(node, "значение флага должно быть true/false");
        return "false";
    }

    private static string CaseLiteral(ScriptNode node, string value) => Literal(new ScriptNode
        { Id = node.Id, Kind = node.Kind, ValueKind = node.ValueKind, Value = value });

    private static void RequireKey(ScriptNode node, string value)
    {
        if (value.Length is < 1 or > 80 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || char.IsAsciiDigit(value[0]))
            Fail(node, "имя переменной: латинские буквы, цифры и _, без цифры в начале");
    }

    private static void ValidateCommand(ScriptNode node)
    {
        if (CalculationCompiler.IsCalculation(node.Kind))
        {
            try { _ = CalculationCompiler.Compile(node.Kind, node.Calculation ?? new CalculationSettings(), node.Variable); }
            catch (ArgumentException error) { Fail(node, error.Message); }
        }
        if (node.Kind == ScriptNodeKind.Subsystem && (string.IsNullOrWhiteSpace(node.Path) || !ScriptDocument.IsVisualPath(node.Path))) Fail(node, "подсистема должна ссылаться на файл .knegraph");
        if (node.IsTrigger)
        {
            if (node.Trigger == null) Fail(node, "настройте событие триггера");
            string expected = node.Kind switch { ScriptNodeKind.TriggerButton => "button", ScriptNodeKind.TriggerHotspot => "hotspot", ScriptNodeKind.TriggerTextClosed => "text_closed", _ => "key" };
            if (node.Trigger!.Kind != expected) Fail(node, "тип события не соответствует блоку триггера");
            if (node.Kind == ScriptNodeKind.TriggerKey && TriggerSettings.NormalizeKeys(node.Trigger.Keys).Contains('+')) Fail(node, "для сочетания используйте «При комбинации нажатий»");
        }
        if (node.Kind == ScriptNodeKind.RegisterCharacter && node.Character == null) Fail(node, "задайте профиль персонажа");
        if (node.Kind == ScriptNodeKind.UpdateCharacter && node.Character == null) Fail(node, "задайте новый профиль с ID существующего персонажа");
        if (node.Kind == ScriptNodeKind.SetSpeaker && string.IsNullOrWhiteSpace(node.SpeakerId)) Fail(node, "выберите ID зарегистрированного персонажа");
        if (node.Kind == ScriptNodeKind.CompareVariables) RequireKey(node, node.CompareVariable);
        if (node.Kind == ScriptNodeKind.RandomBranch && node.Next != null) Fail(node, "у случайной ветки нет общего продолжения: соедините каждый выход отдельно");
        if (node.Kind == ScriptNodeKind.ConfigureButton && (node.UiTarget == "dialogue" || !GameplayUiStyle.IsTarget(node.UiTarget)))
            Fail(node, "выберите игровую кнопку: choice, next, save, load, auto_save или pause");
        if (node.Kind == ScriptNodeKind.ShowScreen && (node.Opacity < 0 || node.Opacity > 1 || node.ScreenLayer is not ("main" or "overlay")))
            Fail(node, "прозрачность должна быть от 0 до 1, слой — main или overlay");
        if (node.Kind == ScriptNodeKind.ShaderContainer && !ScriptDocument.IsShaderPath(node.Path)) Fail(node, "выберите файл .kneshader");
        if (node.Kind == ScriptNodeKind.RemoveLayerShaders && (node.Target is not ("scene" or "background" or "sprite" or "ui" or "screen") || node.Target == "screen" && (string.IsNullOrWhiteSpace(node.Path) || node.Path.Length > 128))) Fail(node, "выберите слой и укажите название экрана для цели «Экран»");
        if (node.Kind is ScriptNodeKind.IncludeLua or ScriptNodeKind.IncludeCs or ScriptNodeKind.Script)
        {
            string extension = Path.GetExtension(node.Path).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(node.Path) || (node.Kind == ScriptNodeKind.IncludeLua && extension != ".lua") ||
                (node.Kind == ScriptNodeKind.IncludeCs && extension != ".cs") ||
                (node.Kind == ScriptNodeKind.Script && extension is not (".lua" or ".krl" or ".cs")))
                Fail(node, "выберите файл .lua / .cs, для скрипта также доступен .krl");
        }
        if (node.Kind is ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice or ScriptNodeKind.Switch or ScriptNodeKind.Parallel or ScriptNodeKind.Async or ScriptNodeKind.RandomBranch &&
            (node.Options.Count == 0 || node.Options.Any(o => string.IsNullOrWhiteSpace(o.Text))))
            Fail(node, "укажите от 1 до 20 непустых вариантов выбора или ветвей");
        if (node.Kind == ScriptNodeKind.TimedChoice)
        {
            try { (node.ChoiceSettings ?? new ChoiceSettings()).Validate(node.Options.Count); }
            catch (ArgumentException error) { Fail(node, error.Message); }
        }
        if (node.Kind is ScriptNodeKind.Condition or ScriptNodeKind.ConditionalGoto or ScriptNodeKind.Branching or ScriptNodeKind.CompareVariables or ScriptNodeKind.SetVariable or ScriptNodeKind.Switch or ScriptNodeKind.While or
            ScriptNodeKind.ForEach or ScriptNodeKind.TryCatch or ScriptNodeKind.SetFlag or ScriptNodeKind.CheckFlag or ScriptNodeKind.Random or
            ScriptNodeKind.Async or ScriptNodeKind.Join or ScriptNodeKind.WaitCondition) RequireKey(node, node.Variable);
        if (node.Kind is ScriptNodeKind.Condition or ScriptNodeKind.Branching or ScriptNodeKind.ConditionalGoto or ScriptNodeKind.SetVariable or ScriptNodeKind.While or ScriptNodeKind.WaitCondition)
        {
            _ = Literal(node);
            if (node.Kind != ScriptNodeKind.SetVariable && node.Comparison is not (ScriptComparison.Equal or ScriptComparison.NotEqual) && node.ValueKind != ScriptValueKind.Number)
                Fail(node, "сравнения >, <, >=, <= требуют числового значения");
        }
        if (node.Kind is ScriptNodeKind.SetFlag or ScriptNodeKind.CheckFlag) _ = FlagLiteral(node);
        if (node.Kind == ScriptNodeKind.CheckFlag && node.Comparison is not (ScriptComparison.Equal or ScriptComparison.NotEqual))
            Fail(node, "флаги поддерживают только равенство и неравенство");
        if (node.Kind == ScriptNodeKind.Switch)
        {
            var values = node.Options.Select(o => CaseLiteral(node, o.Text)).ToArray();
            if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) Fail(node, "значения Case должны быть уникальны");
        }
        if (node.Kind == ScriptNodeKind.ForEach)
        {
            RequireKey(node, node.Path); RequireKey(node, node.IterationKey);
            if (node.Variable == node.IterationKey || node.Variable == node.Path || node.IterationKey == node.Path)
                Fail(node, "массив, элемент и индекс For Each требуют разных имён");
        }
        if (node.Kind == ScriptNodeKind.Random && node.Minimum > node.Maximum) Fail(node, "минимум случайного диапазона не должен превышать максимум");
        if (node.Duration is < 0 or > 3600) Fail(node, "длительность должна быть от 0 до 3600 секунд");
        if (node.Kind is ScriptNodeKind.AudioChannel or ScriptNodeKind.StopSound or ScriptNodeKind.Sound or ScriptNodeKind.Music && node.Channel is not ("music" or "sfx" or "voice" or "ambient"))
            Fail(node, "канал аудио: music, sfx, voice или ambient");
        if (node.Kind is ScriptNodeKind.MasterVolume or ScriptNodeKind.AudioChannel && node.Amount is < 0 or > 1)
            Fail(node, "громкость должна быть от 0 до 1");
        if (node.Kind == ScriptNodeKind.Camera && node.Amount is < .05 or > 20) Fail(node, "масштаб камеры должен быть от 0.05 до 20");
        if (node.Kind == ScriptNodeKind.Shake && node.Amount is < 0 or > 500) Fail(node, "сила тряски должна быть от 0 до 500");
        if (node.Kind is ScriptNodeKind.Shake or ScriptNodeKind.Flash && node.Duration > 60) Fail(node, "длительность вспышки или тряски должна быть не больше 60 секунд");
        if (node.Kind is ScriptNodeKind.MoveBackground or ScriptNodeKind.MoveCharacter or ScriptNodeKind.Camera &&
            (Math.Abs(node.OffsetX) > 100000 || Math.Abs(node.OffsetY) > 100000)) Fail(node, "смещения должны быть от −100000 до 100000");
        if (node.Kind == ScriptNodeKind.Animation && node.Amount is < 0 or > 360) Fail(node, "сила анимации должна быть от 0 до 360");
        if (node.Kind == ScriptNodeKind.Title && node.Text.Length > 10000) Fail(node, "титр допускает не больше 10000 символов");
        if (node.Kind == ScriptNodeKind.Flash)
        {
            try
            {
                if (System.Windows.Media.ColorConverter.ConvertFromString(node.Value) is not System.Windows.Media.Color)
                    Fail(node, "укажите корректный цвет вспышки, например #FFFFFF");
            }
            catch (Exception error) when (error is FormatException or NotSupportedException or ArgumentException)
            { Fail(node, "укажите корректный цвет вспышки, например #FFFFFF"); }
        }
        if (node.Kind == ScriptNodeKind.Animation && node.Target is not ("background" or "sprite")) Fail(node, "цель анимации: background или sprite");
        if (node.Kind == ScriptNodeKind.Animation && node.Text is not ("fade_in" or "fade_out" or "pulse" or "rotate" or "none"))
            Fail(node, "анимация: fade_in, fade_out, pulse, rotate или none");
        if (node.Kind is ScriptNodeKind.Character or ScriptNodeKind.Music or ScriptNodeKind.Sound or ScriptNodeKind.Voice && string.IsNullOrWhiteSpace(node.Path))
            Fail(node, "укажите путь к ресурсу");
        if (node.Kind == ScriptNodeKind.End && node.EndAction == ScriptEndAction.Screen)
            if (string.IsNullOrWhiteSpace(node.Path)) Fail(node, "укажите идентификатор экрана");
        if (node.Kind is ScriptNodeKind.GotoScene or ScriptNodeKind.CallScene or ScriptNodeKind.ConditionalGoto)
        {
            if (string.IsNullOrWhiteSpace(node.Path)) Fail(node, "укажите имя функции");
            if (!LuaSource.IsIdentifier(node.Path)) Fail(node, "цель перехода должна быть именем функции, например chapter, без пути к файлу");
        }
        if (node.Kind == ScriptNodeKind.GotoScene && node.Next != null) Fail(node, "у постоянного перехода нет продолжения в этой сцене");
        if (node.Kind == ScriptNodeKind.Anchor && (string.IsNullOrWhiteSpace(node.Text) || node.Text.Length > 160)) Fail(node, "задайте имя якоря длиной до 160 символов");
        if (node.Kind == ScriptNodeKind.SendFlow && node.Next != null) Fail(node, "у отправки потока нет обычного продолжения");
        if (node.Kind == ScriptNodeKind.Character && node.Position is not ("left" or "center" or "right")) Fail(node, "позиция персонажа: left, center или right");
    }
}
