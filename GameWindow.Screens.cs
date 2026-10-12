using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KNE;

public partial class GameWindow
{
    private GamePreferences preferences = new();
    private bool preferencesDirty;
    private string? activeScreen;
    private readonly Stack<string> screenHistory = new();
    private readonly List<(Button Button, string Action)> screenButtons = [];
    private bool openingSaveBrowser;

    private void LoadPreferences()
    {
        try
        {
            string path = PathSafety.ResolveRelative(saves.DirectoryPath, "preferences.json");
            if (File.Exists(path) && new FileInfo(path).Length < 16 * 1024)
                preferences = JsonSerializer.Deserialize<GamePreferences>(File.ReadAllText(path)) ?? new();
            preferences.MusicVolume = double.IsFinite(preferences.MusicVolume) ? Math.Clamp(preferences.MusicVolume, 0, 1) : 0.8;
            preferences.SoundVolume = double.IsFinite(preferences.SoundVolume) ? Math.Clamp(preferences.SoundVolume, 0, 1) : 0.8;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { QueueOutput("Настройки игрока не прочитаны: " + ex.Message); }
        music.Volume = preferences.MusicVolume; sound.Volume = preferences.SoundVolume;
    }

    private void FlushPreferences()
    {
        if (!preferencesDirty) return;
        string path = PathSafety.ResolveRelative(saves.DirectoryPath, "preferences.json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(saves.DirectoryPath);
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences));
            PathSafety.RejectLinks(path); File.Move(temporary, path, true); preferencesDirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { preferencesDirty = false; QueueOutput("Настройки игрока не сохранены: " + ex.Message); Notify("Не удалось сохранить настройки игрока."); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string ResolveScreenText(string text) => text.Replace("{{title}}", project.Title).Replace("{{subtitle}}", project.Engine.Game.Subtitle)
        .Replace("{{start_label}}", project.Engine.Game.StartLabel).Replace("{{continue_label}}", project.Engine.Game.ContinueLabel).Replace("{{exit_label}}", project.Engine.Game.ExitLabel);

    private void ShowLayout(string id)
    {
        var layout = project.Engine.Screens.Screens.FirstOrDefault(s => s.Id == id);
        if (layout == null) { Notify("Экран не найден."); return; }
        activeScreen = id;
        MenuOverlay.Visibility = Visibility.Visible; DialoguePanel.Visibility = Visibility.Collapsed; GameControls.Visibility = Visibility.Collapsed;
        ScreenViewbox.Visibility = Visibility.Visible; SaveBrowserPanel.Visibility = Visibility.Collapsed;
        MenuScroll.Visibility = Visibility.Collapsed;
        ScreenCanvas.Children.Clear(); screenButtons.Clear();
        foreach (var element in layout.Elements)
        {
            FrameworkElement view;
            // Reuse named controls for their established game actions and keyboard/accessibility behavior.
            Button? known = element.Type == "button" ? element.Action switch
            {
                "start" => StartButton, "continue" => ContinueButton, "resume" => ResumeButton, "exit" => ExitButton,
                "save_slot" => SaveSelectedButton, "load_slot" => LoadSelectedButton, "delete_slot" => DeleteSelectedButton, _ => null
            } : null;
            TextBlock? knownText = element.Type == "text" ? element.Id switch { "title" => MenuTitle, "subtitle" => MenuSubtitle, _ => null } : null;
            if (known != null && !ScreenCanvas.Children.Contains(known))
            {
                if (known.Parent is Panel panel) panel.Children.Remove(known);
                known.Content = ResolveScreenText(element.Text); known.FontSize = element.FontSize; known.Visibility = Visibility.Visible; view = known;
            }
            else if (knownText != null && !ScreenCanvas.Children.Contains(knownText))
            {
                if (knownText.Parent is Panel panel) panel.Children.Remove(knownText);
                knownText.Text = ResolveScreenText(element.Text); knownText.FontSize = element.FontSize; view = knownText;
            }
            else if (element.Type is "save_list" or "save_name")
            {
                view = element.Type == "save_list" ? SaveSlotsList : SaveSlotName;
                if (view.Parent is Panel panel) panel.Children.Remove(view);
            }
            else
            {
                try { view = ScreenRenderer.Create(element, project.Engine.Theme, project.Path, ResolveScreenText); }
                catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FormatException)
                { QueueOutput("Ошибка элемента оформления: " + ex.Message); Notify("Не удалось показать элемент «" + element.Text + "»."); continue; }
                if (view is Button button) button.Click += async (_, _) => await DispatchScreenActionAsync(element);
            }
            view.Width = element.Width; view.Height = element.Height; Canvas.SetLeft(view, element.X); Canvas.SetTop(view, element.Y);
            view.Margin = new Thickness(0);
            view.Tag = element;
            if (view is Button actionButton) screenButtons.Add((actionButton, element.Action));
            if (view is CheckBox toggle && element.Action is "fullscreen" or "animation")
            {
                toggle.IsChecked = element.Action == "fullscreen" ? fullscreen : preferences.Animate;
                toggle.Click += (_, _) =>
                {
                    if (element.Action == "fullscreen") { ToggleFullscreen(); toggle.IsChecked = fullscreen; }
                    else { preferences.Animate = toggle.IsChecked == true; preferencesDirty = true; AnimatedBackdrop.Configure(project.Engine.Theme, preferences.Animate); ShowLayout(id); }
                };
            }
            if (view is StackPanel stack && element.Type == "slider" && element.Action is "music_volume" or "sound_volume")
            {
                var slider = stack.Children.OfType<Slider>().Single();
                slider.Value = element.Action == "music_volume" ? preferences.MusicVolume : preferences.SoundVolume;
                slider.ValueChanged += (_, _) =>
                {
                    if (element.Action == "music_volume") { preferences.MusicVolume = slider.Value; music.Volume = slider.Value; }
                    else { preferences.SoundVolume = slider.Value; sound.Volume = slider.Value; }
                    preferencesDirty = true;
                };
            }
            ScreenCanvas.Children.Add(view);
            ScreenRenderer.Animate(view, element, preferences.Animate && project.Engine.Theme.AnimationEnabled);
        }
        UpdateScreenButtons();
        MenuOverlay.BeginAnimation(OpacityProperty, null); MenuOverlay.Opacity = 1;
        if (preferences.Animate && project.Engine.Theme.AnimationEnabled)
            MenuOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25)));
    }

    private async Task DispatchScreenActionAsync(ScreenElement element)
    {
        if (execution != null) return;
        switch (element.Action)
        {
            case "start": StartButton_Click(StartButton, new()); break;
            case "continue": await LoadGameAsync(saves.Exists("auto") ? "auto" : "quick"); break;
            case "resume": ShowGameplay(); break;
            case "save": SaveButton_Click(SaveButton, new()); break;
            case "saves": await ShowSaveBrowserAsync(); break;
            case "settings": Navigate(project.Engine.Screens.Find("settings")?.Id); break;
            case "navigate":
                if (project.Engine.Screens.Screens.FirstOrDefault(s => s.Id == element.Target)?.Kind == "saves") await ShowSaveBrowserAsync();
                else Navigate(element.Target);
                break;
            case "back": GoBackScreen(); break;
            case "exit": Close(); break;
            case "fullscreen": ToggleFullscreen(); break;
            case "save_slot": SaveSelected_Click(SaveSelectedButton, new()); break;
            case "load_slot": LoadSelected_Click(LoadSelectedButton, new()); break;
            case "delete_slot": DeleteSelected_Click(DeleteSelectedButton, new()); break;
            case "animation": preferences.Animate = !preferences.Animate; preferencesDirty = true; AnimatedBackdrop.Configure(project.Engine.Theme, preferences.Animate); if (activeScreen != null) ShowLayout(activeScreen); break;
        }
    }
    private void Navigate(string? target)
    {
        if (target == null) { Notify("Экран отключён в оформлении."); return; }
        screenHistory.Push(activeScreen ?? "gameplay"); ShowLayout(target);
    }
    private void GoBackScreen()
    {
        string previous = screenHistory.TryPop(out string? value) ? value : ready ? "gameplay" : project.Engine.Screens.Find("main_menu")?.Id ?? "gameplay";
        if (previous == "gameplay") ShowGameplay(); else ShowLayout(previous);
    }
    private void ScreenBack_Click(object sender, RoutedEventArgs e) => GoBackScreen();
    private void UpdateScreenButtons()
    {
        foreach (var (button, action) in screenButtons)
            button.IsEnabled = execution == null && action switch
            {
                "resume" or "save" => ready,
                "continue" => saves.Exists("auto") || saves.Exists("quick"),
                "settings" => project.Engine.Screens.Find("settings") != null,
                "saves" => project.Engine.Screens.Find("saves") != null,
                "save_slot" => ready,
                "load_slot" => SaveSlotsList.SelectedItem is GameSaveEntry { Damaged: false },
                "delete_slot" => SaveSlotsList.SelectedItem != null,
                _ => true
            };
    }

    private async Task ShowSaveBrowserAsync()
    {
        if (execution != null || openingSaveBrowser || closed) return;
        openingSaveBrowser = true;
        try
        {
            var slots = await saves.ListAsync();
            if (closed) return;
            var layout = project.Engine.Screens.Find("saves");
            if (layout == null) { Notify("Экран сохранений отключён."); return; }
            screenHistory.Push(activeScreen ?? "gameplay");
            SaveSlotsList.ItemsSource = slots; SaveSelectedButton.IsEnabled = ready; LoadSelectedButton.IsEnabled = DeleteSelectedButton.IsEnabled = false;
            ShowLayout(layout.Id);
        }
        catch (Exception ex) { Notify("Не удалось открыть сохранения: " + ex.Message); }
        finally { openingSaveBrowser = false; }
    }
    private void SaveSlot_Changed(object sender, SelectionChangedEventArgs e)
    {
        var slot = SaveSlotsList.SelectedItem as GameSaveEntry;
        if (slot != null) SaveSlotName.Text = slot.Slot;
        LoadSelectedButton.IsEnabled = slot != null && !slot.Damaged; DeleteSelectedButton.IsEnabled = slot != null;
    }
    private async void SaveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || execution != null) return;
        string slot = SaveSlotName.Text.Trim();
        bool saved = false;
        await ExecuteAsync(async () => { await saves.SaveAsync(slot, await runtime.GetCheckpointAsync(execution!.Token), execution.Token); saved = true; });
        if (saved) { SaveSlotsList.ItemsSource = await saves.ListAsync(); Notify("Сохранено: " + slot); }
    }
    private async void LoadSelected_Click(object sender, RoutedEventArgs e)
    { if (SaveSlotsList.SelectedItem is GameSaveEntry slot) await LoadGameAsync(slot.Slot); }
    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SaveSlotsList.SelectedItem is not GameSaveEntry slot || execution != null) return;
        if (MessageBox.Show(this, "Удалить слот «" + slot.Slot + "»?", "Сохранения", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { saves.Delete(slot.Slot); SaveSlotsList.ItemsSource = await saves.ListAsync(); }
        catch (Exception ex) { Notify(ex.Message); }
    }
}

public sealed class GamePreferences
{
    public bool Animate { get; set; } = true;
    public bool? Fullscreen { get; set; }
    public double MusicVolume { get; set; } = 0.8;
    public double SoundVolume { get; set; } = 0.8;
}
