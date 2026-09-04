using LabControl.Shared;

namespace LabControl.Agent;

/// <summary>
/// The Windows service that runs on every student PC as LocalSystem. M0 is a skeleton:
/// it proves the project compiles and publishes for win-x64 and win-arm64. The service
/// host, enrollment, the session helper and the job loop arrive in M2
/// (docs/ROADMAP.md).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine($"LabControl agent (skeleton) — service '{Defaults.ServiceName}'");
        Console.WriteLine($"data directory: {Defaults.AgentDataDirectory}");
        Console.WriteLine($"protocol version: {Defaults.ProtocolVersion}");
        Console.WriteLine("Not implemented yet: this becomes a Windows service in milestone M2.");
        return 0;
    }
}
