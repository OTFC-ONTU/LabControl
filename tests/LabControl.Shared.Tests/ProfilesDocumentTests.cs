using Xunit;

using System.Text.Json.Nodes;
using LabControl.Shared.Identity;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Tests;

/// <summary>
/// M5 (D-55): the index of saved labs is an ordinary versioned document, and <c>lab.json</c>
/// reaches schema 2 through a real migration step that keeps every field a version-1 file
/// had. Backups sealed before M5 still open, because the sealed lab walks the same chain.
/// </summary>
public sealed class ProfilesDocumentTests
{
    [Fact]
    public void The_profiles_index_round_trips_with_snake_case_enums()
    {
        var directory = Directory.CreateTempSubdirectory("labcontrol-tests");
        try
        {
            var path = Path.Combine(directory.FullName, Defaults.ProfilesFileName);
            var document = new ProfilesDocument
            {
                LastUsedLabId = "5b1c",
                Labs =
                [
                    new ProfileRecord
                    {
                        LabId = "5b1c",
                        LabName = "ОНТФК lab 214",
                        Directory = "labs/5b1c",
                        AuthorityFingerprint = ProfileRecord.AuthorityFingerprintOf([1, 2, 3]),
                        Access = ProfileAccess.Administrator,
                        Authorization = ProfileAuthorization.Authorized,
                        InstanceId = "i",
                        InstanceName = "MacBook-2026",
                        PcCount = 14,
                        AddedAtUnix = 1,
                        LastUsedUnix = 2,
                        Source = ProfileSource.Migrated,
                    },
                    new ProfileRecord
                    {
                        LabId = "7e",
                        LabName = "Room 7",
                        Directory = "labs/7e",
                        Access = ProfileAccess.Teacher,
                        Authorization = ProfileAuthorization.NeedsAuthorization,
                        Source = ProfileSource.LabFile,
                    },
                ],
            };

            JsonStore.Save(path, document, ProfilesDocument.Migrations);
            var text = File.ReadAllText(path);

            Assert.StartsWith($"{{\n  \"{Defaults.SchemaVersionFieldName}\": {Defaults.ProfilesSchemaVersion}", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Contains("\"last_used_lab_id\": \"5b1c\"", text, StringComparison.Ordinal);
            Assert.Contains("\"access\": \"administrator\"", text, StringComparison.Ordinal);
            Assert.Contains("\"access\": \"teacher\"", text, StringComparison.Ordinal);
            Assert.Contains("\"authorization\": \"authorized\"", text, StringComparison.Ordinal);
            Assert.Contains("\"authorization\": \"needs_authorization\"", text, StringComparison.Ordinal);
            Assert.Contains("\"source\": \"migrated\"", text, StringComparison.Ordinal);
            Assert.Contains("\"source\": \"lab_file\"", text, StringComparison.Ordinal);
            Assert.Contains("\"authority_fingerprint\"", text, StringComparison.Ordinal);

            var reloaded = JsonStore.Load<ProfilesDocument>(path, ProfilesDocument.Migrations);
            Assert.Equal("5b1c", reloaded.LastUsedLabId);
            Assert.Equal(2, reloaded.Labs.Count);
            Assert.Equal(ProfileAccess.Administrator, reloaded.Labs[0].Access);
            Assert.Equal(ProfileSource.Migrated, reloaded.Labs[0].Source);
            Assert.Equal(14, reloaded.Labs[0].PcCount);
            Assert.Equal(ProfileAuthorization.NeedsAuthorization, reloaded.Labs[1].Authorization);
            Assert.Equal(ProfileSource.LabFile, reloaded.Labs[1].Source);
            Assert.Equal(document.Labs[0].AuthorityFingerprint, reloaded.Labs[0].AuthorityFingerprint);
            Assert.Equal(64, reloaded.Labs[0].AuthorityFingerprint.Length);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_profiles_index_from_a_newer_build_is_refused()
    {
        var json = JsonStore.Serialize(new ProfilesDocument(), ProfilesDocument.Migrations)
            .Replace($"\"{Defaults.SchemaVersionFieldName}\": {Defaults.ProfilesSchemaVersion}",
                     $"\"{Defaults.SchemaVersionFieldName}\": {Defaults.ProfilesSchemaVersion + 1}",
                     StringComparison.Ordinal);

        var error = Assert.Throws<SchemaVersionException>(
            () => JsonStore.Parse<ProfilesDocument>(json, Defaults.ProfilesFileName, ProfilesDocument.Migrations));
        Assert.Equal(Defaults.ProfilesSchemaVersion + 1, error.FileVersion);
        Assert.Contains(Defaults.ProfilesFileName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_1_lab_document_migrates_to_2_keeping_every_field_and_defaulting_the_new_ones()
    {
        // A lab.json exactly as an M4 console wrote it: no device-book, no delivery fields.
        const string version1 = """
            {
              "schema_version": 1,
              "lab_id": "5b1c",
              "lab_name": "ОНТФК lab 214",
              "machines": [
                {
                  "agent_id": "a",
                  "number": 7,
                  "hostname": "PC-07",
                  "mac": "02:00:5E:00:00:07",
                  "certificate_serial": "0A",
                  "certificate_not_after_unix": 123,
                  "last_ip": "10.0.0.7",
                  "enrolled_at_unix": 1,
                  "last_seen_unix": 2,
                  "setup_readiness_codes": ["network.wol_unverified"],
                  "agent_version": "0.1.4",
                  "protocol_version": 1,
                  "logged_on_user": "student",
                  "last_instance_id": "i"
                }
              ],
              "instances": [
                { "instance_id": "i", "name": "MacBook-2026", "certificate_serial": "0B", "first_seen_unix": 3, "last_seen_unix": 4, "is_this_machine": true }
              ],
              "revocations": [],
              "layout": [ { "number": 7, "column": 2, "row": 1 } ]
            }
            """;

        Assert.Equal(2, Defaults.LabSchemaVersion);
        var lab = JsonStore.Parse<LabDocument>(version1, Defaults.LabFileName, LabDocument.Migrations);

        Assert.Equal(2, lab.SchemaVersion);
        Assert.Equal("5b1c", lab.LabId);
        Assert.Equal("ОНТФК lab 214", lab.LabName);
        var machine = Assert.Single(lab.Machines);
        Assert.Equal(("a", 7, "PC-07", "02:00:5E:00:00:07", "0A", 123L, "10.0.0.7", 1L, 2L),
            (machine.AgentId, machine.Number, machine.Hostname, machine.Mac, machine.CertificateSerial, machine.CertificateNotAfterUnix, machine.LastIp, machine.EnrolledAtUnix, machine.LastSeenUnix));
        Assert.Equal("network.wol_unverified", Assert.Single(machine.SetupReadinessCodes));
        Assert.Equal(("0.1.4", 1, "student", "i"), (machine.AgentVersion, machine.ProtocolVersion, machine.LoggedOnUser, machine.LastInstanceId));
        Assert.Empty(machine.RevocationSerialsSeen);
        Assert.Equal(0, machine.LastInstanceObservedUnix);

        var instance = Assert.Single(lab.Instances);
        Assert.Equal(("i", "MacBook-2026", "0B", 3L, 4L, true), (instance.InstanceId, instance.Name, instance.CertificateSerial, instance.FirstSeenUnix, instance.LastSeenUnix, instance.IsThisMachine));
        Assert.Equal(ProfileAccess.Unknown, instance.Access);
        Assert.Equal(0, instance.AuthorizedAtUnix);
        Assert.Equal(0, instance.RevokedAtUnix);
        Assert.Equal((2, 1), (lab.Layout[0].Column, lab.Layout[0].Row));

        // Saved again, it is a version-2 file with the new fields present.
        var saved = JsonStore.Serialize(lab, LabDocument.Migrations);
        var root = (JsonObject)JsonNode.Parse(saved)!;
        Assert.Equal(2, root[Defaults.SchemaVersionFieldName]!.GetValue<int>());
        Assert.Contains("\"revocation_serials_seen\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"access\": \"unknown\"", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void The_new_lab_fields_round_trip()
    {
        var lab = new LabDocument
        {
            LabId = "x",
            Machines = [new MachineRecord { AgentId = "a", RevocationSerialsSeen = ["0A", "0B"], LastInstanceObservedUnix = 5 }],
            Instances = [new InstanceRecord { InstanceId = "t", Access = ProfileAccess.Teacher, AuthorizedAtUnix = 6, RevokedAtUnix = 7 }],
        };

        var text = JsonStore.Serialize(lab, LabDocument.Migrations);
        Assert.Contains("\"access\": \"teacher\"", text, StringComparison.Ordinal);
        var reloaded = JsonStore.Parse<LabDocument>(text, Defaults.LabFileName, LabDocument.Migrations);
        Assert.Equal(new[] { "0A", "0B" }, reloaded.Machines[0].RevocationSerialsSeen);
        Assert.Equal(5, reloaded.Machines[0].LastInstanceObservedUnix);
        Assert.Equal((ProfileAccess.Teacher, 6L, 7L), (reloaded.Instances[0].Access, reloaded.Instances[0].AuthorizedAtUnix, reloaded.Instances[0].RevokedAtUnix));
    }

    [Fact]
    public void A_backup_sealed_with_a_version_1_lab_still_opens_and_a_newer_one_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();

        // Export serialises the lab document as it stands, so a version-1 stamp is exactly
        // what an M4 console's backup carries.
        var old = new LabDocument
        {
            SchemaVersion = 1,
            LabId = lab.LabId,
            LabName = lab.LabName,
            Machines = [new MachineRecord { AgentId = "a", Number = 7, Mac = "02:00:5E:00:00:07" }],
            Instances = [new InstanceRecord { InstanceId = "i", Name = "old", IsThisMachine = true }],
        };
        var exported = LabBackup.Export(lab, old, new Dictionary<string, string>(), "old", now);
        var parsed = LabBackup.Parse(LabBackup.Serialize(exported), "old" + Defaults.BackupFileExtension);

        var payload = LabBackup.Open(parsed, lab);
        Assert.Equal(Defaults.LabSchemaVersion, payload.Lab.SchemaVersion);
        Assert.Equal(7, Assert.Single(payload.Lab.Machines).Number);
        Assert.Empty(payload.Lab.Machines[0].RevocationSerialsSeen);
        Assert.Equal(ProfileAccess.Unknown, Assert.Single(payload.Lab.Instances).Access);

        var future = new LabDocument { SchemaVersion = Defaults.LabSchemaVersion + 1, LabId = lab.LabId, LabName = lab.LabName };
        var tooNew = LabBackup.Parse(LabBackup.Serialize(LabBackup.Export(lab, future, new Dictionary<string, string>(), "new", now)), "new" + Defaults.BackupFileExtension);
        var error = Assert.Throws<SchemaVersionException>(() => LabBackup.Open(tooNew, lab));
        Assert.Equal(Defaults.LabSchemaVersion + 1, error.FileVersion);
    }

    [Fact]
    public void The_authority_fingerprint_is_the_sha256_of_the_ca_certificate()
    {
        using var lab = TestLab.Create();
        var fingerprint = ProfileRecord.AuthorityFingerprintOf(lab.Document.Authority);
        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(fingerprint, ProfileRecord.AuthorityFingerprintOf(lab.Authority.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert)));
        Assert.NotEqual(fingerprint, ProfileRecord.AuthorityFingerprintOf(TestLab.Create().Document.Authority));
    }
}
