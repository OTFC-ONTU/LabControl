using LabControl.Shared.Files;
using LabControl.Shared.Jobs;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class HandoutDeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "labcontrol-handout-" + Guid.NewGuid().ToString("N"));
    private string Materials => Path.Combine(_root, "Desktop", "Materials");
    private static SendFileRequest Request(byte[] bytes) => new(FileHash.Sha256Hex(bytes), FileHash.Sha256Hex(bytes), "lesson.txt", false);

    [Fact]
    public void Verified_handout_replaces_same_name_and_preserves_other_files()
    {
        Directory.CreateDirectory(Materials);
        File.WriteAllText(Path.Combine(Materials, "lesson.txt"), "old");
        File.WriteAllText(Path.Combine(Materials, "personal.txt"), "keep");
        var bytes = "new lesson"u8.ToArray();
        Assert.Equal(bytes.Length, HandoutDelivery.Deliver(new MemoryStream(bytes), Materials, Request(bytes), CancellationToken.None));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Materials, "lesson.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Materials, "personal.txt")));
        Assert.Equal(2, Directory.GetFiles(Materials).Length);
    }

    [Fact]
    public void Corruption_and_cancellation_preserve_the_existing_file_and_remove_partial()
    {
        Directory.CreateDirectory(Materials);
        var target = Path.Combine(Materials, "lesson.txt");
        File.WriteAllText(target, "old");
        var expected = "expected"u8.ToArray();
        Assert.Throws<InvalidDataException>(() => HandoutDelivery.Deliver(new MemoryStream("wrong"u8.ToArray()), Materials, Request(expected), CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => HandoutDelivery.Deliver(new MemoryStream(expected), Materials, Request(expected), new CancellationToken(true)));
        Assert.Equal("old", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(Materials));
    }

    [Fact]
    public void A_redirected_directory_is_refused_without_writing_to_the_target()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateSymbolicLink(Path.Combine(_root, "Desktop"), elsewhere);
        var bytes = "lesson"u8.ToArray();
        Assert.Throws<IOException>(() => HandoutDelivery.Deliver(new MemoryStream(bytes), Materials, Request(bytes), CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    [Fact]
    public void A_redirected_existing_file_is_preserved()
    {
        Directory.CreateDirectory(Materials);
        var personal = Path.Combine(_root, "personal.txt");
        File.WriteAllText(personal, "keep");
        File.CreateSymbolicLink(Path.Combine(Materials, "lesson.txt"), personal);
        var bytes = "lesson"u8.ToArray();
        Assert.Throws<IOException>(() => HandoutDelivery.Deliver(new MemoryStream(bytes), Materials, Request(bytes), CancellationToken.None));
        Assert.Equal("keep", File.ReadAllText(personal));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
