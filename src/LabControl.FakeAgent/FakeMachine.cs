using LabControl.Shared;

namespace LabControl.FakeAgent;

/// <summary>
/// One simulated student PC. In M0 it only has an identity and a power state; M1 gives
/// it enrollment, a link to the console and injectable failures.
/// </summary>
public sealed class FakeMachine
{
    public FakeMachine(int number)
    {
        Number = number;
        AgentId = Guid.NewGuid().ToString("d");
        Name = string.Format(Defaults.MachineNameFormat, number);
        Mac = FormatMac(number);
        BootedAt = DateTimeOffset.UtcNow;
    }

    public int Number { get; }

    public string AgentId { get; }

    public string Name { get; }

    /// <summary>Deterministic per number, so a restarted FakeAgent keeps the same MACs.</summary>
    public string Mac { get; }

    public DateTimeOffset BootedAt { get; }

    public bool PoweredOn { get; set; } = true;

    public TimeSpan Uptime => DateTimeOffset.UtcNow - BootedAt;

    /// <summary>Locally administered MAC (second-least-significant bit of the first octet set).</summary>
    private static string FormatMac(int number) => $"02:00:5E:00:00:{number:X2}";
}
