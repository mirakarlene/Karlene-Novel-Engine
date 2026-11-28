using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KNE;

public partial class GameWindow
{
    private GamePreferences preferences = new();
    private ProjectTranslations translations = ProjectTranslations.Empty;
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
            preferences.TextSpeed = Math.Clamp(preferences.TextSpeed, 0, DialogueSpeed.Maximum);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { QueueOutput("Настройки игрока не прочитаны: " + ex.Message); }
        try { translations = ProjectTranslations.Load(project, preferences.Language); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { translations = ProjectTranslations.Empty; QueueOutput("Перевод не загружен: " + ex.Message); }
        music.Volume = preferences.MusicVolume; sound.Volume = preferences.SoundVolume;
    }

    private void FlushPreferences()
    {
        if (!preferencesDirty || !SaveStorageWritable) return;
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

    private string ResolveScreenText(string text) => translations.ScreenText(project, GameTextCategory.Interface, text);

    private void ChangePresentationLanguage(string code)
    {
        translations = ProjectTranslations.Load(project, code);
        preferences.Language = code;
        preferencesDirty = true;
        if (runtime is ProcessGameSession session) session.PresentationLanguage = code;
        Title = translations.Translate(GameTextCategory.MainMenu, runtime.WindowSettings.Title);
        if (activeScreen is { } screen) ShowLayout(screen);
        FlushPreferences();
    }

    private void AddLanguageSelector()
    {
        string[] languages;
        try { languages = ProjectTranslations.Languages(project); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            QueueOutput("Список переводов недоступен: " + ex.Message);
            return;
        }
        if (languages.Length < 2) return;

        string current = ProjectTranslations.LanguageCode(preferences.Language) ?? languages[0];
        if (!languages.Contains(current)) current = languages[0];
        string title = ResolveScreenText(GameTextCatalog.LanguageSettingLabel);
        var label = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = 17,
            VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "GameTextBrush");
        var selector = new ComboBox
        {
            ItemsSource = languages.Select(code => new GameLanguageOption(code, ProjectTranslations.LanguageLabel(code))).ToArray(),
            DisplayMemberPath = nameof(GameLanguageOption.Label), SelectedValuePath = nameof(GameLanguageOption.Code),
            SelectedValue = current, MinWidth = 160, FontSize = 17, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(selector, title);
        bool restoringSelection = false;
        selector.SelectionChanged += (_, _) =>
        {
            if (restoringSelection || selector.SelectedValue is not string code) return;
            try { ChangePresentationLanguage(code); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                QueueOutput("Перевод не загружен: " + ex.Message);
                Notify(GameTextCatalog.LanguageLoadFailed);
                restoringSelection = true;
                try { selector.SelectedValue = current; }
                finally { restoringSelection = false; }
            }
        };

        var row = new Grid { Width = 400, Height = 48 };
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new() { Width = new GridLength(250) });
        row.Children.Add(label);
        Grid.SetColumn(selector, 1);
        row.Children.Add(selector);
        var occupied = ScreenCanvas.Children.OfType<FrameworkElement>()
            .Where(view => view.Tag is not ScreenElement { Type: "panel" or "image" })
            .Select(view => new Rect(Canvas.GetLeft(view), Canvas.GetTop(view), view.Width, view.Height)).ToArray();
        foreach (double x in new[] { 440, 24, 856 })
        foreach (double y in new[] { 474d, 660d }.Concat(Enumerable.Range(0, 11).Select(index => (double)index * 60 + 12)))
        {
            if (occupied.Any(bounds => bounds.IntersectsWith(new Rect(x, y, row.Width, row.Height)))) continue;
            Canvas.SetLeft(row, x);
            Canvas.SetTop(row, y);
            ScreenCanvas.Children.Add(row);
            return;
        }

        var footer = new StackPanel();
        if (DialogueSpeedFallback.Content is FrameworkElement existing)
        {
            DialogueSpeedFallback.Content = null;
            footer.Children.Add(existing);
        }
        footer.Children.Add(row);
        DialogueSpeedFallback.Content = footer;
        DialogueSpeedFallback.Visibility = Visibility.Visible;
        ScreenViewbox.Margin = new Thickness(0, 0, 0, ScreenViewbox.Margin.Bottom + row.Height +
            (footer.Children.Count == 1 ? DialogueSpeedFallback.Margin.Bottom : 0));
    }

