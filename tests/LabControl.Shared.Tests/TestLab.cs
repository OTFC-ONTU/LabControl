using LabControl.Shared.Identity;

namespace LabControl.Shared.Tests;

/// <summary>
/// Shared fixture helpers. Every test lab uses a deliberately low PBKDF2 iteration count:
/// the production 600 000 is a quarter of a second per unlock, and these tests unlock
/// dozens of times. The count is stored per wrapping, so nothing about the format changes.
/// </summary>
internal static class TestLab
{
    public const int Iterations = 1_000;

    public const string HolderName = "Viacheslav";
    public const string Passphrase = "correct horse battery staple";

    public static LabKey Create(out RecoveryCode recoveryCode) =>
        LabKey.Create("ОНТФК lab 214", HolderName, Passphrase, out recoveryCode, iterations: Iterations);

    public static LabKey Create()
    {
        var key = Create(out _);
        return key;
    }
}
