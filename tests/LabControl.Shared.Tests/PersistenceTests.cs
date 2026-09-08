using Xunit;

using System.Text.Json;
using System.Text.Json.Nodes;
using LabControl.Shared;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Tests;

/// <summary>
/// D-20: every file carries a schema version, migrates forward through tested steps, and
/// is <b>refused</b> — naming the version needed — when it was written by a newer build. A
/// silent partial read of lab.json loses machines; of a backup, the lab.
/// </summary>
public sealed class PersistenceTests
{
    [Fact]
    public void Every_document_this_build_writes_carries_its_version_first()
    {
        var json = JsonStore.Serialize(new LabDocument { LabId = "x" }, LabDocument.Migrations);

        Assert.StartsWith($"{{\n  \"{Defaults.SchemaVersionFieldName}\": {Defaults.LabSchemaVersion}",
            json.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_from_a_newer_build_is_refused_and_names_the_version_needed()
    {
        var json = JsonStore.Serialize(new LabDocument(), LabDocument.Migrations)
            .Replace($"\"{Defaults.SchemaVersionFieldName}\": {Defaults.LabSchemaVersion}",
                     $"\"{Defaults.SchemaVersionFieldName}\": {Defaults.LabSchemaVersion + 1}",
                     StringComparison.Ordinal);

        var error = Assert.Throws<SchemaVersionException>(
            () => JsonStore.Parse<LabDocument>(json, Defaults.LabFileName, LabDocument.Migrations));

        Assert.Equal(Defaults.LabSchemaVersion + 1, error.FileVersion);
        Assert.Equal(Defaults.LabSchemaVersion, error.SupportedVersion);
        Assert.Contains((Defaults.LabSchemaVersion + 1).ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains(Defaults.LabFileName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_ours_is_refused_rather_than_half_read()
    {
        Assert.Throws<InvalidDataException>(
            () => JsonStore.Parse<LabDocument>("{\"machines\":[]}", Defaults.LabFileName, LabDocument.Migrations));
        Assert.Throws<InvalidDataException>(
            () => JsonStore.Parse<LabDocument>("not json", Defaults.LabFileName, LabDocument.Migrations));
        Assert.Throws<InvalidDataException>(
            () => JsonStore.Parse<LabDocument>("[]", Defaults.LabFileName, LabDocument.Migrations));
    }

    [Fact]
    public void A_migration_chain_walks_a_document_forward_one_step_at_a_time()
    {
        var migrations = new SchemaMigrations(
            currentVersion: 3,
            new SchemaMigration(1, root => { root["added_in_2"] = true; return root; }),
            new SchemaMigration(2, root => { root["added_in_3"] = true; return root; }));

        var root = (JsonObject)JsonNode.Parse($"{{\"{Defaults.SchemaVersionFieldName}\":1}}")!;
        var upgraded = migrations.Upgrade("example.json", root);

        Assert.Equal(3, upgraded[Defaults.SchemaVersionFieldName]!.GetValue<int>());
        Assert.True(upgraded["added_in_2"]!.GetValue<bool>());
        Assert.True(upgraded["added_in_3"]!.GetValue<bool>());
    }

    [Fact]
    public void A_missing_migration_step_is_a_bug_in_the_build_not_bad_input()
    {
        var migrations = new SchemaMigrations(currentVersion: 2);
        var root = (JsonObject)JsonNode.Parse($"{{\"{Defaults.SchemaVersionFieldName}\":1}}")!;

        Assert.Throws<InvalidOperationException>(() => migrations.Upgrade("example.json", root));
    }

    [Fact]
    public void The_lab_document_round_trips_through_disk()
    {
        var directory = Directory.CreateTempSubdirectory("labcontrol-tests");
        try
        {
            var path = Path.Combine(directory.FullName, Defaults.LabFileName);
            var document = new LabDocument
            {
                LabId = Guid.NewGuid().ToString("d"),
                LabName = "ОНТФК lab 214",
                Machines =
                [
                    new MachineRecord { AgentId = "a", Number = 7, Hostname = "PC-07", Mac = "02:00:5E:00:00:07" },
                ],
                Instances = [new InstanceRecord { InstanceId = "i", Name = "MacBook-2026", IsThisMachine = true }],
                Layout = [new LayoutTile { Number = 7, Column = 2, Row = 1 }],
            };

            JsonStore.Save(path, document, LabDocument.Migrations);
            var reloaded = JsonStore.Load<LabDocument>(path, LabDocument.Migrations);

            Assert.Equal(document.LabId, reloaded.LabId);
            Assert.Equal("PC-07", Assert.Single(reloaded.Machines).Hostname);
            Assert.True(Assert.Single(reloaded.Instances).IsThisMachine);
            Assert.Equal(2, Assert.Single(reloaded.Layout).Column);

            // Snake case on the wire, so hand-reading the file is pleasant.
            var text = File.ReadAllText(path);
            Assert.Contains("\"lab_id\"", text, StringComparison.Ordinal);
            Assert.Contains("\"agent_id\"", text, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void An_interrupted_save_never_leaves_a_half_written_file()
    {
        var directory = Directory.CreateTempSubdirectory("labcontrol-tests");
        try
        {
            var path = Path.Combine(directory.FullName, Defaults.LabFileName);
            JsonStore.Save(path, new LabDocument { LabName = "first" }, LabDocument.Migrations);
            JsonStore.Save(path, new LabDocument { LabName = "second" }, LabDocument.Migrations);

            // The temporary file is renamed over the target, never appended in place.
            Assert.Equal("second", JsonStore.Load<LabDocument>(path, LabDocument.Migrations).LabName);
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.tmp"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_secret_file_is_written_for_its_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;   // Windows inherits the ACL of the per-user data directory instead.
        }

        var directory = Directory.CreateTempSubdirectory("labcontrol-tests");
        try
        {
            var path = Path.Combine(directory.FullName, Defaults.LabKeyFileName);
            JsonStore.Save(path, new LabDocument(), LabDocument.Migrations, ownerOnly: true);

            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_document_that_does_not_exist_yet_reads_as_nothing()
    {
        Assert.Null(JsonStore.LoadIfExists<LabDocument>(
            Path.Combine(Path.GetTempPath(), $"labcontrol-{Guid.NewGuid():n}.json"), LabDocument.Migrations));
    }

    [Fact]
    public void Enums_are_written_as_names_so_a_file_stays_readable()
    {
        var json = JsonStore.Serialize(
            new LabControl.Shared.Identity.LabKeyDocument
            {
                Wrappings = [new LabControl.Shared.Identity.KeyWrapping
                {
                    Kind = LabControl.Shared.Identity.KeyWrappingKind.Recovery,
                }],
            },
            LabControl.Shared.Identity.LabKeyDocument.Migrations);

        Assert.Contains("\"recovery\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\": 2", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_persisted_format_starts_at_version_one_and_only_lab_and_enrollment_json_have_moved_on()
    {
        SchemaMigrations[] chains =
        [
            LabControl.Shared.Identity.LabKeyDocument.Migrations,
            InstanceDocument.Migrations,
            AgentConfigDocument.Migrations,
            ProfilesDocument.Migrations,
            AccessDocument.Migrations,
            LabFileDocument.Migrations,
            DeviceRequestDocument.Migrations,
            DeviceGrantDocument.Migrations,
        ];

        Assert.All(chains, chain => Assert.Equal(1, chain.CurrentVersion));

        // lab.json (M5, D-55) and enrollment.json (D-60) are past version 1: their chains
        // must walk a version-1 file forward rather than leave a gap (D-20).
        Assert.Equal(2, LabDocument.Migrations.CurrentVersion);
        var root = (JsonObject)JsonNode.Parse($"{{\"{Defaults.SchemaVersionFieldName}\":1,\"lab_id\":\"x\"}}")!;
        var upgraded = LabDocument.Migrations.Upgrade(Defaults.LabFileName, root);
        Assert.Equal(2, upgraded[Defaults.SchemaVersionFieldName]!.GetValue<int>());
        Assert.Equal("x", upgraded["lab_id"]!.GetValue<string>());

        Assert.Equal(2, EnrollmentDocument.Migrations.CurrentVersion);
        var enrollment = (JsonObject)JsonNode.Parse($"{{\"{Defaults.SchemaVersionFieldName}\":1,\"lab_id\":\"x\",\"codes\":[]}}")!;
        var enrollmentUpgraded = EnrollmentDocument.Migrations.Upgrade(Defaults.EnrollmentFileName, enrollment);
        Assert.Equal(2, enrollmentUpgraded[Defaults.SchemaVersionFieldName]!.GetValue<int>());
    }

    [Fact]
    public void Json_options_are_shared_so_two_readers_cannot_disagree()
    {
        Assert.Equal(JsonNamingPolicy.SnakeCaseLower, JsonStore.Options.PropertyNamingPolicy);
    }
}
