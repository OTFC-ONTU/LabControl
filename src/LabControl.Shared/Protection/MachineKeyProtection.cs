using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using LabControl.Shared.Link;

namespace LabControl.Shared.Protection;

/// <summary>
/// How a student PC keeps its private key at rest (ARCHITECTURE §5, INSTALLER.md step 4):
/// DPAPI at <b>machine</b> scope, because the agent runs as <c>LocalSystem</c> and the key
/// must survive any change to user profiles. A copied <c>agent.key</c> is inert on another
/// PC, and on this PC it is readable only by whoever can read
/// <c>C:\ProgramData\LabControl\</c> — SYSTEM and Administrators, never <c>student</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MachineKeyProtection
{
    /// <summary>Binds the blob to its purpose so a blob written for something else cannot be swapped in.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("labcontrol/agent-key");

    public static KeyProtection Dpapi { get; } = new(
        bytes => ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine),
        bytes => ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine));

    private static readonly byte[] RekeyEntropy = Encoding.UTF8.GetBytes("labcontrol/trust-rekey");
    public static KeyProtection RekeyJournal { get; } = new(
        bytes => ProtectedData.Protect(bytes, RekeyEntropy, DataProtectionScope.LocalMachine),
        bytes => ProtectedData.Unprotect(bytes, RekeyEntropy, DataProtectionScope.LocalMachine));
}
