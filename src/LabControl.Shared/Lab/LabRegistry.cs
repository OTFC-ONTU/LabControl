using System.Security.Cryptography.X509Certificates;
using LabControl.Shared.Discovery;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Lab;

/// <summary>
/// This console's view of the lab: the machines, the other teacher machines, the
/// revocations and the room layout. The list is a <b>cache</b> — the lab's truth is the
/// lab key plus what the agents know (ARCHITECTURE §3.7) — which is what makes alternating
/// between two teacher machines cheap: the second machine catches up by watching the
/// agents connect, not by being told.
/// </summary>
public sealed class LabRegistry
{
    private readonly X509Certificate2 _authority;
    private readonly Lock _gate = new();

    public LabRegistry(LabDocument document, X509Certificate2 authority)
    {
        Document = document;
        _authority = authority;
        Revocations = new RevocationSet();

        // Anything already in lab.json still has to verify: a hand-edited file must not be
        // able to revoke a certificate the lab key never revoked.
        foreach (var record in document.Revocations.ToArray())
        {
            if (!Revocations.TryAdd(authority, record.ToEntry()))
            {
                document.Revocations.Remove(record);
            }
        }
    }

    public LabDocument Document { get; }

    /// <summary>
    /// Runs <paramref name="persist"/> with the document held still. Thirty PCs saying
    /// <c>Hello</c> at once mutate the list from thirty threads; serializing it outside the
    /// lock would sooner or later throw mid-enumeration.
    /// </summary>
    public void Persist(Action<LabDocument> persist)
    {
        lock (_gate)
        {
            persist(Document);
        }
    }

    /// <summary>Every signed revocation this console holds; merged as a set (D-21).</summary>
    public RevocationSet Revocations { get; }

    /// <summary>Raised whenever something happened that <c>lab.json</c> should be saved for.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------ machines

    /// <summary>
    /// Raised when a PC arrives with a number another record already holds. The old record
    /// is the same physical PC reinstalled (new agent id, same sticker) and has been dropped
    /// in favour of the new one — the number is the identity (D-25). Arguments: replaced, replacement.
    /// </summary>
    public event Action<MachineRecord, MachineRecord>? Replaced;

