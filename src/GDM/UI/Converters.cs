using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

/// <summary>Picks LTR for Latin text (file names, English titles) so brackets and dots don't get mirrored.</summary>
public sealed class TextDirectionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => TextDir.IsRtl(value as string) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
