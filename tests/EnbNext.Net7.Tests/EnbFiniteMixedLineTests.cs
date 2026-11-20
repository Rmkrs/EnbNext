using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbFiniteMixedLineTests
{
    [InstalledRetailTheory]
    [InlineData(0f)]
    [InlineData(120f)]
    public void InstalledAssemblyRetainsFiniteLineWithoutReplacingSiblingRuntimes(float hue)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        var mixed=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(EnbCurrentRetailRObjEffectAsset.Load(assets,2019,new(hue,0,0)).Mixed);
        Assert.Null(mixed.Animation);
        Assert.Equal(2,mixed.Streaks.Count);Assert.Equal(4,mixed.Sprites.Count);
        var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,1,123,randomStreams:new());
        Assert.Null(runtime.AnimatedSprites);
        var line=Assert.Single(runtime.Sprites,s=>s.RetainedLine is not null).RetainedLine!;
        Assert.Equal(3,runtime.Sprites.Count(s=>s.RetainedLine is null && s.Continuous is null));
        var history=new EnbCurrentRetailParticlePoseHistory();
        void Advance(uint age)
        {
            history.Record(age,Matrix4x4.CreateRotationZ(age*.001f)*Matrix4x4.CreateTranslation(age*.01f,0,0));
            runtime.Advance(mixed,history,1);
        }
        Advance(0);Advance(31);Advance(61);
        Assert.Equal(30,line.EmittedCount);Assert.False(line.IsEmitting);Assert.True(line.HasLineEndpoint);
        Assert.Equal(30,line.WorldBirths.Count);
        var births=line.WorldBirths.ToArray();
        Advance(61);Assert.Equal(births,line.WorldBirths);
        Advance(200);
        Assert.Equal(30,line.EmittedCount);Assert.True(line.HasLineEndpoint);
        Assert.Equal(births,line.WorldBirths);
        Assert.Equal(2f,line.CurrentWorld.Translation.X,3);
        Assert.All(line.WorldBirths,b=>Assert.True(b.World.Translation.X<1));
        Advance(561);
        Assert.Empty(line.WorldBirths);Assert.Empty(line.Births);Assert.Empty(line.Worlds);
        Assert.Equal(30,line.EmittedCount);Assert.True(line.HasLineEndpoint);
        var fresh=new EnbCurrentRetailMixedParticleRuntime(mixed,1,123,randomStreams:new());
        var freshHistory=new EnbCurrentRetailParticlePoseHistory();
        freshHistory.Record(0,Matrix4x4.Identity);fresh.Advance(mixed,freshHistory,1);
        freshHistory.Record(61,Matrix4x4.Identity);fresh.Advance(mixed,freshHistory,1);
        Assert.Equal(30,Assert.Single(fresh.Sprites,s=>s.RetainedLine is not null).RetainedLine!.EmittedCount);
        Assert.Null(mixed.RecoveryDiagnostic);
    }

    [InstalledRetailTheory]
    [InlineData(0f)]
    [InlineData(120f)]
    public void RiftShockwaveRetainsMergedLightningAllowanceAndSphereSiblings(float hue)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        var asset=EnbCurrentRetailRObjEffectAsset.Load(assets,2203,new(hue,0,0));
        Assert.NotNull(asset.Spheres);Assert.NotEmpty(asset.Spheres.Spheres);
        var mixed=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(asset.Mixed);
        Assert.Null(mixed.RecoveryDiagnostic);
        int index=mixed.Sprites.ToList().FindIndex(s=>s.Definition.RenderMode==2);
        Assert.True(index>=0);
        var definition=mixed.Sprites[index].Definition;
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.SupportsJetLine(definition));
        Assert.Equal(20,definition.MaximumEmissions);
        Assert.Equal(5u,definition.LinePropertyWords[1]);
        var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,1,123,randomStreams:new());
        var line=Assert.IsType<EnbCurrentRetailAnimatedParticleRuntime.Emitter>(runtime.Sprites[index].RetainedLine);
        var history=new EnbCurrentRetailParticlePoseHistory();
        void Advance(uint age)
        {
            history.Record(age,Matrix4x4.CreateTranslation(age*.01f,0,0));
            runtime.Advance(mixed,history,1);
        }
        Advance(0);Advance(101);
        Assert.InRange(line.EmittedCount,1,19);
        Advance(211);
        Assert.Equal(20,line.EmittedCount);Assert.False(line.IsEmitting);Assert.True(line.HasLineEndpoint);
        // Follow the retained finite owner into the production packet builder,
        // rather than treating successful asset preparation as rendered coverage.
        EnbResolvedRenderCamera.TryCreate(new(250,-140,140),Quaternion.Identity,
            new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        EnbW3dDrawTask? Draw(uint age)
        {
            var child=mixed.Sprites[index];
            var instance=new EnbW3dParticleEmitterDrawInstance(definition.Name,definition.Name,1,
                EnbRenderPassKind.OrdinaryWorld,definition,child.Texture,line.CurrentWorld,"",child.PivotIndex,index)
                {RuntimeTextureName=child.RuntimeTextureName,RetainedTexture=child.RetainedTexture};
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(child.Key,1,1,instance,camera,
                EnbW3dLightEnvironment.BlackNoLights,123,0,age,out var packet,out _,out var error,
                retainedBirths:line.Births,worldBirths:line.WorldBirths,
                activeEmissionRun:line.HasLineEndpoint?line.EmissionRun:null,motionHistory:line.MotionHistory),error);
            return packet;
        }
        var submitted=Assert.IsType<EnbW3dDrawTask>(Draw(211));
        Assert.NotEmpty(submitted.Mesh.Indices);
        Assert.NotNull(submitted.CurrentRetailViewPositions);
        Assert.Equal(submitted.DeformedGeometry!.Positions.Count,submitted.CurrentRetailViewPositions.Count);
        Assert.All(submitted.CurrentRetailViewPositions,p=>Assert.True(float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)));
        var births=line.WorldBirths.ToArray();Assert.Equal(20,births.Length);
        Advance(211);Advance(400);
        Assert.Equal(births,line.WorldBirths);Assert.Equal(20,line.EmittedCount);
        Assert.All(line.WorldBirths,b=>Assert.True(b.World.Translation.X<2.2f));
        Assert.Equal(4f,line.CurrentWorld.Translation.X,3);
        Advance(711);
        Assert.Empty(line.WorldBirths);Assert.Empty(line.Births);Assert.Empty(line.Worlds);
        Assert.True(line.HasLineEndpoint);
        Assert.Null(Draw(711));
    }
}
