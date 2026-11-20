using System.Reflection;
using EnbNext.Assets;
using EnbNext.Engine;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // builds the production base factory of every installed FILE/NAME base asset
public sealed class EnbInstalledCpuRootCorpusTests
{
    [Fact]
    public void EveryInstalledW3dBaseAssetBuildsItsProductionFactoryRoot()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.NotNull(assets.ClientBaseAssetMounts);
        var resolver = assets.BaseAssetResolver!;
        var renderer = new EnbWorldRenderer(() => null, () => null, () => null, enableAudio: false);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo resolve = typeof(EnbWorldRenderer).GetMethod("ResolveSceneBaseFactory", flags)!;
        MethodInfo sphere = typeof(EnbWorldRenderer).GetMethod("TryComputeRootWorldSphere", BindingFlags.Static | BindingFlags.NonPublic)!;
        var sphereFailures = new List<string>();
        var treeFailures = new List<string>();
        var packetFailures = new List<string>();
        int packetBuilds = 0, lanes = 0;
        var laneFailures = new List<string>();
        Assert.True(EnbResolvedRenderCamera.TryCreate(new Vector3(0, 0, -500), Quaternion.Identity, new(800, 600, 1), Math.PI / 2, new(1, 100000), out var camera));
        var packetResources = new HashSet<EnbW3dRenderResource>(ReferenceEqualityComparer.Instance);
        Type listType = typeof(EnbWorldRenderer).GetNestedType("RootTaskList", BindingFlags.NonPublic)!;
        MethodInfo append = typeof(EnbWorldRenderer).GetMethods(flags).Single(m => m.Name == "TryAppendRootInstanceTree" && m.GetParameters().Length == 5);
        var failures = new List<string>();
        int built = 0;
        for (int id = 0; id <= 32767; id++)
        {
            EnbBaseAssetResolution r;
            try { r = resolver.Resolve(id); } catch { continue; }
            if (r.Status != EnbBaseAssetResolutionStatus.W3d || r.W3d is not { } w3d) continue;
            var definition = SceneObject(id);
            var obj = new EnbRenderObjectState(definition);
            var spatial = new EnbRenderSpatialState(); spatial.AdoptDefinition(definition.Spatial!); obj.Spatial = spatial;
            try
            {
                object? factory = resolve.Invoke(renderer, [obj, assets]);
                if (factory is null) continue; // not an ordinary FILE/NAME factory (RSL etc.)
                object? root = factory.GetType().GetProperty("Root")!.GetValue(factory);
                string? diagnostic = (string?)factory.GetType().GetProperty("Diagnostic")!.GetValue(factory);
                if (root is not null)
                {
                    built++;
                    object?[] sphereArgs = [root, System.Numerics.Matrix4x4.Identity, null, null];
                    if (!(bool)sphere.Invoke(null, sphereArgs)!)
                        sphereFailures.Add($"B{id} {w3d.LogicalFileName}/{w3d.RequestedObjectName}: {sphereArgs[3]}");
                    var resource = (EnbW3dRenderResource)root.GetType().GetProperty("Resource")!.GetValue(root)!;
                    if (packetResources.Add(resource))
                    {
                        void Build(string what, EnbW3dHlodSelection? selection, IReadOnlyList<EnbW3dWeightedAnimationSample> blend)
                        {
                            packetBuilds++;
                            // The prepared (production) builder deforms authored SKIN meshes through
                            // the evaluated hierarchy pose, as the live renderer does.
                            if (!EnbW3dDrawPacketCompiler.TryBuildPrepared(obj, resource, null, 0, EnbRenderPassKind.OrdinaryWorld, selection,
                                    EnbW3dLightEnvironment.BlackNoLights, blend, null, new EnbW3dMeshOccurrencePreparation(), out var draws, out string? why))
                            {
                                packetFailures.Add($"B{id} {resource.LogicalFileName} {what}: {why}");
                                return;
                            }
                            foreach (var packet in draws.Packets)
                            {
                                // Authored SKIN meshes reach the lanes only after the live per-frame
                                // skin deformation; this static harness does not reproduce that step.
                                if (packet.Mesh.BoneIndices.Count != 0 && packet.DeformedGeometry is null) continue;
                                if (!EnbW3dIndexedSubmissionCompiler.TryCompile(packet, out var submission, out why))
                                { laneFailures.Add($"B{id} {resource.LogicalFileName} {what} {packet.Mesh.Name} submission: {why}"); continue; }
                                lanes++;
                                if (submission.Runs.IsDefaultOrEmpty) { laneFailures.Add($"B{id} {packet.Mesh.Name} no runs"); continue; }
                                var semantics = submission.Runs[0].Binding.Semantics;
                                byte stages = semantics.PermutationKey.TextureStageCount;
                                bool mapped = submission.Runs.Any(run => run.Binding.Semantics.Mapper0Program.Kind != EnbW3dMapperRuntimeProgramKind.Disabled ||
                                    run.Binding.Semantics.Mapper1Program.Kind != EnbW3dMapperRuntimeProgramKind.Disabled);
                                bool ok;
                                if (stages is 1 or 2 || stages == 0 && mapped)
                                    ok = EnbW3dProgrammableTextureLaneCompiler.TryCompile(submission, camera, EnbRenderTextureRuntimeSettings.RetailDefaults,
                                        EnbRenderTextureBackendCapabilities.ModernDefault, b => assets.Textures!.Resolve(b.LogicalName).Image, out _, out why);
                                else if (stages == 0 && semantics.VertexLighting.LightingEnabled)
                                    ok = EnbW3dProgrammableLightingLaneCompiler.TryCompile(submission, camera, out _, out why);
                                else if (stages == 0)
                                    ok = EnbW3dProgrammableShaderLaneCompiler.TryCompileFirstLane(submission, camera, out _, out why);
                                else { ok = false; why = $"unsupported stage count {stages}"; }
                                if (!ok) laneFailures.Add($"B{id} {resource.LogicalFileName} {what} {packet.Mesh.Name} stages {stages}: {why}");
                            }
                        }
                        Build("default", null, []);
                        foreach (var hlod in resource.HierarchicalLods)
                            foreach (var level in hlod.Levels)
                                Build($"hlod {hlod.Name}#{level.Ordinal}", new EnbW3dHlodSelection(hlod.Name, level.Ordinal), []);
                        foreach (var animation in resource.Animations)
                            Build($"anim {animation.Name}", null, [new EnbW3dWeightedAnimationSample(animation.Name, 0f, 1f, animation.HierarchyName)]);
                    }
                    object list = listType.GetMethod("Rent")!.Invoke(Activator.CreateInstance(listType, true), [renderer])!;
                    object?[] treeArgs = [obj, root, false, list, null];
                    if (!(bool)append.Invoke(renderer, treeArgs)!)
                        treeFailures.Add($"B{id} {w3d.LogicalFileName}/{w3d.RequestedObjectName}: {treeArgs[4]}");
                }
                else failures.Add($"B{id} {w3d.LogicalFileName}/{w3d.RequestedObjectName}: {diagnostic}");
            }
            catch (TargetInvocationException ex)
            {
                failures.Add($"B{id} {w3d.LogicalFileName}: {ex.InnerException?.GetType().Name} {ex.InnerException?.Message}");
            }
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-cpu-root-corpus.txt"), [$"built {built} packet-builds {packetBuilds}", .. failures, "sphere:", .. sphereFailures, "tree:", .. treeFailures, "packets:", .. packetFailures, $"lanes {lanes}:", .. laneFailures]);
        Assert.True(built > 1000, $"built {built}");
        Assert.Empty(failures);
        Assert.Empty(sphereFailures);
        Assert.Empty(treeFailures);
        Assert.True(packetBuilds > 1000, $"packet builds {packetBuilds}");
        Assert.Empty(packetFailures);
        Assert.Empty(laneFailures);
    }

