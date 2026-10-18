using System.Windows;
using System.Windows.Media;

namespace KNE;

/// <summary>A persisted editor palette option; the actual colors live in Theme.xaml.</summary>
public sealed record EditorThemeOption(string Id, string Label);

/// <summary>Applies editor-only dynamic colors without changing the authored game palette.</summary>
public static class EditorTheme
{
    private const string Marker = "KNE.ThemeLoaded";
    public static IReadOnlyList<EditorThemeOption> Themes { get; } = [new("kne", "KNE · акварель"), new("light", "Светлая"), new("dark", "Тёмная"), new("rose", "Розовый кварц"), new("ocean", "Ночной океан"), new("forest", "Лесной туман"), new("amber", "Тёплый янтарь")];
    public static string Normalize(string? theme) => Themes.Any(option => option.Id == theme) ? theme! : "kne";
    /// <summary>Installs a window-scoped palette and updates existing editor descendants.</summary>
    public static void Apply(Window window, string theme)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.Resources.Contains(Marker))
        {
            foreach (string file in new[] { "Theme.xaml", "EditorStyles.xaml" })
            {
                string uri = "/KNE;component/src/Editor/Themes/" + file;
                if (!window.Resources.MergedDictionaries.Any(dictionary => dictionary.Source?.OriginalString.EndsWith(file, StringComparison.Ordinal) == true))
                    window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(uri, UriKind.Relative) });
            }
            window.Resources[Marker] = true;
        }
        var palette = (ResourceDictionary)window.FindResource("Theme." + Normalize(theme));
        foreach (object key in palette.Keys)
            window.Resources[key] = palette[key] is Freezable value ? value.CloneCurrentValue() : palette[key];
        window.Resources[SystemColors.WindowBrushKey] = window.Resources["Editor.Surface"];
        window.Resources[SystemColors.WindowTextBrushKey] = window.Resources["Editor.Text"];
        window.Resources[SystemColors.ControlBrushKey] = window.Resources["Editor.Surface"];
        window.Resources[SystemColors.ControlTextBrushKey] = window.Resources["Editor.Text"];
        window.Resources[SystemColors.HighlightBrushKey] = window.Resources["Editor.Selection"];
        window.Resources[SystemColors.HighlightTextBrushKey] = window.Resources["Editor.Text"];
        window.SetResourceReference(Window.BackgroundProperty, "Editor.Background");
        window.SetResourceReference(Window.ForegroundProperty, "Editor.Text");
    }
}
