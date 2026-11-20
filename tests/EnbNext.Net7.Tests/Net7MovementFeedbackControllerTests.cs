using System.Numerics;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Timing;

namespace EnbNext.Net7.Tests;

public sealed class Net7MovementFeedbackControllerTests
{
    [Fact]
    public void SteeringUsesIndependentStrict250MillisecondThrottlesAndZeroAlwaysResets()
    {
        var clock = new ManualClock(1000);
        var controller = new Net7MovementFeedbackController(clock);

        Assert.True(controller.TryAcceptHorizontal(1f));
        clock.Now = 1250;
        Assert.False(controller.TryAcceptHorizontal(1f));
        Assert.True(controller.TryAcceptVertical(1f));
        Assert.True(controller.TryAcceptHorizontal(0f));
        clock.Now = 1500;
        Assert.False(controller.TryAcceptHorizontal(1f));
        clock.Now = 1501;
        Assert.True(controller.TryAcceptHorizontal(1f));
    }

    [Fact]
    public void SectorResetClearsHorizontalSteeringThrottleState()
    {
        var clock = new ManualClock(1000);
        var controller = new Net7MovementFeedbackController(clock);

        Assert.True(controller.TryAcceptHorizontal(1f));
        clock.Now = 1100;
        Assert.False(controller.TryAcceptHorizontal(0.5f));

        controller.ResetSectorState();

        // A retail sector redirect installs a fresh role-3 Connection, whose
        // horizontal throttle timestamp starts from constructor state rather
        // than inheriting the previous sector's accepted command.
        Assert.True(controller.TryAcceptHorizontal(0.5f));
    }

    [Fact]
    public void SectorResetClearsVerticalSteeringThrottleState()
    {
        var clock = new ManualClock(1000);
        var controller = new Net7MovementFeedbackController(clock);

        Assert.True(controller.TryAcceptVertical(-1f));
        clock.Now = 1100;
        Assert.False(controller.TryAcceptVertical(-0.375f));

        controller.ResetSectorState();

        // It owns a separate Connection+0xCFA8 timestamp. The fresh
        // role-3 connection installed for the next sector starts it from
        // constructor state rather than carrying the prior sector forward.
        Assert.True(controller.TryAcceptVertical(-0.375f));
    }

    [Fact]
    public void SectorResetPreservesMasterOwnedPositionSnapshotCaches()
    {
        var controller = new Net7MovementFeedbackController(new ManualClock(0));
        var sample = new Net7PositionFeedbackSample(new Vector3(1, 2, 3), Vector3.UnitX, 7);

        Assert.True(controller.TrySelectSnapshot(sample, out _));
        Assert.True(controller.TrySelectSnapshot(sample, out _));
        Assert.True(controller.TrySelectSnapshot(sample, out _));
        Assert.False(controller.TrySelectSnapshot(sample, out _));

        controller.ResetSectorState();

        // Caches live with the master/control MVAS worker. Replacing the
        // role-3 Gameplay connection at a sector redirect must not manufacture a
        // fresh snapshot baseline/countdown.
        Assert.False(controller.TrySelectSnapshot(sample, out _));
    }

    [Fact]
    public void PositionSnapshotStartsFromRawZeroCachesWithoutSyntheticBaseline()
    {
        var controller = new Net7MovementFeedbackController(new ManualClock(0));
        var positionOnly = new Net7PositionFeedbackSample(new Vector3(1, 2, 3), Vector3.Zero, 0);

        // Retail's caches begin as raw zero DWORDs. With a zero selected-state
        // triplet and nonzero XYZ, the first eligible change is position-only.
        Assert.True(controller.TrySelectSnapshot(positionOnly, out bool full));
        Assert.False(full);
    }

