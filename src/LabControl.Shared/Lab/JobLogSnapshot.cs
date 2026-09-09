using LabControl.Shared.Protocol;

namespace LabControl.Shared.Lab;

/// <summary>
/// A detached log view, copied while the queue is locked. Deliberately excludes job arguments.
/// <paramref name="LabId"/> and <paramref name="InstanceId"/> say which lab the job belonged
/// to and which console instance delivered it (M5, D-57 item 4).
/// </summary>
public sealed record JobLogSnapshot(
    string Id, string AgentId, Job.Types.Kind Kind, JobState State,
    long CreatedAtUnix, long DeliveredAtUnix, long CompletedAtUnix,
    int Percent, int ExitCode, string Message, string[] Output,
    string LabId = "", string InstanceId = "");
