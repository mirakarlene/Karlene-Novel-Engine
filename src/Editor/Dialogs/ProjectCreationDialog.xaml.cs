using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace KNE;

/// <summary>Project wizard with authoring examples followed by window, graphics and palette choices.</summary>
public partial class ProjectCreationDialog : Window
{
    public ProjectAuthoringMode AuthoringMode => VisualModeCard.IsChecked == true ? ProjectAuthoringMode.Visual : ProjectAuthoringMode.Code;
    private ProjectResolution? resolution = ProjectCreationOptions.Resolutions[0];
    private string palette = "violet";
    /// <summary>The validated choices submitted on the second wizard page.</summary>
    public ProjectCreationOptions Options
    {
        get
        {
            int width = resolution?.Width ?? Dimension(CustomWidthInput.Text, "Ширина");
            int height = resolution?.Height ?? Dimension(CustomHeightInput.Text, "Высота");
            var options = new ProjectCreationOptions { Width = width, Height = height, GraphicsApi = (string?)GraphicsApiInput.SelectedValue ?? "auto", MaxFps = (int?)FrameRateInput.SelectedValue ?? 60, Palette = palette };
            options.Validate(); return options;
        }
    }
    public ProjectCreationDialog()
    {
        InitializeComponent();
        GraphicsApiInput.ItemsSource = GraphicsSettings.Apis; GraphicsApiInput.SelectedValue = "auto";
        FrameRateInput.ItemsSource = GraphicsSettings.FrameRates; FrameRateInput.SelectedValue = 60;
        foreach (var preset in ProjectCreationOptions.Resolutions) AddResolution(preset);
        AddResolution(null);
        foreach (var item in ProjectCreationOptions.Palettes) AddPalette(item);
    }
    private static int Dimension(string text, string name) => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : throw new InvalidDataException(name + ": введите целое число пикселей.");
    private RadioButton Card(string name, string group, object content)
    {
        var card = new RadioButton { Name = name, GroupName = group, Content = content, Style = (Style)FindResource("SetupCard") };
        RegisterName(name, card); return card;
    }
    private void AddResolution(ProjectResolution? preset)
    {
        var body = new StackPanel();
        var canvas = new Canvas { Width = 160, Height = 72, HorizontalAlignment = HorizontalAlignment.Center };
        double width = (preset?.Width ?? 1920) * .035, height = (preset?.Height ?? 1080) * .035;
        var screen = new Border { Width = width, Height = height, CornerRadius = new(3), BorderThickness = new(1.5), BorderBrush = new SolidColorBrush(Color.FromRgb(167, 144, 241)), Background = new LinearGradientBrush(Color.FromRgb(59, 40, 95), Color.FromRgb(124, 92, 255), 45) };
        Canvas.SetLeft(screen, (160 - width) / 2); Canvas.SetTop(screen, (72 - height) / 2); canvas.Children.Add(screen);
        screen.Child = new TextBlock { Text = preset == null ? "?" : "KNE", Foreground = Brushes.White, FontSize = 9, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(canvas);
        body.Children.Add(new TextBlock { Text = preset?.Title ?? "Свой вариант", FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 3) });
        body.Children.Add(new TextBlock { Text = preset?.Description ?? "Укажите ширину и высоту вручную", FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        var card = Card("Resolution_" + (preset?.Id ?? "custom"), "Resolution", body); card.Tag = preset;
        card.Checked += (_, _) => { resolution = preset; CustomResolutionFields.IsEnabled = preset == null; SetupErrorText.Text = ""; };
        ResolutionCards.Children.Add(card); if (preset?.Id == "fhd") card.IsChecked = true;
    }
    private void AddPalette(GraphicsOption<string> item)
    {
        var theme = new GameTheme(); if (item.Value != "violet") ThemePalettes.Apply(theme, item.Value);
        SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
        var body = new StackPanel(); var preview = new Border { Height = 28, CornerRadius = new(5), Background = Brush(theme.BackgroundColor), Padding = new(7) };
        var swatches = new UniformGrid { Columns = 3 };
        foreach (string color in new[] { theme.PatternColor, theme.AccentColor, theme.ButtonColor }) swatches.Children.Add(new Border { Background = Brush(color), Margin = new(3, 0, 3, 0), CornerRadius = new(2) });
        preview.Child = swatches; body.Children.Add(preview); body.Children.Add(new TextBlock { Text = item.Label, FontSize = 12, Margin = new(0, 3, 0, 0) });
        var card = Card("Palette_" + item.Value, "Palette", body); card.Tag = item.Value;
        card.Checked += (_, _) => palette = item.Value; PaletteCards.Children.Add(card); card.IsChecked = item.Value == "violet";
    }
    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        AuthoringPage.Visibility = Visibility.Collapsed; SettingsPage.Visibility = Visibility.Visible;
        Title = "Параметры новой игры"; Width = Math.Min(1040, SystemParameters.WorkArea.Width - 24); Height = Math.Min(740, SystemParameters.WorkArea.Height - 24);
        MinWidth = Math.Min(760, Width); MinHeight = Math.Min(520, Height);
        ContinueButton.IsDefault = false; CreateButton.IsDefault = true; CreateButton.Focus();
    }
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SettingsPage.Visibility = Visibility.Collapsed; AuthoringPage.Visibility = Visibility.Visible;
        Title = "Способ работы над проектом"; Width = 860; Height = 550;
        CreateButton.IsDefault = false; ContinueButton.IsDefault = true; ContinueButton.Focus();
    }
    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadOptions(out _)) return;
        DialogResult = true;
    }
    internal bool TryReadOptions(out ProjectCreationOptions? options)
    {
        try { options = Options; SetupErrorText.Text = ""; return true; }
        catch (InvalidDataException error) { options = null; SetupErrorText.Text = error.Message; return false; }
    }
}
