using System.Numerics;
using System.Reflection;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationBeamTextureTests
{
    [InstalledRetailTheory]
    [InlineData("av_tele01",false)] [InlineData("av_tele01",true)]
    [InlineData("av_tele02",false)] [InlineData("av_tele02",true)]
    [InlineData("av_tele03",false)] [InlineData("av_tele03",true)]
    [InlineData("av_tele04",false)] [InlineData("av_tele04",true)]
    public void TeleportParticlesRetainEmptyOrFailedTexture(string name,bool empty)
    {
        var paths=EnbInstalledAssetLocator.TryLocate()!;
        var original=EnbRetailAssetBundle.Load(paths);
        var resources=original.RetailMixResources!;
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext.Tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
            byte[] Add(string file){Assert.True(resources.TryReadResource(file,out var bytes,out _));return files[file]=bytes.ToArray();}
            Assert.True(EnbCurrentRetailW3dAggregateDecoder.TryDecode(Add(name+".w3d"),out var aggregate,out _));
            Add(aggregate.BaseModelName+".w3d");
            if(resources.TryReadResource(aggregate.BaseModelName+"-1.w3d",out var animation,out _))files[aggregate.BaseModelName+"-1.w3d"]=animation;
            foreach(var emitter in aggregate.Attachments.Select(a=>a.SubobjectName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var bytes=Add(emitter+".w3d");bool changed=false;
                for(int offset=8;offset<bytes.Length;)
                {
                    uint type=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                    int size=(int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+4))&0x7fffffff);
                    if(type==0x503)
                    {
                        bytes.AsSpan(offset+8,260).Clear();
                        if(!empty)System.Text.Encoding.ASCII.GetBytes("absent-teleport-656.tga").CopyTo(bytes.AsSpan(offset+8));
                        changed=true;
                    }
                    offset=checked(offset+8+size);
                }
                Assert.True(changed);
            }
            using(var writer=new BinaryWriter(File.Create(Path.Combine(directory,"mixfile_art_01.mix"))))
            {
                uint offset=(uint)(16+files.Values.Sum(b=>b.Length));
                writer.Write(0x3158494du);writer.Write(offset);writer.Write(offset+4+(uint)files.Count*12);writer.Write(0u);
                foreach(var bytes in files.Values)writer.Write(bytes);
                writer.Write((uint)files.Count);uint cursor=16,index=0;
                foreach(var bytes in files.Values){writer.Write(0x10000000u+index++);writer.Write(cursor);writer.Write((uint)bytes.Length);cursor+=(uint)bytes.Length;}
                writer.Write((uint)files.Count);
                foreach(var file in files.Keys){var bytes=System.Text.Encoding.ASCII.GetBytes(file+"\0");writer.Write((byte)bytes.Length);writer.Write(bytes);}
            }
            var assets=EnbRetailAssetBundle.Load(paths with {MixFilesDirectory=directory});
            var beam=new EnbStationBeamEffect(assets,name);
            var emitters=(List<EnbW3dParticleEmitterDrawInstance>)typeof(EnbStationBeamEffect).GetField("_emitters",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(beam)!;
            Assert.NotEmpty(emitters);
            Assert.True(EnbResolvedRenderCamera.TryCreate(new(100,100,100),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,100000),out var camera));
            //Drive actual scene frames through the authored visibility window;
            //a late first observation no longer reconstructs historical births.
            var packets=Enumerable.Range(0,(int)Math.Ceiling(beam.OwnerDurationMilliseconds/17)+2)
                .SelectMany(i=>beam.Build(i*17L,Matrix4x4.Identity,camera))
                .Where(p=>p.Mesh.Name.EndsWith(".ParticleBuffer",StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(packets);
            foreach(var packet in packets)
            {
                var bindings=packet.Mesh.MaterialPasses.SelectMany(p=>p.TextureStages).SelectMany(s=>s.Textures).ToArray();
                if(empty)Assert.Empty(bindings);
                else {Assert.NotEmpty(bindings);Assert.All(bindings,b=>{Assert.True(b.ResolveLogicalNameAtRuntime);Assert.Equal(EnbTextureFormat.CurrentRetailMissingArgb8,assets.Textures.Resolve(b.LogicalName).Image!.Format);});}
            }
            Assert.Empty(beam.Build((long)Math.Ceiling(beam.TailDurationMilliseconds)+1,Matrix4x4.Identity,camera));
        }
        finally {Directory.Delete(directory,true);}
    }
}
