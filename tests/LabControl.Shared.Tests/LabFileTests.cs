using Xunit;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabControl.Shared.Identity;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Tests;

/// <summary>
/// The files exchanged offline (M5, D-56): the signed envelope of each kind, what a lab
/// file must never carry, the merge that cannot go backwards, the request/grant round trip,
/// the role in the subject OU and the <c>instance:</c> pseudo-serial that outlives a renewal.
/// </summary>
public sealed class LabFileTests
{
    // Leaves are clamped inside the CA's window, and the test CA is minted at the real clock.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static LabDocument SampleLab(LabKey lab, int pcs = 3) => new()
    {
        LabId = lab.LabId,
        LabName = lab.LabName,
        Machines = Enumerable.Range(1, pcs).Select(n => new MachineRecord
        {
            AgentId = $"agent-{n}", Number = n, Hostname = $"PC-{n:00}", Mac = $"02:00:5E:00:00:{n:X2}", LastIp = $"10.0.0.{n}", CertificateSerial = $"AA{n:X2}", CertificateNotAfterUnix = Now.AddYears(4).ToUnixTimeSeconds(),
        }).ToList(),
        Layout = [new LayoutTile { Number = 1, Column = 0, Row = 0 }, new LayoutTile { Number = 2, Column = 1, Row = 0 }],
        Instances = [new InstanceRecord { InstanceId = "admin-1", Name = "MacBook", IsThisMachine = true }],
    };

    private static LabFilePayload Snapshot(LabKey lab, LabDocument document, long version, ScriptsDocument? scripts = null) =>
        LabFile.Snapshot(lab, document, scripts, "admin-1", "MacBook", version, Now);

    // ------------------------------------------------------------------ envelopes

