using LabControl.Shared.Protocol;

namespace LabControl.Shared.Link;

/// <summary>
/// The part of an agent that differs between a real PC and a simulated one: what it says
/// about itself and how it runs a job. The link library owns everything else — discovery,
/// enrolment, the stream, heartbeats, renewal, idempotency.
/// </summary>
public interface IAgentBehaviour
{
    /// <summary>The <c>Hello</c> fields only the host knows: version, boot time, update state.</summary>
    void Describe(Hello hello);

    /// <summary>Sent once after <c>Hello</c>; <c>null</c> when the host has nothing to report yet.</summary>
    Inventory? DescribeInventory();

    /// <summary>
    /// Runs one job. Progress lines go back through <paramref name="report"/>; the returned
    /// result is sent as <c>JobResult</c> and remembered so a re-sent job never runs twice.
    /// Must not throw: an unexpected failure is a failed result, not a dead link.
    /// </summary>
    Task<JobResult> RunJobAsync(Job job, Func<JobProgress, Task> report, CancellationToken token);
}
