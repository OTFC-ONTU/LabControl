using LabControl.Shared;

namespace LabControl.Agent.Session;

/// <summary>
/// Spawned by the agent service into the interactive session with a SYSTEM token, because
/// a session-0 service can neither see the desktop nor inject input (D-06). M0 is a
/// skeleton; capture, input injection and the lock/broadcast overlay arrive in M2–M5.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("LabControl session helper (skeleton)");
        Console.WriteLine($@"pipe to the service: \\.\pipe\{Defaults.SessionPipeName}");
        Console.WriteLine("Not implemented yet: session plumbing lands in M2, capture in M3, overlay in M5.");
        return 0;
    }
}
