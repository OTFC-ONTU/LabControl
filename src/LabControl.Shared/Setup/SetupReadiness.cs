using System.Text;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Setup;

/// <summary>Nonsecret, bounded snapshot of installation advisories. Never a connectivity test.</summary>
public sealed class SetupReadiness : ISchemaVersioned
{
    public const string EventCode = "setup.readiness";
    public static readonly SchemaMigrations Migrations = new(1);
    public int SchemaVersion { get; set; } = 1;
    public string[] Codes { get; set; } = [];

    public static bool IsKnown(string? code) => code is "antivirus.third_party" or
        "antivirus.inventory_unavailable" or "network.wol_unverified" or
        "network.configuration_warning" or "report.unavailable";

    /// <summary>Codes that stay informational: the wake test is still to be done, or the
    /// network driver simply does not expose Wake-on-LAN settings (D-66). They are shown in
    /// the tooltip, never as an amber tile line.</summary>
    public static bool IsInformational(string code) => code is "network.wol_unverified" or "network.configuration_warning";

    public static bool NeedsAttention(string code) => IsKnown(code) && !IsInformational(code);

    /// <summary>The severity a readiness snapshot deserves in the event log.</summary>
    public static bool AnyNeedsAttention(IEnumerable<string> codes) => codes.Any(NeedsAttention);

    public static SetupReadiness Create(IEnumerable<string> codes)
    {
        var bounded = codes.Take(17).ToArray();
        if (bounded.Length > 16 || bounded.Any(code => !IsKnown(code)))
            throw new InvalidDataException("Invalid setup readiness codes.");
        return new() { Codes = bounded.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
    }

    public string Serialize() => JsonStore.Serialize(Create(Codes), Migrations);

    public static SetupReadiness Parse(string payload)
    {
        if (payload.Length > Defaults.SetupReadinessMaxBytes || Encoding.UTF8.GetByteCount(payload) > Defaults.SetupReadinessMaxBytes)
            throw new InvalidDataException("Setup readiness report is too large.");
        var report = JsonStore.Parse<SetupReadiness>(payload, Defaults.SetupReadinessFileName, Migrations);
        return Create(report.Codes ?? throw new InvalidDataException("Missing readiness codes."));
    }

    /// <summary>Caller owns the protected Setup directory and installation lock.</summary>
    public static void Save(string directory, IEnumerable<string> codes) =>
        JsonStore.Save(Path.Combine(directory, Defaults.SetupReadinessFileName), Create(codes), Migrations);

    public static SetupReadiness? Read(string directory)
    {
        var path = Path.Combine(directory, Defaults.SetupReadinessFileName);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > Defaults.SetupReadinessMaxBytes) throw new InvalidDataException("Setup readiness report is too large.");
            using var reader = new StreamReader(stream);
            var chars = new char[Defaults.SetupReadinessMaxBytes + 1];
            var count = reader.ReadBlock(chars, 0, chars.Length);
            return Parse(new string(chars, 0, count));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
