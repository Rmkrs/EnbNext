using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR11MotionPresentationTests
{
    private static EnbSceneCameraStateSnapshot Settings => new(1, 7, false, null, null, null, null, false,
        [new(0, 0, -1, 0, MathF.PI / 2, 0), new(1, 0, 2, 0, MathF.PI / 2, 0)],
        75, new(6, 600000), []);

    private static EnbSceneTransformSnapshot Pose(Vector3 position = default, Quaternion? orientation = null,
        Vector3 rotation = default) => new(1000, position, orientation ?? Quaternion.Identity,
            Vector3.Zero, 0, rotation, Vector3.Transform(Vector3.UnitX, orientation ?? Quaternion.Identity));

    private static EnbNormalCameraFrame Frame(EnbNormalCameraController controller, EnbSceneTransformSnapshot pose,
        double dt, EnbSceneCameraStateSnapshot? settings = null)
    {
        Assert.True(controller.TryResolveFrame(settings ?? Settings, pose, 1, dt, new(800, 600, 1), out var frame));
        return frame;
    }

    [Fact]
    public void ThirdPersonFollowsPositionWithRetailDistanceDependentRate()
    {
        var controller = new EnbNormalCameraController();
        // Native79BFC0 retains the tiny trig residual; exact pose words have their own native oracle.
        Assert.InRange(Vector3.Distance(new(-150,0,0),Frame(controller,Pose(),0).Camera.Position),0,.00001f);
        var frame = Frame(controller, Pose(new(100, 0, 0)), 20);
        Assert.Equal(-147.2f, frame.Camera.Position.X, 4); // 100 * (1+.004*100)*.001*20
        Assert.Equal(0, frame.Camera.Position.Y);
    }

    [Fact]
    public void CameraOrientationLagsTurningAndAcceptsEquivalentQuaternionSigns()
    {
        var settings = Settings with { Modes = [new(0, 0, 0, 0, 1, 0), new(1, 0, 0, 0, 1, 0)] };
        var controller = new EnbNormalCameraController();
        Frame(controller, Pose(), 0, settings);
        Quaternion desired = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .5f);
        var actual = Frame(controller, Pose(orientation: desired), 20, settings).Camera.WorldOrientation;
        Assert.True(Quaternion.Dot(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .02f), actual) > .999999f);
        var negative = new Quaternion(-desired.X, -desired.Y, -desired.Z, -desired.W);
        Assert.True(Quaternion.Dot(actual, Frame(controller, Pose(orientation: negative), 0, settings).Camera.WorldOrientation) > .999999f);
    }

    [Fact]
    public void FirstPersonHandoffAndLargeDiscontinuitiesDoNotEaseAcrossSpace()
    {
        var controller = new EnbNormalCameraController();
        Frame(controller, Pose(), 0);
        Assert.Equal(9850, Frame(controller, Pose(new(10000, 0, 0)), 16).Camera.Position.X);
        controller.ResetMotion();
        Assert.Equal(-150, Frame(controller, Pose(), 0).Camera.Position.X);
        Assert.Equal(-50, Frame(controller, Pose(new(100, 0, 0)), 16, Settings with { AnchorRenderIdentity = 2 }).Camera.Position.X);
        controller.SelectFirstPerson(Settings);
        Assert.Equal(275, Frame(controller, Pose(new(200, 0, 0)), 16).Camera.Position.X);
        Assert.Equal(375, Frame(controller, Pose(new(300, 0, 0)), 16).Camera.Position.X);
    }

    [Fact]
    public void AngularInputBanksWhileLinearVelocityAndPitchAloneDoNot()
    {
        var pose = Pose(rotation: new(7, 9, 0)) with { Velocity = new(200, -100, 99) };
        Assert.True(EnbNormalCameraController.TryResolveNormalPlayerBankTarget(pose, 1, out float bank));
        Assert.Equal(0, bank);
        Assert.True(EnbNormalCameraController.TryResolveNormalPlayerBankTarget(pose with { Rotation = new(0, 0, .001f) }, 1, out bank));
        Assert.Equal(-.125f, bank, 6);
    }

    [Fact]
    public void ShipBankUsesRetailSlewedIncrementAndReturnsToLevel()
    {
        var bank = new EnbShipVisualBank();
        bank.Advance(-.5f, true, EnbShipVisualBank.ReferenceStepMilliseconds);
        Assert.Equal(-.02f, bank.Increment, 6);
        Assert.Equal(-.02f, bank.Angle, 6);
        bank.Advance(-.5f, true, EnbShipVisualBank.ReferenceStepMilliseconds);
        Assert.Equal(-.02f, bank.Increment, 6);
        Assert.Equal(-.04f, bank.Angle, 6);
        for (int i = 0; i < 100; i++) bank.Advance(-.5f, true, 16);
        Assert.InRange(bank.Angle, -.52f, -.48f);
        float before = bank.Angle;
        bank.Advance(0, true, 0);
        Assert.Equal(before, bank.Angle);
        for (int i = 0; i < 100; i++) bank.Advance(0, true, 16);
        Assert.InRange(bank.Angle, -.02001f, .02001f);
        Assert.Equal(0, bank.Increment);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(165)]
    public void BankSpeedDoesNotDependOnDisplayRefresh(int hz)
    {
        var bank = new EnbShipVisualBank();
        var reference = new EnbShipVisualBank();
        for (int second = 0; second < 3; second++)
        {
            float target = second == 0 ? -.6f : second == 1 ? .6f : 0;
            for (int i = 0; i < hz; i++) bank.Advance(target, true, 1000.0 / hz);
            for (int i = 0; i < 60; i++) reference.Advance(target, true, 1000.0 / 60);
            Assert.InRange(Math.Abs(bank.Angle - reference.Angle), 0, .00001f);
        }
    }

    [Fact]
    public void ConstantSpeedSubTickDisplacementStaysContinuousAtIrregularFrames()
    {
        var world = EnbR11FlightTests.FlightWorld();
        world.ApplyLocalFlightControls(7, 1000, null, null, 1);
        world.TrySampleObject(7, 3000, out var previous);
        uint time = 3000;
        for (int i = 0; i < 180; i++)
        {
            uint dt = new uint[] { 8, 17, 23, 11, 31, 16 }[i % 6];
            time += dt;
            world.TrySampleObject(7, time, out var next);
            Assert.InRange(Vector3.Distance(next.Position, previous.Position), dt * .15f - .001f, dt * .15f + .001f);
            previous = next;
        }
    }

    [Fact]
    public void CameraFollowsMovingShipEveryFrameWithoutLockingItToTheScreen()
    {
        var controller = new EnbNormalCameraController();
        var previous = Frame(controller, Pose(), 0).Camera;
        float distance = 0;
        for (int i = 0; i < 100; i++)
        {
            double dt = new[] { 8d, 17, 23, 11, 31, 16 }[i % 6];
            distance += (float)dt * .15f;
            var camera = Frame(controller, Pose(new(distance, 0, 0)), dt).Camera;
            Assert.True(camera.Position.X > previous.Position.X);
            Assert.True(camera.Position.X < distance - 150);
            previous = camera;
        }
    }

    [Fact]
    public void VisualBankFollowsClientObjectAcrossCompositionBaseAndSpatialChanges()
    {
        var world = EnbR11FlightTests.FlightWorld();
        world.ApplyLocalFlightControls(7, 1000, 1, null, null);
        var builder = new EnbPresentationSnapshotBuilder();
        var runtime = new EnbShipVisualBankRuntime();
        EnbSceneSnapshot PlayerFrame(uint time)
        {
            var snapshot = builder.Build(world, null, time);
            return snapshot with { Objects = snapshot.Objects.Select(o =>
            {
                var pose = o.Spatial!.Transform;
                var matrix = EnbRetailCameraQuaternionBasis.Create(pose.Orientation);
                matrix.Translation = pose.Position;
                return o with { ObjectType = 1, Spatial = o.Spatial with
                    { Transform = pose with { ProviderTransform = matrix } } };
            }).ToImmutableArray() };
        }
        runtime.ApplyFrame(PlayerFrame(1000), 0);
        var raw = PlayerFrame(1016);
        var physical = Assert.Single(raw.Objects).Spatial!.Transform!;
        var visualScene = runtime.ApplyFrame(raw, 16);
        var visual = Assert.Single(visualScene.Objects).Spatial!.Transform!;
        Assert.Null(Assert.Single(raw.Objects).ShipVisualBankRadians);
        Assert.NotEqual(0f, Assert.Single(visualScene.Objects).ShipVisualBankRadians);
        Assert.NotEqual(physical.Orientation, visual.Orientation);
        Assert.True(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(physical, out var physicalMatrix, out _));
        Assert.True(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(visual, out var visualMatrix, out _));
        Assert.NotEqual(physicalMatrix, visualMatrix);
        Assert.Equal(physical.Position, visual.Position);
        Assert.Equal(physical.Rotation, visual.Rotation);
        Assert.Equal(physical.OrientationColumn0, visual.OrientationColumn0);
        Assert.Equal(physical, Assert.Single(raw.Objects).Spatial!.Transform);
        Assert.Equal(visual, Assert.Single(runtime.ApplyFrame(raw, 16).Objects).Spatial!.Transform);

        var replaced = raw with { Objects = raw.Objects.Select(o => o with { VisualGeneration = o.VisualGeneration + 1,
            BaseAssetMaterializationRevision=o.BaseAssetMaterializationRevision+1 }).ToImmutableArray() };
        Assert.Equal(visual, Assert.Single(runtime.ApplyFrame(replaced, 16).Objects).Spatial!.Transform);
        var moved=Assert.Single(replaced.Objects);
        var position=physical.Position+new Vector3(10000,20000,30000);
        var matrix=physical.ProviderTransform!.Value;matrix.Translation=position;
        moved=moved with {Spatial=moved.Spatial! with {Transform=physical with {Position=position,ProviderTransform=matrix}}};
        var movedVisual=Assert.Single(runtime.ApplyFrame(raw with {Objects=[moved]},16).Objects);
        Assert.Equal(Assert.Single(visualScene.Objects).ShipVisualBankRadians,movedVisual.ShipVisualBankRadians);
        Assert.Equal(position,movedVisual.Spatial!.Transform.Position);
        var organic=moved with {ShipPresentation=moved.ShipPresentation! with {IsOrganic=true}};
        Assert.Null(Assert.Single(runtime.ApplyFrame(raw with {Objects=[organic]},16).Objects).ShipVisualBankRadians);
        Assert.Equal(movedVisual.ShipVisualBankRadians,Assert.Single(runtime.ApplyFrame(raw with {Objects=[moved]},16).Objects).ShipVisualBankRadians);
        runtime.ApplyFrame(raw with {Objects=[]},16);
        Assert.Equal(physical,Assert.Single(runtime.ApplyFrame(raw,16).Objects).Spatial!.Transform);
        runtime.ApplyFrame(PlayerFrame(1032),16);
        Assert.Equal(physical,Assert.Single(runtime.ApplyFrame(raw with {Objects=[Assert.Single(raw.Objects) with {RenderIdentity=999}]},16).Objects).Spatial!.Transform);
        runtime.Clear();
        Assert.Equal(physical, Assert.Single(runtime.ApplyFrame(raw, 16).Objects).Spatial!.Transform);
    }

    [Fact]
    public void TypeZeroBanksOnlyWhileItsHeadingGoalRequiresTurning()
    {
        var scene=new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(),null,1000);
        var actor=Assert.Single(scene.Objects) with {ObjectType=0,IsLocalShip=false};
        var pose=Pose(rotation:new(0,0,.001f)) with {ProviderTransform=Matrix4x4.Identity};
        actor=actor with {Spatial=actor.Spatial! with {Transform=pose}};
        scene=scene with {Objects=[actor]};
        var runtime=new EnbShipVisualBankRuntime();
        runtime.ApplyFrame(scene,0);
        Assert.Equal(0,Assert.Single(runtime.ApplyFrame(scene with {ClientTime=1500},500).Objects).ShipVisualBankRadians);
        actor=actor with {Spatial=actor.Spatial! with {Transform=pose with {BankHeadingTarget=1.7f}}};
        var turning=Assert.Single(runtime.ApplyFrame(scene with {ClientTime=2000,Objects=[actor]},500).Objects);
        Assert.True(turning.ShipVisualBankRadians<-.15f);
        Assert.NotEqual(pose.ProviderTransform,turning.Spatial!.Transform.ProviderTransform);
        var stopped=runtime.ApplyFrame(scene with {ClientTime=7000},5000);
        Assert.InRange(Assert.Single(stopped.Objects).ShipVisualBankRadians!.Value,-.02501f,.02501f);
    }

    [Fact]
    public void OrganicAndNonPlayerObjectsRetainTheirAuthoredMotion()
    {
        var world = EnbR11FlightTests.FlightWorld();
        world.ApplyLocalFlightControls(7, 1000, 1, null, null);
        var scene = new EnbPresentationSnapshotBuilder().Build(world, null, 1100);
        var original = Assert.Single(scene.Objects) with { ObjectType = 1 };
        var organic = original with { ShipPresentation = original.ShipPresentation! with { IsOrganic = true } };
        var runtime = new EnbShipVisualBankRuntime();
        var snapshot = scene with { Objects = [organic, original with { RenderIdentity = 99, ObjectType = 25 }] };
        runtime.ApplyFrame(snapshot with { ClientTime = 1084 }, 0);
        Assert.True(snapshot.Objects.SequenceEqual(runtime.ApplyFrame(snapshot, 16).Objects));
    }
}
