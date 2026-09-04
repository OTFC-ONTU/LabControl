using Xunit;

using System.Security.Cryptography.X509Certificates;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;

namespace LabControl.Shared.Tests;

/// <summary>
/// The console instance's private key never leaves its machine, so it is protected by the
/// machine (ARCHITECTURE §3.2). What matters here is that a protected secret opens again,
/// that it will not open under the wrong name, and that a document is always reopened with
/// the protector that wrote it.
/// </summary>
public sealed class SecretProtectorTests
{
    [Fact]
    public void The_file_fallback_round_trips_and_refuses_a_swapped_reference()
    {
        var protector = new FileSecretProtector();
        byte[] secret = [1, 2, 3, 4, 5, 6, 7, 8];

        var protectedSecret = protector.Protect("instance-a", secret);

        Assert.True(protector.TryUnprotect(protectedSecret, out var opened));
        Assert.Equal(secret, opened);
        Assert.DoesNotContain(secret, protectedSecret.Payload.Chunk(secret.Length));

        // Renaming the item must not open it: the reference is the AEAD's associated data.
        protectedSecret.Reference = "instance-b";
        Assert.False(protector.TryUnprotect(protectedSecret, out _));
    }

    [Fact]
    public void A_damaged_blob_fails_instead_of_returning_rubbish()
    {
        var protector = new FileSecretProtector();
        var protectedSecret = protector.Protect("instance-a", [9, 9, 9]);

        protectedSecret.Payload[^1] ^= 0xFF;
        Assert.False(protector.TryUnprotect(protectedSecret, out _));

        Assert.False(protector.TryUnprotect(new ProtectedSecret { Reference = "instance-a" }, out _));
    }

    [Fact]
    public void A_document_is_reopened_with_the_protector_that_wrote_it()
    {
        var written = new FileSecretProtector().Protect("instance-a", [1, 2, 3]);

        Assert.Equal(ProtectorNames.File, SecretProtector.For(written).Name);
        Assert.Throws<InvalidDataException>(
            () => SecretProtector.For(new ProtectedSecret { Protector = "something-else" }));
    }

    [Fact]
    public void This_machine_picks_a_protector_that_actually_works()
    {
        var chosen = SecretProtector.ForCurrentPlatform();

        Assert.True(chosen.IsAvailable);

        // On a Mac this writes a real Keychain item, so it is removed again: a developer's
        // Keychain must not fill up with one probe per test run.
        var probe = chosen.Protect("instance-probe", [1]);
        try
        {
            Assert.Equal(chosen.Name, SecretProtector.For(probe).Name);
        }
        finally
        {
            chosen.Forget(probe);
        }
    }

    [Fact]
    public void A_console_instance_reopens_from_its_document()
    {
        using var lab = TestLab.Create();
        var protector = new FileSecretProtector();

        using var minted = ConsoleInstance.Mint(lab, "MacBook-2026", protector);
        var document = JsonStore.Parse<InstanceDocument>(
            JsonStore.Serialize(minted.Document, InstanceDocument.Migrations),
            "instance.json",
            InstanceDocument.Migrations);

        using var reopened = ConsoleInstance.Open(document);

        Assert.Equal(minted.InstanceId, reopened.InstanceId);
        Assert.Equal(minted.CertificateSerial, reopened.CertificateSerial);
        Assert.True(reopened.Certificate.HasPrivateKey);

        // And it can still sign a beacon the lab's agents will accept.
        var now = DateTimeOffset.UtcNow;
        Assert.True(reopened.CreateBeacon("192.168.1.23", 47800, now)
            .TryVerify(LabTrustTests.PublicOnly(lab.Authority), lab.LabId, now, out _));
    }

    [Fact]
    public void The_instance_file_never_contains_the_raw_private_key()
    {
        using var lab = TestLab.Create();
        using var minted = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector());

        var json = JsonStore.Serialize(minted.Document, InstanceDocument.Migrations);
        using var key = minted.Certificate.GetECDsaPrivateKey();
        var raw = Convert.ToBase64String(key!.ExportPkcs8PrivateKey());

        Assert.DoesNotContain(raw, json, StringComparison.Ordinal);
    }
}
