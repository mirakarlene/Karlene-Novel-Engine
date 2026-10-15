using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KNE;

public partial class AppearanceEditor : UserControl
{
    public AppearanceDocument? Document { get; private set; }
    private ProjectInfo? project;
    private GameScreen? screen;
    private ScreenElement? selected;
    private readonly Stack<string> undo = new();
    private readonly Stack<string> redo = new();
    private bool filling;
    private bool fieldsDirty;
    private Point? dragStart;
    private Point dragOrigin;
    private string? dragSnapshot;
    public bool IsDirty => Document?.IsDirty == true || fieldsDirty;

    public AppearanceEditor()
    {
        InitializeComponent();
        ElementAction.ItemsSource = new[] { new AppearanceChoice("none", "Без действия"), new("start", "Новая игра"), new("continue", "Продолжить"),
            new("resume", "Вернуться к игре"), new("save", "Быстрое сохранение"), new("saves", "Выбор сохранений"), new("settings", "Настройки"),
            new("back", "Назад"), new("exit", "Выйти"), new("navigate", "Перейти на экран"), new("fullscreen", "Полный экран"),
            new("animation", "Анимация"), new("music_volume", "Громкость музыки"), new("sound_volume", "Громкость звука"),
            new("save_slot", "Записать ручной слот"), new("load_slot", "Загрузить выбранный слот"), new("delete_slot", "Удалить выбранный слот") };
        ScreenRole.ItemsSource = new[] { new AppearanceChoice("custom", "Свой экран"), new("main_menu", "Главное меню"), new("pause", "Пауза"), new("settings", "Настройки"), new("saves", "Сохранения"), new("gameplay", "Игровая сцена") };
        PaletteChoice.ItemsSource = new[] { new AppearanceChoice("violet", "Фиолетовая"), new("ocean", "Океан"), new("rose", "Розовая"), new("forest", "Лес"), new("amber", "Янтарь"), new("custom", "Своя") };
        PatternChoice.ItemsSource = new[] { new AppearanceChoice("waves", "Синусоиды"), new("ornament", "Орнамент"), new("grid", "Сетка"), new("dots", "Точки"), new("none", "Без узора") };
        PaletteChoice.SelectionChanged += (_, _) =>
        {
            if (filling || Document == null || PaletteChoice.SelectedValue is not string palette) return;
            var theme = new GameTheme(); ThemePalettes.Apply(theme, palette);
            if (palette != "custom") { BackgroundColor.Text = theme.BackgroundColor; PatternColor.Text = theme.PatternColor; }
        };
        foreach (var text in new[] { ScreenName, ElementText, ElementImage, ElementX, ElementY, ElementWidth, ElementHeight, ElementFont,
            MotionOrder, MotionX, MotionY, MotionDelay, MotionDuration, BackgroundColor, PatternColor, BackgroundPath, AnimationSpeed, WaveAmplitude })
            text.TextChanged += (_, _) => { if (!filling) fieldsDirty = true; };
        foreach (var choice in new[] { ElementAction, TargetScreen, PaletteChoice, PatternChoice, ScreenRole })
            choice.SelectionChanged += (_, _) => { if (!filling) fieldsDirty = true; };
        MotionLoop.Click += (_, _) => fieldsDirty = true;
        AnimateBackground.Click += (_, _) => fieldsDirty = true;
    }

    public async Task OpenAsync(ProjectInfo value)
    {
        project = value; Document = await AppearanceDocument.OpenAsync(value);
        undo.Clear(); redo.Clear(); Refresh(Document.Layout.Screens.FirstOrDefault()?.Id);
        EditorStatus.Text = "Оформление · изменения применяются при следующем запуске игры";
    }

