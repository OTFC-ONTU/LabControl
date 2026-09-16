using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>The hard-limit clamp and the session arithmetic (M6, D-69).</summary>
public sealed class ClassroomPolicyTests
{
    private const long Now = 1_800_000_000;
    private static readonly long Hour = (long)Defaults.LockHardLimit.TotalSeconds;

    [Fact]
    public void Thirty_days_becomes_one_hour_and_no_proposal_is_the_ceiling()
    {
        Assert.Equal(Now + Hour, ClassroomPolicy.Clamp(Now + 30 * 24 * 3600, Defaults.LockHardLimit, Now));
        Assert.Equal(Now + Hour, ClassroomPolicy.Clamp(0, Defaults.LockHardLimit, Now));
        Assert.Equal(Now + Hour, ClassroomPolicy.Clamp(-5, Defaults.LockHardLimit, Now));
        Assert.Equal(Now + 60, ClassroomPolicy.Clamp(Now + 60, Defaults.LockHardLimit, Now));
        Assert.Equal(Now + 4 * 3600, ClassroomPolicy.Clamp(Now + 30 * 24 * 3600, Defaults.ExamHardLimit, Now));
    }

    [Fact]
    public void The_clamp_never_overflows()
    {
        Assert.Equal(long.MaxValue, ClassroomPolicy.Clamp(long.MaxValue, Defaults.LockHardLimit, long.MaxValue - 10));
        Assert.Equal(long.MaxValue, ClassroomPolicy.Clamp(0, Defaults.LockHardLimit, long.MaxValue));
        Assert.Equal(long.MaxValue - 5, ClassroomPolicy.Clamp(long.MaxValue - 5, Defaults.LockHardLimit, long.MaxValue - 10));
        Assert.Equal((long)TimeSpan.MaxValue.TotalSeconds, ClassroomPolicy.Clamp(long.MaxValue, TimeSpan.MaxValue, 0));
        Assert.Equal(long.MaxValue, ClassroomPolicy.Clamp(long.MaxValue, TimeSpan.MaxValue, long.MaxValue - 1));
        Assert.Equal(Now, ClassroomPolicy.Clamp(long.MaxValue, TimeSpan.Zero, Now));
        Assert.Equal(Now, ClassroomPolicy.Clamp(0, TimeSpan.FromSeconds(-1), Now));
    }

    [Fact]
    public void A_session_is_valid_until_the_earlier_of_until_and_its_hard_limit()
    {
        var untilFirst = new OverlaySession { UntilUnix = Now + 10, HardLimitUnix = Now + Hour };
        Assert.True(untilFirst.IsStillValid(Now + 9));
        Assert.False(untilFirst.IsStillValid(Now + 10));
        Assert.Equal(Now + 10, untilFirst.DeadlineUnix);

        var limitFirst = new ExamSession { UntilUnix = Now + Hour, HardLimitUnix = Now + 10 };
        Assert.True(limitFirst.IsStillValid(Now + 9));
        Assert.True(limitFirst.Ended(Now + 10));
        Assert.Equal(Now + 10, limitFirst.DeadlineUnix);

        var untilLifted = new InternetSession { UntilUnix = 0, HardLimitUnix = Now + 10 };
        Assert.True(untilLifted.IsStillValid(Now + 9));
        Assert.False(untilLifted.IsStillValid(Now + 10));
        Assert.Equal(Now + 10, untilLifted.DeadlineUnix);

        // No hard limit at all is no session at all.
        Assert.False(new OverlaySession { UntilUnix = Now + 10 }.IsStillValid(Now));
    }

    [Fact]
    public void The_policy_lists_its_sessions_and_knows_when_it_is_empty()
    {
        var policy = new ClassroomPolicy();
        Assert.True(policy.IsEmpty);
        Assert.Empty(policy.Sessions);

        policy.Overlay = new OverlaySession { Kind = Overlay.Types.Kind.Lock, HardLimitUnix = Now + 1 };
        policy.Internet = new InternetSession { HardLimitUnix = Now + 2 };
        Assert.False(policy.IsEmpty);
        Assert.Equal(2, policy.Sessions.Count());
    }
}
