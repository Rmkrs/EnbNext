using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailStationLightingTests
{
    private static EnbRetailStationLight Light(int id, Vector3 position, Vector3 rgb,
        EnbRetailStationLightKind kind = EnbRetailStationLightKind.Point)
        => new(id,kind,Matrix4x4.CreateTranslation(position),Vector3.Zero,rgb,1,0,100,Vector3.UnitZ,.5f);

    [Fact]
    public void AmbientAccumulatesEvenWhenLightLosesRanking()
    {
        var lights=Enumerable.Range(0,6).Select(i=>Light(i,new(0,0,50),new(i+1)) with {Ambient=new(.1f)}).ToImmutableArray();
        var result=new EnbRetailStationLighting(new(.1f),0,lights).Select(Vector3.Zero);
        Assert.InRange(result.Ambient.X,.39999f,.40001f);
        Assert.Equal(new[]{5,4,3,2},result.RankedOrdinals);
        Assert.Equal(4,result.Environment.Lights.Count(l=>l.Enabled));
    }

    [Fact]
    public void StableTiesAndOpposingReplaceFourthUsingStrongestThree()
    {
        var lights=Enumerable.Range(0,6).Select(i=>Light(i,new(0,0,10),new(1,0,0),EnbRetailStationLightKind.Directional)).ToImmutableArray();
        var result=new EnbRetailStationLighting(Vector3.Zero,.5f,lights).Select(Vector3.Zero);
        Assert.Equal(new[]{0,1,2,3},result.RankedOrdinals);
        Assert.Equal(new Vector3(0,1.5f,1.5f),result.Environment.Lights[3].Light.Diffuse);
        Assert.Equal(-Vector3.UnitZ,result.Environment.Lights[3].Light.Direction);
        Assert.Equal(Vector3.UnitZ,result.Environment.Lights[0].Light.Direction);
    }

    [Fact]
    public void OpposingUsesSquaredStrengthRatiosForDirectionAndColor()
    {
        var a=Light(0,Vector3.Zero,new(3,0,0),EnbRetailStationLightKind.Directional);
        var b=a with {Ordinal=1,Diffuse=new(2,0,0),Transform=Matrix4x4.CreateRotationY(MathF.PI/2)};
        var c=a with {Ordinal=2,Diffuse=new(1,0,0),Transform=Matrix4x4.CreateRotationX(MathF.PI/2)};
        var selected=new EnbRetailStationLighting(Vector3.Zero,.5f,[a,b,c]).Select(Vector3.Zero);
        var opposing=selected.Environment.Lights[3].Light;
        Assert.InRange(Vector3.Distance(new(0,2,2),opposing.Diffuse),0,1e-5f);
        Assert.InRange(Vector3.Distance(Vector3.Normalize(new(-4,1,-9)),opposing.Direction),0,1e-5f);
    }

    [Fact]
    public void PointSpotAndDirectionalUseObjectTranslationAndLightTransform()
    {
        var point=Light(0,new(0,0,50),new(.7f));
        var spot=Light(1,new(0,0,50),Vector3.One,EnbRetailStationLightKind.Spot);
        var directional=Light(2,Vector3.Zero,new(.2f),EnbRetailStationLightKind.Directional);
        var rig=new EnbRetailStationLighting(Vector3.Zero,0,[point,spot,directional]);
        Assert.Equal(new[]{0,2,1},rig.Select(Vector3.Zero).RankedOrdinals);
        Assert.Equal(new[]{1,0,2},rig.Select(new(0,0,75)).RankedOrdinals);
        var turned=rig with {Lights=[point,spot with {Transform=Matrix4x4.CreateRotationX(MathF.PI)*spot.Transform},directional]};
        Assert.Equal(new[]{1,0,2},turned.Select(Vector3.Zero).RankedOrdinals);
        Assert.Equal(new[]{0,2,1},turned.Select(new(0,0,75)).RankedOrdinals);
    }

    [Fact]
    public void IntensityIsAppliedOnlyWhenOpposingIsNonzero()
    {
        var light=Light(3,Vector3.Zero,new(.4f,.2f,.1f),EnbRetailStationLightKind.Directional) with {Intensity=2};
        Assert.Equal(light.Diffuse,new EnbRetailStationLighting(Vector3.Zero,0,[light]).Select(Vector3.Zero).Environment.Lights[0].Light.Diffuse);
        var result=new EnbRetailStationLighting(Vector3.Zero,.5f,[light]).Select(Vector3.Zero);
        Assert.Equal(light.Diffuse*2,result.Environment.Lights[0].Light.Diffuse);
        Assert.Equal(2,result.Environment.Lights.Count(l=>l.Enabled));
        Assert.InRange(result.Environment.Lights[1].Light.Diffuse.X,.09999f,.10001f);
        Assert.InRange(result.Environment.Lights[1].Light.Diffuse.Y,.29999f,.30001f);
        Assert.InRange(result.Environment.Lights[1].Light.Diffuse.Z,.39999f,.40001f);
    }

    [Fact]
    public void ZeroLightsAndAmbientOnlyDoNotInventDirectLights()
    {
        var empty=new EnbRetailStationLighting(new(.25f),.5f,[]).Select(Vector3.Zero);
        Assert.All(empty.Environment.Lights,l=>Assert.False(l.Enabled));
        Assert.Equal(0x3f3f3fu,empty.Environment.GlobalAmbientRgb24);
        var ambient=Light(0,new(0,0,50),Vector3.Zero) with {Ambient=Vector3.One};
        var result=new EnbRetailStationLighting(Vector3.Zero,.5f,[ambient]).Select(Vector3.Zero);
        Assert.Empty(result.RankedOrdinals);Assert.Equal(new Vector3(.5f),result.Ambient);
        Assert.Equal(0xfffffeu,EnbRetailStationLighting.UnregisteredEnvironment.GlobalAmbientRgb24);
    }

    [Fact]
    public void EqualNearFarAndZeroStrongestRetainNativeArithmetic()
    {
        var equal=Light(0,new(0,0,10),Vector3.One) with {Near=50,Far=50};
        Assert.Equal(Vector3.One,new EnbRetailStationLighting(Vector3.Zero,0,[equal]).Select(Vector3.Zero).Environment.Lights[0].Light.Diffuse);
        var a=Light(0,Vector3.Zero,Vector3.One,EnbRetailStationLightKind.Directional) with {Intensity=0};
        var result=new EnbRetailStationLighting(Vector3.Zero,.5f,[a,a with {Ordinal=1}]).Select(Vector3.Zero);
        Assert.True(float.IsNaN(result.Environment.Lights[2].Light.Direction.X));
    }

    [Fact]
    public void AuthoredVariantsDefaultsAndSequentialPivotTermination()
    {
        var hierarchy=new EnbW3dRenderHierarchy(1,"room",Vector3.Zero,false,
            [Pivot(0,"ROOT",uint.MaxValue),Pivot(1,"D_LIGHT00",0),Pivot(2,"D_LIGHT02",0)],[]);
        var ini=EnbRetailIniDocument.Parse("[STARBASE-2-1-3]\nLightRGB0=.2,.4,.6\nIntensity0=2\nFar0=90\nAmbient=.1,.2,.3\nFill=.6");
        var rig=EnbRetailStationLighting.FromAuthored(ini,2,1,0xc0,hierarchy);
        Assert.Single(rig.Lights);Assert.Equal(new Vector3(.2f,.4f,.6f),rig.Lights[0].Diffuse);
        Assert.Equal(0,rig.Lights[0].Near);Assert.Equal(90,rig.Lights[0].Far);Assert.Equal(.6f,rig.Opposing);
        Assert.Equal(new Vector3(.1f,.2f,.3f),rig.Ambient);
        var missing=EnbRetailStationLighting.FromAuthored(ini,2,1,0,hierarchy);
        Assert.Equal(1,missing.Lights[0].Intensity);Assert.Equal(50,missing.Lights[0].Near);Assert.Equal(100,missing.Lights[0].Far);
        var defaults=EnbRetailStationLighting.FromAuthored(EnbRetailIniDocument.Parse("[STARBASE-2-1-1]"),2,1,0,hierarchy);
        Assert.Equal(0,defaults.Lights[0].Intensity);Assert.Equal(0,defaults.Lights[0].Near);Assert.Equal(50,defaults.Lights[0].Far);
        var noPivot=EnbRetailStationLighting.FromAuthored(ini,2,1,0,hierarchy.Copy().SetPivots([hierarchy.Pivots[0]]));
        Assert.Equal(new Vector3(100,50,10),noPivot.Lights[0].Transform.Translation);Assert.Equal(.7f,noPivot.Opposing);
    }

    private static EnbW3dRenderPivot Pivot(int ordinal,string name,uint parent)
        => new(ordinal,name,parent,Vector3.Zero,Vector3.Zero,Quaternion.Identity,Matrix4x4.Identity);

    [Fact]
    public void RoomChangesReplaceLightsRetainOnlyNativeAmbientAndTeardownResets()
    {
        var state=new EnbRetailStationLightingState();
        var authored=new EnbRetailStationLighting(new(.2f),.8f,[Light(7,Vector3.UnitZ,Vector3.One)]);
        var noPivots=new EnbRetailStationLighting(Vector3.Zero,.7f,[Light(8,Vector3.UnitX,Vector3.One)]) {ReplacesSceneAmbient=false};
        state.Enter(authored);var old=state.Current.Select(Vector3.Zero);
        state.Enter(noPivots);Assert.Equal(authored.Ambient,state.Current.Ambient);
        Assert.Equal(8,Assert.Single(state.Current.Lights).Ordinal);
        Assert.Equal(7,Assert.Single(old.RankedOrdinals));
        state.Reset();Assert.Empty(state.Current.Lights);Assert.Equal(Vector3.Zero,state.Current.Ambient);
        state.Enter(noPivots);Assert.Equal(Vector3.Zero,state.Current.Ambient);
    }
}
