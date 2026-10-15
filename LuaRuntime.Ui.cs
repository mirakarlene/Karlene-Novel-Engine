using System.Text.Json;
using NLua;

namespace KNE;

public sealed partial class LuaRuntime
{
    private bool TryInvokeUi(string command, LuaTable args, out string problem)
    {
        problem = "";
        if (command != "configure_ui") return false;
        try
        {
            string target = args[1] as string ?? throw new ArgumentException("Нужно имя элемента UI.");
            if (!GameplayUiStyle.IsTarget(target)) throw new ArgumentException("Неизвестный элемент UI: " + target);
            string json = args[2] as string ?? throw new ArgumentException("Нужна JSON-конфигурация UI.");
            if (json.Length > 32768) throw new ArgumentException("Конфигурация UI слишком длинная.");
            var style = JsonSerializer.Deserialize<GameplayUiStyle>(json, GameplayUiStyle.JsonOptions)
                ?? throw new ArgumentException("Конфигурация UI не может быть null.");
            style.Validate(project.Path);
            lock (stateLock)
                visuals = visuals with
                {
                    UiConfigurations = visuals.UiConfigurations.Where(item => item.Target != target).Append(new(target, style)).ToArray(),
                    UiVersion = visuals.UiVersion + 1
                };
        }
        catch (Exception error) when (error is ArgumentException or JsonException or System.IO.IOException or UnauthorizedAccessException or FormatException or NotSupportedException)
        { problem = command + ": " + error.Message; }
        return true;
    }
}
