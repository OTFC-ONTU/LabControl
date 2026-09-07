using LabControl.Shared.Protocol;

namespace LabControl.Shared.Lab;

/// <summary>A detached log view, copied while the queue is locked. Deliberately excludes job arguments.</summary>
public sealed record JobLogSnapshot(
    string Id, string AgentId, Job.Types.Kind Kind, JobState State,
    long CreatedAtUnix, long DeliveredAtUnix, long CompletedAtUnix,
    int Percent, int ExitCode, string Message, string[] Output);