    private static EnbSceneObjectSnapshot SceneObject(int baseAsset)
        => new(
            RenderIdentity: (ulong)baseAsset + 1, VisualGeneration: 1, GameId: (uint)baseAsset + 1,
            IsLocalShip: false, IsCurrentTarget: false, ObjectType: 1, BaseAsset: baseAsset, Scale: 1f,
            HsvH: 0, HsvS: 0, HsvV: 0, RelationshipRaw: 0, IsAttacking: false, Name: "corpus",
            Radar: new EnbSceneRadarSnapshot(false, 0, null),
            Spatial: new EnbSceneSpatialSnapshot(EnbSceneSpatialProviderKind.Fixed,
                new EnbSceneTransformSnapshot(ClientTime: 100, Position: Vector3.Zero, Orientation: Quaternion.Identity,
                    Velocity: Vector3.Zero, Speed: 0, Rotation: Vector3.Zero, OrientationColumn0: Vector3.UnitX), Planet: null),
            BaseAssetReference: null, ClientBaseAssetReference: null,
            Decals: ImmutableArray<EnbSceneDecalSnapshot>.Empty, ColorizationEntries: ImmutableArray<EnbSceneColorizationSnapshot>.Empty,
            RenderStateLinks: ImmutableArray<EnbSceneRenderStateLinkSnapshot>.Empty, ActiveRenderStates: ImmutableArray<uint>.Empty,
            RenderStateActions: ImmutableArray<EnbSceneRenderStateActionSnapshot>.Empty, Attachments: ImmutableArray<EnbSceneAttachmentSnapshot>.Empty,
            NameDecal: null, SubParts: ImmutableArray<EnbSceneSubPartSnapshot>.Empty, Avatar: null,
            SpecialTargetEndpointId: 0, SpecialTargetTransition: 0);
}
