using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LabControl.Agent;

/// <summary>
/// Logon, logoff, lock, unlock and console switches, as the service control manager reports
/// them (<c>SERVICE_CONTROL_SESSIONCHANGE</c>). Raised from the SCM's thread; subscribers
/// must return quickly. Silent when the agent runs in the foreground — the supervisor's poll
/// covers that case, just a little later (D-30).
/// </summary>
internal sealed class SessionChangeSource
{
    public event Action<SessionChangeReason, int>? Changed;

    public void Raise(SessionChangeReason reason, int sessionId) => Changed?.Invoke(reason, sessionId);
}

/// <summary>
/// The stock Windows-service lifetime with session-change notifications switched on. The
/// hosting package's <see cref="WindowsServiceLifetime"/> is a <see cref="ServiceBase"/> that
/// leaves <c>CanHandleSessionChangeEvent</c> off; this subclass turns it on and forwards
/// the notifications to <see cref="SessionChangeSource"/>.
/// </summary>
internal sealed class SessionChangeLifetime : WindowsServiceLifetime
{
    private readonly SessionChangeSource _source;

    public SessionChangeLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> hostOptions,
        IOptions<WindowsServiceLifetimeOptions> options,
        SessionChangeSource source)
        : base(environment, applicationLifetime, loggerFactory, hostOptions, options)
    {
        _source = source;
        CanHandleSessionChangeEvent = true;
    }

    protected override void OnSessionChange(SessionChangeDescription description)
    {
        base.OnSessionChange(description);
        try
        {
            _source.Raise(description.Reason, description.SessionId);
        }
        catch
        {
            // A subscriber's failure must never propagate into the SCM callback.
        }
    }
}
