using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbCurrentRetailRadiusSoundScheduleTests
{
    [Fact]
    public void OutsideRadiusPreservesOverdueDeadlineAndRandomState()
    {
        var schedule = new EnbCurrentRetailRadiusSoundSchedule(100, 40000, 90000);
        uint rng = 1;
        Assert.False(schedule.TryAttempt(200, false, () => Assert.Fail("Outside"), ref rng));
        Assert.Equal(100u, schedule.NextDeadline);
        Assert.Equal(1u, rng);
        Assert.True(schedule.TryAttempt(200, true, () => { }, ref rng));
        Assert.Equal(0x41c67ea6u, rng);
        Assert.Equal(200u + 69087u, schedule.NextDeadline);
    }

    [Theory]
    [InlineData(99u, false)]
    [InlineData(100u, true)]
    [InlineData(0x80000063u, true)]
    [InlineData(0x80000064u, false)]
    public void DeadlineUsesUnsignedHalfRange(uint now, bool expected)
    {
        var schedule = new EnbCurrentRetailRadiusSoundSchedule(100, 1, 1);
        uint rng = 1;
        Assert.Equal(expected, schedule.TryAttempt(now, true, () => { }, ref rng));
        Assert.Equal(expected ? 1ul : 0ul, schedule.AttemptCount);
    }

    [Fact]
    public void NextDeadlineWrapsAndDoesNotFireEarlyAcrossWrap()
    {
        var schedule = new EnbCurrentRetailRadiusSoundSchedule(uint.MaxValue - 5, 10, 10);
        uint rng = 7;
        Assert.True(schedule.TryAttempt(uint.MaxValue - 5, true, () => { }, ref rng));
        Assert.Equal(4u, schedule.NextDeadline);
        Assert.False(schedule.TryAttempt(3, true, () => Assert.Fail("Early"), ref rng));
        Assert.True(schedule.TryAttempt(4, true, () => { }, ref rng));
        Assert.Equal(7u, rng); // Equal bounds do not consume native RNG.
    }

    [Fact]
    public void DeclinedAttemptStillAdvancesAndDoesNotCatchUpMissedPeriods()
    {
        var schedule = new EnbCurrentRetailRadiusSoundSchedule(100, 40, 40);
        uint rng = 0;
        int calls = 0;
        Assert.True(schedule.TryAttempt(1000, true, () => calls++, ref rng));
        Assert.Equal(1040u, schedule.NextDeadline);
        Assert.Equal(1, calls); // Callback need not produce a voice.
        Assert.False(schedule.TryAttempt(1000, true, () => calls++, ref rng));
    }

    [Fact]
    public void CallbackRunsBeforeIntervalConsumptionAndSeesPreviousDeadline()
    {
        var schedule = new EnbCurrentRetailRadiusSoundSchedule(100, 40000, 90000);
        uint rng = 1;
        uint expectedState = 1;
        EnbCurrentRetailIntegerRandom.Next(ref expectedState, 0, 100);
        int expectedInterval = EnbCurrentRetailIntegerRandom.Next(ref expectedState, 40000, 90000);
        schedule.TryAttempt(100, true, () =>
        {
            Assert.Equal(100u, schedule.NextDeadline);
            EnbCurrentRetailIntegerRandom.Next(ref rng, 0, 100);
        }, ref rng);
        Assert.Equal(expectedState, rng);
        Assert.Equal(100u + (uint)expectedInterval, schedule.NextDeadline);
    }

    [Fact]
    public void ClipCompletionDoesNotRetireRepeatingOwner()
    {
        var schedule = new EnbCurrentRetailRadiusSoundSchedule(0, 40, 40);
        uint rng = 1;
        int starts = 0;
        bool clipPlaying = false;
        Action attempt = () => { starts++; clipPlaying = true; };
        schedule.TryAttempt(0, true, attempt, ref rng);
        clipPlaying = false; // Backend completion occurs independently.
        Assert.False(schedule.TryAttempt(39, true, attempt, ref rng));
        Assert.False(clipPlaying);
        Assert.True(schedule.TryAttempt(40, true, attempt, ref rng));
        Assert.True(clipPlaying);
        Assert.True(schedule.TryAttempt(80, true, attempt, ref rng)); // Still playing is no gate.
        Assert.Equal(3, starts);
        schedule.Retire();
        Assert.False(schedule.TryAttempt(120, true, attempt, ref rng));
        Assert.True(schedule.IsRetired);
        Assert.Equal(3ul, schedule.AttemptCount);
    }

    [Fact]
    public void TwoOwnersConsumeCallerSharedStreamWithoutPerOwnerReseeding()
    {
        var first = new EnbCurrentRetailRadiusSoundSchedule(0, 40000, 90000);
        var second = new EnbCurrentRetailRadiusSoundSchedule(0, 40000, 90000);
        uint rng = 1, expectedState = 1;
        uint firstDelay = (uint)EnbCurrentRetailIntegerRandom.Next(ref expectedState, 40000, 90000);
        uint secondDelay = (uint)EnbCurrentRetailIntegerRandom.Next(ref expectedState, 40000, 90000);
        first.TryAttempt(0, true, () => { }, ref rng);
        second.TryAttempt(0, true, () => { }, ref rng);
        Assert.Equal(firstDelay, first.NextDeadline);
        Assert.Equal(secondDelay, second.NextDeadline);
        Assert.Equal(expectedState, rng);
    }

    [Fact]
    public void ZeroIntervalRemainsDueForEachActualUpdate()
    {
        var schedule = new EnbCurrentRetailRadiusSoundSchedule(0, 0, 0);
        uint rng = 7;
        Assert.True(schedule.TryAttempt(0, true, () => { }, ref rng));
        Assert.True(schedule.TryAttempt(0, true, () => { }, ref rng));
        Assert.Equal(2ul, schedule.AttemptCount);
        Assert.Equal(7u, rng);
    }
}
