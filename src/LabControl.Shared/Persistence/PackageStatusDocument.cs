namespace LabControl.Shared.Persistence;

/// <summary>What one PC last said about one package (D-89).</summary>
public sealed class PcPackageStatus
{
    public string AgentId { get; set; } = string.Empty;

    /// <summary>A <see cref="Packages.PackageOutcomes"/> code, or <c>console.timed_out</c> when the PC never answered.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>The installer the answer was about: an answer about an older version is not about this one.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public long AtUnix { get; set; }
}

/// <summary>Every PC's latest answer for one catalog entry.</summary>
public sealed class PackageStatusEntry
{
    public string Package { get; set; } = string.Empty;

    public List<PcPackageStatus> Pcs { get; set; } = [];
}

/// <summary>
/// <c>package-status.json</c> beside <c>lab.json</c> (M7 portion 4, D-89): the answer of each PC's
/// last <c>install_package</c> job per package, so <i>Install on all missing</i> and the
/// <i>Packages</i> tab know who has what after a restart. A cache of what the PCs said — never in a
/// backup; a <i>Check PCs</i> rebuilds it.
/// </summary>
public sealed class PackageStatusDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.PackageStatusSchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.PackageStatusSchemaVersion;

    public string LabId { get; set; } = string.Empty;

    public List<PackageStatusEntry> Packages { get; set; } = [];
}
