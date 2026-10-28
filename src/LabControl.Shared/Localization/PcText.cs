using System.Globalization;
using System.Resources;

namespace LabControl.Shared.Localization;

/// <summary>
/// What a PC writes to its teacher (M7, D-87): event messages, job results and progress lines,
/// from <c>PcStrings.resx</c> in <see cref="PcLanguage.Current"/>. Stable codes, exception
/// text, paths, program names and the teacher's own data are placeholders and are never
/// translated; developer logs stay English.
/// </summary>
public static class PcText
{
    private static readonly ResourceManager Resources = new("LabControl.Shared.Localization.PcStrings", typeof(PcText).Assembly);

    /// <summary>A resource string formatted with the invariant culture; the name itself if the resource is missing, never an exception.</summary>
    public static string Get(string name, params object?[] arguments)
    {
        string? template;
        try
        {
            template = Resources.GetString(name, PcLanguage.Current.Culture);
        }
        catch (MissingManifestResourceException)
        {
            template = null;
        }

        template ??= name;
        try
        {
            return arguments.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, arguments);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
