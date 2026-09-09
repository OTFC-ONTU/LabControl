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
/// <para>
/// The take-over rule compares only this PC's clock with itself (D-58): a beacon that
/// arrived after the link came up, whose <c>take</c> belongs to that same beacon, moves the
/// room. Nothing here reads the taker's clock as if it were ours, so a teacher machine
/// half a minute out of step can still take its own room back.
/// </para>
/// </summary>
public sealed class BeaconGate
{
    private readonly X509Certificate2 _authority;
    private readonly string _labId;
    private readonly Dictionary<string, DateTimeOffset> _nextDial = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReconnectBackoff> _backoff = new(StringComparer.Ordinal);
    private readonly HashSet<string> _honouredTakes = new(StringComparer.Ordinal);

    /// <summary>When a verified beacon of this lab last arrived from an endpoint (M5 portion 8).</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastBeacon = new(StringComparer.Ordinal);

    /// <summary>Endpoints whose backoff a beacon has already stepped over since the lab was last heard from.</summary>
    private readonly HashSet<string> _resumed = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private string? _linkedInstanceId;

    /// <summary>
    /// When the current link came up, read from <b>this PC's own clock</b> (D-58). It is
    /// only ever compared with the arrival time of a later beacon, read from the same
    /// clock; the taker's timestamps are never measured against it.
    /// </summary>
    private DateTimeOffset _linkedAt;

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
            // A gap in this lab's own beacons means the room stopped being served and is being
            // served again — the switch away and back (M5 portion 8). That is information the
            // exponential dial backoff cannot have, so the next beacon after such a gap is
            // allowed to step over the wait once. A console that beacons without a pause
            // produces no gap and therefore no extra dial, which is what keeps a console that
            // is up but cannot be linked from being dialled every beacon interval.
            var seen = _lastBeacon.TryGetValue(beacon.Endpoint, out var previous);
            _lastBeacon[beacon.Endpoint] = now;
            if (!seen || now - previous >= Defaults.BeaconResumeGap)
            {
                _resumed.Remove(beacon.Endpoint);
            }

            if (_linkedInstanceId is not null)
            {
                if (string.Equals(_linkedInstanceId, beacon.InstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    return new BeaconVerdict(BeaconAction.Ignore, "already linked to this console", beacon);
                }

                // The one exception to "ignore while connected": another teacher machine
                // pressed Take over the lab (§3.7.2). Three conditions decide it, and none
                // of them puts the taker's clock against this PC's (D-58) — a teacher
                // machine minutes out of step still moves the room.
                if (beacon.TakeAtUnix == 0)
                {
                    return new BeaconVerdict(BeaconAction.Ignore, "linked to another console of this lab", beacon);
                }

                // (b) Arrival order, on this PC's own clock alone. A datagram already in
                // flight when the link came up answered a question that has since been
                // answered by the link itself, so it is not a reason to leave.
                if (now < _linkedAt)
                {
                    return new BeaconVerdict(BeaconAction.Ignore, "the take-over was already in flight when this link was established", beacon);
                }

                // (c) The press must belong to the beacon carrying it. `ts` was already
                // checked against this PC's clock (±BeaconMaxSkew) by TryVerify, so bounding
                // `take` against `ts` bounds how old a press may be without ever comparing
                // the taker's clock with ours.
                var pressed = DateTimeOffset.FromUnixTimeSeconds(beacon.TakeAtUnix);
                var sent = DateTimeOffset.FromUnixTimeSeconds(beacon.SentAtUnix);
                if ((sent - pressed).Duration() > Defaults.TakeOverWindow + Defaults.BeaconMaxSkew)
                {
                    return new BeaconVerdict(BeaconAction.Ignore, "the take-over does not belong to this beacon", beacon);
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
                // Only a wait a failed dial imposed may be stepped over, and only once per
                // gap: the short spacing between two beacon-driven dials is the flood guard
                // itself and stays. The escalation is deliberately kept — if this dial fails
                // too, the next wait carries on from where it was instead of restarting at
                // the floor.
                if (!_backoff.ContainsKey(beacon.Endpoint) || !_resumed.Add(beacon.Endpoint))
                {
                    return new BeaconVerdict(BeaconAction.Ignore, "a dial to this endpoint is already due later", beacon);
                }

                _nextDial[beacon.Endpoint] = now + Defaults.MinDialInterval;
                return new BeaconVerdict(BeaconAction.Dial, $"dialling {beacon.Endpoint} — this lab is being served again", beacon);
            }

            _resumed.Add(beacon.Endpoint);
            _nextDial[beacon.Endpoint] = now + Defaults.MinDialInterval;
            return new BeaconVerdict(BeaconAction.Dial, $"dialling {beacon.Endpoint}", beacon);
        }
    }

    /// <summary>
    /// Called once the link is up, so further beacons are ignored. <paramref name="at"/> must
    /// come from the same clock the host passes to <see cref="Consider(Beacon, DateTimeOffset)"/>
    /// — the take-over rule compares the two and nothing else (D-58).
    /// </summary>
    public void Linked(string instanceId, string endpoint, DateTimeOffset at)
    {
        lock (_gate)
        {
            _linkedInstanceId = instanceId;
            _linkedAt = at;
            _nextDial.Remove(endpoint);
            _backoff.Remove(endpoint);
            _resumed.Remove(endpoint);
        }
    }

    /// <summary>Called when the link ends, for any reason.</summary>
    public void Unlinked()
    {
        lock (_gate)
        {
            _linkedInstanceId = null;
            _linkedAt = default;
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
