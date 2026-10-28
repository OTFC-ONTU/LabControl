using System.Globalization;
using LabControl.Shared.Persistence;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>
/// The stock words on the student's screen are in the language of the console that set them
/// (M7 portion 2, D-86 item 5, D-87): a lock and an exam keep the language they arrived with,
/// through a restart, and hand it to the "time is up" lock; an empty or unknown language is
/// English; and the words never follow the process's own culture.
/// </summary>
public sealed class StudentTextLanguageTests
{
    private const string LabId = "1c383cd3-0b7c-4d69-9a37-4bfd08bb6c2e";
    private const string Instance = "console-a";
    private const long Now = 1_800_000_000;

    private sealed class Rig
    {
        public FakeMachine Machine { get; } = new();

        public PolicyDocument? Saved { get; private set; }

        public PolicyJournal Journal { get; }

        public Rig(string? document = null)
        {
            Journal = new PolicyJournal(Machine, LabId, () => document, saved => Saved = saved);
        }
    }

    private static Overlay Lock(string language) => new() { Kind = Overlay.Types.Kind.Lock, SessionId = "l1", Message = "Eyes on me", Language = language };

    [Theory]
    [InlineData("uk", "uk")]
    [InlineData("ru", "ru")]
    [InlineData("en", "en")]
    [InlineData("", "en")]
    [InlineData("de", "en")]
    [InlineData("UK", "uk")]
    public void A_lock_keeps_the_language_it_arrived_with_and_anything_unknown_is_english(string sent, string kept)
    {
        var rig = new Rig();

        rig.Journal.SetOverlay(Lock(sent), Instance, Now);

        Assert.Equal(kept, rig.Journal.Snapshot().Overlay!.Language);
        Assert.Equal(kept, rig.Machine.Shown!.Language);
    }

    [Fact]
    public void The_language_survives_a_restart_and_a_file_from_an_older_build_is_english()
    {
        var first = new Rig();
        first.Journal.SetOverlay(Lock("ru"), Instance, Now);
        var document = JsonStore.Serialize(first.Saved!, PolicyDocument.Migrations);
        Assert.Contains("\"language\": \"ru\"", document);

        var rebooted = new Rig(document);
        rebooted.Journal.Start(Now + 60);
        Assert.Equal("ru", rebooted.Machine.Shown!.Language);

        // An older build wrote no language at all: the lock comes back, in English.
        var node = System.Text.Json.Nodes.JsonNode.Parse(document)!;
        Assert.True(node["policy"]!["overlay"]!.AsObject().Remove("language"));
        var older = new Rig(node.ToJsonString());
        older.Journal.Start(Now + 60);
        Assert.NotNull(older.Machine.Shown);
        Assert.Null(older.Machine.Shown!.Language);
        Assert.Equal("en", ProductLanguageOf(older.Machine.Shown.Language));
    }

    [Fact]
    public void Time_is_up_is_written_in_the_exams_language()
    {
        var rig = new Rig();
        rig.Journal.SetExam(new ExamMode { SessionId = "e1", Active = true, UntilSeconds = 600, Language = "uk" }, Instance, Now);
        Assert.Equal("uk", rig.Journal.Snapshot().Exam!.Language);

        rig.Journal.Tick(Now + 600);

        var held = rig.Journal.Snapshot().Overlay!;
        Assert.Equal("uk", held.Language);
        Assert.Equal(OverlayText.TimeUpMessage("uk"), held.Message);
        Assert.NotEqual(OverlayText.TimeUpMessage("en"), held.Message);
    }

    [Fact]
    public void The_words_follow_the_language_asked_for_never_the_process_culture()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-UA");

            Assert.Equal("Until the teacher unlocks it", OverlayText.CountdownText(null));
            Assert.Equal("Доки викладач не розблокує", OverlayText.CountdownText(null, "uk"));
            Assert.Equal("Пока преподаватель не разблокирует", OverlayText.CountdownText(null, "ru"));
            Assert.Equal("notepad.exe не дозволено під час іспиту", OverlayText.NotAllowedText("notepad.exe", "uk"));
            Assert.Equal("Time is up. Please take your hands off the keyboard.", OverlayText.TimeUpMessage("fr"));
            Assert.Contains("Notepad.exe", OverlayText.ClosingText(["Notepad.exe"], 10, "ru"));
            Assert.NotEqual(OverlayText.ClosingText(["Notepad.exe"], 10, "en"), OverlayText.ClosingText(["Notepad.exe"], 10, "ru"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("ru")]
    [InlineData("en")]
    public void The_lock_and_the_strip_compose_in_every_language(string language)
    {
        using var lockPicture = OverlayText.Compose(new OverlayContent(string.Empty, "PC-07", 125, language), 1280, 720);
        Assert.Equal(1280, lockPicture.Width);

        var strip = new ExamStripContent("Variant 2", 2537, OverlayText.NotAllowedText("notepad.exe", language), language);
        var (width, height) = OverlayText.StripSize(1920, 1080, strip);
        using var stripPicture = OverlayText.ComposeStrip(strip, width, height);
        Assert.Equal(width, stripPicture.Width);
    }

    private static string ProductLanguageOf(string? code) => LabControl.Shared.Localization.ProductLanguages.Of(code).Code;
}
