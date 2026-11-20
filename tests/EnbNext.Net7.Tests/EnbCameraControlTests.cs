using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbCameraControlTests
{
    internal static EnbSceneCameraStateSnapshot Camera() => new(null, null, false, null, null, null, null, false,
        [new(0,0,0,0,MathF.PI/2,0), new(1,0,3,.1f,MathF.PI/2,.03f), new(2,1,4,0,MathF.PI/2,0)],
        75, new(1,1000000), []);
    internal static EnbSceneObjectSnapshot Ship(int state, ulong sequence)
    {
        var world = new Net7WorldState(); world.MaterializeCreate(new(23,1,1620,1,0,0,0));
        return new EnbPresentationSnapshotBuilder().Build(world,null,0).Objects[0] with
        {
            Spatial = new(EnbSceneSpatialProviderKind.Fixed, new(0,Vector3.Zero,Quaternion.Identity,new(10,0,0),10,Vector3.Zero,Vector3.UnitX),null),
            ShipPresentation = new(false,0,state,0,false,null,0,[],[]) { WarpChanges = [new(sequence,state,0,0)] }
        };
    }
    [Fact]
    public void ControlsClampRestoreAndSaveWithoutChangingOtherSlots()
    {
        var c = new EnbNormalCameraController(); var state = Camera();
        Assert.True(c.AdjustCamera(state,.25,0,20,false));
        Assert.Equal(-.5f,c.GetCurrentMode(state)!.HeadingRadians,5);
        c.AdjustCamera(state,0,100,20,false); Assert.Equal(1.4835299f,c.GetCurrentMode(state)!.TiltRadians,5);
        c.AdjustCamera(state,0,100,20,true); Assert.Equal(10,c.GetCurrentMode(state)!.Distance);
        c.AdjustCamera(state,0,-100,20,true); Assert.Equal(.5f,c.GetCurrentMode(state)!.Distance);
        c.RestoreCamera(); Assert.Equal(state.Modes[1],c.GetCurrentMode(state));
        c.AdjustCamera(state,.2,.2,20,false); var saved = c.GetCurrentMode(state)!;
        c.RememberSavedMode(saved); c.AdjustCamera(state,.3,.3,20,false); c.RestoreCamera();
        Assert.Equal(saved,c.GetCurrentMode(state));
        c.SelectNextThirdPersonMode(state); Assert.Equal(state.Modes[2],c.GetCurrentMode(state));
        c.SelectPreviousThirdPersonMode(state); Assert.Equal(saved,c.GetCurrentMode(state));
        c.SelectFirstPerson(state); Assert.False(c.AdjustCamera(state,1,1,20,true));
    }
    [Fact]
    public void SavedIniRoundTripsAnglesAndPreservesUnrelatedContent()
    {
        string input = "; keep me\r\n[Mode00]\r\nDistance=0\r\n[Mode01]\r\nHeading=0\r\nCustom=keep\r\n[Mode02]\r\nDistance=9\r\n";
        var mode = new EnbRetailCameraMode(1,.7f,5,.2f,1.4f,.03f);
        string output = EnbRetailCameraModeWriter.Update(input,mode);
        var loaded = EnbRetailCameraModeCatalog.ParseText(output).Modes[1];
        Assert.Equal(mode.Distance,loaded.Distance);
        Assert.Equal(mode.HeadingRadians,loaded.HeadingRadians,5);
        Assert.Equal(mode.TiltRadians,loaded.TiltRadians,5);
        Assert.Contains("Custom=keep",output); Assert.StartsWith("; keep me\r\n[Mode00]\r\nDistance=0",output);
        Assert.EndsWith("[Mode02]\r\nDistance=9\r\n",output);
    }
    [Fact]
    public void WarpViewsUseFixedDepartureThenChaseAndNeverChangeSavedMode()
    {
        var w = new EnbWarpCameraRuntime(new EnbRetailCameraRandomSource(() => 0x80000000u)); var ship = Ship(1,1);
        var first = w.Update(ship,0,true,null,false,0,Vector3.Zero,new(-225,0,0));
        Assert.NotNull(first); Assert.False(first.Value.Snap); Assert.Equal("departure setup",w.Phase);
        ship = Ship(2,2);
        var entry = w.Update(ship,1000,true,null,false,0,new(30,0,0),Vector3.Zero);
        Assert.Equal(first.Value.Position,entry!.Value.Position); Assert.True(entry.Value.Snap);
        Assert.Null(w.Update(ship,7001,true,null,false,0,Vector3.Zero,Vector3.Zero)!.Value.Position);
        Assert.Equal("travel",w.Phase);
        Assert.Null(w.Update(Ship(8,3),7100,true,null,false,0,Vector3.Zero,Vector3.Zero));
        Assert.Equal("normal",w.Phase);
    }
    [Fact]
    public void CockpitWarpShowsExteriorDuringCinematicsAndRestoresCockpitBetweenThem()
    {
        var runtime=new EnbWarpCameraRuntime(new EnbRetailCameraRandomSource(() => 0x80000000u));
        var controller=new EnbNormalCameraController();
        var camera=Camera();
        controller.SelectFirstPerson(camera);
        var ship=Ship(1,1);
        var target=ship with {Spatial=ship.Spatial! with {Transform=ship.Spatial.Transform with {Position=new(10000,0,0)}}};
        void Check(uint time, bool firstPerson, EnbSceneObjectSnapshot? destination=null, bool enabled=true)
        {
            var pose=runtime.Update(ship,time,enabled,destination,destination is not null,0,Vector3.Zero,Vector3.Zero);
            Assert.True(controller.TryResolveFrame(camera,ship.Spatial!.Transform,1,16,new(800,600,1),out var frame,pose));
            Assert.Equal(firstPerson,frame.IsFirstPerson);
            Assert.True(controller.IsFirstPerson); // Persistent selection is unchanged.
        }
        Check(0,false); // Departure setup.
        ship=Ship(2,2);
        Check(1000,false); // Fixed departure.
        Check(7001,true); // Normal travel resumes the cockpit.
        Check(12002,false,target); // Fixed destination approach.
        ship=Ship(8,3);
        Check(12500,false,target);
        Check(14501,true,target); // Exit hold finished.
        ship=Ship(1,4);
        Check(15000,true,enabled:false);
        ship=Ship(2,5);
        Check(16000,true,enabled:false);
    }
    [Fact]
    public void DisabledWarpCameraRemovesChaseLagAndDoesNotReplayOldCharge()
    {
        var w = new EnbWarpCameraRuntime(); var ship = Ship(2,2);
        Assert.Null(w.Update(ship,0,true,null,false,0,Vector3.Zero,Vector3.Zero));
        Assert.True(w.Update(ship,0,false,null,false,0,Vector3.Zero,Vector3.Zero)!.Value.Snap);
        var c = new EnbNormalCameraController(); var camera = Camera();
        c.TryResolveFrame(camera,ship.Spatial!.Transform,1,16,new(800,600,1),out var first);
        var moved = ship.Spatial.Transform with {Position=new(100,0,0)};
        c.TryResolveFrame(camera,moved,1,16,new(800,600,1),out var next,new(null,null,true));
        Assert.InRange(Vector3.Distance(first.Camera.Position+new Vector3(100,0,0),next.Camera.Position),0,.001f);
    }
    [Fact]
    public void FinalApproachHonorsNearStartGuardAndExitHold()
    {
        var w = new EnbWarpCameraRuntime(new EnbRetailCameraRandomSource(() => 0x80000000u)); var ship=Ship(1,1);
        var target = ship with {Spatial=ship.Spatial! with {Transform=ship.Spatial.Transform with {Position=new(10000,0,0)}}};
        Assert.Null(w.Update(ship,0,true,target,true,0,Vector3.Zero,Vector3.Zero));
        w.Reset(); w.Update(ship,0,true,null,false,0,Vector3.Zero,Vector3.Zero);
        ship=Ship(2,2); w.Update(ship,1000,true,null,false,0,Vector3.Zero,Vector3.Zero);
        w.Update(ship,7001,true,null,false,0,Vector3.Zero,Vector3.Zero);
        w.Update(ship,12002,true,target,true,0,Vector3.Zero,Vector3.Zero);
        Assert.Equal("destination approach",w.Phase);
        w.Update(Ship(8,3),12500,true,target,true,0,Vector3.Zero,Vector3.Zero);
        Assert.NotNull(w.Update(Ship(8,3),14500,true,target,true,0,Vector3.Zero,Vector3.Zero));
        Assert.Null(w.Update(Ship(8,3),14501,true,target,true,0,Vector3.Zero,Vector3.Zero));
    }
    [Fact]
    public void WarpCameraOptionUsesRetailDefaultAndPersistedOverride()
    {
        Assert.True(EnbRetailPlayerOptionsCatalog.Parse(EnbRetailIniDocument.Parse("")).CameraWarp);
        Assert.False(EnbRetailPlayerOptionsCatalog.Parse(EnbRetailIniDocument.Parse("[BOOL-038]\nOptionName=Camera_Warp\nOptionValue=no")).CameraWarp);
    }

    [Fact]
    public void SaveWritesCharacterIniAndFreshBundleReloadsIt()
    {
        var installed=EnbInstalledAssetLocator.TryLocate(); if(installed is null)return;
        string temporary=Path.Combine(Path.GetTempPath(),"EnbNext-camera-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var paths=installed with {ClientOutputDirectory=temporary};
            var assets=EnbRetailAssetBundle.Load(paths); const uint character=12345;
            string optionsPath=Path.Combine(temporary,$"player{EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(character)}_options.ini");
            string unrelated="; keep this comment\n[BOOL-038]\nOptionName=Camera_Warp\nOptionValue=no\n[FLOAT-001]\nOptionName=Sound_SFX\nOptionValue=42\n";
            File.WriteAllText(optionsPath,unrelated);
            Assert.False(assets.ResolvePlayerOptions(character).Options.CameraWarp);
            assets.SaveCinematicOptions(character,true,true);
            Assert.True(assets.ResolvePlayerOptions(character).Options.CameraWarp);
            var reloadedOptions=EnbRetailAssetBundle.Load(paths).ResolvePlayerOptions(character).Options;
            Assert.True(reloadedOptions.CameraWarp); Assert.True(reloadedOptions.CameraGate);
            Assert.Equal(42,reloadedOptions.SoundSfx);
            Assert.Contains("; keep this comment",File.ReadAllText(optionsPath));
            assets.SaveCinematicOptions(character,new EnbRetailCameraOptions(false,true,false,true,false,true,false));
            var all = EnbRetailAssetBundle.Load(paths).ResolvePlayerOptions(character).Options;
            Assert.False(all.CameraShip); Assert.True(all.CameraWarp); Assert.False(all.CameraLoot);
            Assert.True(all.CameraGate); Assert.False(all.CameraLand); Assert.True(all.CameraDock); Assert.False(all.CameraBridge);
            Assert.Equal(42,all.SoundSfx);
            Assert.Contains("; keep this comment",File.ReadAllText(optionsPath));
            var original=assets.ResolveCameraModes(character)!.Catalog.Modes.ToArray();
            var changed=original[1] with {HeadingRadians=.6f,Distance=4.2f,TiltRadians=.3f};
            assets.SaveCameraMode(character,changed);
            var reloaded=EnbRetailAssetBundle.Load(paths).ResolveCameraModes(character)!;
            Assert.True(reloaded.UsesSessionOverride);
            Assert.Equal(changed.Distance,reloaded.Catalog.Modes[1].Distance);
            Assert.Equal(changed.HeadingRadians,reloaded.Catalog.Modes[1].HeadingRadians,5);
            Assert.Equal(original[0],reloaded.Catalog.Modes[0]);
            Assert.Equal(original.Skip(2),reloaded.Catalog.Modes.Skip(2));
        }
        finally { Directory.Delete(temporary,true); }
    }
}