    [Fact]
    public void A_lab_file_round_trips_and_a_tampered_or_newer_one_is_refused()
    {
        using var lab = TestLab.Create();
        var document = LabFile.Export(lab, Snapshot(lab, SampleLab(lab), 5));
        var json = LabFile.Serialize(document);

        var parsed = LabFile.Parse(json, "room.lclab");
        var opened = LabFile.Open(parsed, "room.lclab");
        Assert.Equal(lab.LabId, opened.LabId);
        Assert.Equal(3, opened.Roster.Count);
        Assert.Equal(5, opened.SnapshotVersion);
        Assert.Equal(ProfileRecord.AuthorityFingerprintOf(lab.Document.Authority), opened.AuthorityFingerprint);

        // Re-import against the pinned CA passes; against another lab's CA it is "same id, different key".
        var pinned = LabTrustTests.PublicOnly(lab.Authority);
        LabFile.Open(parsed, "room.lclab", pinned);
        using var other = TestLab.Create();
        var refused = Assert.Throws<InvalidDataException>(() => LabFile.Open(parsed, "room.lclab", LabTrustTests.PublicOnly(other.Authority)));
        Assert.Contains("different key", refused.Message, StringComparison.Ordinal);

        // A flipped payload byte breaks the signature; a flipped signature too.
        var tampered = JsonNode.Parse(json)!.AsObject();
        var payload = Convert.FromBase64String(tampered["payload"]!.GetValue<string>());
        payload[^1] ^= 0x01;
        tampered["payload"] = Convert.ToBase64String(payload);
        Assert.Throws<InvalidDataException>(() => LabFile.Open(LabFile.Parse(tampered.ToJsonString(), "t.lclab"), "t.lclab"));

        var forged = JsonNode.Parse(json)!.AsObject();
        forged["signature"] = Convert.ToBase64String(new byte[64]);
        Assert.Throws<InvalidDataException>(() => LabFile.Open(LabFile.Parse(forged.ToJsonString(), "f.lclab"), "f.lclab"));

        // Signed by another lab's key, carrying our CA: refused as not signed by that CA.
        var crossSigned = LabFile.Export(other, Snapshot(lab, SampleLab(lab), 5));
        Assert.Throws<InvalidDataException>(() => LabFile.Open(crossSigned, "x.lclab"));

        var newer = JsonNode.Parse(json)!.AsObject();
        newer["schema_version"] = Defaults.LabFileSchemaVersion + 1;
        Assert.Throws<SchemaVersionException>(() => LabFile.Parse(newer.ToJsonString(), "n.lclab"));

        // The wrong kind under the right extension is refused by name.
        var wrongKind = JsonNode.Parse(json)!.AsObject();
        wrongKind["kind"] = DeviceGrantDocument.KindValue;
        var kind = Assert.Throws<InvalidDataException>(() => LabFile.Parse(wrongKind.ToJsonString(), "k.lclab"));
        Assert.Contains("device grant", kind.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_and_a_grant_round_trip_and_tampering_or_a_newer_schema_is_refused()
    {
        using var lab = TestLab.Create();
        var access = NewAccess(lab);
        var request = DeviceAuthorization.CreateRequest(access, new FileSecretProtector(), lab.LabName, "1.0.0", Now);
        var requestJson = DeviceAuthorization.SerializeRequest(request);

        var parsedRequest = DeviceAuthorization.ParseRequest(requestJson, "r.lcreq");
        var requestPayload = DeviceAuthorization.OpenRequest(parsedRequest, "r.lcreq", out var deviceKey);
        deviceKey.Dispose();
        Assert.Equal(access.InstanceId, requestPayload.InstanceId);
        Assert.Equal(DeviceRequestPayload.TeacherAccess, requestPayload.RequestedAccess);

        var tamperedRequest = JsonNode.Parse(requestJson)!.AsObject();
        var bytes = Convert.FromBase64String(tamperedRequest["payload"]!.GetValue<string>());
        var text = System.Text.Encoding.UTF8.GetString(bytes).Replace("\"teacher\"", "\"admin__\"", StringComparison.Ordinal);
        tamperedRequest["payload"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        Assert.Throws<InvalidDataException>(() => DeviceAuthorization.OpenRequest(DeviceAuthorization.ParseRequest(tamperedRequest.ToJsonString(), "t.lcreq"), "t.lcreq", out _));

        var newerRequest = JsonNode.Parse(requestJson)!.AsObject();
        newerRequest["schema_version"] = Defaults.DeviceRequestSchemaVersion + 1;
        Assert.Throws<SchemaVersionException>(() => DeviceAuthorization.ParseRequest(newerRequest.ToJsonString(), "n.lcreq"));

        var registry = new LabRegistry(SampleLab(lab), lab.Authority);
        var granted = DeviceAuthorization.Approve(lab, parsedRequest, "r.lcreq", registry.Document, registry.Revocations, Snapshot(lab, registry.Document, 7), Now);
        var grantJson = DeviceAuthorization.SerializeGrant(granted.Grant);
        var pinned = LabTrustTests.PublicOnly(lab.Authority);

        var grantPayload = DeviceAuthorization.OpenGrant(DeviceAuthorization.ParseGrant(grantJson, "g.lcgrant"), "g.lcgrant", pinned);
        Assert.Equal(access.InstanceId, grantPayload.InstanceId);
        Assert.Equal(7, grantPayload.Snapshot.SnapshotVersion);
        Assert.Equal(Now.Add(Defaults.TeacherCertificateLifetime).ToUnixTimeSeconds(), grantPayload.ExpiresUnix);

        var tamperedGrant = JsonNode.Parse(grantJson)!.AsObject();
        var grantBytes = Convert.FromBase64String(tamperedGrant["payload"]!.GetValue<string>());
        grantBytes[10] ^= 0x01;
        tamperedGrant["payload"] = Convert.ToBase64String(grantBytes);
        Assert.Throws<InvalidDataException>(() => DeviceAuthorization.OpenGrant(DeviceAuthorization.ParseGrant(tamperedGrant.ToJsonString(), "t.lcgrant"), "t.lcgrant", pinned));

        using var other = TestLab.Create();
        Assert.Throws<InvalidDataException>(() => DeviceAuthorization.OpenGrant(DeviceAuthorization.ParseGrant(grantJson, "g.lcgrant"), "g.lcgrant", LabTrustTests.PublicOnly(other.Authority)));

        var newerGrant = JsonNode.Parse(grantJson)!.AsObject();
        newerGrant["schema_version"] = Defaults.DeviceGrantSchemaVersion + 1;
        Assert.Throws<SchemaVersionException>(() => DeviceAuthorization.ParseGrant(newerGrant.ToJsonString(), "n.lcgrant"));

        granted.Certificate.Dispose();
    }

    [Fact]
    public void A_signature_made_under_one_domain_is_refused_under_every_other()
    {
        using var lab = TestLab.Create();
        var pinned = LabTrustTests.PublicOnly(lab.Authority);

        // A genuine lab-file payload — its own CA inside, so LoadAuthority passes — signed by
        // the lab key under the grant domain and under the request domain: both refused as
        // "not signed by the key of the lab", never accepted as a lab file.
        var payload = Snapshot(lab, SampleLab(lab), 5);
        var bytes = SignedEnvelope.PayloadBytes(payload);
        foreach (var signature in new[] { SignedEnvelope.Sign(SignedEnvelope.DeviceGrantDomain, lab, bytes), SignedEnvelope.Sign(SignedEnvelope.DeviceRequestDomain, lab, bytes) })
        {
            var document = new LabFileDocument { LabId = lab.LabId, LabName = lab.LabName, Payload = Convert.ToBase64String(bytes), Signature = signature };
            var refused = Assert.Throws<InvalidDataException>(() => LabFile.Open(document, "x.lclab"));
            Assert.Contains("not signed by the key of lab", refused.Message, StringComparison.Ordinal);
            Assert.Throws<InvalidDataException>(() => LabFile.Open(document, "x.lclab", pinned));
        }

        // The right domain, the same bytes: accepted — the domain is the only difference.
        LabFile.Open(new LabFileDocument { LabId = lab.LabId, LabName = lab.LabName, Payload = Convert.ToBase64String(bytes), Signature = SignedEnvelope.Sign(SignedEnvelope.LabFileDomain, lab, bytes) }, "ok.lclab");

        // A grant payload signed under the lab-file domain is not a grant.
        var grantPayload = new DeviceGrantPayload { LabId = lab.LabId, InstanceId = Guid.NewGuid().ToString("d"), Snapshot = payload };
        var grantBytes = SignedEnvelope.PayloadBytes(grantPayload);
        var grant = new DeviceGrantDocument { LabId = lab.LabId, LabName = lab.LabName, Payload = Convert.ToBase64String(grantBytes), Signature = SignedEnvelope.Sign(SignedEnvelope.LabFileDomain, lab, grantBytes) };
        Assert.Throws<InvalidDataException>(() => DeviceAuthorization.OpenGrant(grant, "x.lcgrant", pinned));

        // A request payload signed by its own key under the lab-file domain is not a request.
        var access = NewAccess(lab);
        var protector = new FileSecretProtector();
        DeviceAuthorization.CreateRequest(access, protector, lab.LabName, "1.0.0", Now);
        Assert.True(SecretProtector.For(access.PendingKey!).TryUnprotect(access.PendingKey!, out var pkcs8));
        using var deviceKey = LabCertificates.CreateKey();
        deviceKey.ImportPkcs8PrivateKey(pkcs8, out _);
        var requestPayload = new DeviceRequestPayload { LabId = lab.LabId, InstanceId = access.InstanceId, InstanceName = access.InstanceName, Csr = LabCertificates.CreateDeviceSigningRequest(deviceKey, access.InstanceName), CreatedAtUnix = Now.ToUnixTimeSeconds() };
        var requestBytes = SignedEnvelope.PayloadBytes(requestPayload);
        var wrong = new DeviceRequestDocument { LabId = lab.LabId, LabName = lab.LabName, Payload = Convert.ToBase64String(requestBytes), Signature = SignedEnvelope.Sign(SignedEnvelope.LabFileDomain, deviceKey, requestBytes) };
        var notRequest = Assert.Throws<InvalidDataException>(() => DeviceAuthorization.OpenRequest(wrong, "x.lcreq", out _));
        Assert.Contains("not signed by the key inside it", notRequest.Message, StringComparison.Ordinal);
        var right = new DeviceRequestDocument { LabId = lab.LabId, LabName = lab.LabName, Payload = Convert.ToBase64String(requestBytes), Signature = SignedEnvelope.Sign(SignedEnvelope.DeviceRequestDomain, deviceKey, requestBytes) };
        DeviceAuthorization.OpenRequest(right, "ok.lcreq", out var opened);
        opened.Dispose();
    }

    // ------------------------------------------------------------------ must-not-contain

    [Fact]
    public void A_serialised_lab_file_carries_no_key_no_wrapping_no_code_and_no_instance()
    {
        using var lab = TestLab.Create(out var recovery);
        var document = SampleLab(lab);
        var scripts = new ScriptsDocument { LabId = lab.LabId, Scripts = [new ScriptRecord { Id = "s1", Name = "Say hello", Text = "Write-Host hello" }] };
        var json = LabFile.Serialize(LabFile.Export(lab, Snapshot(lab, document, 1, scripts)));

        // The payload is base64; decode it so the assertions see the real text.
        var envelope = JsonNode.Parse(json)!.AsObject();
        var payloadText = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(envelope["payload"]!.GetValue<string>()));
        var everything = json + payloadText;

        var keyJson = JsonStore.Serialize(lab.Document, LabKeyDocument.Migrations);
        var keyNode = JsonNode.Parse(keyJson)!.AsObject();
        Assert.DoesNotContain("authority_private_key", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("wrappings", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("master", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestLab.HolderName, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(recovery.ToString(), everything, StringComparison.Ordinal);
        foreach (var wrapping in keyNode["wrappings"]!.AsArray())
        {
            var sealedSecret = wrapping!["secret"]!.AsObject();
            foreach (var property in sealedSecret)
            {
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var s) && s.Length > 8)
                {
                    Assert.DoesNotContain(s, everything, StringComparison.Ordinal);
                }
            }
        }

        Assert.DoesNotContain("enrollment", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("codes", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("private_key", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("endorsement", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("instance-", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("packages", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("logs", everything, StringComparison.Ordinal);

        // What it does carry: the public CA, the roster, the layout, the script.
        Assert.Contains("\"authority\"", payloadText, StringComparison.Ordinal);
        Assert.Contains("02:00:5E:00:00:01", payloadText, StringComparison.Ordinal);
        Assert.Contains("Say hello", payloadText, StringComparison.Ordinal);
        Assert.Contains("\"layout\"", payloadText, StringComparison.Ordinal);

        // The CA private key itself, in any encoding, is absent: the DER inside is a certificate, not a key.
        var opened = LabFile.Open(LabFile.Parse(json, "a.lclab"), "a.lclab");
        using var authority = X509CertificateLoader.LoadCertificate(opened.Authority);
        Assert.False(authority.HasPrivateKey);
    }

    // ------------------------------------------------------------------ merge

    [Fact]
    public void The_merge_adds_and_updates_pcs_but_never_deletes_one_this_console_has_seen()
    {
        using var lab = TestLab.Create();
        var authority = LabTrustTests.PublicOnly(lab.Authority);

        // The administrator's snapshot: PCs 1..3. The teacher's local lab: PC-02 seen linked here, PC-05 seen here, PC-09 never seen.
        var snapshot = Snapshot(lab, SampleLab(lab), 10);
        var local = new LabDocument
        {
            LabId = lab.LabId,
            LabName = lab.LabName,
            Machines =
            [
                new MachineRecord { AgentId = "agent-2", Number = 2, Hostname = "old-name", LastSeenUnix = Now.ToUnixTimeSeconds(), LastIp = "10.0.0.99", CertificateSerial = "BEEF" },
                new MachineRecord { AgentId = "agent-5", Number = 5, LastSeenUnix = Now.ToUnixTimeSeconds() },
                new MachineRecord { AgentId = "agent-9", Number = 9 },
            ],
        };

        var report = LabFile.Merge(local, snapshot, authority, new RevocationSet(), Now);
        Assert.Equal(2, report.MachinesAdded);
        Assert.Equal(1, report.MachinesUpdated);
        Assert.True(report.LayoutReplaced);
        Assert.False(report.OlderSnapshot);
        Assert.Equal([1, 2, 3, 5, 9], local.Machines.Select(m => m.Number));
        Assert.Equal(10, local.ImportedSnapshotVersion);
        Assert.Equal(2, local.Layout.Count);

        // A PC seen linked here keeps what this console observed — name, serial, address —
        // and only gains what it lacked (the MAC).
        var two = local.Machines.Single(m => m.Number == 2);
        Assert.Equal("old-name", two.Hostname);
        Assert.Equal("BEEF", two.CertificateSerial);
        Assert.Equal("10.0.0.99", two.LastIp);
        Assert.Equal("02:00:5E:00:00:02", two.Mac);

        // A snapshot that says number 5 now belongs to another agent cannot take PC-05 from a
        // console that has itself seen agent-5 linked; one it never saw is replaced.
        var reassigned = Snapshot(lab, new LabDocument
        {
            LabId = lab.LabId,
            Machines = [new MachineRecord { AgentId = "agent-5b", Number = 5 }, new MachineRecord { AgentId = "agent-9b", Number = 9 }],
        }, 11);
        var second = LabFile.Merge(local, reassigned, authority, new RevocationSet(), Now);
        Assert.Equal(1, second.MachinesKept);
        Assert.Equal("agent-5", local.Machines.Single(m => m.Number == 5).AgentId);
        Assert.Equal("agent-9b", local.Machines.Single(m => m.Number == 9).AgentId);
        Assert.Equal(5, local.Machines.Count);
    }

    [Fact]
    public void Revocations_are_unioned_and_an_older_snapshot_rolls_back_neither_layout_nor_revocations()
    {
        using var lab = TestLab.Create();
        var authority = LabTrustTests.PublicOnly(lab.Authority);
        var registry = new LabRegistry(new LabDocument { LabId = lab.LabId, LabName = lab.LabName }, authority);

        var adminLab = SampleLab(lab);
        adminLab.Revocations.Add(RevocationRecord.From(RevocationSet.Create(lab, "0AAA", "stolen", Now)));
        var newer = Snapshot(lab, adminLab, 20);

        var older = Snapshot(lab, new LabDocument { LabId = lab.LabId, Layout = [new LayoutTile { Number = 1, Column = 9, Row = 9 }] }, 3);
        older.Revocations.Add(RevocationRecord.From(RevocationSet.Create(lab, "0BBB", "lost", Now)));

        var first = LabFile.Merge(registry.Document, newer, authority, registry.Revocations, Now);
        Assert.Equal(1, first.RevocationsAdded);
        Assert.True(registry.Revocations.IsRevoked("AAA"));
        Assert.Equal(20, registry.Document.ImportedSnapshotVersion);

        // Locally learned revocation, never in any file: must survive every merge.
        registry.Revoke(lab, "0CCC", "local", Now);

        var second = LabFile.Merge(registry.Document, older, authority, registry.Revocations, Now);
        Assert.True(second.OlderSnapshot);
        Assert.False(second.LayoutReplaced);
        Assert.Equal(1, second.RevocationsAdded);
        Assert.Equal(20, registry.Document.ImportedSnapshotVersion);
        Assert.Equal(0, registry.Document.Layout.Single(t => t.Number == 1).Column);
        Assert.True(registry.Revocations.IsRevoked("AAA"));
        Assert.True(registry.Revocations.IsRevoked("BBB"));
        Assert.True(registry.Revocations.IsRevoked("CCC"));
        Assert.Equal(3, registry.Document.Revocations.Count);

        // Re-importing the very same newest file changes nothing more.
        var third = LabFile.Merge(registry.Document, newer, authority, registry.Revocations, Now);
        Assert.True(third.OlderSnapshot);
        Assert.Equal(0, third.RevocationsAdded);

        // A forged revocation in a snapshot is dropped, not stored.
        using var other = TestLab.Create();
        var forged = Snapshot(lab, new LabDocument { LabId = lab.LabId }, 30);
        forged.Revocations.Add(RevocationRecord.From(RevocationSet.Create(other, "0DDD", "forged", Now)));
        LabFile.Merge(registry.Document, forged, authority, registry.Revocations, Now);
        Assert.False(registry.Revocations.IsRevoked("DDD"));
        Assert.Equal(3, registry.Document.Revocations.Count);
    }

    [Fact]
    public void The_snapshot_counter_is_monotonic_per_console()
    {
        Assert.Equal(Now.ToUnixTimeSeconds(), LabFile.NextSnapshotVersion(0, Now));
        Assert.Equal(Now.ToUnixTimeSeconds() + 1, LabFile.NextSnapshotVersion(Now.ToUnixTimeSeconds(), Now));
        Assert.Equal(Now.ToUnixTimeSeconds() + 100, LabFile.NextSnapshotVersion(Now.ToUnixTimeSeconds() + 99, Now.AddDays(-1)));
    }

    // ------------------------------------------------------------------ request / grant

    [Fact]
    public void The_request_grant_round_trip_yields_a_teacher_instance_and_a_rerun_carries_the_same_key()
    {
        using var lab = TestLab.Create();
        var protector = new FileSecretProtector();
        var access = NewAccess(lab);

        var request = DeviceAuthorization.CreateRequest(access, protector, lab.LabName, "1.0.0", Now);
        Assert.Equal(AccessState.RequestPending, access.State);
        Assert.NotNull(access.PendingKey);
        Assert.Equal(AccessDocument.PendingKeyReference(access.InstanceId), access.PendingKey!.Reference);
        var fingerprint = access.CsrFingerprint;

        // A re-run: the same key, the same CSR fingerprint, the same created_at — a lost file costs nothing.
        var again = DeviceAuthorization.CreateRequest(access, protector, lab.LabName, "1.0.0", Now.AddHours(2));
        Assert.Equal(fingerprint, access.CsrFingerprint);
        DeviceAuthorization.OpenRequest(request, "1.lcreq", out var key1);
        DeviceAuthorization.OpenRequest(again, "2.lcreq", out var key2);
        using (key1)
        using (key2)
        {
            Assert.Equal(key1.ExportSubjectPublicKeyInfo(), key2.ExportSubjectPublicKeyInfo());
        }

        // The request holds only the public half.
        var requestJson = DeviceAuthorization.SerializeRequest(request);
        Assert.DoesNotContain("private", requestJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pending_key", requestJson, StringComparison.Ordinal);

        var registry = new LabRegistry(SampleLab(lab), lab.Authority);
        var granted = DeviceAuthorization.Approve(lab, request, "1.lcreq", registry.Document, registry.Revocations, Snapshot(lab, registry.Document, 4), Now);
        Assert.False(granted.IsRenewal);
        Assert.Equal(ConsoleAccess.Teacher, LabName.AccessOf(granted.Certificate));
        Assert.Contains(Defaults.TeacherOrganizationalUnit, granted.Certificate.Subject, StringComparison.Ordinal);
        Assert.Equal(Now.Add(Defaults.TeacherCertificateLifetime).ToUnixTimeSeconds(), new DateTimeOffset(granted.Certificate.NotAfter.ToUniversalTime()).ToUnixTimeSeconds());

        var pinned = LabTrustTests.PublicOnly(lab.Authority);
        var imported = DeviceAuthorization.ImportGrant(granted.Grant, "1.lcgrant", access, pinned, registry.Revocations, protector, null, Now);
        Assert.Equal(AccessState.Authorized, access.State);
        Assert.Null(access.PendingKey);
        Assert.Equal(access.InstanceId, imported.Instance.InstanceId);
        Assert.Equal(ConsoleInstance.ProtectionReference(access.InstanceId), imported.Instance.PrivateKey.Reference);
        Assert.Equal(4, imported.Snapshot.SnapshotVersion);

        // The instance opens, beacons verify against the CA, and the leaf validates as a console of this lab with teacher access.
        using var instance = ConsoleInstance.Open(imported.Instance);
        var trust = LabTrust.FromAuthority(pinned);
        Assert.True(trust.TryValidate(instance.Certificate, LabRole.Console, registry.Revocations, out var name, out _, Now));
        Assert.Equal(ConsoleAccess.Teacher, name.Access);
        Assert.Equal(access.InstanceId, name.Id);
        var beacon = instance.CreateBeacon("127.0.0.1", 47800, Now);
        Assert.True(Discovery.Beacon.TryParse(beacon.ToDatagram(), out var parsedBeacon));
        Assert.True(parsedBeacon.TryVerify(pinned, lab.LabId, Now, out var beaconFailure), beaconFailure.ToString());

        // The same grant twice: the pending key is gone, so it is refused, and the profile is unchanged.
        Assert.Throws<InvalidDataException>(() => DeviceAuthorization.ImportGrant(granted.Grant, "1.lcgrant", access, pinned, registry.Revocations, protector, imported.Instance, Now));
        Assert.Equal(AccessState.Authorized, access.State);

        // A renewal: a fresh key under the same id; the administrator sees a renewal; the device gets a new leaf.
        registry.RecordInstance(access.InstanceId, "Teacher laptop", LabCertificates.SerialOf(granted.Certificate), Now, isThisMachine: false).Access = ProfileAccess.Teacher;
        registry.Document.Instances.Single(i => i.InstanceId == access.InstanceId).AuthorizedAtUnix = Now.ToUnixTimeSeconds();
        var renewal = DeviceAuthorization.CreateRequest(access, protector, lab.LabName, "1.0.0", Now.AddDays(300));
        Assert.Equal(AccessState.Authorized, access.State);
        var renewed = DeviceAuthorization.Approve(lab, renewal, "r.lcreq", registry.Document, registry.Revocations, Snapshot(lab, registry.Document, 5), Now.AddDays(300));
        Assert.True(renewed.IsRenewal);
        var reimported = DeviceAuthorization.ImportGrant(renewed.Grant, "r.lcgrant", access, pinned, registry.Revocations, protector, imported.Instance, Now.AddDays(300));
        Assert.True(reimported.IsRenewal);
        Assert.Equal(access.InstanceId, reimported.Instance.InstanceId);
        Assert.NotEqual(imported.Instance.Certificate, reimported.Instance.Certificate);
        using var reopened = ConsoleInstance.Open(reimported.Instance);
        Assert.NotEqual(instance.CertificateSerial, reopened.CertificateSerial);

        // A grant for a different device, or one certifying another key, is refused.
        var stranger = NewAccess(lab);
        DeviceAuthorization.CreateRequest(stranger, protector, lab.LabName, "1.0.0", Now);
        Assert.Throws<InvalidDataException>(() => DeviceAuthorization.ImportGrant(renewed.Grant, "r.lcgrant", stranger, pinned, registry.Revocations, protector, null, Now));
        granted.Certificate.Dispose();
        renewed.Certificate.Dispose();
    }

    [Fact]
    public void A_request_from_another_lab_or_a_withdrawn_device_is_refused_and_an_administrator_request_cannot_be_forged()
    {
        using var lab = TestLab.Create();
        using var other = TestLab.Create();
        var protector = new FileSecretProtector();
        var registry = new LabRegistry(SampleLab(lab), lab.Authority);
        var snapshot = Snapshot(lab, registry.Document, 1);

        var foreign = DeviceAuthorization.CreateRequest(NewAccess(other), protector, other.LabName, "1.0.0", Now);
        Assert.Throws<InvalidDataException>(() => DeviceAuthorization.Approve(lab, foreign, "f.lcreq", registry.Document, registry.Revocations, snapshot, Now));

        var withdrawn = NewAccess(lab);
        registry.Revoke(lab, LabCertificates.InstanceSerial(withdrawn.InstanceId), "left the school", Now);
        var request = DeviceAuthorization.CreateRequest(withdrawn, protector, lab.LabName, "1.0.0", Now);
        var refused = Assert.Throws<InvalidDataException>(() => DeviceAuthorization.Approve(lab, request, "w.lcreq", registry.Document, registry.Revocations, snapshot, Now));
        Assert.Contains("withdrawn", refused.Message, StringComparison.Ordinal);

        // The issued leaf is always a teacher leaf, whatever the CSR's subject said.
        var access = NewAccess(lab);
        using var key = LabCertificates.CreateKey();
        var csr = new CertificateRequest($"CN=Admin, OU={Defaults.ConsoleOrganizationalUnit}, O=LabControl", key, HashAlgorithmName.SHA256).CreateSigningRequest();
        using var issued = LabCertificates.IssueTeacherDevice(lab.Authority, lab.LabId, access.InstanceId, "Laptop", csr, Now);
        Assert.Equal(ConsoleAccess.Teacher, LabName.AccessOf(issued));
        Assert.True(LabName.TryFromCertificate(issued, out var name));
        Assert.Equal(LabRole.Console, name.Role);
        Assert.Equal(access.InstanceId, name.Id);
    }

    // ------------------------------------------------------------------ role and pseudo-serial

    [Fact]
    public void A_teacher_leaf_parses_as_a_console_with_teacher_access_and_an_administrator_leaf_as_before()
    {
        using var lab = TestLab.Create();
        using var admin = ConsoleInstance.Mint(lab, "MacBook", new FileSecretProtector(), Now);
        var trust = LabTrust.FromAuthority(LabTrustTests.PublicOnly(lab.Authority));

        Assert.True(trust.TryValidate(admin.Certificate, LabRole.Console, null, out var adminName, out _, Now));
        Assert.Equal(ConsoleAccess.Administrator, adminName.Access);
        Assert.Contains(Defaults.ConsoleOrganizationalUnit, admin.Certificate.Subject, StringComparison.Ordinal);

        using var key = LabCertificates.CreateKey();
        var instanceId = Guid.NewGuid().ToString("d");
        using var teacher = LabCertificates.IssueTeacherDevice(lab.Authority, lab.LabId, instanceId, "Laptop", LabCertificates.CreateDeviceSigningRequest(key, "Laptop"), Now);
        Assert.True(trust.TryValidate(teacher, LabRole.Console, null, out var teacherName, out _, Now));
        Assert.Equal(LabRole.Console, teacherName.Role);
        Assert.Equal(ConsoleAccess.Teacher, teacherName.Access);
        Assert.Equal(instanceId, teacherName.Id);

        // The SAN is the very same shape, so an agent that knows nothing of the OU parses it as a console.
        Assert.True(LabName.TryParse(LabName.ForConsole(lab.LabId, instanceId).ToUri().ToString(), out var plain));
        Assert.Equal(ConsoleAccess.Unknown, plain.Access);
        Assert.Equal(teacherName with { Access = ConsoleAccess.Unknown }, plain);

        // Agents' certificates carry no console access.
        using var agentCertificate = LabTrustTests.IssueAgent(lab, Guid.NewGuid().ToString("d"), 3);
        Assert.True(LabName.TryFromCertificate(agentCertificate, out var agentName));
        Assert.Equal(ConsoleAccess.Unknown, agentName.Access);
    }

    [Fact]
    public void An_instance_revocation_refuses_a_leaf_renewed_after_the_withdrawal_and_is_signed_like_any_serial()
    {
        using var lab = TestLab.Create();
        var pinned = LabTrustTests.PublicOnly(lab.Authority);
        var trust = LabTrust.FromAuthority(pinned);
        var instanceId = Guid.NewGuid().ToString("d");

        using var key1 = LabCertificates.CreateKey();
        using var first = LabCertificates.IssueTeacherDevice(lab.Authority, lab.LabId, instanceId, "Laptop", LabCertificates.CreateDeviceSigningRequest(key1, "Laptop"), Now);
        var revocations = new RevocationSet();
        Assert.True(trust.TryValidate(first, LabRole.Console, revocations, out _, out _, Now));

        var entry = RevocationSet.Create(lab, Defaults.InstanceRevocationPrefix + instanceId.ToUpperInvariant(), "left the school", Now);
        Assert.Equal(LabCertificates.InstanceSerial(instanceId), entry.Serial);
        Assert.StartsWith(Defaults.InstanceRevocationPrefix, entry.Serial, StringComparison.Ordinal);
        Assert.True(RevocationSet.Verify(pinned, entry));
        Assert.True(revocations.TryAdd(pinned, entry));

        // The wire and disk forms are the ordinary ones: an older agent stores it inertly.
        var record = RevocationRecord.From(entry);
        Assert.True(revocations.TryAdd(pinned, record.ToEntry()) == false);
        Assert.Equal(entry.Serial, record.Serial);

        Assert.False(trust.TryValidate(first, LabRole.Console, revocations, out _, out var failure, Now));
        Assert.Equal(TrustFailure.Revoked, failure);

        // A leaf minted after the withdrawal under the same instance id is refused too.
        using var key2 = LabCertificates.CreateKey();
        using var renewed = LabCertificates.IssueTeacherDevice(lab.Authority, lab.LabId, instanceId, "Laptop", LabCertificates.CreateDeviceSigningRequest(key2, "Laptop"), Now.AddDays(1));
        Assert.NotEqual(LabCertificates.SerialOf(first), LabCertificates.SerialOf(renewed));
        Assert.False(trust.TryValidate(renewed, LabRole.Console, revocations, out _, out failure, Now.AddDays(1)));
        Assert.Equal(TrustFailure.Revoked, failure);

        // Another device, and an agent, are untouched; a forged instance entry is dropped.
        using var key3 = LabCertificates.CreateKey();
        using var otherDevice = LabCertificates.IssueTeacherDevice(lab.Authority, lab.LabId, Guid.NewGuid().ToString("d"), "Other", LabCertificates.CreateDeviceSigningRequest(key3, "Other"), Now);
        Assert.True(trust.TryValidate(otherDevice, LabRole.Console, revocations, out _, out _, Now));
        using var other = TestLab.Create();
        Assert.False(revocations.TryAdd(pinned, RevocationSet.Create(other, Defaults.InstanceRevocationPrefix + "x", "forged", Now)));

        // The registry signs it through Revoke like any serial and persists the record.
        var registry = new LabRegistry(new LabDocument { LabId = lab.LabId }, pinned);
        registry.Revoke(lab, LabCertificates.InstanceSerial(instanceId), "again", Now);
        Assert.Single(registry.Document.Revocations);
        Assert.Equal(LabCertificates.InstanceSerial(instanceId), registry.Document.Revocations[0].Serial);
    }

    // ------------------------------------------------------------------ review findings (portion 3)

    [Fact]
    public void A_request_naming_this_machine_an_administrator_or_an_unknown_console_is_refused_and_a_teacher_renewal_still_works()
    {
        using var lab = TestLab.Create();
        var protector = new FileSecretProtector();
        var thisMachine = Guid.NewGuid().ToString("d");
        var otherAdmin = Guid.NewGuid().ToString("d");
        var seenOnly = Guid.NewGuid().ToString("d");
        var document = SampleLab(lab);
        document.Instances =
        [
            new InstanceRecord { InstanceId = thisMachine, Name = "MacBook", IsThisMachine = true, Access = ProfileAccess.Administrator, CertificateSerial = "A1" },
            new InstanceRecord { InstanceId = otherAdmin, Name = "Windows desk PC", Access = ProfileAccess.Administrator, CertificateSerial = "A2" },
            new InstanceRecord { InstanceId = seenOnly, Name = "Seen in a beacon" },
        ];
        var registry = new LabRegistry(document, lab.Authority);
        var snapshot = Snapshot(lab, document, 1);

        foreach (var (id, expected) in new[] { (thisMachine, "own device id"), (otherAdmin, "administrator"), (seenOnly, "unknown authority") })
        {
            var access = NewAccess(lab);
            access.InstanceId = id;
            var request = DeviceAuthorization.CreateRequest(access, protector, lab.LabName, "1.0.0", Now);
            var refused = Assert.Throws<InvalidDataException>(() => DeviceAuthorization.Approve(lab, request, "x.lcreq", registry.Document, registry.Revocations, snapshot, Now));
            Assert.Contains(expected, refused.Message, StringComparison.Ordinal);

            // The registry is the last line: even a caller that skipped Approve cannot turn the record into a teacher device.
            Assert.Throws<InvalidOperationException>(() => registry.RecordAuthorization(id, "Laptop", "B1", "fp", Now));
        }

        Assert.Equal(ProfileAccess.Administrator, document.Instances.Single(i => i.InstanceId == thisMachine).Access);
        Assert.True(document.Instances.Single(i => i.InstanceId == thisMachine).IsThisMachine);
        Assert.Equal(ProfileAccess.Administrator, document.Instances.Single(i => i.InstanceId == otherAdmin).Access);
        Assert.Equal(ProfileAccess.Unknown, document.Instances.Single(i => i.InstanceId == seenOnly).Access);

        // A Hello or a beacon naming an administrator later never lowers the record.
        registry.RecordInstance(otherAdmin, "Windows desk PC", "A3", Now, isThisMachine: false);
        Assert.Equal(ProfileAccess.Administrator, document.Instances.Single(i => i.InstanceId == otherAdmin).Access);
        Assert.Equal(["A2", "A3"], document.Instances.Single(i => i.InstanceId == otherAdmin).CertificateSerials);

        // A genuine teacher device: first approval, then a renewal with a fresh key under the same id.
        var teacher = NewAccess(lab);
        var first = DeviceAuthorization.CreateRequest(teacher, protector, lab.LabName, "1.0.0", Now);
        var granted = DeviceAuthorization.Approve(lab, first, "t.lcreq", registry.Document, registry.Revocations, snapshot, Now);
        var record = registry.RecordAuthorization(granted.InstanceId, granted.InstanceName, LabCertificates.SerialOf(granted.Certificate), granted.PublicKeyFingerprint, Now);
        Assert.False(granted.IsRenewal);
        Assert.Equal(ProfileAccess.Teacher, record.Access);
        Assert.Equal(granted.PublicKeyFingerprint, record.PublicKeyFingerprint);

        var pinned = LabTrustTests.PublicOnly(lab.Authority);
        var imported = DeviceAuthorization.ImportGrant(granted.Grant, "t.lcgrant", teacher, pinned, registry.Revocations, protector, null, Now);
        DeviceAuthorization.ForgetReplaced(imported);

        // The very same request again — same key — is refused: a renewal must present a fresh key.
        var replay = Assert.Throws<InvalidDataException>(() => DeviceAuthorization.Approve(lab, first, "t.lcreq", registry.Document, registry.Revocations, snapshot, Now.AddDays(1)));
        Assert.Contains("already certified", replay.Message, StringComparison.Ordinal);

        var renewal = DeviceAuthorization.CreateRequest(teacher, protector, lab.LabName, "1.0.0", Now.AddDays(300));
        var renewed = DeviceAuthorization.Approve(lab, renewal, "r.lcreq", registry.Document, registry.Revocations, snapshot, Now.AddDays(300));
        Assert.True(renewed.IsRenewal);
        Assert.NotEqual(granted.PublicKeyFingerprint, renewed.PublicKeyFingerprint);
        registry.RecordAuthorization(renewed.InstanceId, renewed.InstanceName, LabCertificates.SerialOf(renewed.Certificate), renewed.PublicKeyFingerprint, Now.AddDays(300));

        // Both leaves are in the record's history, so a withdrawal can revoke them all.
        Assert.Equal([LabCertificates.SerialOf(granted.Certificate), LabCertificates.SerialOf(renewed.Certificate)], record.CertificateSerials);
        Assert.Equal(LabCertificates.SerialOf(renewed.Certificate), record.CertificateSerial);
        granted.Certificate.Dispose();
        renewed.Certificate.Dispose();
    }

    [Fact]
    public void A_keystore_that_fails_while_importing_a_grant_leaves_the_device_able_to_re_request_under_the_same_id()
    {
        using var lab = TestLab.Create();
        var protector = new FileSecretProtector();
        var registry = new LabRegistry(SampleLab(lab), lab.Authority);
        var pinned = LabTrustTests.PublicOnly(lab.Authority);
        var access = NewAccess(lab);

        var request = DeviceAuthorization.CreateRequest(access, protector, lab.LabName, "1.0.0", Now);
        var pendingBefore = access.PendingKey!;
        var fingerprint = access.CsrFingerprint;
        var granted = DeviceAuthorization.Approve(lab, request, "1.lcreq", registry.Document, registry.Revocations, Snapshot(lab, registry.Document, 1), Now);

        var broken = new FailingProtector();
        Assert.Throws<InvalidOperationException>(() => DeviceAuthorization.ImportGrant(granted.Grant, "1.lcgrant", access, pinned, registry.Revocations, broken, null, Now));
        Assert.Equal(0, broken.Forgotten);

        // Nothing changed: the pending key is still there, the state still pending, the same request regenerates.
        Assert.Same(pendingBefore, access.PendingKey);
        Assert.Equal(AccessState.RequestPending, access.State);
        Assert.Equal(0, access.AuthorizedAtUnix);
        DeviceAuthorization.CreateRequest(access, protector, lab.LabName, "1.0.0", Now.AddHours(1));
        Assert.Equal(fingerprint, access.CsrFingerprint);

        // The same grant lands once the keystore works; only then is the pending item forgotten.
        var imported = DeviceAuthorization.ImportGrant(granted.Grant, "1.lcgrant", access, pinned, registry.Revocations, protector, null, Now);
        Assert.Equal(AccessState.Authorized, access.State);
        Assert.Null(access.PendingKey);
        Assert.Equal([pendingBefore], imported.Replaced);
        using var instance = ConsoleInstance.Open(imported.Instance);
        Assert.Equal(access.InstanceId, instance.InstanceId);

        // A renewal under the same keystore reference replaces the item; the previous key is not in the forget list.
        registry.RecordAuthorization(access.InstanceId, "Laptop", LabCertificates.SerialOf(granted.Certificate), granted.PublicKeyFingerprint, Now);
        var renewal = DeviceAuthorization.CreateRequest(access, protector, lab.LabName, "1.0.0", Now.AddDays(300));
        var renewed = DeviceAuthorization.Approve(lab, renewal, "r.lcreq", registry.Document, registry.Revocations, Snapshot(lab, registry.Document, 2), Now.AddDays(300));
        var reimported = DeviceAuthorization.ImportGrant(renewed.Grant, "r.lcgrant", access, pinned, registry.Revocations, protector, imported.Instance, Now.AddDays(300));
        var replaced = Assert.Single(reimported.Replaced);
        Assert.EndsWith("-pending", replaced.Reference, StringComparison.Ordinal);
        Assert.Equal(imported.Instance.PrivateKey.Reference, reimported.Instance.PrivateKey.Reference);
        granted.Certificate.Dispose();
        renewed.Certificate.Dispose();
    }

    [Fact]
    public void A_withdrawn_device_writes_its_next_request_under_a_fresh_id_whoever_asks()
    {
        using var lab = TestLab.Create();
        var protector = new FileSecretProtector();
        var access = NewAccess(lab);
        DeviceAuthorization.CreateRequest(access, protector, lab.LabName, "1.0.0", Now);
        var oldId = access.InstanceId;
        var oldKey = access.CsrFingerprint;

        access.State = AccessState.Revoked;
        access.RevokedAtUnix = Now.ToUnixTimeSeconds();
        var request = DeviceAuthorization.CreateRequest(access, protector, lab.LabName, "1.0.0", Now.AddDays(1));
        Assert.NotEqual(oldId, access.InstanceId);
        Assert.NotEqual(oldKey, access.CsrFingerprint);
        Assert.Equal(AccessState.RequestPending, access.State);
        Assert.Equal(0, access.RevokedAtUnix);
        var payload = DeviceAuthorization.OpenRequest(request, "n.lcreq", out var key);
        key.Dispose();
        Assert.Equal(access.InstanceId, payload.InstanceId);
        Assert.Equal(AccessDocument.PendingKeyReference(access.InstanceId), access.PendingKey!.Reference);

        // A lost grant: the device can discard its pending key on purpose and ask again with a fresh one.
        var pending = access.CsrFingerprint;
        DeviceAuthorization.CreateRequest(access, protector, lab.LabName, "1.0.0", Now.AddDays(2), freshKey: true);
        Assert.NotEqual(pending, access.CsrFingerprint);
        Assert.Equal(access.InstanceId, payload.InstanceId);
    }

    [Fact]
    public void An_older_snapshot_cannot_re_add_a_removed_pc_or_renumber_a_linked_one_and_a_newer_one_respects_both_too()
    {
        using var lab = TestLab.Create();
        var authority = LabTrustTests.PublicOnly(lab.Authority);
        var registry = new LabRegistry(new LabDocument { LabId = lab.LabId, LabName = lab.LabName }, authority);

        // This console: PC-02 seen linked, PC-03 removed after its certificate was revoked, snapshot 20 applied.
        registry.Document.Machines.Add(new MachineRecord { AgentId = "agent-2", Number = 2, Hostname = "seen-here", Mac = "02:00:5E:00:00:02", LastSeenUnix = Now.ToUnixTimeSeconds(), CertificateSerial = "AA02" });
        registry.Document.ImportedSnapshotVersion = 20;
        registry.Revoke(lab, "AA03", "PC-03 was removed", Now);

        // An older snapshot: renumbers PC-02 to 7 and re-lists PC-03, brings PC-04 and a new revocation.
        var older = Snapshot(lab, SampleLab(lab, 4), 3);
        older.Roster.Single(r => r.AgentId == "agent-2").Number = 7;
        older.Roster.Single(r => r.AgentId == "agent-2").Hostname = "renamed";
        older.Revocations.Add(RevocationRecord.From(RevocationSet.Create(lab, "0BBB", "lost", Now)));

        var report = LabFile.Merge(registry.Document, older, authority, registry.Revocations, Now);
        Assert.True(report.OlderSnapshot);
        Assert.Equal(0, report.MachinesAdded);
        Assert.Equal(0, report.MachinesUpdated);
        Assert.Equal(1, report.RevocationsAdded);
        Assert.False(report.LayoutReplaced);
        var only = Assert.Single(registry.Document.Machines);
        Assert.Equal(2, only.Number);
        Assert.Equal("seen-here", only.Hostname);
        Assert.Equal(20, registry.Document.ImportedSnapshotVersion);
        Assert.True(registry.Revocations.IsRevoked("BBB"));

        // A newer snapshot adds PC-01 and PC-04, still cannot renumber or rename the linked PC-02, and still cannot re-add PC-03.
        var newer = Snapshot(lab, SampleLab(lab, 4), 21);
        newer.Roster.Single(r => r.AgentId == "agent-2").Number = 7;
        newer.Roster.Single(r => r.AgentId == "agent-2").Hostname = "renamed";
        newer.Roster.Single(r => r.AgentId == "agent-2").Mac = "02:00:5E:00:00:99";
        report = LabFile.Merge(registry.Document, newer, authority, registry.Revocations, Now);
        Assert.False(report.OlderSnapshot);
        Assert.Equal(2, report.MachinesAdded);
        Assert.Equal(1, report.MachinesUpdated);
        Assert.Equal(1, report.MachinesKept);
        Assert.Equal([1, 2, 4], registry.Document.Machines.Select(m => m.Number));
        var two = registry.Document.Machines.Single(m => m.AgentId == "agent-2");
        Assert.Equal(2, two.Number);
        Assert.Equal("seen-here", two.Hostname);
        Assert.Equal("02:00:5E:00:00:02", two.Mac);
        Assert.Equal("10.0.0.2", two.LastIp);
        Assert.DoesNotContain(registry.Document.Machines, m => m.AgentId == "agent-3");
        Assert.Equal(21, registry.Document.ImportedSnapshotVersion);
    }

    [Fact]
    public void The_subject_ou_grants_access_only_when_it_is_the_one_exact_unit_and_never_throws()
    {
        using var lab = TestLab.Create();
        using var admin = ConsoleInstance.Mint(lab, "MacBook", new FileSecretProtector(), Now);
        Assert.Equal(ConsoleAccess.Administrator, LabName.AccessOf(admin.Certificate));

        using var key = LabCertificates.CreateKey();
        var instanceId = Guid.NewGuid().ToString("d");
        var trust = LabTrust.FromAuthority(LabTrustTests.PublicOnly(lab.Authority));

        foreach (var subject in new[]
                 {
                     $"CN=Laptop, OU={Defaults.ConsoleOrganizationalUnit}, OU=Other, O=LabControl",
                     $"CN=Laptop, OU=Other, OU={Defaults.ConsoleOrganizationalUnit}, O=LabControl",
                     "CN=Laptop, O=LabControl",
                     $"CN=Laptop, OU={Defaults.ConsoleOrganizationalUnit}\\ , O=LabControl",
                     $"CN=Laptop, OU=\"{Defaults.ConsoleOrganizationalUnit} \", O=LabControl",
                     $"CN=Laptop, OU={Defaults.ConsoleOrganizationalUnit}+CN=x, O=LabControl",
                     $"OU={Defaults.ConsoleOrganizationalUnit}+OU={Defaults.TeacherOrganizationalUnit}",
                 })
        {
            using var leaf = LeafWithSubject(lab, subject, instanceId, key);
            Assert.True(ConsoleAccess.Unknown == LabName.AccessOf(leaf), $"{subject} → {leaf.Subject}");

            // Still a console of this lab by its SAN — but with no authority to sign anything.
            Assert.True(trust.TryValidate(leaf, LabRole.Console, null, out var name, out var failure, Now), $"{subject}: {failure}");
            Assert.Equal(ConsoleAccess.Unknown, name.Access);
        }

        using var classic = LeafWithSubject(lab, $"CN=Laptop, OU={Defaults.ConsoleOrganizationalUnit}, O=LabControl", instanceId, key);
        Assert.Equal(ConsoleAccess.Administrator, LabName.AccessOf(classic));
        using var teacher = LeafWithSubject(lab, $"O=LabControl, OU={Defaults.TeacherOrganizationalUnit}, CN=Laptop", instanceId, key);
        Assert.Equal(ConsoleAccess.Teacher, LabName.AccessOf(teacher));
    }

    [Fact]
    public void A_request_with_an_empty_or_overlong_name_or_a_key_off_p256_is_refused_as_data()
    {
        using var lab = TestLab.Create();
        var labId = lab.LabId;

        static DeviceRequestDocument Envelope(string labId, DeviceRequestPayload payload, ECDsa key)
        {
            var bytes = SignedEnvelope.PayloadBytes(payload);
            return new DeviceRequestDocument { LabId = labId, LabName = "x", Payload = Convert.ToBase64String(bytes), Signature = SignedEnvelope.Sign(SignedEnvelope.DeviceRequestDomain, key, bytes) };
        }

        using var key = LabCertificates.CreateKey();
        foreach (var name in new[] { string.Empty, "   ", new string('n', Defaults.MaxInstanceNameLength + 1), "Lap\ntop" })
        {
            var payload = new DeviceRequestPayload { LabId = labId, InstanceId = Guid.NewGuid().ToString("d"), InstanceName = name, Csr = LabCertificates.CreateDeviceSigningRequest(key, "x"), CreatedAtUnix = Now.ToUnixTimeSeconds() };
            var refused = Assert.Throws<InvalidDataException>(() => DeviceAuthorization.OpenRequest(Envelope(labId, payload, key), "n.lcreq", out _));
            Assert.Contains("name", refused.Message, StringComparison.Ordinal);
        }

        // A P-384 key inside a well-formed CSR: refused before any signature is checked.
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var csr = new CertificateRequest("CN=Laptop", p384, HashAlgorithmName.SHA256).CreateSigningRequest();
        var wide = new DeviceRequestPayload { LabId = labId, InstanceId = Guid.NewGuid().ToString("d"), InstanceName = "Laptop", Csr = csr, CreatedAtUnix = Now.ToUnixTimeSeconds() };
        var curve = Assert.Throws<InvalidDataException>(() => DeviceAuthorization.OpenRequest(Envelope(labId, wide, key), "p384.lcreq", out _));
        Assert.Contains("P-256", curve.Message, StringComparison.Ordinal);

        // Garbage where the CSR should be, and an RSA key: data errors, not crashes.
        var junk = new DeviceRequestPayload { LabId = labId, InstanceId = Guid.NewGuid().ToString("d"), InstanceName = "Laptop", Csr = [1, 2, 3], CreatedAtUnix = Now.ToUnixTimeSeconds() };
        Assert.Throws<InvalidDataException>(() => DeviceAuthorization.OpenRequest(Envelope(labId, junk, key), "junk.lcreq", out _));
        using var rsa = RSA.Create(2048);
        var rsaCsr = new CertificateRequest("CN=Laptop", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();
        var rsaPayload = new DeviceRequestPayload { LabId = labId, InstanceId = Guid.NewGuid().ToString("d"), InstanceName = "Laptop", Csr = rsaCsr, CreatedAtUnix = Now.ToUnixTimeSeconds() };
        Assert.Throws<InvalidDataException>(() => DeviceAuthorization.OpenRequest(Envelope(labId, rsaPayload, key), "rsa.lcreq", out _));

        // The name the device itself writes is bounded the same way.
        var access = NewAccess(lab);
        access.InstanceName = new string('n', Defaults.MaxInstanceNameLength + 40);
        var request = DeviceAuthorization.CreateRequest(access, new FileSecretProtector(), lab.LabName, "1.0.0", Now);
        var opened = DeviceAuthorization.OpenRequest(request, "long.lcreq", out var deviceKey);
        deviceKey.Dispose();
        Assert.Equal(Defaults.MaxInstanceNameLength, opened.InstanceName.Length);
    }

    /// <summary>A console leaf of this lab with an arbitrary subject, for the OU tests.</summary>
    private static X509Certificate2 LeafWithSubject(LabKey lab, string subject, string instanceId, ECDsa key)
    {
        var request = new CertificateRequest(new X500DistinguishedName(subject), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(LabName.ForConsole(lab.LabId, instanceId).ToUri());
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(lab.Authority, true, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        serial[0] |= 0x40;
        var notBefore = new DateTimeOffset(lab.Authority.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        return request.Create(lab.Authority, notBefore, notBefore.AddDays(30), serial);
    }

    private sealed class FailingProtector : ISecretProtector
    {
        public int Forgotten { get; private set; }

        public string Name => "failing";

        public bool IsAvailable => true;

        public ProtectedSecret Protect(string reference, ReadOnlySpan<byte> secret) => throw new InvalidOperationException("The keystore refused the item.");

        public bool TryUnprotect(ProtectedSecret secret, out byte[] plaintext) => throw new NotSupportedException();

        public void Forget(ProtectedSecret secret) => Forgotten++;
    }

    // ------------------------------------------------------------------ dormant codes (D-60)

    [Fact]
    public void A_dormant_code_is_refused_until_activated_and_the_enrollment_document_migrates_from_version_1()
    {
        using var lab = TestLab.Create();
        var authority = new EnrollmentAuthority(new EnrollmentDocument { LabId = lab.LabId });
        var printable = authority.Generate(2, "stick-1", Now, "admin-1", "MacBook");
        Assert.Single(authority.Document.Batches);
        Assert.Equal("MacBook", authority.Document.Batches[0].IssuedByInstanceName);

        // A backup lands on another profile: every outstanding code sleeps.
        Assert.Equal(2, authority.MarkDormant(Now));
        Assert.Equal(0, authority.UnusedCodeCount);
        Assert.Equal(2, authority.DormantCodeCount);
        Assert.Equal("stick-1", Assert.Single(authority.DormantBatches).Batch);

        using var key = LabCertificates.CreateKey();
        var request = new EnrollRequest
        {
            LabId = lab.LabId,
            AgentId = Guid.NewGuid().ToString("d"),
            Number = 4,
            EnrollmentCode = printable[0],
            Csr = Google.Protobuf.ByteString.CopyFrom(LabCertificates.CreateSigningRequest(key, "PC-04")),
        };

        var refused = authority.Redeem(lab, request, Now);
        Assert.Equal(EnrollmentOutcome.DormantCode, refused.Outcome);
        Assert.Contains("Use codes from the imported backup", refused.Message, StringComparison.Ordinal);
        Assert.False(authority.Document.Codes[0].IsBurned);

        Assert.Equal(2, authority.ActivateDormant());
        Assert.Equal(0, authority.DormantCodeCount);
        var issued = authority.Redeem(lab, request, Now);
        Assert.Equal(EnrollmentOutcome.Issued, issued.Outcome);
        issued.Certificate!.Dispose();

        // A newer stick voids dormant codes too (D-28): sleeping is not a way to outlive a stick.
        var second = new EnrollmentAuthority(new EnrollmentDocument { LabId = lab.LabId });
        second.Generate(3, "stick-2", Now);
        second.MarkDormant(Now);
        Assert.Equal(3, second.Supersede(Now.AddMinutes(1)));
        Assert.Equal(0, second.DormantCodeCount);

        // A version-1 enrollment.json reads as version 2 with every code active.
        var v1 = """{"schema_version":1,"lab_id":"x","codes":[{"code":"ABCDEFGHJKMNPQRSTVWX","created_at_unix":1,"batch":"old"}]}""";
        var migrated = JsonStore.Parse<EnrollmentDocument>(v1, "enrollment.json", EnrollmentDocument.Migrations);
        Assert.Equal(Defaults.EnrollmentSchemaVersion, migrated.SchemaVersion);
        Assert.Single(migrated.Codes);
        Assert.True(migrated.Codes[0].IsUsable);
        Assert.Empty(migrated.Batches);
        var v3 = v1.Replace("\"schema_version\":1", $"\"schema_version\":{Defaults.EnrollmentSchemaVersion + 1}", StringComparison.Ordinal);
        Assert.Throws<SchemaVersionException>(() => JsonStore.Parse<EnrollmentDocument>(v3, "enrollment.json", EnrollmentDocument.Migrations));
    }

    [Fact]
    public void The_access_document_round_trips_through_the_store()
    {
        using var lab = TestLab.Create();
        var access = NewAccess(lab);
        DeviceAuthorization.CreateRequest(access, new FileSecretProtector(), lab.LabName, "1.0.0", Now);
        var json = JsonStore.Serialize(access, AccessDocument.Migrations);
        Assert.Contains("\"state\": \"request_pending\"", json, StringComparison.Ordinal);
        var back = JsonStore.Parse<AccessDocument>(json, "access.json", AccessDocument.Migrations);
        Assert.Equal(access.InstanceId, back.InstanceId);
        Assert.Equal(AccessState.RequestPending, back.State);
        Assert.Equal(access.PendingKey!.Reference, back.PendingKey!.Reference);
    }

    private static AccessDocument NewAccess(LabKey lab) => new()
    {
        LabId = lab.LabId,
        State = AccessState.NeedsAuthorization,
        InstanceId = Guid.NewGuid().ToString("d"),
        InstanceName = "Teacher laptop",
        Authority = lab.Document.Authority,
    };
}
