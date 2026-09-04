using Xunit;

using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Tests;

/// <summary>
/// The lab key is the one thing that must survive the teacher machine (D-13), so these
/// cover the whole ARCHITECTURE §3.2 promise: a forgotten passphrase is recoverable, a
/// lost recovery sheet is survivable, an ill teacher does not stop an exam, and the file
/// alone is useless to whoever finds it.
/// </summary>
public sealed class LabKeyTests
{
    [Fact]
    public void A_new_lab_has_one_holder_and_a_recovery_code()
    {
        using var lab = TestLab.Create(out var recoveryCode);

        Assert.Equal([TestLab.HolderName], lab.HolderNames);
        Assert.Equal(2, lab.Document.Wrappings.Count);
        Assert.Equal(Defaults.RecoveryCodeBytes, recoveryCode.Bytes.Length);
        Assert.True(Guid.TryParse(lab.LabId, out _));
    }

    [Fact]
    public void The_holder_passphrase_opens_the_key()
    {
        using var created = TestLab.Create();
        var document = Roundtrip(created.Document);

        Assert.True(LabKey.TryUnlock(document, TestLab.HolderName, TestLab.Passphrase, out var reopened));
        using (reopened)
        {
            Assert.Equal(created.LabId, reopened.LabId);
            Assert.Equal(created.Authority.Thumbprint, reopened.Authority.Thumbprint);
            Assert.True(reopened.Authority.HasPrivateKey);
        }
    }

    [Fact]
    public void A_passphrase_alone_finds_its_holder()
    {
        using var created = TestLab.Create();

        Assert.True(LabKey.TryUnlock(Roundtrip(created.Document), TestLab.Passphrase, out var reopened));
        reopened.Dispose();
    }

    [Fact]
    public void A_wrong_passphrase_opens_nothing()
    {
        using var created = TestLab.Create();
        var document = Roundtrip(created.Document);

        Assert.False(LabKey.TryUnlock(document, TestLab.HolderName, "not the passphrase", out _));
        Assert.False(LabKey.TryUnlock(document, "someone else", TestLab.Passphrase, out _));
    }

    [Fact]
    public void The_recovery_code_opens_the_key_after_a_trip_through_paper()
    {
        using var created = TestLab.Create(out var recoveryCode);
        var document = Roundtrip(created.Document);

        // What the teacher reads off the printed sheet, dashes, lower case and all.
        Assert.True(RecoveryCode.TryParse(recoveryCode.ToPrintableString().ToLowerInvariant(), out var retyped));
        Assert.True(LabKey.TryUnlock(document, retyped, out var reopened));

        using (reopened)
        {
            Assert.Equal(created.LabId, reopened.LabId);
        }
    }

    [Fact]
    public void A_second_holder_can_open_the_lab_and_a_removed_one_cannot()
    {
        const string colleague = "Colleague";
        const string colleaguePassphrase = "an exam day passphrase";

        using var created = TestLab.Create();
        created.AddHolder(colleague, colleaguePassphrase, iterations: TestLab.Iterations);

        var document = Roundtrip(created.Document);
        Assert.True(LabKey.TryUnlock(document, colleague, colleaguePassphrase, out var asColleague));

        // Removing the original holder does not need their cooperation (ARCHITECTURE §3.2).
        using (asColleague)
        {
            asColleague.RemoveHolder(TestLab.HolderName);
            document = Roundtrip(asColleague.Document);
        }

        Assert.False(LabKey.TryUnlock(document, TestLab.HolderName, TestLab.Passphrase, out _));
        Assert.False(LabKey.TryUnlock(document, TestLab.Passphrase, out _));
        Assert.True(LabKey.TryUnlock(document, colleague, colleaguePassphrase, out var stillOpen));
        stillOpen.Dispose();
    }

    [Fact]
    public void The_last_holder_cannot_be_removed()
    {
        using var lab = TestLab.Create();

        var error = Assert.Throws<InvalidOperationException>(() => lab.RemoveHolder(TestLab.HolderName));
        Assert.Contains("last key holder", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_same_holder_cannot_be_added_twice()
    {
        using var lab = TestLab.Create();

        Assert.Throws<InvalidOperationException>(
            () => lab.AddHolder(TestLab.HolderName.ToLowerInvariant(), "another passphrase", iterations: TestLab.Iterations));
    }

    [Fact]
    public void Reprinting_the_recovery_code_invalidates_the_old_sheet()
    {
        using var lab = TestLab.Create(out var original);
        var replacement = lab.ResetRecoveryCode(iterations: TestLab.Iterations);

        var document = Roundtrip(lab.Document);
        Assert.False(LabKey.TryUnlock(document, original, out _));
        Assert.True(LabKey.TryUnlock(document, replacement, out var reopened));
        reopened.Dispose();
    }

    [Fact]
    public void A_tampered_file_does_not_open()
    {
        using var lab = TestLab.Create();
        var document = Roundtrip(lab.Document);

        // Flip one bit of the sealed authority key: AES-GCM must notice.
        document.AuthorityPrivateKey.Ciphertext[0] ^= 0x01;

        Assert.Throws<InvalidDataException>(
            () => LabKey.TryUnlock(document, TestLab.HolderName, TestLab.Passphrase, out _));
    }

    [Fact]
    public void The_stored_file_never_contains_the_passphrase_or_the_recovery_code()
    {
        using var lab = TestLab.Create(out var recoveryCode);
        var json = JsonStore.Serialize(lab.Document, LabKeyDocument.Migrations);

        Assert.DoesNotContain(TestLab.Passphrase, json, StringComparison.Ordinal);
        Assert.DoesNotContain(recoveryCode.Canonical, json, StringComparison.OrdinalIgnoreCase);

        // The holder's name is stored in the clear, on purpose: you must be able to see who
        // can open the lab without opening it (ARCHITECTURE §3.2).
        Assert.Contains(TestLab.HolderName, json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recovery_code_never_prints_itself_by_accident()
    {
        var code = RecoveryCode.Generate();

        Assert.DoesNotContain(code.Canonical, code.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static LabKeyDocument Roundtrip(LabKeyDocument document) =>
        JsonStore.Parse<LabKeyDocument>(
            JsonStore.Serialize(document, LabKeyDocument.Migrations),
            Defaults.LabKeyFileName,
            LabKeyDocument.Migrations);
}
