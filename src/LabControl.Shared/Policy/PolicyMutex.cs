namespace LabControl.Shared.Policy;

/// <summary>
/// The kernel mutex every restore or apply pass holds (M6, D-69): the service at start and
/// stop, <c>agent.exe --restore-policy</c> from the watchdog task, and any apply in between.
/// Both the service and the task start at boot, and <c>JsonStore</c> protects the bytes of
/// <c>policy.json</c>, not the decision one of them is making from it. The wait is bounded
/// (<see cref="Defaults.PolicyMutexWait"/>): a loser reports <see cref="PolicyEvents.Busy"/>
/// and does nothing, rather than two passes restoring and re-applying over each other. An
/// abandoned mutex — the previous holder died mid-pass — is taken over: the next pass restores
/// first anyway, so nothing half-done can be trusted or needs to be.
/// </summary>
public static class PolicyMutex
{
    /// <summary>
    /// Takes the mutex, waiting at most <paramref name="wait"/> (<see cref="Defaults.PolicyMutexWait"/>
    /// when <c>null</c>). Returns <c>null</c> when it is held elsewhere for the whole wait; the
    /// caller then reports <see cref="PolicyEvents.Busy"/>. Disposing the lease releases it.
    /// </summary>
    public static IDisposable? TryAcquire(TimeSpan? wait = null, string? name = null)
    {
        var mutex = new Mutex(false, name ?? Defaults.PolicyMutexName);
        bool held;
        try
        {
            held = mutex.WaitOne(wait ?? Defaults.PolicyMutexWait);
        }
        catch (AbandonedMutexException)
        {
            held = true;
        }

        if (!held)
        {
            mutex.Dispose();
            return null;
        }

        return new Lease(mutex);
    }

    private sealed class Lease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            var held = Interlocked.Exchange(ref _mutex, null);
            if (held is null)
            {
                return;
            }

            try
            {
                held.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread: released by a thread other than the taker. The
                // mutex is still disposed below, which the kernel treats as a release.
            }

            held.Dispose();
        }
    }
}
