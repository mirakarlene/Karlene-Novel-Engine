using System.Windows;
using System.Windows.Media;

namespace KNE;

internal partial class GameHistoryWindow : Window
{
    internal GameHistoryWindow(GameWindow owner, DialogueJournalSnapshot journal)
    {
        InitializeComponent();
        Owner = owner;
        Resources = owner.Resources;
        Background = owner.Background;
        FontFamily = owner.FontFamily;
        var text = (Brush)owner.FindResource("GameTextBrush");
        Foreground = Entries.Foreground = text;
        Entries.Background = owner.Background;
        Entries.ItemsSource = journal.Entries;
        Empty.Visibility = journal.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (journal.Entries.Count > 0)
            Loaded += (_, _) => Entries.ScrollIntoView(journal.Entries[^1]);
    }
}
