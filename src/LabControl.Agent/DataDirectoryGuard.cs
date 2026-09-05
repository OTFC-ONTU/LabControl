using System.Security.AccessControl;
using System.Security.Principal;
using LabControl.Shared;

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
                return [$"{directory} does not exist."];
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
                        problems.Add($"{directory} lets '{name}' read it{(rule.IsInherited ? " (inherited from the parent)" : string.Empty)}; the private key and configuration must be readable by SYSTEM and Administrators only. Re-run Setup, or: icacls \"{directory}\" /inheritance:r /grant:r \"SYSTEM:(OI)(CI)F\" \"Administrators:(OI)(CI)F\"");
                        break;
                    }
                }
            }

            var keyPath = Path.Combine(directory, Defaults.AgentKeyFileName);
            if (!File.Exists(keyPath))
            {
                problems.Add($"{keyPath} is missing; this PC has no private key and cannot connect. Run Setup again.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            problems.Add($"Could not verify the permissions on {directory}: {ex.Message}");
        }

        return problems;
    }
}
