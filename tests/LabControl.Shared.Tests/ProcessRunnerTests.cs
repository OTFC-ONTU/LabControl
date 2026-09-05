using Xunit;

using System.Diagnostics;
using LabControl.Shared.Jobs;

namespace LabControl.Shared.Tests;

/// <summary>
/// The supervision every script gets (PROTOCOL <c>run_script</c>, ROADMAP M2): lines
/// streamed as they appear, the exit code kept, a silent process killed at the inactivity
/// timeout. Driven with <c>sh</c> here; the agent drives it with powershell.exe and cmd.exe.
/// </summary>
public sealed class ProcessRunnerTests
{
    private static ProcessStartInfo Sh(string script) => new("/bin/sh", ["-c", script]);

    [Fact]
    public async Task A_hundred_lines_and_exit_3_arrive_as_a_hundred_lines_and_exit_3()
    {
        var lines = new List<string>();
        var outcome = await ProcessRunner.RunAsync(
            Sh("i=1; while [ $i -le 100 ]; do echo \"line $i of 100\"; i=$((i+1)); done; exit 3"),
            TimeSpan.FromSeconds(10),
            line => { lock (lines) { lines.Add(line); } return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, outcome.ExitCode);
        Assert.False(outcome.TimedOut);
        Assert.False(outcome.Ok);
        Assert.Equal(100, lines.Count);
        Assert.Equal("line 1 of 100", lines[0]);
        Assert.Equal("line 100 of 100", lines[^1]);
        Assert.Equal(100, outcome.Lines);
    }

    [Fact]
    public async Task Stderr_lines_are_marked_and_a_clean_exit_is_ok()
    {
        var lines = new List<string>();
        var outcome = await ProcessRunner.RunAsync(
            Sh("echo out; echo err 1>&2; exit 0"),
            TimeSpan.FromSeconds(10),
            line => { lock (lines) { lines.Add(line); } return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Ok);
        Assert.Contains("out", lines);
        Assert.Contains(ProcessRunner.StderrPrefix + "err", lines);
    }

    [Fact]
    public async Task A_process_that_goes_silent_is_killed_at_the_inactivity_timeout_with_its_children()
    {
        var lines = new List<string>();
        var watch = Stopwatch.StartNew();
        var outcome = await ProcessRunner.RunAsync(
            Sh("echo hanging now; sleep 300 & sleep 300; echo never"),
            TimeSpan.FromSeconds(1),
            line => { lock (lines) { lines.Add(line); } return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.TimedOut);
        Assert.False(outcome.Ok);
        Assert.Equal(["hanging now"], lines);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Output_keeps_the_process_alive_past_the_timeout()
    {
        // Eight lines, 400 ms apart, with a 1 s inactivity timeout: never silent long enough.
        var lines = 0;
        var outcome = await ProcessRunner.RunAsync(
            Sh("i=1; while [ $i -le 8 ]; do echo tick; sleep 0.4; i=$((i+1)); done; exit 0"),
            TimeSpan.FromSeconds(1),
            _ => { Interlocked.Increment(ref lines); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Ok, $"exit {outcome.ExitCode}, timed out {outcome.TimedOut}");
        Assert.Equal(8, lines);
    }

    [Fact]
    public async Task Cancelling_the_agent_kills_the_process_and_says_so()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var outcome = await ProcessRunner.RunAsync(
            Sh("sleep 300"),
            TimeSpan.FromSeconds(30),
            _ => Task.CompletedTask,
            cancel.Token);

        Assert.True(outcome.Cancelled);
        Assert.False(outcome.TimedOut);
    }
}
