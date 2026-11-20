using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbWarpAggregateBindingTests
{
    [InstalledRetailTheory]
    [InlineData(0,false)] [InlineData(0,true)]
    [InlineData(710,false)] [InlineData(710,true)]
    [InlineData(674,false)] [InlineData(674,true)]
    public void ParticleTexturesAdmitEmptyAndNativeFailureImage(int effectId,bool empty)
    {
        var paths=EnbInstalledAssetLocator.TryLocate()!;
        var original=EnbRetailAssetBundle.Load(paths);
        int baseId=effectId==0 ? 216 : original.PresentationCatalog!.FindEffect(effectId)!.BaseAssetId!.Value;
        var resolved=original.BaseAssetResolver!.Resolve(baseId);
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext.Tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var files=new List<(string Name,byte[] Bytes)>();
            foreach(var name in resolved.W3d!.CurrentRetailAggregate!.RenderDefinition.Attachments.Select(a=>a.SubobjectName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Assert.True(original.RetailMixResources!.TryReadResource(name+".w3d",out var bytes,out _));
                bytes=bytes.ToArray();bool changed=false;
                void Change(int start,int end)
                {
                    for(int offset=start;offset<end;)
                    {
                        uint type=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                        uint size=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+4));
                        int next=checked(offset+8+(int)(size&0x7fffffff));
                        if(type==0x503)
                        {
                            bytes.AsSpan(offset+8,260).Clear();
                            if(!empty)System.Text.Encoding.ASCII.GetBytes("absent-warp-655.tga").CopyTo(bytes.AsSpan(offset+8));
                            changed=true;
                        }
                        else if(type==0x500)Change(offset+8,next);
                        offset=next;
                    }
                }
                Change(0,bytes.Length);Assert.True(changed);
                files.Add((name+".w3d",bytes));
            }
            using(var writer=new BinaryWriter(File.Create(Path.Combine(directory,"mixfile_art_01.mix"))))
            {
                uint directoryOffset=(uint)(16+files.Sum(f=>f.Bytes.Length));
                writer.Write(0x3158494du);writer.Write(directoryOffset);writer.Write(directoryOffset+4+(uint)files.Count*12);writer.Write(0u);
                foreach(var file in files)writer.Write(file.Bytes);
                writer.Write((uint)files.Count);uint cursor=16;
                for(int i=0;i<files.Count;i++){writer.Write(0x10000000u+(uint)i);writer.Write(cursor);writer.Write((uint)files[i].Bytes.Length);cursor+=(uint)files[i].Bytes.Length;}
                writer.Write((uint)files.Count);
                foreach(var file in files){var name=System.Text.Encoding.ASCII.GetBytes(file.Name+"\0");writer.Write((byte)name.Length);writer.Write(name);}
            }
            var assets=EnbRetailAssetBundle.Load(paths with {MixFilesDirectory=directory});
            var particles=effectId==0 ? EnbWarpTunnelAsset.Load(assets,resolved).Particles.Select(p=>(p.Asset.Definition,p.Asset.Texture,p.Asset.RuntimeTextureName)).ToArray() :
                EnbWarpPlumeAsset.LoadAttachEffect(assets,effectId,Vector3.Zero,resolved).Particles.Select(p=>(p.Asset.Definition,p.Asset.Texture,p.Asset.RuntimeTextureName)).ToArray();
            Assert.NotEmpty(particles);
            foreach(var particle in particles)
            {
                Assert.Null(particle.Texture);
                if(empty){Assert.Empty(particle.Definition.TextureFileName);Assert.Null(particle.RuntimeTextureName);}
                else Assert.Equal(EnbTextureFormat.CurrentRetailMissingArgb8,assets.Textures.Resolve(particle.RuntimeTextureName!).Image!.Format);
            }
        }
        finally {Directory.Delete(directory,true);}
    }

    [InstalledRetailTheory]
    [InlineData(0)] [InlineData(710)] [InlineData(674)]
    public void UnrequestedLibraryClipDoesNotChangeWarpPose(int effectId)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        int baseId=effectId==0 ? 216 : assets.PresentationCatalog!.FindEffect(effectId)!.BaseAssetId!.Value;
        var resolved=WarpAggregateBindingFixture.Resolve(assets,baseId,unusedClip:true);
        var resource=effectId==0 ? EnbWarpTunnelAsset.Load(assets,resolved).Resource :
            EnbWarpPlumeAsset.LoadAttachEffect(assets,effectId,Vector3.Zero,resolved).Resource;
        Assert.Contains(resource.Animations,a=>a.Name.Equals("UNREQUESTED",StringComparison.OrdinalIgnoreCase));
        var tree=resource.Hierarchies.Single();var world=Matrix4x4.CreateTranslation(10,20,30);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(tree,world,out var pose,out var why),why);
        Assert.Equal(world,pose[0]);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(tree,world,resource.Animations.Single(a=>a.Name.Equals("UNREQUESTED",StringComparison.OrdinalIgnoreCase)),0,null,out var animated,out why),why);
        if(tree.Pivots.Count>1)Assert.True(Vector3.Distance(pose[1].Translation,animated.WorldTransforms[1].Translation)>10000);
    }

    [InstalledRetailTheory]
    [InlineData(0,false)] [InlineData(0,true)]
    [InlineData(710,false)] [InlineData(710,true)]
    [InlineData(674,false)] [InlineData(674,true)]
    public void MissingEmitterBoneUsesRootWithOrWithoutForcedDetail(int effectId,bool forceChild)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        int baseId=effectId==0 ? 216 : assets.PresentationCatalog!.FindEffect(effectId)!.BaseAssetId!.Value;
        var resolved=assets.BaseAssetResolver!.Resolve(baseId);
        var source=resolved.W3d!;var aggregate=source.CurrentRetailAggregate!;
        var definition=aggregate.RenderDefinition with {Flags=forceChild?aggregate.RenderDefinition.Flags|1u:aggregate.RenderDefinition.Flags&~1u,
            Attachments=aggregate.RenderDefinition.Attachments.Select(a=>a with {BoneName="missing-emitter-bone"}).ToArray()};
        var changed=resolved with {W3d=source with {CurrentRetailAggregate=new(definition,
            aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)}};
        if(effectId==0)
        {
            var original=EnbWarpTunnelAsset.Load(assets,baseId);
            var loaded=EnbWarpTunnelAsset.Load(assets,changed);
            Assert.Equal(original.Particles.Count,loaded.Particles.Count);
            Assert.All(loaded.Particles,p=>Assert.Equal(0,p.Pivot));
            Assert.Equal(original.Particles.Select(p=>p.Asset.Definition.Name),loaded.Particles.Select(p=>p.Asset.Definition.Name));
            Assert.Equal(original.Resource.Meshes.Select(m=>m.Name),loaded.Resource.Meshes.Select(m=>m.Name));
        }
        else
        {
            var original=EnbWarpPlumeAsset.LoadAttachEffect(assets,effectId);
            var loaded=EnbWarpPlumeAsset.LoadAttachEffect(assets,effectId,Vector3.Zero,changed);
            Assert.Equal(original.Particles.Count,loaded.Particles.Count);
            Assert.All(loaded.Particles,p=>Assert.Equal(0,p.Pivot));
            Assert.Equal(original.Particles.Select(p=>p.Asset.Definition.Name),loaded.Particles.Select(p=>p.Asset.Definition.Name));
            Assert.Equal(original.Resource.Meshes.Select(m=>m.Name),loaded.Resource.Meshes.Select(m=>m.Name));
        }
    }
}
