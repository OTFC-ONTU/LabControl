using System.Security.AccessControl;
using System.Security.Principal;
using LabControl.Shared;
using LabControl.Shared.Localization;

namespace LabControl.Agent;

/// <summary>
/// Verifies the ACL on <c>C:\ProgramData\LabControl\</c> (ARCHITECTURE §5): SYSTEM and
/// Administrators only, no access for <c>Users</c>. The installer sets it; the agent checks
/// it on every start and reports a wrong one to the console, because a private key that
/// <c>student</c> can read is a PC that <c>student</c> can impersonate (§9).
/// </summary>
internal static class DataDirectoryGuard
{
    private static readonly (WellKnownSidType Sid, string Name)[] MustNotRead =
    [
        (WellKnownSidType.BuiltinUsersSid, "Users"),
        (WellKnownSidType.AuthenticatedUserSid, "Authenticated Users"),
        (WellKnownSidType.WorldSid, "Everyone"),
        (WellKnownSidType.InteractiveSid, "INTERACTIVE"),
    ];

    /// <summary>Problems in plain language; empty when the directory is locked down as it should be.</summary>
    public static IReadOnlyList<string> Check(string directory)
    {
        var problems = new List<string>();

        try
        {
            if (!Directory.Exists(directory))
            {
                return [PcText.Get("DataDirMissing", directory)];
            }

            var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

            foreach (var (sidType, name) in MustNotRead)
            {
                var sid = new SecurityIdentifier(sidType, null);
                foreach (FileSystemAccessRule rule in rules)
                {
                    if (rule.AccessControlType == AccessControlType.Allow
                        && rule.IdentityReference.Equals(sid)
                        && (rule.FileSystemRights & (FileSystemRights.ReadData | FileSystemRights.ListDirectory | FileSystemRights.ReadAndExecute)) != 0)
                    {
                        problems.Add(PcText.Get("DataDirReadable", directory, name, rule.IsInherited ? PcText.Get("DataDirInherited") : string.Empty,
                        $"icacls \"{directory}\" /inheritance:r /grant:r \"SYSTEM:(OI)(CI)F\" \"Administrators:(OI)(CI)F\""));
                        break;
                    }
                }
            }

            var keyPath = Path.Combine(directory, Defaults.AgentKeyFileName);
            if (!File.Exists(keyPath))
            {
                problems.Add(PcText.Get("DataDirNoKey", keyPath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            problems.Add(PcText.Get("DataDirUnverified", directory, ex.Message));
        }

        return problems;
    }
}
