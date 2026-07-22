using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace WcfPrTriage.Converters;

/// <summary>
/// Produces a left margin proportional to a <see cref="TreeViewItem"/>'s depth, so the custom
/// TreeViewItem template indents nested nodes.
/// </summary>
public sealed class DepthMarginConverter : IValueConverter
{
    public double Indent { get; set; } = 15;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int depth = 0;
        DependencyObject? item = value as DependencyObject;
        DependencyObject? parent = item is null ? null : VisualTreeHelper.GetParent(item);
        while (parent is not null)
        {
            if (parent is TreeViewItem)
                depth++;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return new Thickness(depth * Indent, 0, 0, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
