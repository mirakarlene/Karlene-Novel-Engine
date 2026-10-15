using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KNE;

/// <summary>Uses the same geometry and styles for live games and frozen scene previews.</summary>
internal sealed class GameplayUiPresenter
{
    private readonly Grid scene;
    private readonly Border dialogue;
    private readonly TextBlock speaker;
    private readonly TextBlock text;
    private readonly StackPanel choices;
    private readonly IReadOnlyDictionary<string, Button> buttons;
    private readonly string root;
    private readonly Dictionary<DependencyObject, Dictionary<DependencyProperty, object>> originals = [];
    private readonly List<(FrameworkElement Child, DependencyObject Parent, int Index)> moved = [];
    private readonly Dictionary<string, ImageSource> textures = new(StringComparer.OrdinalIgnoreCase);
    private GameplayUiConfiguration[]? rendered;
    private Button[] renderedChoices = [];

    public GameplayUiPresenter(Grid scene, Border dialogue, TextBlock speaker, TextBlock text, StackPanel choices,
        IReadOnlyDictionary<string, Button> buttons, string root)
    {
        this.scene = scene; this.dialogue = dialogue; this.speaker = speaker; this.text = text;
        this.choices = choices; this.buttons = buttons; this.root = root;
    }

    public void Apply(SceneVisuals visuals)
    {
        var currentChoices = choices.Children.OfType<Button>().ToArray();
        if ((ReferenceEquals(rendered, visuals.UiConfigurations) || rendered is not null && rendered.SequenceEqual(visuals.UiConfigurations)) && currentChoices.SequenceEqual(renderedChoices)) return;
        var focused = Keyboard.FocusedElement as UIElement;
        Restore();
        foreach (var entry in visuals.UiConfigurations)
        {
            if (entry.Target == "dialogue") ApplyDialogue(entry.Style);
            else if (entry.Target == "choice")
            {
                MoveToScene(choices, dialogue);
                ApplyGeometry(choices, entry.Style, false);
                Set(choices, FrameworkElement.WidthProperty, entry.Style.Width);
                foreach (var button in currentChoices) ApplyButton(button, entry.Style, position: false);
            }
            else if (buttons.TryGetValue(entry.Target, out var button))
            {
                MoveToScene(button, entry.Target == "next" ? dialogue : button.Parent as FrameworkElement);
                ApplyButton(button, entry.Style, position: true);
            }
        }
        rendered = visuals.UiConfigurations;
        renderedChoices = currentChoices;
        if (focused is { IsVisible: true, IsEnabled: true, IsKeyboardFocused: false }) focused.Focus();
    }

    public void Reset()
    {
        var focused = Keyboard.FocusedElement as UIElement;
        Restore(); rendered = null; renderedChoices = []; textures.Clear();
        if (focused is { IsVisible: true, IsEnabled: true, IsKeyboardFocused: false }) focused.Focus();
    }

    private void ApplyDialogue(GameplayUiStyle style)
    {
        ApplyGeometry(dialogue, style, true);
        Set(dialogue, FrameworkElement.MaxHeightProperty, double.PositiveInfinity);
        Set(dialogue, Border.BackgroundProperty, Background(style));
        Set(dialogue, Border.CornerRadiusProperty, new CornerRadius(style.CornerRadius));
        Set(dialogue, Border.PaddingProperty, Padding(style));
        foreach (var target in new[] { speaker, text })
        {
            Set(target, TextBlock.FontFamilyProperty, new FontFamily(style.FontFamily));
            Set(target, TextBlock.FontSizeProperty, style.FontSize);
            Set(target, TextBlock.FontWeightProperty, Weight(style.FontWeight));
            Set(target, TextBlock.ForegroundProperty, ColorBrush(style.TextColor));
            Set(target, TextBlock.TextAlignmentProperty, ParseTextAlignment(style.TextAlignment));
        }
    }

