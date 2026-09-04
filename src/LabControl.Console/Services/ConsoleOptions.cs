using System.Globalization;
using System.Net;
using LabControl.Shared;

namespace LabControl.Console.Services;

/// <summary>
/// The console's command line (D-27). Every switch is a development affordance for running
/// two console profiles on one machine or moving the bound address; a lab machine runs
/// with no arguments and gets <see cref="Defaults.ConsoleDataDirectory"/> on all interfaces.
/// </summary>
public sealed class ConsoleOptions
{
    public string DataDirectory { get; init; } = Defaults.ConsoleDataDirectory;

    public int Port { get; init; } = Defaults.ConsolePort;

    /// <summary>The address the gRPC server binds to and the beacon names; <c>Any</c> = every interface.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    /// <summary>
    /// Issue agent certificates with this lifetime instead of <see cref="Defaults.AgentCertificateLifetime"/>,
    /// so the renewal path (D-25) can be exercised against <c>FakeAgent</c> without waiting five years.
    /// </summary>
    public TimeSpan? DevelopmentAgentCertificateLifetime { get; init; }

    public static bool TryParse(string[] args, out ConsoleOptions options, out string error)
    {
        var data = Defaults.ConsoleDataDirectory;
        var port = Defaults.ConsolePort;
        var bind = IPAddress.Any;
        TimeSpan? agentLifetime = null;
        options = null!;
        error = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (i + 1 >= args.Length)
            {
                error = $"'{name}' needs a value";
                return false;
            }

            var value = args[++i];
            switch (name)
            {
                case "--data":
                    data = Path.GetFullPath(value);
                    break;

                case "--port":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 0 or > 65535)
                    {
                        error = $"'{value}' is not a port number";
                        return false;
                    }

                    break;

                case "--bind":
                    if (!IPAddress.TryParse(value, out bind!))
                    {
                        error = $"'{value}' is not an IP address";
                        return false;
                    }

                    break;

                case "--dev-agent-cert-days":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days < 1)
                    {
                        error = $"'{value}' is not a number of days";
                        return false;
                    }

                    agentLifetime = TimeSpan.FromDays(days);
                    break;

                default:
                    error = $"unknown argument '{name}'";
                    return false;
            }
        }

        options = new ConsoleOptions
        {
            DataDirectory = data,
            Port = port,
            BindAddress = bind,
            DevelopmentAgentCertificateLifetime = agentLifetime,
        };
        return true;
    }

    public const string Usage =
        "usage: LabControl.Console [--data <directory>] [--port <n>] [--bind <address>] [--dev-agent-cert-days <n>]";
}
