using LabControl.Console.Localization;
using LabControl.Shared.Persistence;
using LabControl.Shared.Setup;

namespace LabControl.Console.Services;

public static class ReadinessPresentation
{
    /// <summary>Never exposes the untrusted wire payload as teacher-facing text.</summary>
    public static string EventText(string payload)
    {
        try
        {
            var report = SetupReadiness.Parse(payload);
            return report.Codes.Length == 0 ? Strings.Get("Readiness.NoWarnings")
                : string.Join(" ", report.Codes.Select(code => Strings.Get("Readiness." + code)));
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or SchemaVersionException)
        {
            return Strings.Get("Readiness.InvalidReport");
        }
    }
}
