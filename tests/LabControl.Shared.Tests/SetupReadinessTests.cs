using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public class SetupReadinessTests
{
    [Fact]
    public void Atomic_snapshot_replaces_previous_warnings_and_missing_is_not_ready()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(SetupReadiness.Read(root));
            SetupReadiness.Save(root, ["antivirus.third_party", "antivirus.third_party"]);
            Assert.Equal(["antivirus.third_party"], SetupReadiness.Read(root)!.Codes);
            SetupReadiness.Save(root, []);
            Assert.Empty(SetupReadiness.Read(root)!.Codes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Disk_and_wire_reports_are_bounded()
    {
        Assert.Throws<InvalidDataException>(() => SetupReadiness.Parse(new string(' ', Defaults.SetupReadinessMaxBytes + 1)));
        Assert.Throws<InvalidDataException>(() => SetupReadiness.Create(Enumerable.Repeat("antivirus.third_party", 17)));
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, Defaults.SetupReadinessFileName), new string(' ', Defaults.SetupReadinessMaxBytes + 1));
            Assert.Throws<InvalidDataException>(() => SetupReadiness.Read(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
