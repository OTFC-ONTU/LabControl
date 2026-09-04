using Avalonia.Markup.Xaml;

namespace LabControl.Console.Localization;

/// <summary>
/// XAML markup extension: <c>Text="{loc:Localize MainWindow.Heading}"</c>.
/// Exists so no view can accidentally hard-code a literal.
/// </summary>
public sealed class LocalizeExtension : MarkupExtension
{
    public LocalizeExtension()
    {
    }

    public LocalizeExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