    /// <summary>
    /// Records a PC from its <c>Hello</c>. A machine whose certificate chains to the lab CA
    /// but that this console has never seen is <b>added</b>, not refused: another teacher
    /// machine may have enrolled it (PROTOCOL, "Versioning"). The PC is now linked to
    /// <paramref name="thisInstanceId"/>, which is what the "held by" banner reads later.
    /// </summary>
    public MachineRecord RecordHello(
        Hello hello, string certificateSerial, string thisInstanceId, DateTimeOffset now, out bool isNew)
    {
        MachineRecord machine;
        MachineRecord? replaced;

        lock (_gate)
        {
            machine = FindOrAdd(hello.AgentId, now, out isNew);
            replaced = ClaimNumber(machine, hello.Number);

            machine.Mac = hello.Mac;
            machine.CertificateSerial = LabCertificates.NormalizeSerial(certificateSerial);
            machine.AgentVersion = hello.AgentVersion;
            machine.ProtocolVersion = hello.ProtocolVersion;
            machine.LastSeenUnix = now.ToUnixTimeSeconds();

            // The strongest observation there is: the PC is talking to this console right
            // now (M5, D-58). It also supersedes any earlier observation of another teacher
            // machine, so a PC this console has held cannot later be claimed for that one.
            machine.LastInstanceId = thisInstanceId;
            machine.LastInstanceObservedUnix = now.ToUnixTimeSeconds();

            Document.Machines.Sort((a, b) => a.Number.CompareTo(b.Number));
        }

        // The console this PC came from is a teacher machine this lab has seen (§3.7.1).
        if (hello.PreviousInstanceId.Length > 0 &&
            !string.Equals(hello.PreviousInstanceId, thisInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            RecordInstance(hello.PreviousInstanceId, string.Empty, string.Empty, now, isThisMachine: false);
        }

        Announce(replaced, machine);
        return machine;
    }

    /// <summary>Enrolment adds the machine before it has ever said <c>Hello</c>.</summary>
    public MachineRecord RecordEnrollment(EnrollRequest request, string certificateSerial, DateTimeOffset now)
    {
        MachineRecord machine;
        MachineRecord? replaced;

        lock (_gate)
        {
            machine = FindOrAdd(request.AgentId, now, out _);
            replaced = ClaimNumber(machine, request.Number);

            machine.Hostname = request.Hostname;
            machine.Mac = request.Mac;
            machine.CertificateSerial = LabCertificates.NormalizeSerial(certificateSerial);
            machine.ProtocolVersion = request.ProtocolVersion;

            Document.Machines.Sort((a, b) => a.Number.CompareTo(b.Number));
        }

        Announce(replaced, machine);
        return machine;
    }

    /// <summary>A renewed certificate (D-25) changes only the serial the console will accept next.</summary>
    public MachineRecord? RecordRenewal(string agentId, string certificateSerial, DateTimeOffset now)
    {
        lock (_gate)
        {
            var machine = Document.Machines.FirstOrDefault(
                m => string.Equals(m.AgentId, agentId, StringComparison.OrdinalIgnoreCase));

            if (machine is null)
            {
                return null;
            }

            machine.CertificateSerial = LabCertificates.NormalizeSerial(certificateSerial);
            machine.LastSeenUnix = now.ToUnixTimeSeconds();
            Changed?.Invoke();
            return machine;
        }
    }

    private MachineRecord FindOrAdd(string agentId, DateTimeOffset now, out bool isNew)
    {
        var machine = Document.Machines.FirstOrDefault(
            m => string.Equals(m.AgentId, agentId, StringComparison.OrdinalIgnoreCase));

        isNew = machine is null;
        if (machine is null)
        {
            machine = new MachineRecord { AgentId = agentId, EnrolledAtUnix = now.ToUnixTimeSeconds() };
            Document.Machines.Add(machine);
        }

        return machine;
    }

    /// <summary>
    /// Gives <paramref name="machine"/> its number. Any <i>other</i> record with that number
    /// is the same PC before it was reinstalled, and is dropped (D-25). Called under the lock.
    /// </summary>
    private MachineRecord? ClaimNumber(MachineRecord machine, int number)
    {
        machine.Number = number;

        var previous = Document.Machines.FirstOrDefault(
            m => m.Number == number && !ReferenceEquals(m, machine));

        if (previous is not null)
        {
            Document.Machines.Remove(previous);
        }

        return previous;
    }

    private void Announce(MachineRecord? replaced, MachineRecord replacement)
    {
        if (replaced is not null)
        {
            Replaced?.Invoke(replaced, replacement);
        }

        Changed?.Invoke();
    }

    public MachineRecord? FindByAgentId(string agentId)
    {
        lock (_gate)
        {
            return Document.Machines.FirstOrDefault(
                m => string.Equals(m.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Removes a machine the teacher does not recognise — a bogus enrolment, say.</summary>
    public bool Forget(string agentId)
    {
        lock (_gate)
        {
            var machine = Document.Machines.FirstOrDefault(
                m => string.Equals(m.AgentId, agentId, StringComparison.OrdinalIgnoreCase));

            if (machine is null)
            {
                return false;
            }

            Document.Machines.Remove(machine);
            Changed?.Invoke();
            return true;
        }
    }

    // ------------------------------------------------------------------ instances

    /// <summary>
    /// Records a console instance — this one at startup, another one from its beacon or
    /// from an agent's <c>previous_instance_id</c> (ARCHITECTURE §3.7.1).
    /// </summary>
    public InstanceRecord RecordInstance(
        string instanceId, string name, string certificateSerial, DateTimeOffset now, bool isThisMachine)
    {
        lock (_gate)
        {
            var instance = Document.Instances.FirstOrDefault(
                i => string.Equals(i.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

            if (instance is null)
            {
                instance = new InstanceRecord { InstanceId = instanceId, FirstSeenUnix = now.ToUnixTimeSeconds() };
                Document.Instances.Add(instance);
            }

            if (name.Length > 0)
            {
                instance.Name = name;
            }

            if (certificateSerial.Length > 0)
            {
                // A record written by an older build has a serial but no history: keep that one too.
                RememberSerial(instance, instance.CertificateSerial);
                instance.CertificateSerial = LabCertificates.NormalizeSerial(certificateSerial);
                RememberSerial(instance, instance.CertificateSerial);
            }

            // Never the access level: a record's authority is set by an approval or a backup,
            // and a Hello or a beacon naming the same id must not lower it (D-56 item 4).
            instance.LastSeenUnix = now.ToUnixTimeSeconds();
            instance.IsThisMachine = isThisMachine || instance.IsThisMachine;

            Changed?.Invoke();
            return instance;
        }
    }

    /// <summary>
    /// Records a device authorization from an approved request (D-56 item 4). The id must be
    /// new or belong to a teacher device already in the book — never to this machine, an
    /// administrator machine or a record of unknown authority, which the approval refuses
    /// before issuing anything (<see cref="DeviceAuthorization.Approve"/>); this is the
    /// last line, so a caller cannot turn the administrator's own record into a teacher's.
    /// </summary>
    public InstanceRecord RecordAuthorization(string instanceId, string name, string certificateSerial, string publicKeyFingerprint, DateTimeOffset now)
    {
        lock (_gate)
        {
            var existing = Document.Instances.FirstOrDefault(
                i => string.Equals(i.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && (existing.IsThisMachine || existing.Access != ProfileAccess.Teacher))
            {
                throw new InvalidOperationException($"Device id {instanceId} belongs to {(existing.IsThisMachine ? "this machine" : "a console that is not a teacher device")}; it cannot be authorized as a teacher device.");
            }
        }

        var instance = RecordInstance(instanceId, name, certificateSerial, now, isThisMachine: false);
        lock (_gate)
        {
            instance.Access = ProfileAccess.Teacher;
            instance.AuthorizedAtUnix = now.ToUnixTimeSeconds();
            instance.RevokedAtUnix = 0;
            instance.PublicKeyFingerprint = publicKeyFingerprint;
        }

        Changed?.Invoke();
        return instance;
    }

    /// <summary>Every serial an instance was ever recorded with, so a withdrawal revokes all of them (D-56 item 6). Called under the lock.</summary>
    private static void RememberSerial(InstanceRecord instance, string normalizedSerial)
    {
        if (normalizedSerial.Length > 0 && !instance.CertificateSerials.Contains(normalizedSerial, StringComparer.Ordinal))
        {
            instance.CertificateSerials.Add(normalizedSerial);
        }
    }

    /// <summary>Records another teacher machine seen beaconing, after the beacon verified.</summary>
    public InstanceRecord RecordBeacon(Beacon beacon, DateTimeOffset now) =>
        RecordInstance(beacon.InstanceId, beacon.InstanceName, string.Empty, now, isThisMachine: false);

    public IReadOnlyList<InstanceRecord> OtherTeacherMachines(string thisInstanceId)
    {
        lock (_gate)
        {
            return Document.Instances
                .Where(i => !string.Equals(i.InstanceId, thisInstanceId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    /// <summary>
    /// Records that another teacher machine has taken a PC over (M5, D-58). This is the one
    /// way a console learns of a holder that is not itself, and it is a <b>positive</b>
    /// observation rather than an inference from silence: the taker's signed <c>take</c>
    /// beacon arrived here, and the PC left this console while that press was live.
    /// </summary>
    public MachineRecord? RecordObservedElsewhere(string agentId, string instanceId, DateTimeOffset now)
    {
        MachineRecord? machine;
        lock (_gate)
        {
            machine = Document.Machines.FirstOrDefault(
                m => string.Equals(m.AgentId, agentId, StringComparison.OrdinalIgnoreCase));

            if (machine is null)
            {
                return null;
            }

            machine.LastInstanceId = instanceId;
            machine.LastInstanceObservedUnix = now.ToUnixTimeSeconds();
        }

        Changed?.Invoke();
        return machine;
    }

    /// <summary>
    /// The PCs this console positively knows are with <paramref name="instanceId"/>: the last
    /// observation names that instance and is younger than
    /// <see cref="Defaults.OwnershipObservationLifetime"/>. Whether that machine is still on
    /// the network, and whether the PC has since come back here, is the caller's business —
    /// this answers only what was learned, never what is presumed (§3.7.2 banner).
    /// </summary>
    public IReadOnlyList<MachineRecord> ObservedElsewhere(string instanceId, DateTimeOffset now)
    {
        lock (_gate)
        {
            return Document.Machines
                .Where(m => IsObservedWith(m.LastInstanceId, m.LastInstanceObservedUnix, instanceId, now))
                .OrderBy(m => m.Number)
                .ToArray();
        }
    }

    /// <summary>
    /// Whether a machine's last positive observation names <paramref name="instanceId"/> and
    /// is still fresh. The instance and the moment are read as one pair under this registry's
    /// own lock, so a caller cannot catch the two halves of an observation being written
    /// (<see cref="RecordHello"/> and <see cref="RecordObservedElsewhere"/> both set them
    /// together) and credit a PC to one console with another console's timestamp.
    /// </summary>
    public bool IsObservedWith(MachineRecord machine, string instanceId, DateTimeOffset now)
    {
        string? observed;
        long observedAtUnix;
        lock (_gate)
        {
            observed = machine.LastInstanceId;
            observedAtUnix = machine.LastInstanceObservedUnix;
        }

        return IsObservedWith(observed, observedAtUnix, instanceId, now);
    }

    /// <summary>
    /// The same rule on a pair already read together. The age is worked out in whole seconds
    /// rather than by building a <see cref="DateTimeOffset"/>, so a <c>lab.json</c> that has
    /// been corrupted into an impossible timestamp is answered "no" instead of throwing under
    /// the lab view. An observation in the future is tolerated only by the clock skew a
    /// beacon is allowed anyway: a lab file written while the clock was ahead must not count
    /// as fresh for a second lifetime on top of the first.
    /// </summary>
    public static bool IsObservedWith(string? observed, long observedAtUnix, string instanceId, DateTimeOffset now)
    {
        if (observedAtUnix <= 0 || instanceId.Length == 0 ||
            observed is not { Length: > 0 } instance ||
            !string.Equals(instance, instanceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var age = now.ToUnixTimeSeconds() - observedAtUnix;
        return age >= -(long)Defaults.BeaconMaxSkew.TotalSeconds &&
               age <= (long)Defaults.OwnershipObservationLifetime.TotalSeconds;
    }

    // ------------------------------------------------------------------ revocation

    /// <summary>
    /// Revokes a certificate. Needs the lab key, which is the point: revocation is a
    /// security action for a stolen machine and nothing else (ARCHITECTURE §3.7.3).
    /// </summary>
    public RevocationEntry Revoke(LabKey lab, string certificateSerial, string reason, DateTimeOffset now)
    {
        var entry = RevocationSet.Create(lab, certificateSerial, reason, now);
        Merge([entry]);
        return entry;
    }

    /// <summary>
    /// Merges revocations offered by an agent or read from a backup, keeping only what the
    /// lab key actually signed. Returns the entries that were new to this console.
    /// </summary>
    public IReadOnlyList<RevocationEntry> Merge(IEnumerable<RevocationEntry> entries)
    {
        var added = Revocations.Merge(_authority, entries);
        if (added.Count == 0)
        {
            return added;
        }

        lock (_gate)
        {
            foreach (var entry in added)
            {
                Document.Revocations.Add(RevocationRecord.From(entry));
            }
        }

        Changed?.Invoke();
        return added;
    }

    // ------------------------------------------------------------------ layout

    /// <summary>
    /// Where every tile sits. A console that has never been arranged by hand — a second
    /// teacher machine, or a fresh import — still looks right, because the default layout
    /// is derived from the PC numbers (ARCHITECTURE §3.7).
    /// </summary>
    public IReadOnlyList<LayoutTile> EffectiveLayout()
    {
        lock (_gate)
        {
            var placed = Document.Layout.ToDictionary(tile => tile.Number);
            var tiles = new List<LayoutTile>(Document.Machines.Count);
            var occupied = new HashSet<(int Column, int Row)>();
            var unplaced = new List<MachineRecord>();

            // Hand-placed tiles first, by number, so that two records claiming one cell — a
            // layout saved by an older build could do that — resolve the same way every time:
            // the lower number keeps the cell, the other is placed like a new PC.
            foreach (var machine in Document.Machines.OrderBy(m => m.Number))
            {
                if (placed.TryGetValue(machine.Number, out var tile) && occupied.Add((tile.Column, tile.Row)))
                {
                    tiles.Add(tile);
                }
                else
                {
                    unplaced.Add(machine);
                }
            }

            // New PCs take the first free cell in reading order, never one that is taken.
            var index = 0;
            foreach (var machine in unplaced)
            {
                (int Column, int Row) cell;
                do
                {
                    cell = (index % Defaults.DefaultTilesPerRow, index / Defaults.DefaultTilesPerRow);
                    index++;
                }
                while (!occupied.Add(cell));

                tiles.Add(new LayoutTile { Number = machine.Number, Column = cell.Column, Row = cell.Row });
            }

            return tiles.OrderBy(t => t.Number).ToList();
        }
    }

    /// <summary>Stores a hand-arranged layout. It travels only in a backup, by design (§3.7).</summary>
    public void SetLayout(IEnumerable<LayoutTile> tiles)
    {
        lock (_gate)
        {
            Document.Layout = tiles.ToList();
        }

        Changed?.Invoke();
    }
}
