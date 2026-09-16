namespace LabControl.Shared.Policy;

/// <summary>How <see cref="PolicyMutex.TryAcquire"/> ended.</summary>
public enum PolicyMutexOutcome
{
    /// <summary>This lease holds the mutex until it is disposed.</summary>
    Acquired = 0,

    /// <summary>Held elsewhere for the whole wait; the caller reports <see cref="PolicyEvents.Busy"/> and does nothing.</summary>
    Busy = 1,

    /// <summary>
    /// The mutex could not be opened or created at all — an existing object whose DACL this
    /// caller cannot open, a platform without named mutexes — and <see cref="PolicyLease.Problem"/>
    /// says why. The caller decides whether an unserialized pass is better than none.
    /// </summary>
    Unavailable = 2,
}

/// <summary>
/// The result of one <see cref="PolicyMutex.TryAcquire"/>: disposable whatever the outcome,
/// so <c>using var lease = PolicyMutex.TryAcquire();</c> reads the same on every path, and
/// disposal releases the mutex only when it was <see cref="PolicyMutexOutcome.Acquired"/>.
/// May be disposed from any thread, any number of times.
/// </summary>
public sealed class PolicyLease : IDisposable
{
    private readonly ManualResetEventSlim? _release;
    private readonly Thread? _owner;
    private int _disposed;

    internal PolicyLease(PolicyMutexOutcome outcome, string problem, ManualResetEventSlim? release, Thread? owner)
    {
        Outcome = outcome;
        Problem = problem;
        _release = release;
        _owner = owner;
    }

    public PolicyMutexOutcome Outcome { get; }

    /// <summary>Why it is <see cref="PolicyMutexOutcome.Unavailable"/>, in the exception's words; empty otherwise.</summary>
    public string Problem { get; }

    public bool Acquired => Outcome == PolicyMutexOutcome.Acquired;

    /// <summary>
    /// Releases the mutex on the thread that took it and waits for that release to have
    /// happened, so a <see cref="PolicyMutex.TryAcquire"/> that follows on any thread finds
    /// it free.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _release is null || _owner is null)
        {
            return;
        }

        _release.Set();
        _owner.Join();
        _release.Dispose();
    }
}

/// <summary>
/// The kernel mutex every restore or apply pass holds (M6, D-69): the service at start and
/// stop, <c>agent.exe --restore-policy</c> from the watchdog task, and any apply in between.
/// Both the service and the task start at boot, and <c>JsonStore</c> protects the bytes of
/// <c>policy.json</c>, not the decision one of them is making from it. The wait is bounded
/// (<see cref="Defaults.PolicyMutexWait"/>): a loser reports <see cref="PolicyEvents.Busy"/>
/// and does nothing, rather than two passes restoring and re-applying over each other. An
/// abandoned mutex — the previous holder died mid-pass — is taken over: the next pass restores
/// first anyway, so nothing half-done can be trusted or needs to be.
/// <para>
/// A Win32 mutant is owned by a <i>thread</i>: it can only be released by the thread that
/// took it, closing the handle does not release it, and it becomes abandoned only when that
/// thread ends. So the lease does not take the mutex on the caller's thread — an
/// <c>async</c> caller would dispose it on another pool thread, the release would fail and
/// the mutex would stay held until the process exits, making every later pass (the
/// <c>OnStop</c> restore, the watchdog in another process) report busy and do nothing.
/// Instead a dedicated thread takes the mutex, holds it for the lease's lifetime and
/// releases it on <see cref="PolicyLease.Dispose"/>, whichever thread disposes. If the
/// process dies while a lease is held, the owner thread dies with it and the mutex is
/// abandoned — exactly the case the watchdog exists for.
/// </para>
/// </summary>
public static class PolicyMutex
{
    /// <summary>
    /// Takes the mutex, waiting at most <paramref name="wait"/> (<see cref="Defaults.PolicyMutexWait"/>
    /// when <c>null</c>). Never throws: the outcome says whether it was
    /// <see cref="PolicyMutexOutcome.Acquired"/>, <see cref="PolicyMutexOutcome.Busy"/> for the
    /// whole wait, or <see cref="PolicyMutexOutcome.Unavailable"/> with the reason. Disposing
    /// the lease releases it, from any thread.
    /// </summary>
    public static PolicyLease TryAcquire(TimeSpan? wait = null, string? name = null)
    {
        var mutexName = name ?? Defaults.PolicyMutexName;
        var timeout = wait ?? Defaults.PolicyMutexWait;
        var decided = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var outcome = PolicyMutexOutcome.Unavailable;
        var problem = string.Empty;

        var owner = new Thread(() =>
        {
            Mutex? mutex = null;
            try
            {
                try
                {
                    mutex = new Mutex(false, mutexName);
                    bool held;
                    try
                    {
                        held = mutex.WaitOne(timeout);
                    }
                    catch (AbandonedMutexException)
                    {
                        // The previous holder died mid-pass; we own it now and restore first anyway.
                        held = true;
                    }

                    outcome = held ? PolicyMutexOutcome.Acquired : PolicyMutexOutcome.Busy;
                }
                catch (Exception ex)
                {
                    // UnauthorizedAccessException (an object with a DACL this caller cannot open),
                    // WaitHandleCannotBeOpenedException, IOException, PlatformNotSupportedException…
                    outcome = PolicyMutexOutcome.Unavailable;
                    problem = ex.Message;
                }

                decided.Set();
                if (outcome != PolicyMutexOutcome.Acquired)
                {
                    return;
                }

                release.Wait();
                try
                {
                    mutex!.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Cannot happen: this is the thread that took it. Nothing to do if it does.
                }
            }
            finally
            {
                mutex?.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "policy-mutex",
        };

        try
        {
            owner.Start();
        }
        catch (Exception ex)
        {
            decided.Dispose();
            release.Dispose();
            return new PolicyLease(PolicyMutexOutcome.Unavailable, ex.Message, null, null);
        }

        decided.Wait();
        decided.Dispose();
        if (outcome != PolicyMutexOutcome.Acquired)
        {
            owner.Join();
            release.Dispose();
            return new PolicyLease(outcome, problem, null, null);
        }

        return new PolicyLease(PolicyMutexOutcome.Acquired, string.Empty, release, owner);
    }
}
