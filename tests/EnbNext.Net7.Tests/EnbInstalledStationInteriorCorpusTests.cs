using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // loads every installed station room variant with its doorways, machinery, lighting and collision
public sealed class EnbInstalledStationInteriorCorpusTests
{
    [Fact]
    public void EveryInstalledStationRoomLoads()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var failures = new List<string>();
        int rooms = 0;
        // Verb swatches (EnbWorldRenderer.Verbs) and the station interior use this reflection selection.
        Assert.True(assets.EnvironmentMaps is { } maps && maps.TryResolveReflection(EnbCurrentRetailStationInterior.ReflectionBaseAsset, out _));
        // Captured station definitions are 0..2 and rooms 0..3; vary every room flag bit.
        for (uint definition = 0; definition < 3; definition++)
        for (uint room = 0; room < 4; room++)
        for (int bit = -1; bit < 8; bit++)
        {
            uint flags = bit < 0 ? 0u : 1u << bit;
            try
            {
                var station = new EnbSceneStationDefinition(definition, [new EnbSceneStationRoom(room, flags, ImmutableArray<uint>.Empty)]);
                var interior = EnbCurrentRetailStationInterior.Load(assets, station, room);
                _ = EnbRetailStationLighting.Load(assets, definition, room, flags, interior.Resource);
                _ = new EnbStationCollision(interior, assets, 1.7f);
                // Camera collision geometry, machinery bone positions and the ambient NPC
                // attachment hierarchy throw only for an authored pose that cannot be evaluated.
                var cameraGeometry = new EnbRetailStationCameraGeometry();
                cameraGeometry.Append(interior.Resource, System.Numerics.Matrix4x4.Identity);
                foreach (var machine in interior.Machinery)
                    for (long clock = 0; clock <= 20000; clock += 100)
                    {
                        var sample = machine.Sample(clock);
                        cameraGeometry.Append(machine.Resource, System.Numerics.Matrix4x4.Identity, sample);
                        var soundObject = new EnbStationSoundObject(machine.Name, machine.Resource, sample);
                        foreach (var pivot in machine.Resource.Hierarchies.Single().Pivots)
                        {
                            soundObject.TryPosition(pivot.Name, false, out _);
                            soundObject.TryPosition(pivot.Name, true, out _);
                        }
                    }
                if (interior.ExtraAttachments is { } attachments &&
                    !EnbNext.Rendering.EnbW3dHierarchyPose.TryEvaluate(attachments, System.Numerics.Matrix4x4.Identity, out _, out var poseError))
                    throw new InvalidDataException(poseError);
                // EnbWorldRenderer.StationCamera decomposes these authored camera poses.
                static void Rigid(System.Numerics.Matrix4x4 m, string name)
                { if (!System.Numerics.Matrix4x4.Decompose(m, out _, out _, out _)) throw new InvalidDataException("camera " + name); }
                if (room == 0)
                {
                    Rigid(interior.PivotTransform("CAMERA_LEAVE"), "CAMERA_LEAVE");
                    for (long clock = 0; clock <= 20000; clock += 250)
                        Rigid(interior.PivotTransform("CAMERA_3RD", clock), "CAMERA_3RD");
                }
                else Rigid(System.Numerics.Matrix4x4.CreateFromQuaternion(interior.CameraOrientation) *
                    System.Numerics.Matrix4x4.CreateTranslation(interior.CameraPosition), "room");
                rooms++;
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or KeyNotFoundException)
            { failures.Add($"def {definition} room {room} flags 0x{flags:X}: {ex.GetType().Name}: {ex.Message}"); }
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-station-corpus.txt"), [$"rooms {rooms}", .. failures]);
        Assert.Empty(failures);
    }
}
