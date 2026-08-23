using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class UpdateCheckScheduleTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsAutomaticCheckDue_WithoutPreviousAttempt_ReturnsTrue() =>
        Assert.True(UpdateCheckSchedule.IsAutomaticCheckDue(Now, null));

    [Theory]
    [InlineData(0, false)]
    [InlineData(23.999, false)]
    [InlineData(24, true)]
    [InlineData(48, true)]
    public void IsAutomaticCheckDue_UsesTwentyFourHourInterval(double elapsedHours, bool expected)
    {
        Assert.Equal(
            expected,
            UpdateCheckSchedule.IsAutomaticCheckDue(Now, Now.AddHours(-elapsedHours)));
    }

    [Fact]
    public void IsAutomaticCheckDue_AfterClockRollback_ReturnsTrue() =>
        Assert.True(UpdateCheckSchedule.IsAutomaticCheckDue(Now, Now.AddHours(1)));
}
