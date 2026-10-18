using System.Windows;
using System.Windows.Media;

namespace KNE;

/// <summary>Applies editor-only dynamic colors without changing the authored game palette.</summary>
public static class EditorTheme
{
    private const string Marker = "KNE.ThemeLoaded";
    /// <summary>Installs a light or dark palette in a window and updates existing editor descendants.</summary>
    public static void Apply(Window window, string theme)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.Resources.Contains(Marker))
        {
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/KNE;component/src/Editor/Themes/EditorStyles.xaml", UriKind.Relative) });
            window.Resources[Marker] = true;
        }
        bool dark = theme == "dark";
        window.Resources["Editor.ThemeName"] = dark ? "dark" : "light";
        void Brush(string key, string light, string night) => window.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
        Brush("Editor.Background", "#F7F6FA", "#191C24");
        Brush("Editor.Surface", "#FFFFFF", "#242935");
        Brush("Editor.Canvas", "#F0F3F7", "#171B23");
        Brush("Editor.Text", "#202733", "#E5EAF3");
        Brush("Editor.Muted", "#536174", "#AAB6CA");
        Brush("Editor.Border", "#B9C3D2", "#56647B");
        Brush("Editor.Hover", "#E6EAF3", "#354158");
        Brush("Editor.Selection", "#CCDEF9", "#37577F");
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
