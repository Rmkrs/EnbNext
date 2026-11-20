using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbOrbitalCompetitionTests
{
    private static EnbRetailOrbitalDefinition Orbital(int index,string intensity,float phase=0)
        => new(index,69,1,0,0,0,phase,0,0,100,0,0,true,intensity,"Sky",.1f,false);

    private static void Rgb(EnbRetailScalarSplineDatabase db,string name,Vector3 value)
    {
        foreach(var (channel,v) in new[]{('R',value.X),('G',value.Y),('B',value.Z)})
        {
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
            writer.Write((ushort)1);writer.Write(EnbRetailScalarSplineDatabase.ComputeCurrentRetailCrc32(name+"_"+channel));
            writer.Write((byte)3);writer.Write((byte)0);writer.Write(1u);writer.Write(v);writer.Write(.5f);
            db.ApplyRuntimeUpdate(stream.ToArray());
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ModeZeroSelectsByIntensityAndVisibilityEvenWithFogDisabled(bool fog)
    {
        var db=EnbRetailScalarSplineDatabase.CreateEmpty();Rgb(db,"Color",new(.2f,.3f,.4f));Rgb(db,"Sky",new(.25f));
        Rgb(db,"A",new(2,0,0));Rgb(db,"B",new(0,1,0));
        var scene=new EnbRetailOrbitalSceneDefinition("Color","Sky","Sky",[Orbital(7,"A"),Orbital(9,"B",.5f),Orbital(12,"A") with {EmitsLight=false}]);
        var environment=new EnbSceneEnvironmentSnapshot(378,false,1070,[],null,0,fog?100:0,false,false,0,0,0,0);
        bool blocked=false;
        bool Sphere(EnbRetailOrbitalDefinition o,Matrix4x4 world,out EnbW3dWorldBounds sphere,out string? error)
        {
            error=null;sphere=o.Index==12?new(blocked?new(0,0,-50):new(0,0,500),blocked?20:1):new(world.Translation,1);return true;
        }
        EnbRetailOrbitalFogEvaluation Evaluate()
        {
            Assert.True(EnbRetailOrbitalFogEvaluator.TryEvaluate(environment,0,Vector3.Zero,scene,db,new(.1f),Sphere,out var result,out var error,true),error);
            Assert.Equal(fog,result.SceneFog.Enabled);Assert.Equal(0x191919u,result.GlobalAmbientRgb24);return result;
        }
        var clear=Evaluate();Assert.Equal(7,clear.DominantOrbitalIndex);Assert.Equal(new(.4f,0,0),clear.DominantLight!.Diffuse);
        Assert.Equal(new Vector3(.4f,.3f,0),clear.AggregateLightRgb);
        blocked=true;var eclipsed=Evaluate();Assert.Equal(9,eclipsed.DominantOrbitalIndex);
        Assert.Equal(new Vector3(0,.3f,0),eclipsed.DominantLight!.Diffuse);Assert.Equal(16,eclipsed.OccludedSampleCount);
        Assert.Equal(new Vector3(.0625f),eclipsed.FogRgb);
    }

    [Fact]
    public void TiesRetainAuthoredOrderAndZeroVisibilityStillSelectsFirst()
    {
        var db=EnbRetailScalarSplineDatabase.CreateEmpty();Rgb(db,"C",Vector3.One);Rgb(db,"Sky",Vector3.One);
        var first=Orbital(20,"C");var second=Orbital(3,"C",.5f);
        var env=new EnbSceneEnvironmentSnapshot(378,false,1070,[],null,0,100,false,false,0,0,0,0);
        bool hidden=false;
        bool Sphere(EnbRetailOrbitalDefinition o,Matrix4x4 w,out EnbW3dWorldBounds s,out string? e)
        {e=null;s=o.Index==999?new(hidden?new(-25,0,-25):new(0,0,500),hidden?40:1):new(w.Translation,1);return true;}
        foreach(var order in new[]{new[]{first,second},new[]{second,first}})
        {
            var scene=new EnbRetailOrbitalSceneDefinition("C","C","C",[..order,Orbital(999,"C") with {EmitsLight=false}]);
            foreach(bool eclipse in new[]{false,true})
            {
                hidden=eclipse;
                Assert.True(EnbRetailOrbitalFogEvaluator.TryEvaluateFog(env,0,Vector3.Zero,scene,db,Sphere,out var result,out var error,true),error);
                Assert.Equal(order[0].Index,result.DominantOrbitalIndex);Assert.Equal(Vector3.One,result.DominantLight!.Diffuse);
                Assert.Equal(eclipse?32:0,result.OccludedSampleCount);
                if(eclipse)Assert.Equal(Vector3.Zero,result.FogRgb);
            }
        }
    }

    [Fact]
    public void NoEmitterClearsSelectionAndBadBoundsAreLocalFailure()
    {
        var db=EnbRetailScalarSplineDatabase.CreateEmpty();Rgb(db,"C",Vector3.One);
        var env=new EnbSceneEnvironmentSnapshot(378,false,1070,[],null,0,100,false,false,0,0,0,0);
        var scene=new EnbRetailOrbitalSceneDefinition("C","C","C",[]);
        bool Sphere(EnbRetailOrbitalDefinition o,Matrix4x4 w,out EnbW3dWorldBounds s,out string? e)
        {s=default;e="missing optional orbital";return false;}
        Assert.True(EnbRetailOrbitalFogEvaluator.TryEvaluateFog(env,0,Vector3.Zero,scene,db,Sphere,out var empty,out _,true));
        Assert.Null(empty.DominantLight);Assert.Equal(-1,empty.DominantOrbitalIndex);
        Assert.False(EnbRetailOrbitalFogEvaluator.TryEvaluateFog(env,0,Vector3.Zero,scene with {Orbitals=[Orbital(0,"C")]},db,Sphere,out _,out var error,true));
        Assert.Contains("missing optional orbital",error);
    }
}
