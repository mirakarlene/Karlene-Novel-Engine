using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KNE;

/// <summary>Keeps preview-radio selection synchronized with the inherited window mode.</summary>
public sealed class AccessibilitySelectedConverter : IMultiValueConverter
{
    /// <inheritdoc/>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Length == 2 && values[0] is string id && values[1] is string selected && id == selected;
    /// <inheritdoc/>
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
