using System.Globalization;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Setup;

/// <summary>
/// What installing a PC means for its trust material (INSTALLER.md step 4): a fresh agent
/// id, a keypair generated on the PC, the pinned authority, one enrollment code from the
/// stick and <c>agent.json</c>. One implementation for <c>agent.exe --install</c>, the M4
/// installer and the simulator, so that what a real PC writes and what the tests exercise
/// can never drift apart (D-29). No certificate yet: that comes from the console at the
/// first connection.
/// </summary>
public static class AgentProvisioning
{
    /// <summary>
    /// Provisions <paramref name="directory"/> with a new agent id.
    /// <paramref name="enrollmentCode"/> is the one the caller took from the stick.
    /// </summary>
    public static DirectoryAgentStore Install(
        string directory,
        SetupPayload payload,
        string enrollmentCode,
        int number,
        string hostname,
        string mac,
        string? consoleHost,
        int consolePort,
        KeyProtection? protection = null)
    {
        if (number < 1 || number > Defaults.MaxStudentPcs)
        {
            throw new ArgumentOutOfRangeException(nameof(number), number, $"A PC number is between 1 and {Defaults.MaxStudentPcs} (D-17).");
        }

        var config = new AgentConfigDocument
        {
            LabId = payload.Document.LabId,
            AgentId = Guid.NewGuid().ToString("d"),
            Number = number,
            Hostname = hostname,
            Mac = mac,
            EnrollmentCode = enrollmentCode,
            ConsoleHost = string.IsNullOrWhiteSpace(consoleHost) ? payload.Document.ConsoleHost : consoleHost,
            ConsolePort = consolePort,
        };

        return DirectoryAgentStore.Install(directory, config, payload.Authority, protection);
    }

    /// <summary>The display name a PC number maps to: <c>PC-07</c>.</summary>
    public static string NameOf(int number) => string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, number);

    /// <summary>
    /// Reads the PC number out of a hostname that follows the naming convention
    /// (<c>PC-07</c> → 7); <c>null</c> for any other name.
    /// </summary>
    public static int? NumberFromHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return null;
        }

        var prefix = Defaults.MachineNameFormat[..Defaults.MachineNameFormat.IndexOf('{')];
        if (!hostname.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(hostname.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
               && number >= 1 && number <= Defaults.MaxStudentPcs
            ? number
            : null;
    }
}
