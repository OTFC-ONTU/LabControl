using System.Security.Cryptography.X509Certificates;
using LabControl.Shared.Discovery;
using LabControl.Shared.Identity;

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
/// beacon flood cost nothing lives here: verify against the pinned CA, refuse a console
/// whose access has been withdrawn, ignore beacons while linked, honour <c>take</c> exactly
/// once per value, and never dial the same endpoint more often than
/// <see cref="Defaults.MinDialInterval"/>.
/// <para>
/// Withdrawal is decided here and not only by the TLS handshake (D-56 item 6): a beacon is
/// what makes a linked PC <i>leave</i> its console, and by the time the handshake could
/// refuse the taker the room has already been given up. A console whose
/// <c>instance:&lt;id&gt;</c> entry this PC holds therefore cannot move it and cannot be
/// dialled at all — which is the whole point of withdrawing a stolen machine.
/// </para>
/// <para>
/// The take-over rule compares only this PC's clock with itself (D-58): a beacon that
/// arrived after the link came up, whose <c>take</c> belongs to that same beacon, moves the
/// room. Nothing here reads the taker's clock as if it were ours, so a teacher machine
/// whose clock is out of step — within the ±<see cref="Defaults.BeaconMaxSkew"/> a beacon
/// must be inside to be heard at all — still takes its own room back.
/// </para>
/// </summary>
public sealed class BeaconGate
{
    private readonly X509Certificate2 _authority;
    private readonly string _labId;
    private readonly RevocationSet _revocations;
    private readonly Dictionary<string, DateTimeOffset> _nextDial = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReconnectBackoff> _backoff = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _honouredTakes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private string? _linkedInstanceId;

    /// <summary>
    /// When the current link came up, read from <b>this PC's own clock</b> (D-58). It is
    /// only ever compared with the arrival time of a later beacon, read from the same
    /// clock; the taker's timestamps are never measured against it.
    /// </summary>
    private DateTimeOffset _linkedAt;

    /// <param name="revocations">
    /// The agent's live revocation set — the same object the link merges into, so an
    /// <c>instance:</c> entry that arrives during a lesson is in force for the very next
    /// beacon (D-56 item 6).
    /// </param>
    public BeaconGate(X509Certificate2 authority, string labId, RevocationSet revocations)
    {
        _authority = authority;
        _labId = labId;
        _revocations = revocations;
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

        // Before anything else a beacon could make this PC do (D-56 item 6). The CA
        // endorsement in the beacon says the instance once existed; only the revocation set
        // says whether it still may drive this room. A withdrawn console is neither followed
        // nor left for — the check comes before the take branch as well as the dial branch.
        if (_revocations.IsRevoked(LabCertificates.InstanceSerial(beacon.InstanceId)))
        {
            return new BeaconVerdict(BeaconAction.Ignore, "this console's access to the lab was withdrawn", beacon);
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
                // pressed Take over the lab (§3.7.2). Three conditions decide it, and none
                // of them puts the taker's clock against this PC's (D-58) — a teacher
                // machine whose clock is out by anything the beacon check tolerates still
                // moves the room.
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

                PruneHonouredTakes(now);
                var token = $"{beacon.InstanceId}/{beacon.TakeAtUnix}";
                if (!_honouredTakes.TryAdd(token, pressed))
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

    /// <summary>
    /// Forgets the tokens of presses that can never be honoured again, so a lab left running
    /// for a term does not accumulate one entry per press for ever. A press is only ever
    /// considered inside <see cref="Defaults.TakeOverWindow"/> + <see cref="Defaults.BeaconMaxSkew"/>
    /// of its own beacon's <c>ts</c>, and that <c>ts</c> must itself be within
    /// <see cref="Defaults.BeaconMaxSkew"/> of now — so anything older than the sum of the
    /// three is unreachable whatever arrives next.
    /// </summary>
    private void PruneHonouredTakes(DateTimeOffset now)
    {
        var oldest = now - (Defaults.TakeOverWindow + Defaults.BeaconMaxSkew + Defaults.BeaconMaxSkew);
        if (_honouredTakes.Count == 0)
        {
            return;
        }

        foreach (var (token, pressed) in _honouredTakes.ToArray())
        {
            if (pressed < oldest)
            {
                _honouredTakes.Remove(token);
            }
        }
    }

    /// <summary>Honoured take-over tokens still worth remembering; for the tests of the pruning rule.</summary>
    public int HonouredTakeCount
    {
        get
        {
            lock (_gate)
            {
                return _honouredTakes.Count;
            }
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
