using Google.Protobuf;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Jobs;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class UpdateSignatureTests
{
    private static UpdateManifest Manifest() => new()
    {
        Version = "0.2.0+01234567", MinInstalledVersion = "0.1.0",
        Files =
        {
            new UpdateFile { RelativePath = Defaults.AgentExecutableName, Size = 10, Sha256 = new string('a', 64) },
            new UpdateFile { RelativePath = Defaults.SessionExecutableName, Size = 20, Sha256 = new string('b', 64) },
        },
    };

    private static SelfUpdateRequest Request(LabKey key, UpdateManifest manifest) =>
        new(manifest.Version, "manifest", new string('c', 64), UpdateManifestSignature.Sign(key, manifest.ToByteArray()));

    [Theory]
    [InlineData("0.1", true)]
    [InlineData("0.1.0+abcdef12", true)]
    [InlineData("0.1.1", true)]
    [InlineData("0.0.9", false)]
    [InlineData("invalid", false)]
    public void Minimum_version_is_signed_and_compared_numerically(string installed, bool expected)
    {
        using var key = TestLab.Create();
        var manifest = Manifest();
        Assert.Equal(expected, UpdateBundle.TryReadVerified(manifest.ToByteArray(), Request(key, manifest),
            key.Authority, installed, out _, out _));
    }

    [Fact]
    public void Wrong_lab_unsigned_and_other_signature_domains_are_refused()
    {
        using var key = TestLab.Create();
        using var other = TestLab.Create();
        var manifest = Manifest();
        var bytes = manifest.ToByteArray();
        var request = Request(key, manifest);
        Assert.False(UpdateBundle.TryReadVerified(bytes, request, other.Authority, "0.1.0", out _, out _));
        foreach (var signature in new[] { "", "garbage", Convert.ToBase64String(key.Sign(bytes)), new string('x', 5000) })
            Assert.False(UpdateBundle.TryReadVerified(bytes, request with { ManifestSignature = signature }, key.Authority, "0.1.0", out _, out _));
    }

    [Fact]
    public void Every_security_relevant_field_is_covered_by_the_exact_byte_signature()
    {
        using var key = TestLab.Create();
        var original = Manifest();
        var request = Request(key, original);
        Action<UpdateManifest>[] mutations =
        [
            m => m.Version = "0.9.0",
            m => m.MinInstalledVersion = "0.0.0",
            m => m.ProbationSeconds = 1,
            m => m.Files[0].RelativePath = "evil.exe",
            m => m.Files[0].Size++,
            m => m.Files[0].Sha256 = new string('d', 64),
            m => m.Files.RemoveAt(1),
        ];
        foreach (var mutate in mutations)
        {
            var changed = original.Clone();
            mutate(changed);
            Assert.False(UpdateBundle.TryReadVerified(changed.ToByteArray(), request, key.Authority, "0.1.0", out _, out var error));
            Assert.Contains("signature", error);
        }
    }

    [Fact]
    public void Wrong_authority_refusal_does_not_echo_untrusted_manifest_content()
    {
        using var trusted = TestLab.Create();
        using var other = TestLab.Create();
        var manifest = Manifest();
        manifest.Version = "untrusted private payload text";
        Assert.False(UpdateBundle.TryReadVerified(manifest.ToByteArray(), Request(other, manifest), trusted.Authority,
            "0.1.0", out _, out var reason));
        Assert.Equal("the manifest has no valid signature from this lab's pinned authority", reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("banana")]
    public void Signed_but_malformed_minimum_is_refused(string minimum)
    {
        using var key = TestLab.Create();
        var manifest = Manifest();
        manifest.MinInstalledVersion = minimum;
        Assert.False(UpdateBundle.TryReadVerified(manifest.ToByteArray(), Request(key, manifest), key.Authority, "0.1.0", out _, out _));
    }

    [Fact]
    public void Signature_survives_job_argument_roundtrip()
    {
        using var key = TestLab.Create();
        var request = Request(key, Manifest());
        var job = new Job { Id = "signed-update", Kind = Job.Types.Kind.SelfUpdate };
        job.Args.Add(request.ToArgs());
        Assert.True(SelfUpdateRequest.TryParse(job, out var parsed, out _));
        Assert.Equal(request, parsed);
    }
}
