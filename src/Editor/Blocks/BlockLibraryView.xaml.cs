using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KNE;

/// <summary>Fixed-size, paged command picker hosted by the main window's modal overlay.</summary>
public partial class BlockLibraryView : UserControl
{
    /// <summary>The four-column, three-row page capacity.</summary>
    public const int PageSize = 12;
    private BlockLibraryPreferenceStore preferenceStore;
    private BlockLibraryPreferences preferences = new();
    private Task? loading;
    private Task saving = Task.CompletedTask;
    private readonly List<Task> preferenceEdits = [];
    private Func<ScriptNodeKind, bool> canAdd = _ => true;
    private int page;
    /// <summary>Raised when a selectable command is chosen.</summary>
    public event EventHandler<BlockDefinition>? BlockSelected;
    /// <summary>Raised when the picker is dismissed without a selection.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Builds the picker with per-user catalog preferences.</summary>
    public BlockLibraryView() : this(new BlockLibraryPreferenceStore()) { }
    /// <summary>Builds the picker with an isolated preference store.</summary>
    public BlockLibraryView(BlockLibraryPreferenceStore preferenceStore)
    {
        ArgumentNullException.ThrowIfNull(preferenceStore);
        this.preferenceStore = preferenceStore;
        InitializeComponent();
        BlockGroups.ItemsSource = new[] { new BlockGroup("favorites", "Избранное", "#B58B20"), new BlockGroup("recent", "Недавнее", "#697384") }
            .Concat(BlockLibrary.Groups).ToArray();
    }

    /// <summary>Configures an isolated preference location before the picker is first opened.</summary>
    public void ConfigurePreferenceStore(BlockLibraryPreferenceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (loading != null) throw new InvalidOperationException("Хранилище нельзя менять после загрузки каталога.");
        preferenceStore = store;
    }

    /// <summary>Resets search and page while loading preferences without blocking the UI thread.</summary>
    public void Open(Func<ScriptNodeKind, bool> availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        canAdd = availability; BlockSearch.Clear(); page = 0;
        BlockGroups.SelectedItem = BlockGroups.Items.Cast<BlockGroup>().First(group => group.Id == "narrative");
        Render(); BlockSearch.Focus(); _ = LoadPreferencesAsync();
    }

    /// <summary>Loads persisted favorites and recent entries once for this view.</summary>
    public Task LoadPreferencesAsync() => loading ??= LoadCoreAsync();
    private async Task LoadCoreAsync() { preferences = await preferenceStore.LoadAsync(); Render(); }

    /// <summary>Whether a preference read, edit or write still needs to complete before shutdown.</summary>
    public bool HasPendingPreferenceSave => (loading is { IsCompleted: false }) || !saving.IsCompleted || preferenceEdits.Any(task => !task.IsCompleted);

    /// <summary>Loads preferences when needed and waits for pending edits to be published.</summary>
    public async Task SavePreferencesAsync() { await LoadPreferencesAsync(); await FlushPendingPreferencesAsync(); }

