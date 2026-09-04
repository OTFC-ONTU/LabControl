using System.Security.Cryptography.X509Certificates;
using LabControl.Shared.Discovery;

namespace LabControl.Shared.Link;

/// <summary>What an agent should do about a beacon it just received.</summary>
public enum BeaconAction
{
    /// <summary>Nothing. The overwhelmingly common answer, including for every forgery.</summary>
    Ignore = 0,

    /// <summary>Connect to the endpoint the beacon names.</summary>
    Dial = 1,

    /// <summary>Drop the current link and connect to the taker (ARCHITECTURE §3.7.2).</summary>
    TakeOver = 2,
}

/// <summary>The decision plus the plain-language reason, which goes straight into the agent's log.</summary>
public sealed record BeaconVerdict(BeaconAction Action, string Reason, Beacon? Beacon = null);

/// <summary>
/// The agent's beacon policy (PROTOCOL, "Discovery beacon"). Everything that makes a
/// beacon flood cost nothing lives here: verify against the pinned CA, ignore beacons
/// while linked, honour <c>take</c> exactly once per value, and never dial the same
/// endpoint more often than <see cref="Defaults.MinDialInterval"/>.
/// </summary>
public sealed class BeaconGate
{
    private readonly X509Certificate2 _authority;
    private readonly string _labId;
    private readonly Dictionary<string, DateTimeOffset> _nextDial = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReconnectBackoff> _backoff = new(StringComparer.Ordinal);
    private readonly HashSet<string> _honouredTakes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private string? _linkedInstanceId;
    private long _linkedAtUnix;

    public BeaconGate(X509Certificate2 authority, string labId)
    {
        _authority = authority;
        _labId = labId;
    }

    /// <summary>The console instance this agent is linked to, if any.</summary>
    public string? LinkedInstanceId
    {
        get
        {
            lock (_gate)
            {
                return _linkedInstanceId;
            }
        }
    }

    public BeaconVerdict Consider(ReadOnlySpan<byte> datagram, DateTimeOffset now) =>
        Beacon.TryParse(datagram, out var beacon)
            ? Consider(beacon, now)
            : new BeaconVerdict(BeaconAction.Ignore, "the datagram is not a LabControl beacon");

    public BeaconVerdict Consider(Beacon beacon, DateTimeOffset now)
    {
        if (!beacon.TryVerify(_authority, _labId, now, out var failure))
        {
            return new BeaconVerdict(BeaconAction.Ignore, Describe(failure));
        }

        lock (_gate)
        {
            if (_linkedInstanceId is not null)
            {
                if (string.Equals(_linkedInstanceId, beacon.InstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    return new BeaconVerdict(BeaconAction.Ignore, "already linked to this console", beacon);
                }

                // The one exception to "ignore while connected": another teacher machine
                // pressed Take over the lab after this link was established (§3.7.2).
                if (beacon.TakeAtUnix <= _linkedAtUnix)
                {
                    return new BeaconVerdict(BeaconAction.Ignore, "linked to another console of this lab", beacon);
                }

                var token = $"{beacon.InstanceId}/{beacon.TakeAtUnix}";
                if (!_honouredTakes.Add(token))
                {
                    // The taker rebroadcasts the same value for 30 s; honouring it once is
                    // what stops that from becoming a re-dial loop.
                    return new BeaconVerdict(BeaconAction.Ignore, "this take-over has already been honoured", beacon);
                }

                return new BeaconVerdict(BeaconAction.TakeOver, $"{beacon.InstanceName} took over the lab", beacon);
            }

            if (_nextDial.TryGetValue(beacon.Endpoint, out var earliest) && now < earliest)
            {
                return new BeaconVerdict(BeaconAction.Ignore, "a dial to this endpoint is already due later", beacon);
            }

            _nextDial[beacon.Endpoint] = now + Defaults.MinDialInterval;
            return new BeaconVerdict(BeaconAction.Dial, $"dialling {beacon.Endpoint}", beacon);
        }
    }

    /// <summary>Called once the link is up, so further beacons are ignored.</summary>
    public void Linked(string instanceId, string endpoint, DateTimeOffset at)
    {
        lock (_gate)
        {
            _linkedInstanceId = instanceId;
            _linkedAtUnix = at.ToUnixTimeSeconds();
            _nextDial.Remove(endpoint);
            _backoff.Remove(endpoint);
        }
    }

    /// <summary>Called when the link ends, for any reason.</summary>
    public void Unlinked()
    {
        lock (_gate)
        {
            _linkedInstanceId = null;
            _linkedAtUnix = 0;
        }
    }

    /// <summary>
    /// Called when a dial did not produce a link. The endpoint backs off exponentially, so
    /// a forged beacon repeated a thousand times a second still costs one handshake every
    /// few seconds and then less.
    /// </summary>
    public TimeSpan DialFailed(string endpoint, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_backoff.TryGetValue(endpoint, out var backoff))
            {
                backoff = new ReconnectBackoff(Defaults.MinDialInterval, Defaults.ReconnectDelayMax);
                _backoff[endpoint] = backoff;
            }

            var delay = backoff.Next();
            _nextDial[endpoint] = now + delay;
            return delay;
        }
    }

    private static string Describe(BeaconFailure failure) => failure switch
    {
        BeaconFailure.WrongVersion => "the beacon uses a protocol version this agent does not know",
        BeaconFailure.WrongLab => "the beacon belongs to another lab",
        BeaconFailure.BadEndorsement => "the beacon is not endorsed by this lab's key",
        BeaconFailure.BadSignature => "the beacon's signature does not match its contents",
        BeaconFailure.StaleTimestamp => "the beacon's timestamp is too far from now",
        _ => "the beacon is malformed",
    };
}
