using System.Collections.Concurrent;
using LabControl.Shared;
using LabControl.Shared.Exam;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Agent;

/// <summary>
/// What the rest of the agent tells the exam's watch (D-75): a power job is about to close
/// everything — the watch pauses so Windows can — and a process the agent itself started for
/// the teacher (a <c>run_script</c> in the student's session) is the teacher's, never judged.
/// Static because the jobs and the watch live in the same service process and never meet.
/// </summary>
internal static class ExamCoordination
{
    private static readonly ConcurrentDictionary<int, DateTimeOffset> AgentProcesses = new();
    private static long _pausedUntilTicks;

    /// <summary>A shutdown, reboot or log off starts: the whitelist pauses for <see cref="Defaults.ExamPowerJobSuspension"/>.</summary>
    public static void PauseForPowerJob()
    {
        var until = DateTimeOffset.UtcNow + Defaults.ExamPowerJobSuspension;
        Interlocked.Exchange(ref _pausedUntilTicks, until.UtcTicks);
        Paused?.Invoke(until);
    }

    /// <summary>Raised when a power job pauses the watch.</summary>
    public static event Action<DateTimeOffset>? Paused;

    public static DateTimeOffset PausedUntil => new(Interlocked.Read(ref _pausedUntilTicks), TimeSpan.Zero);

    /// <summary>The agent started <paramref name="processId"/> in the student's session for the teacher.</summary>
    public static void NoteAgentProcess(int processId)
    {
        AgentProcesses[processId] = DateTimeOffset.UtcNow;
        foreach (var old in AgentProcesses.Where(p => DateTimeOffset.UtcNow - p.Value > TimeSpan.FromHours(12)).Select(p => p.Key).ToList())
        {
            AgentProcesses.TryRemove(old, out _);
        }
    }

    /// <summary>The teacher's process ended: its id may now be reused by a student's program (review finding 4).</summary>
    public static void ForgetAgentProcess(int processId) => AgentProcesses.TryRemove(processId, out _);

    public static bool IsAgentProcess(int processId) => AgentProcesses.ContainsKey(processId);
}

/// <summary>
/// The Windows half of an exam (M6 portion 4, D-75): the strip goes down the helper pipe as an
/// <c>ExamStrip</c> (the helper restarts at logon and logoff, D-30, and is told again by the
/// journal's re-assert), and the allowed-programs watch runs <b>here, in the service</b> — a
/// helper that restarts cannot be what keeps a program closed. Every
/// <see cref="Defaults.ExamWatchInterval"/> the watch looks at the active console session's
/// processes (<see cref="WindowsProcesses"/>), lets the shared <see cref="ProgramWatch"/>
/// decide, asks the helper to close the windows of what was open when the exam began, and
/// ends what must end. Nothing here throws out: a failure is a reason code in
/// <c>ExamState</c> and a line in the log.
/// </summary>
internal sealed class WindowsExamControl : IDisposable
{
    private readonly ILogger _log;
    private readonly Func<ServiceMessage, bool> _sendToHelper;
    private readonly IReadOnlyList<string> _protectedDirectories;
    private readonly IReadOnlyList<string> _systemDirectories;
    private readonly WindowsProcesses _processes = new();
    private readonly ProgramWatch _watch = new(ExamCoordination.IsAgentProcess);
    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private readonly List<(string Name, DateTimeOffset At)> _terminated = [];
    private long _terminatedVersion;
    private ExamSession? _exam;
    private string _notice = string.Empty;
    private DateTimeOffset _noticeUntil;
    private bool _stripDelivered = true;
    private bool _noUser;
    private bool _watchFailed;
    private bool _endRefused;
    private bool _disposed;
    private readonly Action<DateTimeOffset> _onPaused;
    private int _watching;

    /// <param name="protectedDirectories">LabControl's own binaries; nothing under them is ever judged.</param>
    /// <param name="systemDirectories">Where a never-touch name must live to be Windows' own (review finding 3).</param>
    public WindowsExamControl(ILogger log, Func<ServiceMessage, bool> sendToHelper, IReadOnlyList<string> protectedDirectories, IReadOnlyList<string> systemDirectories)
    {
        _log = log;
        _sendToHelper = sendToHelper;
        _protectedDirectories = protectedDirectories;
        _systemDirectories = systemDirectories;
        _timer = new Timer(_ => Watch(), null, Timeout.Infinite, Timeout.Infinite);
        _onPaused = until =>
        {
            lock (_lock)
            {
                _watch.Suspend(until);
            }
        };
        ExamCoordination.Paused += _onPaused;
    }

