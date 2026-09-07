using LabControl.Shared.Jobs;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class SendFileRequestTests
{
    [Theory]
    [InlineData("../work.pdf")]
    [InlineData("C:\\work.pdf")]
    [InlineData("work.pdf:payload")]
    [InlineData("CON.txt")]
    [InlineData("NUL")]
    [InlineData("LPT1.pdf")]
    [InlineData("COM¹.txt")]
    [InlineData("work.pdf.")]
    [InlineData("work.pdf ")]
    [InlineData("bad\nname.pdf")]
    public void Windows_invalid_names_are_refused_on_every_OS(string name) => Assert.False(SendFileRequest.IsValidName(name));

    [Theory]
    [InlineData("Завдання 1.docx", true)]
    [InlineData("guide.PDF", true)]
    [InlineData("app.exe", false)]
    [InlineData("script.ps1", false)]
    [InlineData("shortcut.lnk", false)]
    [InlineData("website.url", false)]
    [InlineData("macro.docm", false)]
    public void Wire_roundtrip_preserves_name_and_limits_opening(string name, bool mayOpen)
    {
        var original = new SendFileRequest(new string('a', 64), new string('a', 64), name, true);
        var job = new Job { Kind = Job.Types.Kind.SendFile };
        job.Args.Add(original.ToArgs());
        Assert.True(SendFileRequest.TryParse(job, out var parsed, out var error), error);
        Assert.Equal(original, parsed);
        Assert.Equal(mayOpen, parsed.MayOpen);
        job.Args.Remove("open");
        Assert.True(SendFileRequest.TryParse(job, out parsed, out _));
        Assert.False(parsed.Open);
        job.Args["open"] = "perhaps";
        Assert.False(SendFileRequest.TryParse(job, out _, out _));
    }
}
