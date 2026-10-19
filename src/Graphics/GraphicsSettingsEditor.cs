using System.Windows;
using System.Windows.Controls;

namespace KNE;

/// <summary>Shared controls for editor and game rendering preferences.</summary>
public sealed class GraphicsSettingsEditor : StackPanel
{
    internal ComboBox ApiInput { get; } = Choice(GraphicsSettings.Apis);
    internal ComboBox FpsInput { get; } = Choice(GraphicsSettings.FrameRates);
    internal ComboBox MsaaInput { get; } = Choice(GraphicsSettings.Antialiasing);
    internal ComboBox TextureInput { get; } = Choice(GraphicsSettings.TextureQualities);
    internal CheckBox VSyncInput { get; } = new() { Content = "Вертикальная синхронизация (VSync)", Margin = new(0, 8, 0, 8) };
    internal CheckBox LimitCacheInput { get; } = new() { Content = "Ограничить кэш ресурсов", Margin = new(0, 8, 0, 4) };
    internal ComboBox CacheLimitInput { get; } = Choice(GraphicsSettings.CacheLimits);
    public GraphicsSettingsEditor()
    {
        Add("Графический API", ApiInput); Children.Add(VSyncInput); Add("Ограничение FPS", FpsInput); Add("Сглаживание", MsaaInput);
        Children.Add(LimitCacheInput); Children.Add(CacheLimitInput);
        LimitCacheInput.Checked += (_, _) => CacheLimitInput.IsEnabled = true; LimitCacheInput.Unchecked += (_, _) => CacheLimitInput.IsEnabled = false;
        Children.Add(new TextBlock { Text = "Кэш хранит подготовленные ресурсы. Лимит не включает активные изображения и память драйвера. API готовит графику один раз; движение выполняет композитор WPF.", TextWrapping = TextWrapping.Wrap, Opacity = .65, FontSize = 11, Margin = new(0, 4, 0, 4) });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8, Margin = new(0, 0, 0, 8) }; Children.Add(status);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void UpdateStatus()
        {
            if (Window.GetWindow(this) is not { } window) return;
            var metrics = GraphicsEnvironment.For(window);
            status.Text = metrics.Error != null ? "Резервный рендер WPF: " + metrics.Error : metrics.Backend.Length == 0 ? "" : "Действующий API этого окна: " + metrics.Backend;
        }
        timer.Tick += (_, _) => UpdateStatus();
        Loaded += (_, _) => { UpdateStatus(); if (IsVisible) timer.Start(); }; Unloaded += (_, _) => timer.Stop();
        IsVisibleChanged += (_, _) => { if (IsLoaded && IsVisible) { UpdateStatus(); timer.Start(); } else timer.Stop(); };
        Value = new();
    }
    public GraphicsSettings Value
    {
        get => new() { Api = (string?)ApiInput.SelectedValue ?? "auto", MaxFps = (int?)FpsInput.SelectedValue ?? 60, MsaaSamples = (int?)MsaaInput.SelectedValue ?? 0, TextureQuality = (string?)TextureInput.SelectedValue ?? "original", VSync = VSyncInput.IsChecked == true, LimitResourceCache = LimitCacheInput.IsChecked == true, ResourceCacheLimitMb = (int?)CacheLimitInput.SelectedValue ?? 64 };
        set { var normalized = GraphicsSettings.Normalize(value); ApiInput.SelectedValue = normalized.Api; FpsInput.SelectedValue = normalized.MaxFps; MsaaInput.SelectedValue = normalized.MsaaSamples; TextureInput.SelectedValue = normalized.TextureQuality; VSyncInput.IsChecked = normalized.VSync; LimitCacheInput.IsChecked = normalized.LimitResourceCache; CacheLimitInput.SelectedValue = normalized.ResourceCacheLimitMb; CacheLimitInput.IsEnabled = normalized.LimitResourceCache; }
    }
    internal static ComboBox Choice<T>(IReadOnlyList<GraphicsOption<T>> options) => new() { ItemsSource = options, DisplayMemberPath = "Label", SelectedValuePath = "Value", Margin = new(0, 4, 0, 8), MinWidth = 180 };
    private void Add(string label, UIElement input) { Children.Add(new TextBlock { Text = label }); Children.Add(input); }
}
