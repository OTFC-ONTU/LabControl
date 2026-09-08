using System.Diagnostics;

namespace LabControl.Shared.Protection;

/// <summary>
/// Instruments the keystore calls (M5 portion 2 review): a Keychain, DPAPI or secret-tool
/// round trip that takes longer than <see cref="Threshold"/> is reported through
/// <see cref="SlowOperation"/>, which the console wires to a warning in its log. This is
/// what turns a 4–6 s "server up" on a real Mac into a line that names the culprit.
/// Shared has no logger of its own; the hook is static and <c>null</c> by default.
/// </summary>
public static class SecretProtectorTiming
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(250);

    /// <summary>(protector name, operation, elapsed) for every call slower than <see cref="Threshold"/>.</summary>
    public static Action<string, string, TimeSpan>? SlowOperation { get; set; }

    public static T Measure<T>(string protector, string operation, Func<T> call)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            return call();
        }
        finally
        {
            Report(protector, operation, clock.Elapsed);
        }
    }

    public static void Measure(string protector, string operation, Action call)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            call();
        }
        finally
        {
            Report(protector, operation, clock.Elapsed);
        }
    }

    private static void Report(string protector, string operation, TimeSpan elapsed)
    {
        if (elapsed > Threshold)
        {
            SlowOperation?.Invoke(protector, operation, elapsed);
        }
    }
}
