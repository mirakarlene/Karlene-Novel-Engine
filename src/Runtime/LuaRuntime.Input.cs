using System.Text.Json;
using NLua;

namespace KNE;

public sealed partial class LuaRuntime
{
    private void InitializeTextInput()
    {
        Register(nameof(PrepareInputForLua), "kne_prepare_input");
        lua!.DoString("""
            function text_input(json)
                if not kne_interactive or not kne_scene_owned(coroutine.running()) then error("text_input() доступен только внутри интерактивной игровой сцены", 2) end
                if type(json) ~= "string" then error("text_input() требует JSON-конфигурацию", 2) end
                local result = kne_prepare_input(json)
                if result[1] ~= "" then error(result[1], 2) end
                return coroutine.yield(kne_event_location({kind = "input", input = result[0]}))
            end
            """, "KNE text input");
    }

    /// <summary>Validates an input configuration and returns canonical JSON and a Lua-safe error string.</summary>
    public string[] PrepareInputForLua(string json)
    {
        try
        {
            var settings = ReadNarrative<TextInputSettings>(json);
            settings.Validate();
            return [JsonSerializer.Serialize(settings, EngineSettings.JsonOptions), ""];
        }
        catch (Exception error) when (error is ArgumentException or JsonException)
        { return ["", "text_input: " + error.Message]; }
    }

    /// <summary>Submits answer data without evaluating it as Lua source, or follows an enabled cancel output.</summary>
    public Task SubmitInputAsync(string text, bool cancel = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 8192) throw new ArgumentException("Ответ слишком длинный.", nameof(text));
        return RunAsync(() =>
        {
            if (!initialized || Story.Kind != "input") throw new InvalidOperationException("Сценарий не ожидает ввода текста.");
            InstallHook();
            string before = FrameSignature(Story);
            SubmitInputCore(text, cancel);
            if (!replaying) history.Add(new StoryAction("input", before, Command: text, Cancel: cancel));
        }, cancellationToken, "Ввод ответа игрока");
    }

    private void SubmitInputCore(string text, bool cancel)
    {
        if (!initialized || Story.Kind != "input" || Story.Input is not { } settings)
            throw new InvalidOperationException("Сценарий не ожидает ввода текста.");
        if (text.Length > 8192) throw new ArgumentException("Ответ слишком длинный.", nameof(text));
        if (cancel)
        {
            if (!settings.BranchOnResult || !settings.HasCancelOutput) throw new InvalidOperationException("Отмена этого ввода недоступна.");
            ResumeStory("cancel");
        }
        else if (settings.ValidateAnswer(text))
        {
            using var state = lua!["state"] as LuaTable ?? throw new InvalidOperationException("state должен оставаться таблицей.");
            state[settings.Variable] = text;
            ResumeStory("success");
        }
        else if (settings.BranchOnResult && settings.HasErrorOutput) ResumeStory("error");
        else lock (stateLock) story = story with { InputFeedback = settings.FailureMessage };
    }

    private StoryFrame ReadInputFrame(StoryFrame frame, LuaTable value)
    {
        if (frame.Kind != "input") return frame;
        var settings = ReadNarrative<TextInputSettings>(value["input"] as string ?? "");
        settings.Validate();
        return frame with
        {
            Text = settings.Prompt,
            Input = settings,
            IsSkippable = false,
            InputFeedback = Story.Kind == "input" && Story.PresentationId == frame.PresentationId ? Story.InputFeedback : ""
        };
    }
}
