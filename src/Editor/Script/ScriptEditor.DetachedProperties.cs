using System.Windows;

namespace KNE;

public partial class ScriptEditor
{
    private Window? detachedBlockProperties;

    private void DetachBlockProperties_Click(object sender, RoutedEventArgs args)
    {
        if (detachedBlockProperties != null || Selected == null || Window.GetWindow(this) is not { } owner) return;
        var window = new Window
        {
            Title = "Свойства блока", Owner = owner, Width = 420, Height = 580, MinWidth = 320, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        window.Resources.MergedDictionaries.Add(owner.Resources);
        window.SetResourceReference(Window.BackgroundProperty, "Editor.Background");
        window.SetResourceReference(Window.ForegroundProperty, "Editor.Text");
        BlockPropertiesDock.Child = null;
        BlockPropertiesView.Margin = new Thickness(14);
        window.Content = BlockPropertiesView;
        DetachBlockPropertiesButton.Visibility = Visibility.Collapsed;
        detachedBlockProperties = window;
        IsVisibleChanged += DetachedPropertiesVisibilityChanged;
        window.Closed += (_, _) =>
        {
            IsVisibleChanged -= DetachedPropertiesVisibilityChanged;
            window.Content = null;
            window.Resources.MergedDictionaries.Clear();
            detachedBlockProperties = null;
            BlockPropertiesView.Margin = new Thickness();
            BlockPropertiesDock.Child = BlockPropertiesView;
            DetachBlockPropertiesButton.Visibility = Visibility.Visible;
        };
        window.Show();
    }

    private void DetachedPropertiesVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) =>
        UpdateDetachedPropertiesVisibility();

    private void UpdateDetachedPropertiesVisibility()
    {
        if (detachedBlockProperties is not { } window) return;
        if (IsVisible && GraphPanel.Visibility == Visibility.Visible)
        {
            if (!window.IsVisible) window.Show();
        }
        else if (window.IsVisible) window.Hide();
    }

    internal void CloseDetachedProperties() => detachedBlockProperties?.Close();
}
