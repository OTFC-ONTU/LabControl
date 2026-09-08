using LabControl.Shared.Persistence;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class UsbInstallerBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "labcontrol-usb-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Build_copies_only_executables_and_preserves_enrollment_and_unrelated_files()
    {
        var (source, payload) = Arrange();
        var trust = File.ReadAllBytes(Path.Combine(payload, Defaults.SetupFileName));
        File.WriteAllText(Path.Combine(source, "lab-key.lck"), "not-for-usb");
        File.WriteAllText(Path.Combine(payload, "teacher.txt"), "keep");
        var builder = new UsbInstallerBuilder(source, "1.2.3");
        Assert.Equal(payload, builder.Build(Path.GetDirectoryName(payload)!));
        Assert.Equal(trust, File.ReadAllBytes(Path.Combine(payload, Defaults.SetupFileName)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(payload, "teacher.txt")));
        Assert.False(File.Exists(Path.Combine(payload, "lab-key.lck")));
        var version = Path.Combine(payload, Defaults.UsbBinariesDirectoryName, Defaults.AgentAppDirectoryName, builder.Version);
        Assert.Equal("agent", File.ReadAllText(Path.Combine(version, Defaults.AgentExecutableName)));
        Assert.Equal("session", File.ReadAllText(Path.Combine(version, Defaults.SessionExecutableName)));
        Assert.Equal("setup", File.ReadAllText(Path.Combine(payload, Defaults.SetupExecutableName)));
        Assert.Equal(payload, builder.Build(payload));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("uk-UA")]
    public void Generated_instructions_unlock_console_before_install_and_fall_back_to_English(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new(culture);
            var (source, payload) = Arrange();
            new UsbInstallerBuilder(source, "1.2.3").Build(payload);
            var text = File.ReadAllText(Path.Combine(payload, Defaults.UsbInstructionsFileName));
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(5, lines.Length);
            Assert.Contains("unlock its lab key before installing", lines[0]);
            Assert.Contains(Defaults.SetupExecutableName, lines[1]);
            Assert.Contains("as an administrator", lines[1]);
            Assert.Contains("selected by default", lines[2]);
            Assert.Contains("Clear that checkbox", lines[2]);
            Assert.Contains("restart Windows", lines[3]);
            Assert.Contains("green and connected", lines[4]);
            Assert.DoesNotContain("single-use-test-code", text);
            Assert.DoesNotContain("Enrol PCs", text);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void Helper_only_change_cannot_silently_replace_an_existing_usb_version()
    {
        var (source, payload) = Arrange();
        var original = new UsbInstallerBuilder(source, "1.2.3");
        original.Build(payload);
        File.WriteAllText(Path.Combine(source, Defaults.SessionExecutableName), "fixed helper");
        var changed = new UsbInstallerBuilder(source, "1.2.3");
        Assert.NotEqual(original.Version, changed.Version);
        Assert.Throws<InvalidOperationException>(() => changed.Build(payload));
        Assert.Equal("session", File.ReadAllText(Path.Combine(payload, Defaults.UsbBinariesDirectoryName,
            Defaults.AgentAppDirectoryName, original.Version, Defaults.SessionExecutableName)));
    }

    [Fact]
    public void A_source_changed_after_validation_is_not_published()
    {
        var (source, payload) = Arrange();
        var builder = new UsbInstallerBuilder(source, "1.2.3");
        File.WriteAllText(Path.Combine(source, Defaults.AgentExecutableName), "changed");
        Assert.Throws<IOException>(() => builder.Build(payload));
        Assert.False(File.Exists(Path.Combine(payload, Defaults.SetupExecutableName)));
        Assert.Empty(Directory.EnumerateFiles(payload, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Different_existing_build_is_refused_without_removing_it()
    {
        var (source, payload) = Arrange();
        var builder = new UsbInstallerBuilder(source, "1.2.3");
        var existing = Path.Combine(payload, Defaults.UsbBinariesDirectoryName, Defaults.AgentAppDirectoryName, "old");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "keep");
        Assert.Throws<InvalidOperationException>(() => builder.ValidateDestination(payload));
        Assert.True(File.Exists(Path.Combine(existing, "keep.txt")));
    }

    [Fact]
    public void Explicit_payload_directory_wins_over_unrelated_nested_LabControl_directory()
    {
        var (_, payload) = Arrange();
        Directory.CreateDirectory(Path.Combine(payload, Defaults.PayloadDirectoryName));
        using var authority = SetupPayload.Open(payload).Authority;
        Assert.NotEmpty(authority.RawData);
    }

    [Fact]
    public void Publish_all_project_folder_layout_is_supported()
    {
        var (source, payload) = Arrange();
        foreach (var (name, folder) in new[]
        {
            (Defaults.AgentExecutableName, AgentBuild.AgentPublishFolder),
            (Defaults.SessionExecutableName, AgentBuild.SessionPublishFolder),
            (Defaults.SetupExecutableName, "LabControl.Setup"),
        })
        {
            Directory.CreateDirectory(Path.Combine(source, folder));
            File.Move(Path.Combine(source, name), Path.Combine(source, folder, name));
        }
        Assert.Equal(payload, new UsbInstallerBuilder(source, "1.2.3").Build(payload));
    }

    private (string Source, string Payload) Arrange()
    {
        var source = Path.Combine(_root, "build");
        var payload = Path.Combine(_root, "usb", Defaults.PayloadDirectoryName);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(source, Defaults.AgentExecutableName), "agent");
        File.WriteAllText(Path.Combine(source, Defaults.SessionExecutableName), "session");
        File.WriteAllText(Path.Combine(source, Defaults.SetupExecutableName), "setup");
        using var lab = TestLab.Create();
        File.WriteAllBytes(Path.Combine(payload, Defaults.CaCertificateFileName), lab.Authority.RawData);
        JsonStore.Save(Path.Combine(payload, Defaults.SetupFileName), new SetupPayloadDocument
        {
            LabId = lab.LabId, EnrollmentCodes = ["single-use-test-code"],
        }, SetupPayloadDocument.Migrations);
        return (source, payload);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
