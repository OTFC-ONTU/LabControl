namespace LabControl.Console.Services;

/// <summary>What the teacher chose in the departure dialog (M5, D-57 item 3).</summary>
public enum DepartureChoice
{
    Stay = 0,
    Leave = 1,
    Wait = 2,
}

/// <summary>
/// The two questions a departure may have to ask, without any window behind them: the
/// report itself and the wait for the jobs it listed. The console answers them with
/// <c>DepartureDialog</c> and <c>WaitForJobsDialog</c>; a test answers them directly.
/// </summary>
public interface IDeparturePrompt
{
    /// <summary>Shows the report and returns what the teacher chose.</summary>
    Task<DepartureChoice> AskAsync(string labName, DepartureReport report);

    /// <summary>Waits until nothing is running any more; <c>false</c> when the teacher cancelled the wait.</summary>
    Task<bool> WaitForJobsAsync(LabSession session);
}

/// <summary>
/// The one place that decides whether the console may leave the active lab (M5, D-57 item 3).
/// <para>
/// Every way out of a room goes through it: <i>Disconnect</i>, closing the main window, and —
/// since portion 8 — quitting the application. Quitting used to release the room straight from
/// the shutdown handler, so a teacher who pressed ⌘Q (or the window manager's close on the
/// last window) left a script, a transfer or an update probation behind without ever being
/// told, which is exactly what the acceptance criterion "surface any running scripts,
/// transfers or update probation before leaving" forbids. The report is the same, the choices
/// are the same, and <i>Stay</i> means the console keeps running with the lab still active.
/// </para>
/// </summary>
public sealed class DepartureFlow
{
    private readonly ActiveLabController _controller;
    private readonly IDeparturePrompt _prompt;

    public DepartureFlow(ActiveLabController controller, IDeparturePrompt prompt)
    {
        _controller = controller;
        _prompt = prompt;
    }

    /// <summary>
    /// Whether the console may leave <paramref name="session"/> now. An empty report leaves at
    /// once and asks nothing; <i>Stay</i> answers <c>false</c> and the session stays live;
    /// <i>Wait for N jobs</i> waits and then leaves, unless the wait itself was cancelled.
    /// A session that is not the active one any more (a switch overtook this question, or the
    /// controller has already released it) is never in the way.
    /// </summary>
    public async Task<bool> MayLeaveAsync(LabSession? session)
    {
        if (session is null || session.IsDisposed || !ReferenceEquals(_controller.Active, session))
        {
            return true;
        }

        var report = session.DescribeDeparture();
        if (report.IsEmpty)
        {
            return true;
        }

        var choice = await _prompt.AskAsync(session.LabName, report);
        return choice switch
        {
            DepartureChoice.Stay => false,
            DepartureChoice.Wait => await _prompt.WaitForJobsAsync(session) && !session.IsDisposed,
            _ => true,
        };
    }

    /// <summary>
    /// Whether the application may quit now: the same question as <see cref="MayLeaveAsync"/>,
    /// asked about whichever lab is active. Quitting with no active lab asks nothing.
    /// </summary>
    public Task<bool> MayQuitAsync() => MayLeaveAsync(_controller.Active);
}
