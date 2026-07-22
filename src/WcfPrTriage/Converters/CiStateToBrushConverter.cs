using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WcfPrTriage.Models;

namespace WcfPrTriage.Converters;

/// <summary>Maps a <see cref="CiState"/> to one of the themed status brushes.</summary>
public sealed class CiStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is CiState state
            ? state switch
            {
                CiState.Failure => "FailureBrush",
                CiState.Success => "SuccessBrush",
                CiState.Running => "RunningBrush",
                CiState.Pending => "UnknownBrush",
                _ => "UnknownBrush",
            }
            : "UnknownBrush";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
