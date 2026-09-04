using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using LabControl.Console.Localization;

namespace LabControl.Console.ViewModels;

public static class SettingsConverters
{
    public static readonly IValueConverter BackupBrush =
        new FuncValueConverter<bool, object?>(current => current ? AvaloniaProperty.UnsetValue : Brushes.DarkOrange);

    public static readonly IValueConverter LiveText =
        new FuncValueConverter<bool, string>(live => live ? Strings.Get("Settings.LiveNow") : string.Empty);

    public static readonly IValueConverter RevokedText =
        new FuncValueConverter<bool, string>(revoked => revoked ? Strings.Get("Settings.Revoked") : string.Empty);
}