    /// <summary>Finishes existing preference work without starting a read or creating a preferences file.</summary>
    public async Task FlushPendingPreferencesAsync()
    {
        if (loading != null) await loading;
        while (true)
        {
            var pending = preferenceEdits.Append(saving).Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }

    /// <summary>Records an insertion after the editor has successfully added the block.</summary>
    public Task RecordUseAsync(BlockDefinition entry) => EditPreferencesAsync(() => preferences.RecordUse(entry));

    private Task EditPreferencesAsync(Action edit)
    {
        preferenceEdits.RemoveAll(task => task.IsCompleted);
        Task pending = ApplyEditAsync(edit); preferenceEdits.Add(pending); return pending;
    }
    private async Task ApplyEditAsync(Action edit) { await LoadPreferencesAsync(); edit(); Render(); await QueueSave(); }

    private Task QueueSave()
    {
        var snapshot = new BlockLibraryPreferences { Favorites = preferences.Favorites.ToList(), Recent = preferences.Recent.ToList() };
        return saving = SaveAfterAsync(saving, snapshot);
    }
    private async Task SaveAfterAsync(Task previous, BlockLibraryPreferences snapshot)
    {
        await previous;
        try { await preferenceStore.SaveAsync(snapshot); PreferenceStatus.Text = ""; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        { PreferenceStatus.Text = "Не удалось сохранить избранное и недавнее: " + error.Message; }
    }
    private void Group_Changed(object sender, SelectionChangedEventArgs e) { page = 0; Render(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) { page = 0; Render(); }
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void PreviousPage_Click(object sender, RoutedEventArgs e) { page--; Render(); }
    private void NextPage_Click(object sender, RoutedEventArgs e) { page++; Render(); }
    private IReadOnlyList<BlockDefinition> Entries(BlockGroup group)
    {
        var lookup = BlockLibrary.Definitions.ToDictionary(BlockLibraryPreferences.Key, StringComparer.Ordinal);
        IEnumerable<BlockDefinition> entries = group.Id switch
        {
            "favorites" => preferences.Favorites.Where(lookup.ContainsKey).Select(key => lookup[key]),
            "recent" => preferences.Recent.Where(lookup.ContainsKey).Select(key => lookup[key]),
            _ => BlockLibrary.Definitions.Where(entry => entry.Group == group.Id)
        };
        string search = BlockSearch.Text.Trim();
        return entries.Where(entry => entry.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            entry.Description.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void Render()
    {
        if (BlockCards == null || BlockGroups.SelectedItem is not BlockGroup group) return;
        var entries = Entries(group); int pages = Math.Max(1, (entries.Count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        BlockCards.Children.Clear();
        foreach (var entry in entries.Skip(page * PageSize).Take(PageSize)) BlockCards.Children.Add(CreateCard(entry));
        EmptyLibraryText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginWarning.Visibility = group.Id == "plugins" ? Visibility.Visible : Visibility.Hidden;
        PreviousPageButton.IsEnabled = page > 0; NextPageButton.IsEnabled = page + 1 < pages;
        PageStatus.Text = $"{page + 1} / {pages}";
    }
    private Button CreateCard(BlockDefinition entry)
    {
        var color = (Color)ColorConverter.ConvertFromString(entry.Color);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = entry.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 25, 8), MaxHeight = 43 });
        text.Children.Add(new TextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 50, Foreground = Brushes.DimGray });
        var content = new Grid(); content.Children.Add(text);
        string key = BlockLibraryPreferences.Key(entry); bool favorite = preferences.Favorites.Contains(key, StringComparer.Ordinal);
        var star = new Button { Content = favorite ? "★" : "☆", Tag = key, Width = 26, Height = 26, Padding = new Thickness(0),
            FontSize = 20, Foreground = favorite ? Brushes.DarkGoldenrod : Brushes.DimGray, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = favorite ? "Убрать из избранного" : "В избранное" };
        System.Windows.Automation.AutomationProperties.SetName(star, "Избранное: " + entry.Title);
        star.Click += async (_, e) =>
        {
            e.Handled = true;
            await EditPreferencesAsync(() => { if (!preferences.Favorites.Remove(key)) preferences.Favorites.Add(key); });
        };
        content.Children.Add(star);
        var button = new Button { Tag = entry.Kind, Content = content, Width = 236, Height = 130, Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(14), Background = Brushes.White, BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(0, 5, 0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
        // Keep starring available even when the command cannot currently be inserted.
        bool available = canAdd(entry.Kind);
        if (!available) { text.Opacity = .5; button.ToolTip = entry.Kind == ScriptNodeKind.Start ? "В схеме уже есть начало." : "Достигнут предел в 500 блоков."; }
        button.Click += (_, e) => { if (ReferenceEquals(e.Source, button) && canAdd(entry.Kind)) BlockSelected?.Invoke(this, entry); };
        return button;
    }
}
