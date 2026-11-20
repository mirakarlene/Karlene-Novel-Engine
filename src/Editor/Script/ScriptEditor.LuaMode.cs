using System.Windows;
using System.Windows.Controls;

namespace KNE;

public partial class ScriptEditor
{
    private void ApplyLuaModeAppearance(bool luaMode)
    {
        if (luaMode)
        {
            CodeMode.Style = GraphMode.Style = (Style)Resources["LuaModeSegment"];
            CodeMode.Margin = new Thickness(0);
            ModeTrack.CornerRadius = new CornerRadius(12);
            ModeTrack.Padding = new Thickness(2);
            ModeTrack.BorderThickness = new Thickness(1);
            ModeTrack.SetResourceReference(Border.BorderBrushProperty, "Editor.Border");
            ModeTrack.SetResourceReference(Border.BackgroundProperty, "Editor.Background");
        }
        else
        {
            CodeMode.ClearValue(StyleProperty);
            GraphMode.ClearValue(StyleProperty);
            CodeMode.Margin = new Thickness(8, 0, 8, 0);
            ModeTrack.ClearValue(Border.CornerRadiusProperty);
            ModeTrack.ClearValue(Border.PaddingProperty);
            ModeTrack.ClearValue(Border.BorderThicknessProperty);
            ModeTrack.ClearValue(Border.BorderBrushProperty);
            ModeTrack.ClearValue(Border.BackgroundProperty);
        }
    }
}
