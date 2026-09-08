using System.Globalization;
using System.Net;
using LabControl.Shared;

namespace LabControl.Console.Services;

/// <summary>
/// The console's command line (D-27, D-59 item 5). Every switch is a development affordance
/// for running two console profiles on one machine or moving the bound address; a lab
/// machine runs with no arguments and gets <see cref="Defaults.ConsoleDataDirectory"/> on all
/// interfaces. Positional arguments are documents to open — a double-clicked lab file, a
/// backup, a grant or a request — and go through the same import as <i>Add labs…</i>.
/// </summary>
public sealed class ConsoleOptions
{
    public string DataDirectory { get; init; } = Defaults.ConsoleDataDirectory;

    public int Port { get; init; } = Defaults.ConsolePort;

    /// <summary>In-process test seam; production discovery uses the shared default port.</summary>
    public int BeaconPort { get; init; } = Defaults.BeaconPort;

    /// <summary>The address the gRPC server binds to and the beacon names; <c>Any</c> = every interface.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    /// <summary>
    /// Issue agent certificates with this lifetime instead of <see cref="Defaults.AgentCertificateLifetime"/>,
    /// so the renewal path (D-25) can be exercised against <c>FakeAgent</c> without waiting five years.
    /// </summary>
    public TimeSpan? DevelopmentAgentCertificateLifetime { get; init; }

    /// <summary>
    /// Documents named on the command line, as full paths: each existed and carried one of
    /// <see cref="Defaults.ConsoleDocumentExtensions"/> when parsed. They are imported as saved
    /// labs and never activate one.
    /// </summary>
    public IReadOnlyList<string> FilesToOpen { get; init; } = [];

    /// <summary>
    /// <c>--import-only</c>: hand <see cref="FilesToOpen"/> to the console already running on
    /// <see cref="DataDirectory"/> and exit; never start a console of its own. The forwarder
    /// mode of a file-type registration (D-59).
    /// </summary>
    public bool ImportOnly { get; init; }

    public static bool TryParse(string[] args, out ConsoleOptions options, out string error)
    {
        var data = Defaults.ConsoleDataDirectory;
        var port = Defaults.ConsolePort;
        var bind = IPAddress.Any;
        TimeSpan? agentLifetime = null;
        var importOnly = false;
        var files = new List<string>();
        options = null!;
        error = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name == "--import-only")
            {
                importOnly = true;
                continue;
            }

            if (name.StartsWith("-psn_", StringComparison.Ordinal))
            {
                // The process serial number older macOS releases append to a Finder launch.
                continue;
            }

            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                if (!TryValidateFile(name, out var file, out error))
                {
                    return false;
                }

                files.Add(file);
                continue;
            }

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
            FilesToOpen = files,
            ImportOnly = importOnly,
        };
        return true;
    }

    /// <summary>
    /// The one rule for a document the console is asked to open, on the command line or over
    /// the single-instance endpoint: it carries a known extension and is an ordinary file of
    /// a plausible size. <paramref name="fullPath"/> is the resolved absolute path.
    /// <para>
    /// The size test is what keeps the console alive: <see cref="File.Exists"/> is also true
    /// of a named pipe or a character device, and reading one of those never returns. Every
    /// document here is a JSON text of a few kilobytes, while a FIFO, a device node and an
    /// empty file all report length 0 — so a length between one byte and
    /// <see cref="Defaults.ConsoleDocumentMaxBytes"/> is both the honest rule and the guard.
    /// A symbolic link is resolved first, so linking to a FIFO does not slip past.
    /// </para>
    /// </summary>
    public static bool TryValidateFile(string path, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "an empty file name";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            error = $"'{path}' is not a file path";
            return false;
        }

        var extension = Path.GetExtension(fullPath);
        if (!Defaults.ConsoleDocumentExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            error = $"'{path}' is not a file this console opens ({string.Join(", ", Defaults.ConsoleDocumentExtensions)})";
            return false;
        }

        try
        {
            var file = new FileInfo(fullPath);
            if (!file.Exists || Directory.Exists(fullPath))
            {
                error = $"'{path}' does not exist";
                return false;
            }

            if (File.ResolveLinkTarget(fullPath, returnFinalTarget: true) is { } target)
            {
                if (target is DirectoryInfo || !target.Exists)
                {
                    error = $"'{path}' does not exist";
                    return false;
                }

                file = target as FileInfo ?? new FileInfo(target.FullName);
            }

            var length = file.Length;
            if (length <= 0)
            {
                error = $"'{path}' is empty or is not an ordinary file";
                return false;
            }

            if (length > Defaults.ConsoleDocumentMaxBytes)
            {
                error = $"'{path}' is {length} bytes; this console opens documents up to {Defaults.ConsoleDocumentMaxBytes} bytes";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"'{path}' could not be read: {ex.Message}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The first file activation on macOS, minus what AppKit echoed back (D-59 item 5):
    /// LaunchServices reports the process's own command-line arguments as opened files, so a
    /// document named on the command line would otherwise be imported twice — once from
    /// <see cref="FilesToOpen"/> and once from the activation. Comparison is on full paths and
    /// case-insensitive, because that is how the file systems this runs on behave; a later
    /// open of the same file is a real request and is not filtered.
    /// </summary>
    public static IReadOnlyList<string> WithoutArgumentEcho(IEnumerable<string> paths, IEnumerable<string> arguments)
    {
        var echoed = arguments.Select(TryFullPath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. paths.Where(path => TryFullPath(path) is not { } full || !echoed.Contains(full))];
    }

    /// <summary>The absolute form of <paramref name="path"/>, or <c>null</c> when it is not a path at all.</summary>
    public static string? TryFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    public const string Usage =
        "usage: LabControl.Console [--data <directory>] [--port <n>] [--bind <address>] [--dev-agent-cert-days <n>] [--import-only] [<file>.lclab|.lcbak|.lcgrant|.lcreq …]";
}
