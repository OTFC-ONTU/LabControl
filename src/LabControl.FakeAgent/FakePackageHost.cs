using System.Text.Json;
using LabControl.Shared.Packages;

namespace LabControl.FakeAgent;

/// <summary>
/// <c>install_package</c> on a simulated PC (D-89): the installer really is pulled and its hash
/// checked into this PC's own directory; running it is pretended. What a PC has installed is
/// the detect values kept in <c>installed-packages.json</c> beside it, so a check after a
/// simulated install, or after a restart of the simulator, finds the program. The silent
/// arguments steer the pretence: <c>/fail</c> ends with 1603, <c>/reboot</c> with 3010,
/// <c>/hang</c> — or no arguments at all for an unrecognised installer — waits out the
/// timeout like a real installer showing a question nobody sees, and <c>/nodetect</c> succeeds
/// without installing anything.
/// </summary>
internal sealed class FakePackageHost(string directory, Func<bool> examHeld) : IPackageHost
{
    private readonly Lock _gate = new();

    private string InstalledPath => Path.Combine(directory, "installed-packages.json");

    public bool ExamHeld => examHeld();

    public string StagingDirectory => Path.Combine(directory, "package-staging");

    public DetectFinding Detect(DetectRule rule)
    {
        lock (_gate)
        {
            var match = Installed().FirstOrDefault(value => rule.Kind == DetectKind.Path
                ? string.Equals(value, rule.Value, StringComparison.OrdinalIgnoreCase)
                : value.StartsWith(rule.Value, StringComparison.OrdinalIgnoreCase));
            return new DetectFinding(match is not null, match is null ? null : "1.0");
        }
    }

    public async Task<InstallerExit> RunInstallerAsync(InstallPackageRequest request, string stagedPath, CancellationToken token)
    {
        var silent = request.Silent;
        if (silent.Contains("/hang", StringComparison.OrdinalIgnoreCase) || (silent.Length == 0 && request.Family == InstallerFamily.Unknown))
        {
            await Task.Delay(request.Timeout, token);
            return new InstallerExit(-1, TimedOut: true);
        }

        await Task.Delay(TimeSpan.FromSeconds(1), token);
        if (silent.Contains("/fail", StringComparison.OrdinalIgnoreCase)) return new InstallerExit(1603, TimedOut: false);
        if (!silent.Contains("/nodetect", StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                var installed = Installed();
                installed.Add(request.Detect.Value);
                File.WriteAllText(InstalledPath, JsonSerializer.Serialize(installed));
            }
        }

        return new InstallerExit(silent.Contains("/reboot", StringComparison.OrdinalIgnoreCase) ? 3010 : 0, TimedOut: false);
    }

    private List<string> Installed()
    {
        try
        {
            return File.Exists(InstalledPath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(InstalledPath)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }
}
