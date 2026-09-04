using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using LabControl.Console.Services;

namespace LabControl.Console.ViewModels;

/// <summary>Colour converters for the jobs and events lists.</summary>
public static class RowConverters
{
    // A null brush would draw nothing at all; UnsetValue leaves the theme's foreground in place.

    public static readonly IValueConverter FailedBrush =
        new FuncValueConverter<bool, object?>(failed => failed ? Brushes.IndianRed : AvaloniaProperty.UnsetValue);

    public static readonly IValueConverter SeverityBrush =
        new FuncValueConverter<EventSeverity, object?>(severity => severity switch
        {
            EventSeverity.Error => Brushes.IndianRed,
            EventSeverity.Warning => Brushes.DarkOrange,
            _ => AvaloniaProperty.UnsetValue,
        });
}
