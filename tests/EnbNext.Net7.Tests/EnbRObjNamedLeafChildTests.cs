using System.Numerics;
using System.Text;
using System.Buffers.Binary;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbRObjNamedLeafChildTests
{
    [Theory]
    [InlineData("V_DECEPT01A.OBJECT015")]
    [InlineData("SK_AFBURN01A.DAZZLE")]
    [InlineData("NAMED_EMPTY.EMPTY")]
    [InlineData("V_DECEPT01A.OBJECT015",true)]
    [InlineData("SK_AFBURN01A.DAZZLE",true)]
    [InlineData("NAMED_EMPTY.EMPTY",true)]
    [InlineData("V_DECEPT01A.OBJECT015",false,true)]
    [InlineData("SK_AFBURN01A.DAZZLE",false,true)]
    [InlineData("NAMED_EMPTY.EMPTY",false,true)]
    [InlineData("NAMED_SPECIAL.LEAF")]
    [InlineData("NAMED_SPECIAL.LEAF",true)]
    [InlineData("NAMED_SPECIAL.LEAF",false,true)]
    public void EffectParentRetainsNamedLeafFromAnotherFile(string childName,bool hmodel=false,bool permanent=false)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        EnbResolvedW3dResource child; string? error;
        if(childName=="NAMED_SPECIAL.LEAF")
        {
            var bytes=EnbSpecialMaterialTests.Root(childName,EnbSpecialMaterialTests.Member(1));
            Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out error),error);
            child=new("named-special.w3d",childName,new("named-special.w3d","fixture",0,bytes.Length),file.Model,[]){PrototypeFile=file};
        }
        else if(childName=="NAMED_EMPTY.EMPTY")
        {
            var payload=new byte[48]; Name(payload,16,childName); var bytes=Chunk(0x750,payload);
            Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out error),error);
            child=new("named-empty.w3d",childName,new("named-empty.w3d","fixture",0,bytes.Length),file.Model,[]){PrototypeFile=file};
        }
        else Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.RetailMixResources,childName,out child,out error),error);
        if(hmodel)
        {
            //Use a fresh prefix: installed-file publication already registers
            //V_DECEPT01A itself and the first registered prototype wins.
            string renamed="LEAFPARENT."+childName.Split('.')[1];
            byte[] bytes;
            if(child.Model.Boxes.SingleOrDefault(b=>b.Name==childName) is {} authoredBox)
            {
                var data=new byte[68];Word(data,0,authoredBox.Version);Word(data,4,authoredBox.Attributes);Name(data,8,renamed);Word(data,40,authoredBox.Color);
                float[] values=[authoredBox.Center.X,authoredBox.Center.Y,authoredBox.Center.Z,authoredBox.Extents.X,authoredBox.Extents.Y,authoredBox.Extents.Z];
                for(int i=0;i<values.Length;i++)Word(data,44+i*4,BitConverter.SingleToUInt32Bits(values[i]));
                bytes=Chunk(0x740,data);
            }
            else if(child.Model.Dazzles.SingleOrDefault(d=>d.Name==childName) is {} dazzle)
                bytes=Chunk(0x900,[..Chunk(0x901,Encoding.ASCII.GetBytes(renamed+"\0")),..Chunk(0x902,Encoding.ASCII.GetBytes(dazzle.Type+"\0"))]);
            else if(child.Model.SpecialMaterials.Count!=0)
                bytes=EnbSpecialMaterialTests.Root(renamed,EnbSpecialMaterialTests.Member(1));
            else {var data=new byte[48];Name(data,16,renamed);bytes=Chunk(0x750,data);}
            Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out error),error);
            childName=renamed;
            child=new("renamed-leaf.w3d",childName,new("renamed-leaf.w3d","fixture",0,bytes.Length),file.Model,[]){PrototypeFile=file};
        }
        using var cache=new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(child,out _,out error),error);
        const string meshName="SPH13.SPH134";
        if(permanent)
        {
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.RetailMixResources,meshName,out var meshSource,out error),error);
            Assert.True(cache.TryLoadFile(meshSource,out _,out error),error);
        }
        byte[] Chunk(uint id,byte[] payload)
        {
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
            writer.Write(id);writer.Write((uint)payload.Length);writer.Write(payload);return stream.ToArray();
        }
        void Word(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
        void Name(byte[] bytes,int at,string value)=>Encoding.ASCII.GetBytes(value).CopyTo(bytes,at);
        string parentName=hmodel?childName.Split('.')[0]:"LEAFPARENT";
        var header=new byte[36];Word(header,0,0x30000);Name(header,4,"LEAFTREE");Word(header,20,1);
        var pivot=new byte[60];Name(pivot,0,"ROOT");Word(pivot,16,uint.MaxValue);Word(pivot,56,0x3f800000);
        var hlod=new byte[40];Word(hlod,0,0x30000);Word(hlod,4,1);Name(hlod,8,parentName);Name(hlod,24,"LEAFTREE");
        var level=new byte[8];Word(level,0,1);Word(level,4,0x7f7fffff);
        var member=new byte[36];Name(member,4,childName);
        byte[] raw=[..Chunk(0x100,[..Chunk(0x101,header),..Chunk(0x102,pivot)]),..Chunk(0x700,[..Chunk(0x701,hlod),..Chunk(0x702,[..Chunk(0x703,level),..Chunk(0x704,member)])])];
        if(permanent)
        {
            var baseMember=new byte[36];Name(baseMember,4,meshName);
            var aggregateHeader=new byte[8];Word(aggregateHeader,0,1);
            raw=[..Chunk(0x100,[..Chunk(0x101,header),..Chunk(0x102,pivot)]),..Chunk(0x700,[..Chunk(0x701,hlod),..Chunk(0x702,[..Chunk(0x703,level),..Chunk(0x704,baseMember)]),..Chunk(0x705,[..Chunk(0x703,aggregateHeader),..Chunk(0x704,member)])])];
        }
        if(hmodel)
        {
            var mh=new byte[40];Word(mh,0,0x40000);Name(mh,4,parentName);Name(mh,20,"LEAFTREE");
            BinaryPrimitives.WriteUInt16LittleEndian(mh.AsSpan(36),1);
            var node=new byte[18];Name(node,0,childName.Split('.')[1]);
            raw=[..Chunk(0x100,[..Chunk(0x101,header),..Chunk(0x102,pivot)]),..Chunk(0x300,[..Chunk(0x301,mh),..Chunk(0x302,node)])];
        }
        Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(raw,out var decoded,out error),error);
        var source=new EnbResolvedW3dResource("leaf-parent.w3d",parentName,new("leaf-parent.w3d","fixture",0,raw.Length),decoded.Model,[]){PrototypeFile=decoded};
        Assert.True(cache.TryLoadFile(source,out _,out error),error);
        Assert.True(cache.TryFindPrototypeSource(parentName,out var parent));
        var selected=EnbCurrentRetailRObjEffectAsset.PreparePrototype(assets,parent,Vector3.Zero,cache.CapturePrototypeSources(out _)).Build();
        var external=Assert.Single(selected.Resource!.ExternalRenderObjects);
        Assert.Equal(childName,external.Name,StringComparer.OrdinalIgnoreCase);
        var obj=new EnbSceneObjectSnapshot(93,1,43,false,false,2,31081,1,0,0,0,0,false,"named-leaf",null!,null,null,null,[],[],[],[],[],[],null,[],null,0,0);
        Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(obj,selected.Resource,Matrix4x4.Identity,0,
            EnbRenderPassKind.OrdinaryWorld,selected.Selection,EnbW3dLightEnvironment.BlackNoLights,out var draw,out error),error);
        if(permanent)
        {
            Assert.NotEmpty(draw.Packets);
            Assert.All(draw.Packets,p=>Assert.True(p.SpecialMaterial is not null || EnbW3dRenderObjectNameSemantics.MatchesMesh(p.Mesh,meshName)));
            Assert.Equal(childName,Assert.Single(selected.Resource.HierarchicalLods.Single().Aggregate!.Members).Name);
        }
        else if(external.SpecialMaterialDefinition is not null)Assert.NotEmpty(draw.Packets);
        else Assert.Empty(draw.Packets);
        Assert.Empty(draw.ParticleEmitters);
        Assert.Equal(external.DazzleDefinition is null?0:1,draw.Dazzles.Count);
        foreach(var dazzleDraw in draw.Dazzles)
        {
            var owner=Matrix4x4.CreateRotationZ(.3f)*Matrix4x4.CreateTranslation(3,4,5);
            var placed=selected.PlaceDazzle(dazzleDraw,owner,2,0,100);
            Assert.Equal(owner,placed.World);Assert.Equal(2,placed.CurrentRetailDazzleScale);
        }
        EnbRObjModelAttachment.Validate(selected);
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(selected.Resource,selected.Resource.HierarchicalLods.Single(),Matrix4x4.Identity,null,0,out var bounds,out error),error);
        if(external.BoxDefinition is {} box)
        {
            //98F1D0 unions child min/max and reconstructs center/extents;
            //the final center need not retain the header's exact float bits.
            var minimum=box.Center-box.Extents;var maximum=box.Center+box.Extents;
            if(permanent)
            {
                var mesh=Assert.Single(selected.Resource.Meshes);
                var meshBox=EnbCurrentRetailRenderBoundsStorage.MeshLocalBox(mesh.BoundingBoxMin,mesh.BoundingBoxMax);
                minimum=Vector3.Min(minimum,meshBox.Center-meshBox.Extent);maximum=Vector3.Max(maximum,meshBox.Center+meshBox.Extent);
            }
            Assert.Equal((minimum+maximum)*.5f,bounds.LocalBox.Center);
            Assert.Equal((maximum-minimum)*.5f,bounds.LocalBox.Extents);
            Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(selected.Resource,selected.Resource.HierarchicalLods.Single(),_=>true,out var costs,out error),error);
            int meshCost=0;
            if(permanent)
            {
                var without=selected.Resource.HierarchicalLods.Single() with {Aggregate=null};
                Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(selected.Resource,without,_=>true,out var baseCosts,out error),error);
                meshCost=Assert.Single(baseCosts);
            }
            Assert.Equal(permanent?meshCost:12,Assert.Single(costs));
            if(permanent)
            {
                Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dAggregateFixedRenderCost(selected.Resource,selected.Resource.HierarchicalLods.Single(),_=>true,out var fixedCost,out error),error);
                Assert.Equal(12,fixedCost);
            }
        }
        else if(childName.EndsWith(".EMPTY",StringComparison.Ordinal))
        {
            Assert.Equal(EnbW3dExternalRenderObjectKind.NullObject,external.Kind);
            if(!permanent)
            {
                Assert.Equal(Vector3.Zero,bounds.LocalBox.Center);
                Assert.Equal(new Vector3(.1f),bounds.LocalBox.Extents);
            }
        }
        else if(child.Model.SpecialMaterials.Count!=0)Assert.NotNull(external.SpecialMaterialDefinition);
        else Assert.NotNull(external.DazzleDefinition);
        if(permanent)
        {
            //The same retained leaf must survive when a real emitter forces
            //the mixed loader. Its presence must not create another buffer.
            var emitterRoot=EnbCurrentRetailRObjEffectAsset.Load(assets,2010).Resource!;
            var emitterHlod=emitterRoot.HierarchicalLods.Single();
            var emitterMember=Assert.Single(emitterHlod.Aggregate!.Members);
            var mixed=EnbCurrentRetailMixedEffectAsset.LoadInline(assets,emitterRoot.Copy().SetExternalRenderObjects([..emitterRoot.ExternalRenderObjects,external]).SetHierarchicalLods([emitterHlod with {Aggregate=emitterHlod.Aggregate with
                    {Members=[emitterMember,new(0,childName)]}}]));
            Assert.Single(mixed.Sprites);Assert.Empty(mixed.Streaks);
            Assert.Equal(childName,mixed.Resource.HierarchicalLods.Single().Aggregate!.Members[1].Name);
            Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(obj,mixed.Resource,Matrix4x4.Identity,0,
                EnbRenderPassKind.OrdinaryWorld,new(emitterHlod.Name,0),EnbW3dLightEnvironment.BlackNoLights,out var mixedDraw,out error),error);
            Assert.NotEmpty(mixedDraw.Packets);
            Assert.Equal(external.DazzleDefinition is null?0:1,mixedDraw.Dazzles.Count);
            var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,1,123,randomStreams:new());
            var history=new EnbCurrentRetailParticlePoseHistory();
            for(uint age=0;age<=550;age+=50)
            {history.Record(age,Matrix4x4.CreateTranslation(age*.1f,0,0));runtime.Advance(mixed,history,1);}
            Assert.NotEmpty(Assert.Single(runtime.Sprites).Continuous!.Births);
        }
    }
}