    private void ApplyButton(Button button, GameplayUiStyle style, bool position)
    {
        if (position) ApplyGeometry(button, style, true);
        else
        {
            Set(button, FrameworkElement.WidthProperty, style.Width);
            Set(button, FrameworkElement.HeightProperty, style.Height);
            Set(button, UIElement.OpacityProperty, style.Opacity);
        }
        Set(button, Control.FontFamilyProperty, new FontFamily(style.FontFamily));
        Set(button, Control.FontSizeProperty, style.FontSize);
        Set(button, Control.FontWeightProperty, Weight(style.FontWeight));
        Set(button, Control.ForegroundProperty, ColorBrush(style.TextColor));
        Set(button, Control.BackgroundProperty, Background(style));
        Set(button, Control.PaddingProperty, Padding(style));
        Set(button, TextBlock.TextAlignmentProperty, ParseTextAlignment(style.TextAlignment));
        Set(button, Control.HorizontalContentAlignmentProperty, style.TextAlignment switch
        { "left" => HorizontalAlignment.Left, "justify" => HorizontalAlignment.Stretch, "right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center });
        Set(button, Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
        Set(button, Control.TemplateProperty, ButtonTemplate(style.CornerRadius));
        if (button.Content is TextBlock label)
        {
            Set(label, TextBlock.TextAlignmentProperty, ParseTextAlignment(style.TextAlignment));
            Set(label, TextBlock.FontFamilyProperty, new FontFamily(style.FontFamily));
            Set(label, TextBlock.FontSizeProperty, style.FontSize);
            Set(label, TextBlock.FontWeightProperty, Weight(style.FontWeight));
            Set(label, TextBlock.ForegroundProperty, ColorBrush(style.TextColor));
        }
    }

    private void ApplyGeometry(FrameworkElement target, GameplayUiStyle style, bool height)
    {
        if (ReferenceEquals(target.Parent, scene)) Set(target, Panel.ZIndexProperty, 10);
        Set(target, FrameworkElement.HorizontalAlignmentProperty, style.HorizontalAlignment switch
        { "right" => HorizontalAlignment.Right, "center" => HorizontalAlignment.Center, "stretch" => HorizontalAlignment.Stretch, _ => HorizontalAlignment.Left });
        Set(target, FrameworkElement.VerticalAlignmentProperty, style.VerticalAlignment switch
        { "bottom" => VerticalAlignment.Bottom, "center" => VerticalAlignment.Center, "stretch" => VerticalAlignment.Stretch, _ => VerticalAlignment.Top });
        Set(target, FrameworkElement.WidthProperty, style.HorizontalAlignment == "stretch" ? double.NaN : style.Width);
        if (height) Set(target, FrameworkElement.HeightProperty, style.VerticalAlignment == "stretch" ? double.NaN : style.Height);
        Set(target, FrameworkElement.MarginProperty, new Thickness(
            style.HorizontalAlignment is "left" or "center" or "stretch" ? style.X : 0,
            style.VerticalAlignment is "top" or "center" or "stretch" ? style.Y : 0,
            style.HorizontalAlignment is "right" or "stretch" ? style.X : 0,
            style.VerticalAlignment is "bottom" or "stretch" ? style.Y : 0));
        Set(target, UIElement.OpacityProperty, style.Opacity);
    }

    private void MoveToScene(FrameworkElement child, FrameworkElement? visibilitySource)
    {
        if (child.Parent is not DependencyObject parent || ReferenceEquals(parent, scene)) return;
        int index = parent is Panel panel ? panel.Children.IndexOf(child) : 0;
        if (parent is Panel oldPanel) oldPanel.Children.Remove(child);
        else if (parent is Decorator decorator) decorator.Child = null;
        else throw new InvalidOperationException("Неподдерживаемый контейнер UI.");
        moved.Add((child, parent, index));
        scene.Children.Add(child);
        Set(child, Panel.ZIndexProperty, 10);
        if (visibilitySource is not null)
        {
            Remember(child, UIElement.VisibilityProperty);
            BindingOperations.SetBinding(child, UIElement.VisibilityProperty, new Binding(nameof(UIElement.Visibility)) { Source = visibilitySource });
        }
    }

    private void Set(DependencyObject target, DependencyProperty property, object value)
    {
        Remember(target, property);
        target.SetValue(property, value);
    }

    private void Remember(DependencyObject target, DependencyProperty property)
    {
        if (!originals.TryGetValue(target, out var properties)) originals.Add(target, properties = []);
        if (!properties.ContainsKey(property)) properties.Add(property, target.ReadLocalValue(property));
    }

    private void Restore()
    {
        foreach (var item in moved.AsEnumerable().Reverse())
        {
            scene.Children.Remove(item.Child);
            if (item.Parent is Panel panel) panel.Children.Insert(Math.Min(item.Index, panel.Children.Count), item.Child);
            else if (item.Parent is Decorator decorator) decorator.Child = item.Child;
        }
        moved.Clear();
        foreach (var (target, properties) in originals)
            foreach (var (property, value) in properties)
            {
                BindingOperations.ClearBinding(target, property);
                if (ReferenceEquals(value, DependencyProperty.UnsetValue)) target.ClearValue(property);
                else if (value is BindingExpression binding) BindingOperations.SetBinding(target, property, binding.ParentBinding);
                else target.SetValue(property, value);
            }
        originals.Clear();
    }

    private Brush Background(GameplayUiStyle style)
    {
        if (style.Texture is null) return ColorBrush(style.BackgroundColor);
        string path = ResourcePaths.Resolve(root, style.Texture);
        if (!textures.TryGetValue(path, out var image))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
            textures.Add(path, image = bitmap);
        }
        var brush = new ImageBrush(image) { Stretch = Stretch.Fill }; brush.Freeze(); return brush;
    }

    private static Brush ColorBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush;
    }
    private static Thickness Padding(GameplayUiStyle style) => new(style.PaddingLeft, style.PaddingTop, style.PaddingRight, style.PaddingBottom);
    private static TextAlignment ParseTextAlignment(string value) => value switch
    { "center" => TextAlignment.Center, "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Left };
    private static FontWeight Weight(string value) => (FontWeight)new FontWeightConverter().ConvertFromString(value)!;

    private static ControlTemplate ButtonTemplate(double radius)
    {
        var border = new FrameworkElementFactory(typeof(Border), "ConfiguredButtonBorder");
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding(nameof(Control.Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content)) { RelativeSource = RelativeSource.TemplatedParent });
        content.SetBinding(ContentPresenter.ContentTemplateProperty, new Binding(nameof(ContentControl.ContentTemplate)) { RelativeSource = RelativeSource.TemplatedParent });
        content.SetBinding(FrameworkElement.HorizontalAlignmentProperty, new Binding(nameof(Control.HorizontalContentAlignment)) { RelativeSource = RelativeSource.TemplatedParent });
        content.SetBinding(FrameworkElement.VerticalAlignmentProperty, new Binding(nameof(Control.VerticalContentAlignment)) { RelativeSource = RelativeSource.TemplatedParent });
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        foreach (var (property, value, opacity) in new[]
                 {
                     (UIElement.IsMouseOverProperty, true, .85),
                     (Button.IsPressedProperty, true, .7),
                     (UIElement.IsEnabledProperty, false, .4)
                 })
        {
            var trigger = new Trigger { Property = property, Value = value };
            trigger.Setters.Add(new Setter(UIElement.OpacityProperty, opacity, "ConfiguredButtonBorder"));
            template.Triggers.Add(trigger);
        }
        return template;
    }
}
