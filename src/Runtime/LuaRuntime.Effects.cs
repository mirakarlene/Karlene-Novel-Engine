using System.Globalization;
using System.Windows.Media;
using NLua;

namespace KNE;

public sealed partial class LuaRuntime
{
    private bool TryInvokeEffect(string command, LuaTable args, out string problem)
    {
        problem = "";
        string Text(int i) => args[i] as string ?? throw new ArgumentException("Аргумент " + i + " должен быть строкой.");
        double Number(int i, double min, double max)
        {
            if (args[i] is not (double or long or int)) throw new ArgumentException("Аргумент " + i + " должен быть числом.");
            double value = Convert.ToDouble(args[i], CultureInfo.InvariantCulture);
            if (!double.IsFinite(value) || value < min || value > max) throw new ArgumentException($"Аргумент {i}: допустимо от {min} до {max}.");
            return value;
        }
        bool Flag(int i, bool fallback) => args[i] is null ? fallback : args[i] is bool value ? value : throw new ArgumentException("Нужен true/false.");
        try
        {
            switch (command)
            {
                case "clear_backgrounds":
                    lock (stateLock) visuals = visuals with { Background = null, BackgroundsCleared = true, BackgroundMotion = new(), BackgroundAnimation = new() };
                    break;
                case "clear_sprites":
                    lock (stateLock) visuals = visuals with { Character = null, CharacterMotion = new(), CharacterAnimation = new() };
                    break;
                case "show_title":
                    string title = InlineText.ToMarkup(ResolveInline(Text(1)));
                    if (title.Length > 10000) throw new ArgumentException("Титр слишком длинный.");
                    lock (stateLock) visuals = visuals with { Title = title };
                    break;
                case "hide_background": SetBackground(null); break;
                case "move_visual":
                    string target = VisualTarget(Text(1));
                    double x = Number(2, -100000, 100000), y = Number(3, -100000, 100000), duration = Number(4, 0, 3600);
                    lock (stateLock)
                        visuals = target == "background" ? visuals with { BackgroundMotion = new(x, y, duration, visuals.BackgroundMotion.Version + 1) }
                            : visuals with { CharacterMotion = new(x, y, duration, visuals.CharacterMotion.Version + 1) };
                    break;
                case "animate_visual":
                    target = VisualTarget(Text(1)); string kind = Text(2);
                    if (kind is not ("fade_in" or "fade_out" or "pulse" or "rotate" or "none")) throw new ArgumentException("Анимация: fade_in, fade_out, pulse, rotate или none.");
                    duration = Number(3, 0, 3600); double amount = Number(4, 0, 360);
                    lock (stateLock)
                        visuals = target == "background" ? visuals with { BackgroundAnimation = new(kind, duration, amount, visuals.BackgroundAnimation.Version + 1) }
                            : visuals with { CharacterAnimation = new(kind, duration, amount, visuals.CharacterAnimation.Version + 1) };
                    break;
                case "screen_shake":
                    double strength = Number(1, 0, 500); duration = Number(2, 0, 60);
                    lock (stateLock) visuals = visuals with { Shake = new(strength, duration, visuals.Shake.Version + 1) };
                    break;
                case "screen_flash":
                    string color = Text(1);
                    if (string.IsNullOrWhiteSpace(color)) throw new ArgumentException("Нужен цвет вспышки.");
                    _ = (Color)ColorConverter.ConvertFromString(color);
                    duration = Number(2, 0, 60);
                    lock (stateLock) visuals = visuals with { Flash = new(color, duration, visuals.Flash.Version + 1) };
                    break;
                case "camera":
                    double zoom = Number(1, 0.05, 20); x = Number(2, -100000, 100000); y = Number(3, -100000, 100000); duration = Number(4, 0, 3600);
                    lock (stateLock) visuals = visuals with { Camera = new(zoom, x, y, duration, visuals.Camera.Version + 1) };
                    break;
                case "show_screen":
                    string id = Text(1);
                    double opacity = args[2] == null ? 1 : Number(2, 0, 1);
                    string layer = args[3] == null ? "overlay" : Text(3);
                    if (layer is not ("main" or "overlay")) throw new ArgumentException("Слой экрана: main или overlay.");
                    if (id.Length == 0)
                    {
                        lock (stateLock) visuals = visuals with { OverlayScreen = null, ScreenOpacity = opacity, ScreenLayer = layer, ScreenVersion = visuals.ScreenVersion + 1 };
                        break;
                    }
                    var screen = project.Engine.Screens.ResolveReference(id);
                    var screenTexts = screen.Elements.Select(element => element.Text).Distinct(StringComparer.Ordinal).ToDictionary(text => text,
                        text => InlineText.ToMarkup(ResolveInline(text.Replace("{{title}}", project.Title).Replace("{{subtitle}}", project.Engine.Game.Subtitle)
                            .Replace("{{start_label}}", project.Engine.Game.StartLabel).Replace("{{continue_label}}", project.Engine.Game.ContinueLabel).Replace("{{exit_label}}", project.Engine.Game.ExitLabel))), StringComparer.Ordinal);
                    foreach (var element in screen.Elements.Where(element => element.Type == "image"))
                        if (!string.IsNullOrWhiteSpace(element.Image)) ResourcePaths.Resolve(project.Path, element.Image);
                    lock (stateLock) visuals = visuals with { OverlayScreen = screen.Id, ScreenOpacity = opacity, ScreenLayer = layer, ScreenTexts = screenTexts, ScreenVersion = visuals.ScreenVersion + 1 };
                    break;
                case "set_audio_channel":
                    string channel = AudioChannel(Text(1)); double volume = Number(2, 0, 1); bool loop = Flag(3, false);
                    UpdateAudio(channel, current => current with { Volume = volume, Loop = loop });
                    break;
                case "play_audio":
                    channel = AudioChannel(Text(1)); string path = ResourcePaths.Resolve(project.Path, Text(2), audio: true); loop = Flag(3, channel is "music" or "ambient");
                    UpdateAudio(channel, current => current with { Path = path, Loop = loop, Version = current.Version + 1 });
                    break;
                case "stop_audio":
                    channel = AudioChannel(Text(1)); UpdateAudio(channel, current => current with { Path = null, Version = current.Version + 1 });
                    break;
                case "set_master_volume":
                    volume = Number(1, 0, 1); lock (stateLock) visuals = visuals with { MasterVolume = volume };
                    break;
                default: return false;
            }
        }
        catch (Exception error) when (error is System.IO.IOException or ArgumentException or FormatException or NotSupportedException or UnauthorizedAccessException)
        { problem = command + ": " + error.Message; }
        return true;
    }

    private static string VisualTarget(string target) => target switch
    { "background" => target, "sprite" or "character" => "character", _ => throw new ArgumentException("Цель: background или sprite.") };
    private static string AudioChannel(string channel) => channel is "music" or "sfx" or "voice" or "ambient" ? channel : throw new ArgumentException("Канал: music, sfx, voice или ambient.");
    private void UpdateAudio(string channel, Func<AudioChannelState, AudioChannelState> update)
    {
        lock (stateLock)
        {
            var previous = visuals.AudioChannels.FirstOrDefault(item => item.Channel == channel) ?? new(channel);
            var next = update(previous);
            visuals = visuals with { AudioChannels = visuals.AudioChannels.Where(item => item.Channel != channel).Append(next).ToArray() };
            if (channel == "music") visuals = visuals with { Music = next.Path, LoopMusic = next.Loop, MusicVersion = next.Version };
            if (channel == "sfx") visuals = visuals with { Sound = next.Path, SoundVersion = next.Version };
        }
    }
}
