using System.Globalization;
using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using LabControl.Shared.Setup;
using Microsoft.Extensions.Logging;

namespace LabControl.Agent;

/// <summary>
/// <c>agent.exe --install --payload &lt;dir&gt; --number N</c>: INSTALLER.md step 4 on its
/// own. Takes one enrollment code from the stick, generates the keypair under DPAPI at
/// machine scope, pins <c>ca.crt</c> and writes <c>agent.json</c>. The M4 installer calls
/// this same code; <c>scripts/dev-install.ps1</c> calls this executable (D-29). It does not
/// need the console to be reachable — enrolment happens at the first connection.
/// </summary>
internal static class Provisioner
{
    public static int Run(string[] args, ILogger log, string usage)
    {
        string? payloadDirectory = null;
        int? number = null;
        string? consoleHost = null;
        var consolePort = Defaults.ConsolePort;
        var force = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--payload":
                    payloadDirectory = i + 1 < args.Length ? Path.GetFullPath(args[++i]) : null;
                    break;

                case "--number":
                    if (i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    {
                        number = n;
                    }

                    break;

                case "--console":
                    if (i + 1 < args.Length)
                    {
                        var value = args[++i];
                        var colon = value.LastIndexOf(':');
                        if (colon > 0 && int.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                        {
                            consoleHost = value[..colon];
                            consolePort = port;
                        }
                        else
                        {
                            consoleHost = value;
                        }
                    }

                    break;

                case "--force":
                    force = true;
                    break;
            }
        }

        if (payloadDirectory is null)
        {
            log.LogError("--payload <dir> is required: the directory holding {Setup} and {Ca}.", Defaults.SetupFileName, Defaults.CaCertificateFileName);
            Console.Error.WriteLine(usage);
            return 2;
        }

        number ??= AgentProvisioning.NumberFromHostname(MachineFacts.Hostname());
        if (number is null)
        {
            log.LogError("--number N is required (1..{Max}); the hostname '{Host}' does not follow the {Pattern} convention.",
                Defaults.MaxStudentPcs, MachineFacts.Hostname(), AgentProvisioning.NameOf(7));
            return 2;
        }

        if (number < 1 || number > Defaults.MaxStudentPcs)
        {
            log.LogError("--number must be between 1 and {Max} (D-17).", Defaults.MaxStudentPcs);
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            log.LogError("Provisioning protects the key with DPAPI and only runs on Windows.");
            return 2;
        }

        try
        {
            var directory = Defaults.AgentDataDirectory;
            if (DirectoryAgentStore.Exists(directory))
            {
                if (!force)
                {
                    using var existing = DirectoryAgentStore.Open(directory, MachineKeyProtection.Dpapi);
                    log.LogInformation("{Directory} is already provisioned as {Pc}, agent {AgentId}, {Enrolled}. Nothing changed; pass --force to provision again with a new agent id.",
                        directory, AgentProvisioning.NameOf(existing.Config.Number), existing.Config.AgentId, existing.Certificate is null ? "not enrolled yet" : "enrolled");
                    return 0;
                }

                foreach (var file in new[] { Defaults.AgentConfigFileName, Defaults.AgentKeyFileName, Defaults.AgentCertificateFileName, Defaults.CaCertificateFileName })
                {
                    var path = Path.Combine(directory, file);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }

                log.LogWarning("Previous provisioning removed (--force); this PC gets a new agent id and the console will replace its old record by number (D-25).");
            }

            var payload = SetupPayload.Open(payloadDirectory);
            var mac = MachineFacts.PrimaryMac();
            if (mac is null)
            {
                log.LogError("No network adapter with a hardware address was found; Wake-on-LAN would have nothing to send to. Connect the PC to the network and run again.");
                return 1;
            }

            var code = payload.TakeCode();
            var hostname = MachineFacts.Hostname();
            using var store = AgentProvisioning.Install(directory, payload, code, number.Value, hostname, mac, consoleHost, consolePort, MachineKeyProtection.Dpapi);

            log.LogInformation("Provisioned {Pc}: agent {AgentId}, lab '{Lab}' ({LabId}), hostname {Host}, MAC {Mac}{Pinned}.",
                AgentProvisioning.NameOf(number.Value), store.Config.AgentId, payload.Document.LabName, payload.Document.LabId, hostname, mac,
                store.Config.ConsoleHost is null ? string.Empty : $", console pinned to {store.Config.ConsoleHost}:{store.Config.ConsolePort}");
            log.LogInformation("The certificate is issued by the console at the first connection; until the teacher opens Enrol PCs there, the agent waits and retries on its own.");

            foreach (var problem in DataDirectoryGuard.Check(directory))
            {
                log.LogWarning("{Problem}", problem);
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or SchemaVersionException or System.Security.Cryptography.CryptographicException)
        {
            log.LogError("Provisioning failed: {Message}", ex.Message);
            return 1;
        }
    }
}