    private sealed record GameLanguageOption(string Code, string Label);

    private void ShowLayout(string id)
    {
        if (startupFailed) return;
        var layout = project.Engine.Screens.Screens.FirstOrDefault(s => s.Id == id);
        if (layout == null) { Notify("Экран не найден."); return; }
        if (layout.Kind == "history")
        {
            if (runtime.DialogueJournal is { Limit: > 0 } journal) OpenHistory(journal);
            return;
        }
        ClearHistoryScreen();
        string ResolveLayoutText(string text) => translations.ScreenText(project, GameTextCatalog.ScreenCategory(layout), text);
        activeScreen = id;
        PauseDialogueReveal();
        MenuOverlay.Visibility = Visibility.Visible; DialoguePanel.Visibility = Visibility.Collapsed; GameControls.Visibility = Visibility.Collapsed;
        ScreenViewbox.Visibility = Visibility.Visible; SaveBrowserPanel.Visibility = Visibility.Collapsed;
        MenuScroll.Visibility = Visibility.Collapsed;
        ScreenCanvas.Children.Clear(); screenButtons.Clear();
        DialogueSpeedFallback.Content = null;
        DialogueSpeedFallback.Visibility = Visibility.Collapsed;
        ScreenViewbox.Margin = new Thickness();
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
                known.Content = ResolveLayoutText(element.Text); known.FontSize = element.FontSize; known.Visibility = Visibility.Visible; view = known;
            }
            else if (knownText != null && !ScreenCanvas.Children.Contains(knownText))
            {
                if (knownText.Parent is Panel panel) panel.Children.Remove(knownText);
                knownText.Text = ResolveLayoutText(element.Text); knownText.FontSize = element.FontSize; view = knownText;
            }
            else if (element.Type is "save_list" or "save_name")
            {
                view = element.Type == "save_list" ? SaveSlotsList : SaveSlotName;
                if (view.Parent is Panel panel) panel.Children.Remove(view);
            }
            else
            {
                try { view = ScreenRenderer.Create(element, project.Engine.Theme, project.Path, ResolveLayoutText, path => assets.Load(path)); }
                catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FormatException)
                { QueueOutput("Ошибка элемента оформления: " + ex.Message); Notify("Не удалось показать элемент «" + element.Text + "»."); continue; }
                if (view is Button button) button.Click += async (_, _) => await DispatchScreenActionAsync(element);
            }
            view.Width = element.Width; view.Height = element.Height; Canvas.SetLeft(view, element.X); Canvas.SetTop(view, element.Y);
            if (element.SupportsOpacity) view.Opacity = element.Opacity;
            view.Margin = new Thickness(0);
            view.Tag = element;
            if (view is Button actionButton) screenButtons.Add((actionButton, element.Action));
            if (view is CheckBox toggle && element.Action is "fullscreen" or "animation")
            {
                toggle.IsChecked = element.Action == "fullscreen" ? fullscreen : preferences.Animate;
                toggle.Click += (_, _) =>
                {
                    if (element.Action == "fullscreen") { ToggleFullscreen(); toggle.IsChecked = fullscreen; }
                    else { preferences.Animate = toggle.IsChecked == true; preferencesDirty = true; ConfigureDefaultBackdrop(); ShowLayout(id); }
                };
            }
            if (view is StackPanel stack && element.Type == "slider" && element.Action is "music_volume" or "sound_volume")
            {
                var slider = stack.Children.OfType<Slider>().Single();
                slider.Value = element.Action == "music_volume" ? preferences.MusicVolume : preferences.SoundVolume;
                slider.ValueChanged += (_, _) =>
                {
                    if (element.Action == "music_volume") preferences.MusicVolume = slider.Value;
                    else preferences.SoundVolume = slider.Value;
                    RefreshAudioVolumes();
                    preferencesDirty = true;
                };
            }
            if (view is StackPanel textSpeed && element.Type == "slider" && element.Action == "text_speed")
                BindTextSpeed(textSpeed);
            ScreenCanvas.Children.Add(view);
            ScreenRenderer.Animate(view, element, preferences.Animate && project.Engine.Theme.AnimationEnabled);
        }
        if (layout.Kind == "settings")
        {
            if (!layout.Elements.Any(element => element is { Type: "slider", Action: "text_speed" })) AddLegacyTextSpeed(layout);
            AddLanguageSelector();
        }
        UpdateScreenButtons();
        MenuOverlay.BeginAnimation(OpacityProperty, null); MenuOverlay.Opacity = 1;
        if (preferences.Animate && project.Engine.Theme.AnimationEnabled)
            MenuOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25)));
    }

    private void BindTextSpeed(StackPanel panel)
    {
        var slider = panel.Children.OfType<Slider>().Single();
        var label = panel.Children.OfType<TextBlock>().Single();
        string title = label.Text;
        AutomationProperties.SetName(slider, title);
        slider.Value = (runtime as ProcessGameSession)?.EffectiveDialogueSpeed ?? preferences.TextSpeed;
        UpdateLabel();
        slider.ValueChanged += (_, _) =>
        {
            preferences.TextSpeed = (int)slider.Value;
            preferencesDirty = true;
            (runtime as ProcessGameSession)?.SetPlayerDialogueSpeed(preferences.TextSpeed);
            UpdateLabel();
        };

        void UpdateLabel() => label.Text = slider.Value == 0 ? $"{title} · мгновенно" : $"{title} · {slider.Value:0} симв/с";
    }

    private void AddLegacyTextSpeed(GameScreen layout)
    {
        var element = ScreenLayouts.DialogueSpeedElement();
        var occupied = layout.Elements.Where(item => item.Type is not ("panel" or "image"))
            .Select(item => new Rect(item.X, item.Y, item.Width, item.Height)).ToArray();
        foreach (double x in new[] { element.X, 24, 856 })
        foreach (double y in new[] { element.Y }.Concat(Enumerable.Range(0, 11).Select(index => (double)index * 60 + 12)))
        {
            if (occupied.Any(bounds => bounds.IntersectsWith(new Rect(x, y, element.Width, element.Height)))) continue;
            element.X = x; element.Y = y;
            var slider = (StackPanel)ScreenRenderer.Create(element, project.Engine.Theme, project.Path, ResolveScreenText);
            Canvas.SetLeft(slider, x); Canvas.SetTop(slider, y);
            slider.Width = element.Width; slider.Height = element.Height;
            BindTextSpeed(slider);
            ScreenCanvas.Children.Add(slider);
            return;
        }
        var footer = (StackPanel)ScreenRenderer.Create(element, project.Engine.Theme, project.Path, ResolveScreenText);
        footer.Width = double.NaN;
        footer.MinWidth = 230;
        BindTextSpeed(footer);
        DialogueSpeedFallback.Content = footer;
        DialogueSpeedFallback.Visibility = Visibility.Visible;
        ScreenViewbox.Margin = new Thickness(0, 0, 0, element.Height + DialogueSpeedFallback.Margin.Bottom);
    }

    private async Task DispatchScreenActionAsync(ScreenElement element)
    {
        var screen = project.Engine.Screens.Screens.FirstOrDefault(screen => screen.Elements.Contains(element));
        if (screen != null && await DispatchTriggerAsync(new("button", Button: "screen:" + screen.Name + "/" + element.Id))) return;
        if (startupFailed || returningToMenu || execution != null) return;
        switch (element.Action)
        {
            case "start": StartButton_Click(StartButton, new()); break;
            case "continue": await LoadGameAsync(saves.Exists("auto") ? "auto" : "quick"); break;
            case "resume": ShowGameplay(); break;
            case "save": SaveButton_Click(SaveButton, new()); break;
            case "saves": await ShowSaveBrowserAsync(); break;
            case "settings": Navigate(project.Engine.Screens.Find("settings")?.Id); break;
            case "navigate":
                switch (project.Engine.Screens.Screens.FirstOrDefault(s => s.Id == element.Target)?.Kind)
                {
                    case "main_menu" when ready: await ReturnToMainMenuAsync(); break;
                    case "saves": await ShowSaveBrowserAsync(); break;
                    default: Navigate(element.Target); break;
                }
                break;
            case "back": GoBackScreen(); break;
            case "exit": Close(); break;
            case "fullscreen": ToggleFullscreen(); break;
            case "save_slot": SaveSelected_Click(SaveSelectedButton, new()); break;
            case "load_slot": LoadSelected_Click(LoadSelectedButton, new()); break;
            case "delete_slot": DeleteSelected_Click(DeleteSelectedButton, new()); break;
            case "animation": preferences.Animate = !preferences.Animate; preferencesDirty = true; ConfigureDefaultBackdrop(); if (activeScreen != null) ShowLayout(activeScreen); break;
        }
    }
    private void Navigate(string? target)
    {
        if (startupFailed) return;
        if (target == null) { Notify("Экран отключён в оформлении."); return; }
        screenHistory.Push(activeScreen ?? "gameplay"); ShowLayout(target);
    }
    private void GoBackScreen()
    {
        if (startupFailed) return;
        string previous = screenHistory.TryPop(out string? value) ? value : ready ? "gameplay" : project.Engine.Screens.Find("main_menu")?.Id ?? "gameplay";
        if (previous == "gameplay") ShowGameplay(); else ShowLayout(previous);
    }
    private void ScreenBack_Click(object sender, RoutedEventArgs e) => GoBackScreen();
    private void UpdateScreenButtons()
    {
        if (startupFailed) return;
        foreach (var (button, action) in screenButtons)
        {
            bool trigger = GameTriggerInput.ButtonId(button, project.Engine.Screens) is { } id && HasButtonTrigger(id);
            button.IsEnabled = execution == null && (trigger || action switch
            {
                "resume" => ready,
                "save" => ready && CanSaveInScene,
                "continue" => saves.Exists("auto") || saves.Exists("quick"),
                "settings" => project.Engine.Screens.Find("settings") != null,
                "saves" => project.Engine.Screens.Find("saves") != null,
                "save_slot" => ready && CanSaveInScene,
                "load_slot" => SaveSlotsList.SelectedItem is GameSaveEntry { Damaged: false },
                "delete_slot" => SaveSlotsList.SelectedItem != null,
                _ => true
            });
        }
    }

    private async Task ShowSaveBrowserAsync()
    {
        if (startupFailed || execution != null || openingSaveBrowser || closed) return;
        openingSaveBrowser = true;
        try
        {
            var slots = await saves.ListAsync();
            if (closed || startupFailed) return;
            var layout = project.Engine.Screens.Find("saves");
            if (layout == null) { Notify("Экран сохранений отключён."); return; }
            screenHistory.Push(activeScreen ?? "gameplay");
            SaveSlotsList.ItemsSource = slots; SaveSelectedButton.IsEnabled = ready && CanSaveInScene; LoadSelectedButton.IsEnabled = DeleteSelectedButton.IsEnabled = false;
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
        if (!ready || execution != null || !CanSaveInScene) return;
        string slot = SaveSlotName.Text.Trim();
        bool saved = false;
        await ExecuteAsync(async () => { await StoreSaveAsync(slot, execution!.Token); saved = true; });
        if (saved) { SaveSlotsList.ItemsSource = await saves.ListAsync(); Notify("Сохранено: " + slot); }
    }
    private async void LoadSelected_Click(object sender, RoutedEventArgs e)
    { if (SaveSlotsList.SelectedItem is GameSaveEntry slot) await LoadGameAsync(slot.Slot); }
    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (startupFailed || SaveSlotsList.SelectedItem is not GameSaveEntry slot || execution != null) return;
        if (!await ConfirmInGameAsync("Сохранения", "Удалить слот «" + slot.Slot + "»?")) return;
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
    public int TextSpeed { get; set; } = DialogueSpeed.Default;
    public string? Language { get; set; }
}