    [Fact]
    public void LegacyPositionFeedbackSpeedPreservesLockSpeedMirrorQuirk()
    {
        var controller = new Net7MovementFeedbackController(new ManualClock(0));

        controller.ObserveLegacyPositionFeedbackKinematics(new Vector3(1.2349f, 0, 0), Vector3.UnitX, lockSpeed: false);
        Assert.Equal(1234, controller.LegacyPositionFeedbackSpeed);

        controller.ObserveLegacyPositionFeedbackKinematics(new Vector3(-2.5f, 0, 0), Vector3.UnitX, lockSpeed: false);
        Assert.Equal(-2500, controller.LegacyPositionFeedbackSpeed);

        controller.ObserveLegacyPositionFeedbackKinematics(new Vector3(9f, 0, 0), Vector3.UnitX, lockSpeed: true);
        Assert.Equal(-2500, controller.LegacyPositionFeedbackSpeed);

        // LockSpeed wrote the hidden retail comparison/display field to 100. A
        // first normal numeric value of exactly 100 therefore skips the mirror.
        controller.ObserveLegacyPositionFeedbackKinematics(new Vector3(0.1f, 0, 0), Vector3.UnitX, lockSpeed: false);
        Assert.Equal(-2500, controller.LegacyPositionFeedbackSpeed);

        controller.ObserveLegacyPositionFeedbackKinematics(new Vector3(0.1019f, 0, 0), Vector3.UnitX, lockSpeed: false);
        Assert.Equal(101, controller.LegacyPositionFeedbackSpeed);

        // 005821C0's __ftol stores the x87 integer indefinite for NaN or out-of-range speeds.
        controller.ObserveLegacyPositionFeedbackKinematics(new Vector3(float.NaN, 0, 0), Vector3.UnitX, lockSpeed: false);
        Assert.Equal(int.MinValue, controller.LegacyPositionFeedbackSpeed);
        controller.ObserveLegacyPositionFeedbackKinematics(new Vector3(0.1019f, 0, 0), Vector3.UnitX, lockSpeed: false);
        controller.ObserveLegacyPositionFeedbackKinematics(new Vector3(3e6f, 0, 0), Vector3.UnitX, lockSpeed: false);
        Assert.Equal(int.MinValue, controller.LegacyPositionFeedbackSpeed);
    }

    [Fact]
    public void SnapshotComparatorUsesOrientationPriorityPositionCompactAndTwoFullFollowups()
    {
        var controller = new Net7MovementFeedbackController(new ManualClock(0));
        var a = new Net7PositionFeedbackSample(new Vector3(1, 2, 3), Vector3.UnitX, 10);

        Assert.True(controller.TrySelectSnapshot(a, out bool full));
        Assert.True(full);
        Assert.True(controller.TrySelectSnapshot(a, out full));
        Assert.True(full);
        Assert.True(controller.TrySelectSnapshot(a, out full));
        Assert.True(full);
        Assert.False(controller.TrySelectSnapshot(a with { LegacyPositionFeedbackSpeed = 99 }, out full));

        var moved = a with { Position = new Vector3(4, 5, 6), LegacyPositionFeedbackSpeed = 99 };
        Assert.True(controller.TrySelectSnapshot(moved, out full));
        Assert.False(full);
        Assert.True(controller.TrySelectSnapshot(moved, out full));
        Assert.True(full);
        Assert.True(controller.TrySelectSnapshot(moved, out full));
        Assert.True(full);
        Assert.False(controller.TrySelectSnapshot(moved, out full));

        var rotated = moved with { OrientationColumn0 = Vector3.UnitY };
        Assert.True(controller.TrySelectSnapshot(rotated, out full));
        Assert.True(full);
    }

    [Fact]
    public void PositionFeedbackWireUsesCompactAndFullLayouts()
    {
        var sample = new Net7PositionFeedbackSample(new Vector3(1, 2, 3), new Vector3(4, 5, 6), -7);
        byte[] compact = Net7GameplayWire.BuildPositionFeedback(sample, false);
        byte[] full = Net7GameplayWire.BuildPositionFeedback(sample, true);

        Assert.Equal(16, compact.Length);
        Assert.Equal(-7, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(compact.AsSpan(12, 4)));
        Assert.Equal(28, full.Length);
        Assert.Equal(BitConverter.SingleToInt32Bits(4f), System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(full.AsSpan(12, 4)));
        Assert.Equal(-7, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(full.AsSpan(24, 4)));
    }

    private sealed class ManualClock(uint now) : INet7RuntimeClock
    {
        public uint Now { get; set; } = now;
        public uint GetMilliseconds() => Now;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