    public async Task<bool> CanLeaveAsync()
    {
        if (!IsDirty) return true;
        var choice = MessageBox.Show(Window.GetWindow(this), "Сохранить изменения оформления?", "Оформление", MessageBoxButton.YesNoCancel);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) await SaveAsync();
        return true;
    }

    public async Task SaveAsync()
    {
        if (Document == null) return;
        if (fieldsDirty && !ApplyFields()) throw new InvalidOperationException(EditorStatus.Text);
        await Document.SaveAsync(); fieldsDirty = false;
        EditorStatus.Text = "Оформление сохранено в engine/screens.json";
    }

    private bool Commit(Action edit)
    {
        if (Document == null || project == null) return false;
        string before = Document.Layout.Serialize(); string? id = screen?.Id; string? elementId = selected?.Id;
        try
        {
            edit(); Document.Layout.Validate();
            EngineSettings.ValidateAppearance(project.Path, Document.Layout.Theme!);
            string after = Document.Layout.Serialize();
            if (before != after) { undo.Push(before); if (undo.Count > 100) { var recent = undo.Take(100).Reverse().ToArray(); undo.Clear(); foreach (var item in recent) undo.Push(item); } redo.Clear(); }
            Refresh(screen?.Id ?? id, selected?.Id); EditorStatus.Text = "Изменения не сохранены";
            return true;
        }
        catch (Exception ex)
        {
            Document.Restore(before); Refresh(id, elementId); EditorStatus.Text = ex.Message; return false;
        }
    }

    private void Refresh(string? id = null, string? elementId = null)
    {
        if (Document == null) return;
        filling = true;
        ScreensList.ItemsSource = null; ScreensList.ItemsSource = Document.Layout.Screens;
        screen = Document.Layout.Screens.FirstOrDefault(s => s.Id == id) ?? Document.Layout.Screens.FirstOrDefault();
        ScreensList.SelectedItem = screen;
        ElementsList.ItemsSource = null; ElementsList.ItemsSource = screen?.Elements;
        selected = screen?.Elements.FirstOrDefault(e => e.Id == elementId);
        ElementsList.SelectedItem = selected;
        TargetScreen.ItemsSource = Document.Layout.Screens.Where(s => s.Kind != "gameplay").ToArray();
        ScreenName.Text = screen?.Name ?? "";
        ScreenRole.SelectedValue = screen?.Kind;
        ScreenRole.IsEnabled = screen?.Kind != "gameplay";
        FillFields(); FillTheme(); filling = false; fieldsDirty = false; Draw(false);
    }

    private void FillFields()
    {
        var element = selected ?? new ScreenElement();
        ElementText.Text = element.Text; ElementImage.Text = element.Image ?? ""; ElementAction.SelectedValue = element.Action; TargetScreen.SelectedValue = element.Target;
        ElementX.Text = Number(element.X); ElementY.Text = Number(element.Y); ElementWidth.Text = Number(element.Width); ElementHeight.Text = Number(element.Height); ElementFont.Text = Number(element.FontSize);
        MotionX.Text = Number(element.MotionX); MotionY.Text = Number(element.MotionY); MotionDelay.Text = Number(element.Delay); MotionDuration.Text = Number(element.Duration);
        MotionOrder.Text = element.MotionOrder.ToString(CultureInfo.InvariantCulture); MotionLoop.IsChecked = element.Loop;
        ApplyElementButton.IsEnabled = selected != null && screen?.Kind != "gameplay";
    }
    private void FillTheme()
    {
        var theme = Document!.Layout.Theme!;
        PaletteChoice.SelectedValue = theme.Palette; PatternChoice.SelectedValue = theme.Pattern ? theme.PatternStyle : "none";
        BackgroundColor.Text = theme.BackgroundColor; PatternColor.Text = theme.PatternColor; BackgroundPath.Text = theme.BackgroundImage ?? "";
        AnimateBackground.IsChecked = theme.AnimationEnabled; AnimationSpeed.Text = Number(theme.AnimationSpeed); WaveAmplitude.Text = Number(theme.WaveAmplitude);
    }

    private void Draw(bool animate)
    {
        DesignCanvas.Children.Clear();
        if (Document == null || screen == null || project == null) return;
        var theme = Document.Layout.Theme!;
        PreviewBackground.Background = EnginePresentation.CreateBackground(theme);
        AnimatedPreview.Configure(theme, animate);
        try
        {
            var imageElement = new ScreenElement { Type = "image", Image = theme.BackgroundImage };
            PreviewImage.Source = ((Image)ScreenRenderer.Create(imageElement, theme, project.Path, Resolve)).Source;
            AnimatedPreview.Visibility = theme.BackgroundImage == null ? Visibility.Visible : Visibility.Collapsed;
            foreach (var element in screen.Elements)
            {
                var view = ScreenRenderer.Create(element, theme, project.Path, Resolve);
                if (!animate) view.IsHitTestVisible = false;
                DesignCanvas.Children.Add(view);
                if (animate) ScreenRenderer.Animate(view, element, theme.AnimationEnabled);
                if (element == selected && !animate)
                {
                    var outline = new Rectangle { Width = element.Width, Height = element.Height, Stroke = Brushes.White, StrokeThickness = 2, StrokeDashArray = [4, 3], IsHitTestVisible = false };
                    Canvas.SetLeft(outline, element.X); Canvas.SetTop(outline, element.Y); DesignCanvas.Children.Add(outline);
                    if (element.MotionX != 0 || element.MotionY != 0)
                    {
                        double x = element.X + element.Width / 2, y = element.Y + element.Height / 2;
                        DesignCanvas.Children.Add(new Line { X1 = x + element.MotionX, Y1 = y + element.MotionY, X2 = x, Y2 = y,
                            Stroke = Brushes.LightSkyBlue, StrokeThickness = 3, StrokeDashArray = [3, 2], IsHitTestVisible = false });
                        var endpoint = new Ellipse { Width = 10, Height = 10, Fill = Brushes.LightSkyBlue, IsHitTestVisible = false };
                        Canvas.SetLeft(endpoint, x - 5); Canvas.SetTop(endpoint, y - 5); DesignCanvas.Children.Add(endpoint);
                    }
                }
            }
            if (screen.Kind == "gameplay")
            {
                DesignCanvas.Children.Add(new TextBlock { Text = "Игровой текст и визуал редактируются в Lua / блок-схемах.\nДля оформления меню выберите другой экран.", Foreground = Brushes.White, FontSize = 26, Margin = new Thickness(100, 300, 0, 0) });
            }
        }
        catch (Exception ex) { EditorStatus.Text = ex.Message; }
    }

    private string Resolve(string text) => text.Replace("{{title}}", project!.Title).Replace("{{subtitle}}", project!.Engine.Game.Subtitle)
        .Replace("{{start_label}}", project.Engine.Game.StartLabel).Replace("{{continue_label}}", project.Engine.Game.ContinueLabel).Replace("{{exit_label}}", project.Engine.Game.ExitLabel);
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static double Value(TextBox input) => double.Parse(input.Text.Replace(',', '.'), CultureInfo.InvariantCulture);
    private void Screens_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (filling || Document == null) return;
        string? next = (ScreensList.SelectedItem as GameScreen)?.Id;
        if (fieldsDirty && !ApplyFields()) return;
        Refresh(next);
    }
    private void Elements_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (filling) return;
        var next = ElementsList.SelectedItem as ScreenElement;
        if (fieldsDirty && !ApplyFields()) return;
        selected = next == null ? null : screen?.Elements.FirstOrDefault(x => x.Id == next.Id);
        filling = true; ElementsList.SelectedItem = selected; FillFields(); filling = false; fieldsDirty = false; Draw(false);
    }

    private void ReadElement()
    {
        if (selected == null || screen?.Kind == "gameplay") return;
        selected.Text = ElementText.Text; selected.Action = ElementAction.SelectedValue as string ?? "none";
        selected.Target = TargetScreen.SelectedValue as string; selected.Image = string.IsNullOrWhiteSpace(ElementImage.Text) ? null : ElementImage.Text;
        selected.X = Value(ElementX); selected.Y = Value(ElementY); selected.Width = Value(ElementWidth); selected.Height = Value(ElementHeight); selected.FontSize = Value(ElementFont);
        selected.MotionX = Value(MotionX); selected.MotionY = Value(MotionY); selected.Delay = Value(MotionDelay); selected.Duration = Value(MotionDuration);
        selected.MotionOrder = int.Parse(MotionOrder.Text, CultureInfo.InvariantCulture); selected.Loop = MotionLoop.IsChecked == true;
    }
    private void ReadTheme()
    {
        var theme = Document!.Layout.Theme!;
        string palette = PaletteChoice.SelectedValue as string ?? "custom";
        if (palette != theme.Palette) ThemePalettes.Apply(theme, palette);
        theme.BackgroundColor = BackgroundColor.Text; theme.PatternColor = PatternColor.Text; theme.BackgroundImage = string.IsNullOrWhiteSpace(BackgroundPath.Text) ? null : BackgroundPath.Text;
        theme.PatternStyle = PatternChoice.SelectedValue as string ?? "waves"; theme.Pattern = theme.PatternStyle != "none";
        theme.AnimationEnabled = AnimateBackground.IsChecked == true; theme.AnimationSpeed = Value(AnimationSpeed); theme.WaveAmplitude = Value(WaveAmplitude);
    }
    private bool ApplyFields() => Commit(() =>
    {
        ReadElement(); ReadTheme();
        if (screen != null) { screen.Name = ScreenName.Text; if (screen.Kind != "gameplay") screen.Kind = ScreenRole.SelectedValue as string ?? "custom"; }
    });
    private void ApplyElement_Click(object sender, RoutedEventArgs e) => ApplyFields();
    private void ApplyTheme_Click(object sender, RoutedEventArgs e) => ApplyFields();
    private async void Save_Click(object sender, RoutedEventArgs e) { try { await SaveAsync(); } catch (Exception ex) { EditorStatus.Text = ex.Message; } }
    private void AddScreen_Click(object sender, RoutedEventArgs e)
    {
        string? added = null;
        string name = "Новый экран"; int suffix = 2;
        while (Document!.Layout.Screens.Any(item => item.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))) name = "Новый экран " + suffix++;
        if (Commit(() => added = Document!.Layout.AddScreen(name).Id)) { Refresh(added); ScreenName.Focus(); ScreenName.SelectAll(); }
    }
    private void RenameScreen_Click(object sender, RoutedEventArgs e) => Commit(() => { if (screen != null) screen.Name = ScreenName.Text; });
    private void DeleteScreen_Click(object sender, RoutedEventArgs e) => Commit(() => { if (screen != null) Document!.Layout.RemoveScreen(screen.Id); });
    private void AddElement_Click(object sender, RoutedEventArgs e)
    {
        if (fieldsDirty && !ApplyFields()) return;
        Commit(() =>
        {
            if (screen == null || screen.Kind == "gameplay") throw new InvalidOperationException("Выберите экран вне геймплея.");
            string type = ((ComboBoxItem)NewElementType.SelectedItem).Tag.ToString()!;
            selected = new() { Type = type, Text = type == "text" ? "Текст" : "Новый элемент", Action = "none" };
            screen.Elements.Add(selected);
        });
    }
    private void DeleteElement_Click(object sender, RoutedEventArgs e) => Commit(() => { if (screen?.Kind != "gameplay" && selected != null) screen?.Elements.Remove(selected); });
    private void ChangeLayer(int offset) => Commit(() =>
    {
        if (screen == null || selected == null) return;
        int old = screen.Elements.IndexOf(selected); int next = Math.Clamp(old + offset, 0, screen.Elements.Count - 1);
        screen.Elements.RemoveAt(old); screen.Elements.Insert(next, selected);
    });
    private void RaiseLayer_Click(object sender, RoutedEventArgs e) => ChangeLayer(1);
    private void LowerLayer_Click(object sender, RoutedEventArgs e) => ChangeLayer(-1);
    private void Undo_Click(object sender, RoutedEventArgs e) { if (Document != null && undo.TryPop(out string? value)) { redo.Push(Document.Layout.Serialize()); Document.Restore(value); Refresh(screen?.Id); } }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (Document != null && redo.TryPop(out string? value)) { undo.Push(Document.Layout.Serialize()); Document.Restore(value); Refresh(screen?.Id); } }
    private void PreviewMotion_Click(object sender, RoutedEventArgs e) { if (fieldsDirty && !ApplyFields()) return; Draw(true); }

    private void Canvas_Down(object sender, MouseButtonEventArgs e)
    {
        if (screen == null || Document == null || screen.Kind == "gameplay") return;
        Point point = e.GetPosition(DesignCanvas);
        var element = screen.Elements.LastOrDefault(x => point.X >= x.X && point.X <= x.X + x.Width && point.Y >= x.Y && point.Y <= x.Y + x.Height);
        if (fieldsDirty && !ApplyFields()) return;
        selected = element == null ? null : screen.Elements.FirstOrDefault(x => x.Id == element.Id);
        filling = true; ElementsList.SelectedItem = selected; FillFields(); filling = false; fieldsDirty = false;
        if (selected != null) { dragStart = point; dragOrigin = new(selected.X, selected.Y); dragSnapshot = Document.Layout.Serialize(); DesignCanvas.CaptureMouse(); }
        Draw(false); e.Handled = true;
    }
    public void MoveSelected(double x, double y)
    {
        if (selected == null) return;
        selected.X = Math.Clamp(x, 0, 1280 - selected.Width); selected.Y = Math.Clamp(y, 0, 720 - selected.Height);
        filling = true; FillFields(); filling = false; Draw(false);
    }
    private void Canvas_Move(object sender, MouseEventArgs e)
    {
        if (dragStart is not { } start || selected == null || e.LeftButton != MouseButtonState.Pressed) return;
        Point point = e.GetPosition(DesignCanvas); MoveSelected(dragOrigin.X + point.X - start.X, dragOrigin.Y + point.Y - start.Y);
    }
    private void EndDrag()
    {
        if (dragSnapshot != null && Document?.Layout.Serialize() != dragSnapshot) { undo.Push(dragSnapshot); redo.Clear(); }
        dragStart = null; dragSnapshot = null;
    }
    private void Canvas_Up(object sender, MouseButtonEventArgs e) { EndDrag(); DesignCanvas.ReleaseMouseCapture(); }
    private void Canvas_LostCapture(object sender, MouseEventArgs e) => EndDrag();
}

public sealed record AppearanceChoice(string Value, string Label);
