using Avalonia.Data.Converters;

namespace LabControl.Console.ViewModels;

/// <summary>Status-to-class converters for the tile template.</summary>
public static class TileConverters
{
    public static readonly IValueConverter IsOutdated =
        new FuncValueConverter<TileStatus, bool>(status => status == TileStatus.Outdated);

    public static readonly IValueConverter IsHeld =
        new FuncValueConverter<TileStatus, bool>(status => status == TileStatus.HeldElsewhere);
}
