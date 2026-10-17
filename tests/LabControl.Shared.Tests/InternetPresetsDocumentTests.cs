using Xunit;

using System.Text.Json.Nodes;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Tests;

/// <summary>The named whitelist presets (M6, D-73): the file on disk, and that the backup and the lab file carry them like the script library.</summary>
public sealed class InternetPresetsDocumentTests
{
    // The lab file's leaves are clamped inside the CA's window, and the test CA is minted at the real clock.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static InternetPresetsDocument Sample(string labId) => new()
    {
        LabId = labId,
        SeedImportedAtUnix = 1_790_000_000,
        Presets =
        [
            new InternetPreset { Id = "p1", Name = "Java docs", Hosts = ["*.jetbrains.com", "docs.oracle.com"], CreatedAtUnix = 1_790_000_000, ModifiedAtUnix = 1_790_000_100 },
            new InternetPreset { Id = "p2", Name = "Пайтон", Hosts = ["docs.python.org"], CreatedAtUnix = 1_790_000_200, ModifiedAtUnix = 1_790_000_200 },
        ],
    };

    [Fact]
    public void The_document_round_trips_with_its_version_first()
    {
        var json = JsonStore.Serialize(Sample("lab"), InternetPresetsDocument.Migrations);
        Assert.StartsWith("{\n  \"schema_version\": " + Defaults.InternetPresetsSchemaVersion, json, StringComparison.Ordinal);

        var back = JsonStore.Parse<InternetPresetsDocument>(json, Defaults.InternetPresetsFileName, InternetPresetsDocument.Migrations);
        Assert.Equal("lab", back.LabId);
        Assert.Equal(1_790_000_000, back.SeedImportedAtUnix);
        Assert.Equal(["p1", "p2"], back.Presets.Select(p => p.Id));
        var java = back.Presets[0];
        Assert.Equal("Java docs", java.Name);
        Assert.Equal(["*.jetbrains.com", "docs.oracle.com"], java.Hosts);
        Assert.Equal(1_790_000_000, java.CreatedAtUnix);
        Assert.Equal(1_790_000_100, java.ModifiedAtUnix);
        Assert.Equal("Пайтон", back.Presets[1].Name);
    }

    [Fact]
    public void A_newer_document_is_refused()
    {
        var json = JsonStore.Serialize(Sample("lab"), InternetPresetsDocument.Migrations)
            .Replace($"\"{Defaults.SchemaVersionFieldName}\": {Defaults.InternetPresetsSchemaVersion}",
                     $"\"{Defaults.SchemaVersionFieldName}\": {Defaults.InternetPresetsSchemaVersion + 1}", StringComparison.Ordinal);

        var error = Assert.Throws<SchemaVersionException>(() => JsonStore.Parse<InternetPresetsDocument>(json, Defaults.InternetPresetsFileName, InternetPresetsDocument.Migrations));
        Assert.Equal(Defaults.InternetPresetsSchemaVersion + 1, error.FileVersion);
    }

    [Fact]
    public void A_clone_shares_no_host_list()
    {
        var original = Sample("lab").Presets[0];
        var copy = original.Clone();
        copy.Hosts.Add("example.org");
        copy.Name = "changed";

        Assert.Equal(2, original.Hosts.Count);
        Assert.Equal("Java docs", original.Name);
    }

    [Fact]
    public void A_backup_carries_the_presets_sealed_and_an_older_backup_has_none()
    {
        using var lab = TestLab.Create();
        var machines = new LabDocument { LabId = lab.LabId };

        var with = LabBackup.Export(lab, machines, new Dictionary<string, string>(), "x", Now, internetPresets: Sample(lab.LabId));
        var parsed = LabBackup.Parse(LabBackup.Serialize(with), "a.lcbak");
        Assert.DoesNotContain("jetbrains", LabBackup.Serialize(with), StringComparison.Ordinal);
        var presets = LabBackup.Open(parsed, lab).InternetPresets!;
        Assert.Equal(Defaults.InternetPresetsSchemaVersion, presets.SchemaVersion);
        Assert.Equal(["Java docs", "Пайтон"], presets.Presets.Select(p => p.Name));
        Assert.Equal(["*.jetbrains.com", "docs.oracle.com"], presets.Presets[0].Hosts);

        // A backup written before M6 has no such member at all (a null one is not written either).
        var older = LabBackup.Export(lab, machines, new Dictionary<string, string>(), "x", Now, scripts: new ScriptsDocument { LabId = lab.LabId });
        var olderPayload = LabBackup.Open(LabBackup.Parse(LabBackup.Serialize(older), "old.lcbak"), lab);
        Assert.NotNull(olderPayload.Scripts);
        Assert.Null(olderPayload.InternetPresets);
    }

    [Fact]
    public void A_backup_whose_sealed_presets_come_from_a_newer_build_is_refused_by_name()
    {
        using var lab = TestLab.Create();
        var newer = Sample(lab.LabId);
        newer.SchemaVersion = Defaults.InternetPresetsSchemaVersion + 1;
        var backup = LabBackup.Parse(LabBackup.Serialize(LabBackup.Export(lab, new LabDocument { LabId = lab.LabId }, new Dictionary<string, string>(), "me", Now, internetPresets: newer)), "n.lcbak");

        var refused = Assert.Throws<SchemaVersionException>(() => LabBackup.Open(backup, lab));
        Assert.Contains(Defaults.InternetPresetsFileName, refused.DocumentName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lab_file_carries_a_copy_of_the_presets_and_an_older_lab_file_has_none()
    {
        using var lab = TestLab.Create();
        var document = new LabDocument { LabId = lab.LabId, LabName = lab.LabName };
        var source = Sample(lab.LabId);

        var payload = LabFile.Snapshot(lab, document, null, "admin-1", "MacBook", 1, Now, source);
        source.Presets[0].Hosts.Add("changed-after-the-snapshot.org");
        Assert.Equal(2, payload.InternetPresets!.Presets[0].Hosts.Count);

        var json = LabFile.Serialize(LabFile.Export(lab, payload));
        var opened = LabFile.Open(LabFile.Parse(json, "a.lclab"), "a.lclab");
        Assert.Equal(["Java docs", "Пайтон"], opened.InternetPresets!.Presets.Select(p => p.Name));
        Assert.Equal(["*.jetbrains.com", "docs.oracle.com"], opened.InternetPresets.Presets[0].Hosts);

        // Without presets the member is absent from the signed payload, exactly as in a lab file from before M6.
        var olderJson = LabFile.Serialize(LabFile.Export(lab, LabFile.Snapshot(lab, document, null, "admin-1", "MacBook", 2, Now)));
        var olderPayloadText = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(JsonNode.Parse(olderJson)!["payload"]!.GetValue<string>()));
        Assert.DoesNotContain("internet_presets", olderPayloadText, StringComparison.Ordinal);
        Assert.Null(LabFile.Open(LabFile.Parse(olderJson, "old.lclab"), "old.lclab").InternetPresets);
    }
}
