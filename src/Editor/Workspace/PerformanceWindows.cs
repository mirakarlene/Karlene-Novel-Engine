using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace KNE;

internal static class SceneMetrics
{
    private static readonly object sync = new();
    private static readonly Dictionary<string, (double Ms, int? Draws, int Objects)> values = new(StringComparer.OrdinalIgnoreCase);
    internal static void Record(string root, string scene, double milliseconds, int? draws, int objects)
    {
        lock (sync)
        {
            string key = Path.Combine(root, scene);
            if (values.Count >= 512 && !values.ContainsKey(key)) values.Remove(values.Keys.First());
            values[key] = (milliseconds, draws, objects);
        }
    }
    internal static string Describe(string root, string? scene)
    {
        lock (sync)
            return scene != null && values.TryGetValue(Path.Combine(root, scene), out var value)
                ? $"Логика: {value.Ms:F2} мс\nDraw Calls графического слоя: {(value.Draws?.ToString() ?? "н/д (WPF)")}\nОбъектов сцены: {value.Objects}" + (value.Objects > 100 ? "\nБольше 100 объектов: проверьте возможность объединения спрайтов." : "")
                : "Замеры появятся после запуска этой сцены. Draw Calls WPF и общая VRAM недоступны; они не подменяются количеством объектов.";
    }
}
