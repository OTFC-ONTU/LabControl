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

    [Fact]
    public void Fixed_diagnostic_code_is_exposed_without_a_native_message()
    {
        var bad = new DiagnosticStep();
        var result = SetupPipeline.Run([bad], false).Single();
        Assert.Contains(nameof(SetupDiagnosticCode.HibernationRegistryReadFailed), result.Detail);
        Assert.DoesNotContain("sensitive-native-detail", result.Detail);
    }

    [Fact]
    public void Defender_diagnostic_code_is_exposed_without_a_native_message()
    {
        var result = SetupPipeline.Run([new DiagnosticStep(SetupDiagnosticCode.DefenderReadBackTimeout)], false).Single();
        Assert.Contains(nameof(SetupDiagnosticCode.DefenderReadBackTimeout), result.Detail);
        Assert.DoesNotContain("sensitive-native-detail", result.Detail);
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

    [Fact]
    public void Diagnostic_status_is_printed_as_hex_without_any_other_detail()
    {
        var error = new SetupDiagnosticException(SetupDiagnosticCode.DefenderProviderRejected, unchecked((int)0x800106BA));
        Assert.Equal("DefenderProviderRejected, status 0x800106BA", error.Describe());
        Assert.Equal("DefenderProviderRejected", new SetupDiagnosticException(SetupDiagnosticCode.DefenderProviderRejected).Describe());
        var result = SetupPipeline.Run([new DiagnosticStep(error)], false, _ => { });
        Assert.Contains("status 0x800106BA", Assert.Single(result).Detail);
    }

    private sealed class DiagnosticStep(SetupDiagnosticException error) : ISetupStep
    {
        public DiagnosticStep(SetupDiagnosticCode code = SetupDiagnosticCode.HibernationRegistryReadFailed) : this(new SetupDiagnosticException(code)) { }
        public string Name => "Test";
        public SetupCheck Check() => throw error;
        public void Apply() => throw new NotSupportedException();
    }
}
