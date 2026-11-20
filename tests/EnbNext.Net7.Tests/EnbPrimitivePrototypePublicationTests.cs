using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbPrimitivePrototypePublicationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ConfiguredNameRejectsFileDuplicateUntilRemoval(bool sphere)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        const string name="#prim!2H0S0V0";
        var source=Source(assets,Bytes(assets,sphere,name),name);
        using var cache=new EnbW3dRenderResourceCache();
        cache.RegisterConfiguredPrototypeName(name);
        Assert.True(cache.TryLoadFile(source,out _,out var error),error);
        Assert.False(cache.TryFindPrototypeSource(name.ToUpperInvariant(),out _));
        Assert.DoesNotContain(name,cache.CapturePrototypeSources(out _).Keys,StringComparer.OrdinalIgnoreCase);
        Assert.True(cache.RemovePrototype(name.ToUpperInvariant()));
        //91D370 destroyed the duplicate;91EDE0 must not reveal it later.
        Assert.False(cache.TryFindPrototypeSource(name,out _));
        Assert.True(cache.TryLoadFile(source,out _,out error),error);
        Assert.True(cache.TryFindPrototypeSource(name,out var selected));
        Assert.Equal(sphere?EnbW3dPrototypeKind.Sphere:EnbW3dPrototypeKind.Ring,selected.RegisteredPrototype!.Value.Kind);
        cache.RegisterConfiguredPrototypeName(name);
        Assert.False(cache.TryFindPrototypeSource(name,out _));
        Assert.True(cache.RemovePrototype(name));
        Assert.True(cache.TryFindPrototypeSource(name,out var earlier));
        Assert.Equal(selected.PrototypeRegistration,earlier.PrototypeRegistration);
    }

    private static byte[] Bytes(EnbRetailAssetBundle assets,bool sphere,string name)
    {
        Assert.True(assets.FileResources.TryRead(sphere?"sk_clx04p2.w3d":"sk_pd01p1.w3d",out var bytes,out _,out _));
        bytes.AsSpan(24,32).Clear();System.Text.Encoding.ASCII.GetBytes(name).CopyTo(bytes,24);
        return bytes;
    }
    private static EnbResolvedW3dResource Source(EnbRetailAssetBundle assets,byte[] bytes,string name)
    {
        Assert.True(EnbRetailW3dFileResolver.TryDecodeFile("retained.w3d",name,bytes,null,"absent-primitive.w3d",assets.MixResources,out var source,out var error),error);
        return source;
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RetainedDefinitionSurvivesDuplicatePublicationAndAbsentFile(bool sphere)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var source=Source(assets,Bytes(assets,sphere,"PRIM739"),"PRIM739");
        Assert.False(source.PrototypeFile!.HasUnrepresentedRenderPrototypes);
        using var cache=new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(source,out _,out var error),error);
        Assert.True(cache.TryFindPrototypeSource("PRIM739",out var selected));
        Assert.Equal(sphere?EnbW3dPrototypeKind.Sphere:EnbW3dPrototypeKind.Ring,selected.RegisteredPrototype!.Value.Kind);
        Assert.True(cache.TryLoadFile(source with {ResourceLocation=new("duplicate","duplicate",0,1)},out _,out error),error);
        Assert.True(cache.TryFindPrototypeSource("PRIM739",out var duplicate));
        Assert.Equal(selected.PrototypeRegistration,duplicate.PrototypeRegistration);
        var built=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,selected,Vector3.Zero,cache.CapturePrototypeSources(out _));
        Assert.Null(built.Resource);
        Assert.Equal(selected.PrototypeRegistration,built.FactoryPrototypeRegistration);
        if(sphere)Assert.Same(source.Model.Spheres[0],Assert.Single(built.Spheres!.Spheres).Asset.Definition);
        else Assert.Same(source.Model.Rings[0],Assert.Single(built.Rings!.Rings).Asset.Definition);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FirstAuthoredPrimitiveKindWinsTheSharedName(bool sphereFirst)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var sphere=Bytes(assets,true,"PRIM739");var ring=Bytes(assets,false,"PRIM739");
        var source=Source(assets,sphereFirst?[..sphere,..ring]:[..ring,..sphere],"PRIM739");
        using var cache=new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(source,out _,out var error),error);
        Assert.True(cache.TryFindPrototypeSource("PRIM739",out var selected));
        Assert.Equal(sphereFirst?EnbW3dPrototypeKind.Sphere:EnbW3dPrototypeKind.Ring,selected.RegisteredPrototype!.Value.Kind);
        var built=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,source,Vector3.Zero);
        Assert.Equal(sphereFirst,built.Spheres is not null);Assert.Equal(!sphereFirst,built.Rings is not null);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AggregateCreatesNamedInstanceAndPublishesButDiscardsChildren(bool sphere)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var source=Source(assets,Bytes(assets,sphere,"PRIM739"),"PRIM739");
        using var cache=new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(source,out _,out var error),error);
        var bytes=EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("WRAP739","PRIM739",[("MS03A","ROOT")],25,1);
        Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out error),error);
        Assert.True(cache.TryLoadFile(new("wrapper.w3d","WRAP739",new("wrapper.w3d","test",0,bytes.Length),file.Model,[]){PrototypeFile=file},out _,out error),error);
        Assert.True(cache.TryFindPrototypeSource("WRAP739",out var wrapper));
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(assets.FileResources,assets.RetailMixResources,wrapper,
            cache.CapturePrototypeSources(out _),out var resolved,out error),error);
        var built=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,resolved,Vector3.Zero,cache.CapturePrototypeSources(out _));
        Assert.True(built.ForceSubObjectLod);Assert.Empty(built.ModelAttachments);Assert.Null(built.Resource);
        Assert.Contains(built.PrototypeLoadSources,p=>p.Model.HierarchyModels.Any(m=>m.Name=="MS03A"));
        Assert.Equal("WRAP739",sphere?Assert.Single(built.Spheres!.Spheres).Asset.Header.Name:Assert.Single(built.Rings!.Rings).Asset.Header.Name);
        Assert.Equal("PRIM739",sphere?source.Model.Spheres[0].Header.Name:source.Model.Rings[0].Header.Name);
    }
    [Theory]
    [InlineData(false,130)] [InlineData(true,130)]
    [InlineData(false,1857)] [InlineData(true,1857)]
    [InlineData(false,1877)] [InlineData(true,1877)]
    public void CompositeChildUsesRetainedDefinitionWithoutMatchingFile(bool sphere,int parentId)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var primitive=Source(assets,Bytes(assets,sphere,"PRIM739"),"PRIM739");
        using var cache=new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(primitive,out _,out var error),error);
        var source=assets.BaseAssetResolver!.Resolve(parentId).W3d!;
        var aggregate=source.CurrentRetailAggregate!;
        source=source with {CurrentRetailAggregate=new(aggregate.RenderDefinition with
            {Attachments=[..aggregate.RenderDefinition.Attachments,new("PRIM739","ROOT")]},
            aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)
            {BaseRegisteredPrototype=aggregate.BaseRegisteredPrototype,BasePrototypeRegistration=aggregate.BasePrototypeRegistration,
             BaseLooseFilePath=aggregate.BaseLooseFilePath}};
        var built=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,source,Vector3.Zero,cache.CapturePrototypeSources(out _));
        if(sphere)Assert.Same(primitive.Model.Spheres[0],Assert.Single(built.Spheres!.Spheres,s=>s.Asset.Header.Name=="PRIM739").Asset.Definition);
        else Assert.Same(primitive.Model.Rings[0],Assert.Single(built.Rings!.Rings,r=>r.Asset.Header.Name=="PRIM739").Asset.Definition);
    }
    [Theory]
    [InlineData(false,130)] [InlineData(true,130)]
    [InlineData(false,1857)] [InlineData(true,1857)]
    [InlineData(false,1877)] [InlineData(true,1877)]
    public void CompositeRetainsPrimitiveAggregateChildAndItsDiscardedPublications(bool sphere,int parentId)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var primitive=Source(assets,Bytes(assets,sphere,"PRIM739"),"PRIM739");
        using var cache=new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(primitive,out _,out var error),error);
        var bytes=EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("WRAP739","PRIM739",[("MS03A","ROOT")],25,0);
        Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out error),error);
        Assert.True(cache.TryLoadFile(new("wrapper.w3d","WRAP739",new("wrapper.w3d","test",0,bytes.Length),file.Model,[]){PrototypeFile=file},out _,out error),error);
        var source=assets.BaseAssetResolver!.Resolve(parentId).W3d!;var aggregate=source.CurrentRetailAggregate!;
        source=source with {CurrentRetailAggregate=new(aggregate.RenderDefinition with
            {Attachments=[..aggregate.RenderDefinition.Attachments,new("WRAP739","ROOT")]},
            aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)
            {BaseRegisteredPrototype=aggregate.BaseRegisteredPrototype,BasePrototypeRegistration=aggregate.BasePrototypeRegistration,
             BaseLooseFilePath=aggregate.BaseLooseFilePath}};
        var built=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,source,Vector3.Zero,cache.CapturePrototypeSources(out _));
        Assert.Contains(built.PrototypeLoadSources,p=>p.Model.HierarchyModels.Any(m=>m.Name=="MS03A"));
        if(sphere)Assert.Same(primitive.Model.Spheres[0],Assert.Single(built.Spheres!.Spheres,s=>s.Asset.Header.Name=="WRAP739").Asset.Definition);
        else Assert.Same(primitive.Model.Rings[0],Assert.Single(built.Rings!.Rings,r=>r.Asset.Header.Name=="WRAP739").Asset.Definition);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RslCreationKeepsThePreparedPrimitiveChildNamespace(bool sphere)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var resolved=assets.BaseAssetResolver!.Resolve(1856);
        string name=resolved.Rsl!.ResourceGraph!.States.First(s=>s.Definition.Type==EnbRslStateType.Object).Definition.Object!;
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.RetailMixResources,name,out var original,out var error),error);
        var aggregate=original.CurrentRetailAggregate!;
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.RetailMixResources,aggregate.BaseRequestedObjectName,out var baseSource,out error),error);
        using var cache=new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(baseSource,out _,out error),error);
        var primitive=Source(assets,Bytes(assets,sphere,"PRIM739"),"PRIM739");
        Assert.True(cache.TryLoadFile(primitive,out _,out error),error);
        void Publish(byte[] bytes,string objectName)
        {
            Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out var why),why);
            Assert.True(cache.TryLoadFile(new(objectName+".w3d",objectName,new(objectName+".w3d","test",0,bytes.Length),file.Model,[]){PrototypeFile=file},out _,out why),why);
        }
        Publish(EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("WRAP739","PRIM739",[],25,0),"WRAP739");
        Publish(EnbR6Z11CurrentRetailAggregateTests.BuildAggregate(name,aggregate.BaseRequestedObjectName,
            [..aggregate.RenderDefinition.Attachments.Select(a=>(a.SubobjectName,a.BoneName)),("WRAP739","ROOT")],25,0),name);
        var prepared=EnbCurrentRetailRObjEffectAsset.PrepareFactory(assets,1856,Vector3.Zero,registeredPrototypes:cache.CapturePrototypeSources(out _));
        var built=prepared.Build();
        if(sphere)Assert.Same(primitive.Model.Spheres[0],Assert.Single(built.Spheres!.Spheres,s=>s.Asset.Header.Name=="WRAP739").Asset.Definition);
        else Assert.Same(primitive.Model.Rings[0],Assert.Single(built.Rings!.Rings,r=>r.Asset.Header.Name=="WRAP739").Asset.Definition);
    }
    [Theory]
    [InlineData(false,false,false)] [InlineData(true,false,false)]
    [InlineData(false,true,false)] [InlineData(true,true,false)]
    [InlineData(false,false,true)] [InlineData(true,false,true)]
    [InlineData(false,true,true)] [InlineData(true,true,true)]
    public void MeshAndPrimitiveCollisionKeepsFirstPublication(bool sphere,bool primitiveFirst,bool separateFiles)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var primitive=Bytes(assets,sphere,"SHIPMESH");
        string path=Path.GetTempFileName();
        try
        {
            EnbR4W3dRenderResourceTests.BuildResolvedResource(0,writePath:path);
            byte[] mesh=File.ReadAllBytes(path);
            for(int offset=0;offset<mesh.Length;)
            {
                int end=checked(offset+8+(int)(BitConverter.ToUInt32(mesh,offset+4)&0x7fffffffu));
                if(BitConverter.ToUInt32(mesh,offset)==0)
                    for(int child=offset+8;child<end;)
                    {
                        if(BitConverter.ToUInt32(mesh,child)==0x1f)mesh.AsSpan(child+8+24,16).Clear();
                        child=checked(child+8+(int)(BitConverter.ToUInt32(mesh,child+4)&0x7fffffffu));
                    }
                offset=end;
            }
            using var cache=new EnbW3dRenderResourceCache();
            byte[] first=primitiveFirst?primitive:mesh,second=primitiveFirst?mesh:primitive;
            var source=Source(assets,separateFiles?first:[..first,..second],"SHIPMESH");
            Assert.True(cache.TryLoadFile(source,out _,out var error),error);
            if(separateFiles)Assert.True(cache.TryLoadFile(Source(assets,second,"SHIPMESH") with
                {ResourceLocation=new("second.w3d","test",0,second.Length)},out _,out error),error);
            Assert.True(cache.TryFindPrototypeSource("SHIPMESH",out var selected));
            Assert.Equal(primitiveFirst?(sphere?EnbW3dPrototypeKind.Sphere:EnbW3dPrototypeKind.Ring):EnbW3dPrototypeKind.Mesh,selected.RegisteredPrototype!.Value.Kind);
            var built=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,selected,Vector3.Zero,cache.CapturePrototypeSources(out _));
            Assert.Equal(primitiveFirst,sphere?built.Spheres is not null:built.Rings is not null);
            Assert.Equal(!primitiveFirst,built.Resource?.Meshes.Count==1);
            if(!separateFiles)
            {
                var direct=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,source,Vector3.Zero);
                Assert.Equal(primitiveFirst,sphere?direct.Spheres is not null:direct.Rings is not null);
                Assert.Equal(!primitiveFirst,direct.Resource?.Meshes.Count==1);
            }
        }
        finally {File.Delete(path);}
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void IncompleteSiblingRemainsUnrepresented(bool sphere)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        byte[] empty=new byte[8];System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(empty,sphere?0x741u:0x742u);
        Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode([..Bytes(assets,sphere,"PRIM739"),..empty],out var file,out var error),error);
        Assert.Single(file.Entries);Assert.True(file.HasUnrepresentedRenderPrototypes);
    }
}
