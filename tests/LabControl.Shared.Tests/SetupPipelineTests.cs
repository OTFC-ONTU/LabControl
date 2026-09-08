using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class SetupPipelineTests
{
    [Fact]
    public void Dry_run_never_applies_any_step()
    {
        var first = new Step();
        var second = new Step();
        var result = SetupPipeline.Run([first, second], true);
        Assert.All(result, row => Assert.Equal(SetupStepStatus.Needed, row.Status));
        Assert.Equal(0, first.Applies + second.Applies);
    }

    [Fact]
    public void Repair_checks_again_and_skips_confirmed_work()
    {
        var step = new Step();
        Assert.True(SetupPipeline.Run([step], false).Single().Applied);
        Assert.False(SetupPipeline.Run([step], false).Single().Applied);
        Assert.Equal(1, step.Applies);
    }

    [Fact]
    public void Unconfirmed_write_stops_pipeline()
    {
        var bad = new Step { ConfirmWrite = false };
        var later = new Step();
        Assert.Equal(SetupStepStatus.Conflict, SetupPipeline.Run([bad, later], false).Single().Status);
        Assert.Equal(0, later.Checks);
    }

    [Fact]
    public void Native_failure_is_not_exposed_and_stops_pipeline()
    {
        var bad = new Step { Fail = true };
        var later = new Step();
        var result = SetupPipeline.Run([bad, later], false).Single();
        Assert.DoesNotContain("sensitive-native-detail", result.Detail);
        Assert.Equal(SetupStepStatus.Conflict, result.Status);
        Assert.Equal(0, later.Checks);
    }

    [Theory]
    [InlineData("--uninstall", "--rekey")]
    [InlineData("--remove-student")]
    [InlineData("--create-student", "--no-student")]
    [InlineData("--number", "31")]
    [InlineData("--number", "0")]
    [InlineData("--dry-run", "--dry-run")]
    [InlineData("--payload")]
    [InlineData("--unknown")]
    public void Invalid_arguments_are_refused(params string[] args) => Assert.Throws<ArgumentException>(() => SetupArguments.Parse(args));

    private sealed class Step : ISetupStep
    {
        public string Name => "Test";
        public int Checks;
        public int Applies;
        public bool ConfirmWrite = true;
        public bool Fail;
        public SetupCheck Check() { Checks++; return new(Applies > 0 && ConfirmWrite ? SetupStepStatus.AlreadyDone : SetupStepStatus.Needed); }
        public void Apply() { Applies++; if (Fail) throw new IOException("sensitive-native-detail"); }
    }
}