    public MachineResult Apply(ExamSession session)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return MachineResult.Failed(PolicyEvents.RefusedStopped);
            }

            if (_exam?.SessionId != session.SessionId)
            {
                // Another exam: what the last one closed is not this one's (VM drill 2026-10-23).
                ForgetTerminatedLocked();
            }

            _exam = session;
            if (session.RestrictPrograms)
            {
                _watch.Begin(session.AllowedPrograms);
                if (ExamCoordination.PausedUntil > DateTimeOffset.UtcNow)
                {
                    _watch.Suspend(ExamCoordination.PausedUntil);
                }

                _timer.Change(TimeSpan.Zero, Defaults.ExamWatchInterval);
            }
            else
            {
                _watch.End();
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                _endRefused = false;
                _watchFailed = false;
            }

            SendStripLocked();
        }

        _log.LogInformation("exam {Session} in force: timer {Timer}, programs {Programs}", session.SessionId,
            session.UntilUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(session.UntilUnix).ToString("u") : "none",
            session.RestrictPrograms ? string.Join(", ", session.AllowedPrograms) : "unrestricted");
        return MachineResult.Done;
    }

    public MachineResult Lift()
    {
        lock (_lock)
        {
            _exam = null;
            ForgetTerminatedLocked();
            _watch.End();
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _notice = string.Empty;
            _endRefused = false;
            _watchFailed = false;
            _noUser = false;
            _sendToHelper(new ServiceMessage { ExamStrip = new ExamStrip { Show = false } });
        }

        return MachineResult.Done;
    }

    private void ForgetTerminatedLocked()
    {
        if (_terminated.Count > 0)
        {
            _terminated.Clear();
            _terminatedVersion++;
        }
    }

    public ExamRuntime Runtime()
    {
        lock (_lock)
        {
            if (_exam is null)
            {
                return ExamRuntime.None;
            }

            var now = DateTimeOffset.UtcNow;
            var recent = _terminated.Where(t => t.At >= now - Defaults.ExamTerminatedWindow)
                .OrderByDescending(t => t.At).Select(t => t.Name).Take(Defaults.ExamTerminatedMax).ToList();
            var reasons = new List<string>();
            if (_noUser)
            {
                reasons.Add(ExamReasons.NoUser);
            }

            if (_watch.Active && _watch.IsSuspended(now))
            {
                reasons.Add(ExamReasons.Suspended);
            }

            if (_watchFailed)
            {
                reasons.Add(ExamReasons.WatchFailed);
            }

            if (_endRefused)
            {
                reasons.Add(ExamReasons.EndRefused);
            }

            if (!_stripDelivered && !_noUser)
            {
                reasons.Add(ExamReasons.StripNotShown);
            }

            var enforced = _watch.Active && !_noUser && !_watchFailed && !_watch.IsSuspended(now);
            return new ExamRuntime(enforced, recent, _terminatedVersion, string.Join(' ', reasons));
        }
    }

    /// <summary>One look at the student's session, on the timer's thread; a look still running when the next is due is not doubled.</summary>
    private void Watch()
    {
        if (Interlocked.Exchange(ref _watching, 1) == 1)
        {
            return;
        }

        try
        {
            ExamSession? exam;
            lock (_lock)
            {
                exam = _exam;
                if (_disposed || exam is null || !_watch.Active)
                {
                    return;
                }
            }

            var context = Context();
            IReadOnlyCollection<ProcessInfo> processes = context.UserSid is null ? [] : _processes.Snapshot(context.SessionId);

            WatchDecision decision;
            lock (_lock)
            {
                var hadUser = !_noUser;
                _noUser = context.UserSid is null;
                _watchFailed = false;
                decision = _watch.Observe(processes, context, DateTimeOffset.UtcNow);
                if (!hadUser && !_noUser)
                {
                    // A student signed in under the exam: a new helper will want the strip.
                    SendStripLocked();
                }
            }

            var warned = decision.Warn.Where(p => !ProgramWhitelist.IsQuiet(p.Name)).ToList();
            if (warned.Count > 0)
            {
                // The student is told first and has the grace to save (a lab PC, 2026-10-23);
                // background helpers they never opened are not named.
                lock (_lock)
                {
                    _notice = OverlayText.ClosingText(warned.Select(p => p.Name), (int)Defaults.ExamSweepGrace.TotalSeconds);
                    _noticeUntil = DateTimeOffset.UtcNow + Defaults.ExamNoticeDuration;
                    SendStripLocked();
                }

                _log.LogInformation("exam: warned that {Programs} close in {Grace} s",
                    string.Join(", ", decision.Warn.Select(p => p.Name)), Defaults.ExamSweepGrace.TotalSeconds);
            }

            if (decision.AskToClose.Count > 0)
            {
                var ask = new ClosePrograms();
                ask.ProcessIds.AddRange(decision.AskToClose.Select(p => (uint)p.ProcessId));
                _sendToHelper(new ServiceMessage { ClosePrograms = ask });
                _log.LogInformation("exam: asked {Programs} to close; they end in {Grace} s if still open",
                    string.Join(", ", decision.AskToClose.Select(p => p.Name)), Defaults.ExamCloseGrace.TotalSeconds);
            }

            var refused = false;
            foreach (var process in decision.End)
            {
                // Decided a moment ago, outside the lock: the exam may have ended or allowed it since (review finding 8).
                lock (_lock)
                {
                    if (_disposed || _exam is null || !_watch.Active
                        || (ProgramNames.TryNormalize(process.Name, out var normalized) && _watch.Allowed.Contains(normalized)))
                    {
                        continue;
                    }
                }

                var (outcome, problem) = WindowsProcesses.End(process);
                if (outcome == EndOutcome.Failed)
                {
                    refused = true;
                    _log.LogWarning("exam: {Program} (pid {Pid}) would not end: {Problem}", process.Name, process.ProcessId, problem);
                    continue;
                }

                if (outcome != EndOutcome.Ended)
                {
                    continue;
                }

                _log.LogInformation("exam: ended {Program} (pid {Pid})", process.Name, process.ProcessId);
                lock (_lock)
                {
                    var now = DateTimeOffset.UtcNow;
                    _terminated.Add((process.Name, now));
                    _terminated.RemoveAll(t => t.At < now - Defaults.ExamTerminatedWindow);
                    _terminatedVersion++;
                    if (!ProgramWhitelist.IsQuiet(process.Name))
                    {
                        _notice = OverlayText.NotAllowedText(process.Name);
                        _noticeUntil = now + Defaults.ExamNoticeDuration;
                        SendStripLocked();
                    }
                }
            }

            lock (_lock)
            {
                _endRefused = refused;
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _watchFailed = true;
            }

            _log.LogWarning(ex, "exam: looking at the student's programs failed: {Message}", ex.Message);
        }
        finally
        {
            Volatile.Write(ref _watching, 0);
        }
    }

    private SessionContext Context()
    {
        var snapshot = InteractiveSession.Snapshot();
        if (snapshot.SessionId is not { } session || !snapshot.HasUser)
        {
            return SessionContext.Nobody;
        }

        var sid = WindowsProcesses.SessionUser(session);
        return sid is null ? SessionContext.Nobody : new SessionContext((int)session, sid, _protectedDirectories) { SystemDirectories = _systemDirectories };
    }

    /// <summary>The strip as it should be now; a helper that is not there is told when it connects (the journal's re-assert).</summary>
    private void SendStripLocked()
    {
        if (_exam is not { } exam)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var strip = new ExamStrip
        {
            Show = true,
            SessionId = exam.SessionId ?? string.Empty,
            Message = exam.Message ?? string.Empty,
            UntilSeconds = exam.UntilUnix > 0 ? Math.Max(1, exam.UntilUnix - now.ToUnixTimeSeconds()) : 0,
            Notice = now < _noticeUntil ? _notice : string.Empty,
        };

        // Nothing to show is nothing missing: a strip with no timer, message or notice is hidden anyway.
        var empty = strip.UntilSeconds == 0 && strip.Message.Length == 0 && strip.Notice.Length == 0;
        _stripDelivered = _sendToHelper(new ServiceMessage { ExamStrip = strip }) || empty;
    }

    public void Dispose()
    {
        ExamCoordination.Paused -= _onPaused;
        lock (_lock)
        {
            _disposed = true;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        _timer.Dispose();
    }
}
