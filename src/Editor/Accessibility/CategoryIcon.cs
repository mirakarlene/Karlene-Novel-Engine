using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace KNE;

/// <summary>Category symbols remain meaningful when hue cannot be distinguished.</summary>
public sealed class CategoryIcon : FrameworkElement
{
    /// <summary>Identifies the category represented by this symbol.</summary>
    public static readonly DependencyProperty GroupProperty = DependencyProperty.Register(nameof(Group), typeof(string), typeof(CategoryIcon), new FrameworkPropertyMetadata("narrative", FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    /// <summary>Gets or sets the catalog group ID.</summary>
    public string Group { get => (string)GetValue(GroupProperty); set => SetValue(GroupProperty, value); }
    /// <summary>Defines the symbol ink.</summary>
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(CategoryIcon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Gets or sets the symbol ink.</summary>
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>Creates a non-interactive category badge, hidden in the normal mode.</summary>
    public CategoryIcon() { Width = Height = 18; IsHitTestVisible = false; Update(); }
    private static void Changed(DependencyObject view, DependencyPropertyChangedEventArgs args) => ((CategoryIcon)view).Update();
    private void Update()
    {
        string label = BlockLibrary.Groups.FirstOrDefault(group => group.Id == Group)?.Title ?? Group;
        ToolTip = label; AutomationProperties.SetName(this, "Категория: " + label);
        BlockAccessibility.Decorate(this, Group, mode => Visibility = mode.Badges ? Visibility.Visible : Visibility.Collapsed);
    }
    /// <summary>Returns a stable silhouette discriminator for a category.</summary>
    /// <param name="group">Catalog group ID.</param><returns>A vector symbol name.</returns>
    public static string SymbolFor(string group) => group switch { "narrative" => "speech", "logic" => "gear", "visual" => "eye", "audio" => "note", "flows" => "arrows", "calculations" => "calculator", "async" => "branches", "triggers" => "lightning", "configurations" => "sliders", "shaders" => "prism", "plugins" => "puzzle", _ => "play" };
    private static readonly Dictionary<string, Geometry> symbols = Build();
    private static Dictionary<string, Geometry> Build()
    {
        var paths = new Dictionary<string, string> {
            ["speech"] = "M2,3 L22,3 22,17 10,17 5,22 5,17 2,17 Z",
            ["gear"] = "F0 M9,1 L15,1 16,5 19,4 23,9 20,12 23,15 19,20 16,19 15,23 9,23 8,19 5,20 1,15 4,12 1,9 5,4 8,5 Z M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8 Z",
            ["eye"] = "F0 M1,12 Q12,-3 23,12 Q12,27 1,12 Z M12,7 A5,5 0 1 0 12,17 A5,5 0 1 0 12,7 Z",
            ["note"] = "M8,3 L21,1 21,17 17,21 13,19 14,16 18,15 18,6 10,8 10,20 6,23 2,21 3,18 7,17 Z",
            ["arrows"] = "M1,5 L17,5 13,1 17,1 23,7 17,13 13,13 17,9 1,9 Z M23,19 L7,19 11,23 7,23 1,17 7,11 11,11 7,15 23,15 Z",
            ["calculator"] = "F0 M3,1 L21,1 21,23 3,23 Z M6,4 L18,4 18,9 6,9 Z M6,12 L10,12 10,16 6,16 Z M14,12 L18,12 18,16 14,16 Z M6,18 L10,18 10,21 6,21 Z M14,18 L18,18 18,21 14,21 Z",
            ["branches"] = "M10,1 L14,1 14,8 22,8 22,23 18,23 18,12 6,12 6,23 2,23 2,8 10,8 Z",
            ["lightning"] = "M13,0 L3,14 10,14 8,24 22,9 14,9 17,0 Z",
            ["sliders"] = "M1,5 L7,5 7,2 11,2 11,5 23,5 23,8 11,8 11,11 7,11 7,8 1,8 Z M1,16 L15,16 15,13 19,13 19,16 23,16 23,19 19,19 19,22 15,22 15,19 1,19 Z",
            ["puzzle"] = "M2,4 L9,4 Q7,-1 12,0 Q17,-1 15,4 L22,4 22,11 Q17,9 18,14 Q17,19 22,17 L22,24 2,24 2,17 Q7,19 6,14 Q7,9 2,11 Z",
            ["prism"] = "M12,1 L23,21 1,21 Z M12,5 L5,18 12,14 19,18 Z",
            ["play"] = "M4,2 L22,12 4,22 Z" };
        return paths.ToDictionary(pair => pair.Key, pair => { var geometry = Geometry.Parse(pair.Value); geometry.Freeze(); return geometry; });
    }
    internal static Geometry GeometryFor(string group) => symbols[SymbolFor(group)];
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24)); dc.DrawGeometry(Foreground, null, GeometryFor(Group)); dc.Pop();
    }
}
