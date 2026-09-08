using Xunit;

using LabControl.Shared.Identity;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Tests;

/// <summary>
/// The backup archive (D-26): opens with whatever opens the lab key, restores the machine
/// list unchanged, and is useless to anyone else — the machine list is not even readable.
/// </summary>
public sealed class BackupTests
{
    [Fact]
    public void A_backup_opens_with_a_passphrase_or_the_recovery_code_and_restores_the_lab()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create(out var recoveryCode);
        var machines = new LabDocument
        {
            LabId = lab.LabId,
            LabName = lab.LabName,
            Machines = [new MachineRecord { AgentId = "a", Number = 7, Mac = "02:00:5E:00:00:07" }],
            Layout = [new LayoutTile { Number = 7, Column = 2, Row = 1 }],
        };

        var enrollment = new EnrollmentDocument
        {
            LabId = lab.LabId,
            Codes = [new EnrollmentCodeRecord { Code = "ABCDEFGHJKMNPQRSTVWX", Batch = "stick-1", CreatedAtUnix = now.ToUnixTimeSeconds() }],
        };

        var exported = LabBackup.Export(lab, machines, new Dictionary<string, string> { ["jdk.yaml"] = "name: jdk" }, "MacBook-2026", now, enrollment);
        var json = LabBackup.Serialize(exported);

        // The file names the lab and its holders in the clear, nothing else.
        Assert.Contains(TestLab.HolderName, json, StringComparison.Ordinal);
        Assert.DoesNotContain("02:00:5E:00:00:07", json, StringComparison.Ordinal);
        Assert.DoesNotContain("jdk", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFGHJKMNPQRSTVWX", json, StringComparison.Ordinal);

        var parsed = LabBackup.Parse(json, "test" + Defaults.BackupFileExtension);
        Assert.Equal(Defaults.BackupSchemaVersion, parsed.SchemaVersion);
        Assert.Equal(lab.LabName, parsed.LabName);
        Assert.Contains(TestLab.HolderName, parsed.LabKey.Wrappings.Select(w => w.Name));

        // With a passphrase.
        Assert.True(LabKey.TryUnlock(parsed.LabKey, TestLab.Passphrase, out var byPassphrase));
        using (byPassphrase)
        {
            var payload = LabBackup.Open(parsed, byPassphrase);
            Assert.Equal(7, Assert.Single(payload.Lab.Machines).Number);
            Assert.Equal((2, 1), (payload.Lab.Layout[0].Column, payload.Lab.Layout[0].Row));
            Assert.Equal("name: jdk", payload.Catalog["jdk.yaml"]);

            // The outstanding enrollment codes travel too, so a stick written on the old
            // machine still enrols PCs on the new one (D-28).
            Assert.Equal("ABCDEFGHJKMNPQRSTVWX", Assert.Single(payload.Enrollment!.Codes).Code);
        }

        // With the recovery code.
        Assert.True(LabKey.TryUnlock(parsed.LabKey, recoveryCode, out var byRecovery));
        using (byRecovery)
        {
            Assert.Single(LabBackup.Open(parsed, byRecovery).Lab.Machines);
        }

        // With the wrong passphrase: nothing.
        Assert.False(LabKey.TryUnlock(parsed.LabKey, "wrong", out _));
    }

    [Fact]
    public void A_backup_sealed_by_another_lab_cannot_be_opened_with_this_key()
    {
        var now = DateTimeOffset.UtcNow;
        using var ours = TestLab.Create();
        using var theirs = TestLab.Create();

        var exported = LabBackup.Export(theirs, new LabDocument { LabId = theirs.LabId }, new Dictionary<string, string>(), "x", now);

        Assert.Throws<InvalidDataException>(() => LabBackup.Open(exported, ours));
    }

    [Fact]
    public void A_backup_whose_sealed_scripts_or_enrollment_come_from_a_newer_build_is_refused_by_name()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var machines = new LabDocument { LabId = lab.LabId, LabName = lab.LabName };

        var newerScripts = new ScriptsDocument { SchemaVersion = Defaults.ScriptsSchemaVersion + 1, LabId = lab.LabId };
        var scripts = LabBackup.Parse(LabBackup.Serialize(LabBackup.Export(lab, machines, new Dictionary<string, string>(), "me", now, scripts: newerScripts)), "s.lcbak");
        var refusedScripts = Assert.Throws<SchemaVersionException>(() => LabBackup.Open(scripts, lab));
        Assert.Contains(Defaults.ScriptsFileName, refusedScripts.DocumentName, StringComparison.Ordinal);
        Assert.Equal(Defaults.ScriptsSchemaVersion + 1, refusedScripts.FileVersion);

        var newerEnrollment = new EnrollmentDocument { SchemaVersion = Defaults.EnrollmentSchemaVersion + 1, LabId = lab.LabId };
        var enrollment = LabBackup.Parse(LabBackup.Serialize(LabBackup.Export(lab, machines, new Dictionary<string, string>(), "me", now, enrollment: newerEnrollment)), "e.lcbak");
        var refusedEnrollment = Assert.Throws<SchemaVersionException>(() => LabBackup.Open(enrollment, lab));
        Assert.Contains(Defaults.EnrollmentFileName, refusedEnrollment.DocumentName, StringComparison.Ordinal);
        Assert.Equal(Defaults.EnrollmentSchemaVersion + 1, refusedEnrollment.FileVersion);

        // Current versions of both still open, stamped with the version this build writes.
        var fine = LabBackup.Parse(LabBackup.Serialize(LabBackup.Export(lab, machines, new Dictionary<string, string>(), "me", now,
            new EnrollmentDocument { LabId = lab.LabId }, new ScriptsDocument { LabId = lab.LabId })), "ok.lcbak");
        var payload = LabBackup.Open(fine, lab);
        Assert.Equal(Defaults.ScriptsSchemaVersion, payload.Scripts!.SchemaVersion);
        Assert.Equal(Defaults.EnrollmentSchemaVersion, payload.Enrollment!.SchemaVersion);
    }

    [Fact]
    public void A_backup_from_a_newer_build_is_refused_by_name()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var json = LabBackup.Serialize(LabBackup.Export(lab, new LabDocument(), new Dictionary<string, string>(), "x", now))
            .Replace($"\"{Defaults.SchemaVersionFieldName}\": {Defaults.BackupSchemaVersion}",
                     $"\"{Defaults.SchemaVersionFieldName}\": {Defaults.BackupSchemaVersion + 1}", StringComparison.Ordinal);

        var error = Assert.Throws<SchemaVersionException>(() => LabBackup.Parse(json, "lab.lcbak"));
        Assert.Equal(Defaults.BackupSchemaVersion + 1, error.FileVersion);
    }
}
