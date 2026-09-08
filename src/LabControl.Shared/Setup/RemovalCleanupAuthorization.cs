using LabControl.Shared.Persistence;

namespace LabControl.Shared.Setup;

public static class RemovalCleanupAuthorization
{
    /// <summary>A completion receipt may cover missing final journals, but never a
    /// different installation or an unfinished installation that reuses this path.</summary>
    public static void Require(string installationId, InstallationDocument? current, bool verifiedCompletionReceipt, bool installDirectoryExists = true)
    {
        if (!Guid.TryParseExact(installationId, "D", out var id) || id == Guid.Empty
            || current is not null && (current.InstallationId != installationId || !current.RemovalReady || !current.InstallDirectoryOwned && installDirectoryExists)
            || current is null && !verifiedCompletionReceipt)
            throw new IOException("Cleanup does not match a verified removal; existing files were preserved.");
    }
}
