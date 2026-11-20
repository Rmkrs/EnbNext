using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

internal static class EffectAggregateAnimationFixture
{
    public static byte[] Wrapper(string name,string baseName,params string[] children)
    {
        byte[] Chunk(uint type,byte[] payload,bool container=false)
        {
            byte[] result=new byte[payload.Length+8];BinaryPrimitives.WriteUInt32LittleEndian(result,type);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4),(uint)payload.Length|(container?0x80000000u:0));
            payload.CopyTo(result,8);return result;
        }
        byte[] header=new byte[20];BinaryPrimitives.WriteUInt32LittleEndian(header,0x40000);Encoding.ASCII.GetBytes(name).CopyTo(header,4);
        byte[] info=new byte[36+64*children.Length];Encoding.ASCII.GetBytes(baseName).CopyTo(info,0);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(32),(uint)children.Length);
        for(int i=0;i<children.Length;i++)
        {
            Encoding.ASCII.GetBytes(children[i]).CopyTo(info,36+64*i);
            Encoding.ASCII.GetBytes("ROOTTRANSFORM").CopyTo(info,68+64*i);
        }
        return Chunk(0x600,[..Chunk(0x601,header),..Chunk(0x602,info),..Chunk(0x604,new byte[20])],true);
    }
    public static string ModelSiblingPrototype(EnbRetailAssetBundle assets)
    {
        string? leaf=Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_HMODEL_DIRECT_LEAF");
        if(leaf=="box")return "V_DECEPT01A.OBJECT015";
        if(leaf=="null")return "ATTACHVOID";
        if(leaf=="dazzle")
        {
            if(!assets.FileResources.TryRead("sk_afburn01a.w3d",out var bytes,out _,out _)||!EnbW3dModel.TryDecode(bytes,out var model))throw new Exception("Dazzle fixture missing");
            return model.Dazzles.Single().Name;
        }
        return Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_HMODEL_CHILD_PROTOTYPE")??"RVC02B";
    }
    public static EnbCurrentRetailRObjEffectAsset DirectSkinAttachment(EnbRetailAssetBundle assets,int baseId,Action<EnbResolvedW3dResource>? observeSource=null)
    {
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,baseId);
        var resource=original.Resource!;var mesh=resource.Meshes.Single();var hlod=resource.HierarchicalLods.Single();
        if(mesh.BoneIndices.Count!=mesh.Positions.Count)throw new Exception("Direct skin fixture requires influenced mesh");
        if(!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,hlod.Name,out var source,out var error)||
            !assets.RetailMixResources!.TryReadResource(source.ResourceLocation,out var bytes))throw new Exception(error);
        byte[] Chunk(uint type,byte[] payload,bool container=false)
        {
            byte[] result=new byte[payload.Length+8];BinaryPrimitives.WriteUInt32LittleEndian(result,type);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4),(uint)payload.Length|(container?0x80000000u:0));
            payload.CopyTo(result,8);return result;
        }
        using var rewritten=new MemoryStream();
        for(int offset=0;offset<bytes.Length;)
        {
            uint type=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            int size=checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+4))&0x7fffffff));
            if(type!=0x700)rewritten.Write(bytes.AsSpan(offset,size+8));
            offset+=size+8;
        }
        byte[] header=new byte[40];BinaryPrimitives.WriteUInt32LittleEndian(header,0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4),1);
        Encoding.ASCII.GetBytes(hlod.Name).CopyTo(header,8);Encoding.ASCII.GetBytes(hlod.HierarchyName).CopyTo(header,24);
        rewritten.Write(Chunk(0x700,[..Chunk(0x701,header),..Chunk(0x702,Chunk(0x703,new byte[8]),true)],true));
        if(!EnbW3dModel.TryDecode(rewritten.ToArray(),out var model))throw new Exception("Direct skin parent did not decode");
        var definition=new EnbCurrentRetailW3dAggregateDefinition(0x10000,"skin-attach",hlod.Name,
            [new(EnbW3dRenderObjectNameSemantics.RegisteredMeshName(mesh),resource.Hierarchies.Single().Pivots.Last().Name)],0x19,1,false);
        var attachedSource=source with {Model=model,
            CurrentRetailAggregate=new(definition,source.LogicalFileName,source.RequestedObjectName,source.ResourceLocation)};
        observeSource?.Invoke(attachedSource);
        var attached=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,attachedSource,System.Numerics.Vector3.Zero);
        return attached with {Resource=attached.Resource!.Copy().SetAnimations(resource.Animations),Animation=original.Animation,AnimationFlags=original.AnimationFlags};
    }

    public static EnbCurrentRetailRObjEffectAsset SelfInitializedDormant(EnbRetailAssetBundle assets,bool selectedFirst,bool startsEnabled)
    {
        var original=assets.BaseAssetResolver!.Resolve(128);
        var name=original.Rsl!.ResourceGraph!.States.Single(s=>s.Definition.Type==EnbRslStateType.Object).Definition.Object;
        string selected=$"ID=10\nTYPE=object\nOBJECT={name}\nRS_STARTS_ENABLED={startsEnabled}\n";
        string dormant="ID=20\nTYPE=object\nOBJECT=SJH12\n";
        var states=EnbRetailRenderStateCatalog.ParseText("[0\\STATE0]\n"+(selectedFirst?selected:dormant)+"[0\\STATE1]\n"+(selectedFirst?dormant:selected));
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadBaseAssetsText("[BASE-31000]\nRSLID=0\nLINKID=10\nRSID=10\n");
        var resolved=new EnbRetailBaseAssetResolver(catalog,assets.MixResources,states,assets.RslResources).Resolve(31000);
        return EnbCurrentRetailRObjEffectAsset.Load(assets,resolved);
    }

    public static EnbCurrentRetailRObjEffectAsset HmodelSkin(EnbRetailAssetBundle assets,int baseId,bool skinConnection=true)
    {
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,baseId);
        var resource=original.Resource!;var mesh=resource.Meshes.Single();var hlod=resource.HierarchicalLods.Single();
        if(mesh.BoneIndices.Count!=mesh.Positions.Count || mesh.ContainerName!=hlod.Name || mesh.Name.Length>15 || hlod.Name.Length>15 || hlod.HierarchyName.Length>15)
            throw new InvalidDataException("Skin fixture requires one influenced mesh with HMODEL-compatible names");
        if(!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,hlod.Name,out var source,out var why))
            throw new InvalidDataException(why);
        if(!assets.RetailMixResources!.TryReadResource(source.ResourceLocation,out var bytes))throw new InvalidDataException("Skin fixture resource unavailable");
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.ASCII,true);
        for(int offset=0;offset<bytes.Length;)
        {
            uint type=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            int size=checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+4))&0x7fffffffu));
            if(type!=0x700)writer.Write(bytes.AsSpan(offset,size+8));
            offset+=size+8;
        }
        writer.Write(0x300u);writer.Write(0x8000004au);
        writer.Write(0x301u);writer.Write(40u);
        var header=new byte[40];BinaryPrimitives.WriteUInt32LittleEndian(header,0x40000);
        Encoding.ASCII.GetBytes(hlod.Name).CopyTo(header,4);Encoding.ASCII.GetBytes(hlod.HierarchyName).CopyTo(header,20);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(36),1);writer.Write(header);
        writer.Write(skinConnection?0x304u:0x302u);writer.Write(18u);
        var node=new byte[18];Encoding.ASCII.GetBytes(mesh.Name).CopyTo(node,0);writer.Write(node);
        if(!EnbW3dModel.TryDecode(stream.ToArray(),out var model))throw new InvalidDataException("HMODEL skin fixture did not decode");
        var loaded=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,source with {Model=model,RegisteredPrototype=null,CurrentRetailAggregate=null},System.Numerics.Vector3.Zero);
        return loaded with {Resource=loaded.Resource!.Copy().SetAnimations(resource.Animations),Animation=original.Animation,AnimationFlags=original.AnimationFlags};
    }

    public static EnbCurrentRetailRObjEffectAsset HmodelStreak(EnbRetailAssetBundle assets,int baseId,bool next,bool continuousOnly=false,string? particlePrototype=null,bool modelSibling=false,string? primitiveSibling=null,bool forceChild=true,string modelPrototype="RVC02B",bool inactiveReturn=false,bool inactiveFresh=false,bool deactivate=false,bool wrapModel=false,bool wrapperHasChildren=true,string? wrappedChildPrototype=null)
    {
        var animated=Load(assets,next,baseId,inactiveReturn:inactiveReturn,inactiveFresh:inactiveFresh,deactivate:deactivate);
        var resolved=assets.BaseAssetResolver!.Resolve(baseId);var source=resolved.W3d;
        if(source is null)
        {
            string name=resolved.Rsl!.ResourceGraph!.States.Single(s=>s.Definition.Type==EnbRslStateType.Object).Definition.Object!;
            if(!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,name,out source,out var why))throw new InvalidDataException(why);
        }
        var aggregate=source.CurrentRetailAggregate!;
        string streak=EnbCurrentRetailRObjEffectAsset.Load(assets,1856).Mixed!.Streaks.Single().Definition.Name;
        var hierarchy=animated.Resource!.Hierarchies.Single();
        int animatedPivot=animated.Animation!.Channels.Select(c=>c.PivotIndex).First(p=>p>0 &&
            EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy,hierarchy.Pivots[p].Name)==p);
        if(Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_SPACE_PIVOT")=="1")
        {
            if(!assets.RetailMixResources!.TryReadResource(aggregate.BaseResourceLocation,out var bytes))throw new Exception("Space pivot base missing");
            int changed=0;
            for(int root=0;root<bytes.Length;)
            {
                uint type=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(root));
                int size=(int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(root+4))&0x7fffffff);
                if(type==0x100)
                    for(int child=root+8;child<root+8+size;)
                    {
                        uint kind=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(child));
                        int length=(int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(child+4))&0x7fffffff);
                        if(kind==0x102)
                        {
                            int ordinal=animatedPivot-(hierarchy.HasSyntheticRetailRoot?1:0);
                            var name=bytes.AsSpan(child+8+ordinal*60,16);name.Clear();name[0]=32;changed++;
                        }
                        child+=8+length;
                    }
                root+=8+size;
            }
            if(changed!=1 || !EnbW3dModel.TryDecode(bytes,out var model))throw new Exception("Space pivot fixture did not decode one tree");
            source=source with {Model=model};
            if(!EnbW3dRenderResourceCompiler.TryCompile(source,out var compiled,out var error))throw new Exception(error);
            hierarchy=compiled.Hierarchies.Single();
            if(hierarchy.Pivots[animatedPivot].Name!=" " || EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy," ")!=animatedPivot)
                throw new Exception("Binary space pivot did not retain its non-root binding");
        }
        if(Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_LARGE_HIERARCHY")=="1")
        {
            if(!assets.RetailMixResources!.TryReadResource(aggregate.BaseResourceLocation,out var bytes))throw new Exception("Large hierarchy base missing");
            int rawParent=animatedPivot-(hierarchy.HasSyntheticRetailRoot?1:0);
            const int count=65_537;
            byte[] Chunk(uint kind,uint flags,byte[] payload)
            {
                byte[] result=new byte[payload.Length+8];
                BinaryPrimitives.WriteUInt32LittleEndian(result,kind);
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4),flags|(uint)payload.Length);
                payload.CopyTo(result,8);return result;
            }
            byte[] Rewrite(byte[] input,bool tree)
            {
                using var output=new MemoryStream();
                for(int offset=0;offset<input.Length;)
                {
                    uint kind=BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(offset));
                    uint word=BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(offset+4));
                    int size=(int)(word&0x7fffffff);
                    byte[] payload=input.AsSpan(offset+8,size).ToArray();
                    if(!tree && kind==0x100)payload=Rewrite(payload,true);
                    else if(tree && kind==0x101)BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20),count);
                    else if(tree && kind==0x102)
                    {
                        int original=payload.Length/60;Array.Resize(ref payload,count*60);
                        for(int pivot=original;pivot<count;pivot++)
                        {
                            var record=payload.AsSpan(pivot*60,60);
                            BinaryPrimitives.WriteUInt32LittleEndian(record[16..],(uint)rawParent);
                            BinaryPrimitives.WriteSingleLittleEndian(record[56..],1);
                        }
                        Encoding.ASCII.GetBytes("LARGE_LAST").CopyTo(payload,(count-1)*60);
                    }
                    output.Write(Chunk(kind,word&0x80000000,payload));offset+=size+8;
                }
                return output.ToArray();
            }
            if(!EnbW3dModel.TryDecode(Rewrite(bytes,false),out var model))throw new Exception("Large hierarchy did not decode");
            source=source with {Model=model};
            if(!EnbW3dRenderResourceCompiler.TryCompile(source,out var compiled,out var error))throw new Exception(error);
            hierarchy=compiled.Hierarchies.Single();
            animatedPivot=EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy,"LARGE_LAST");
            if(animatedPivot<65_536)throw new Exception("Large hierarchy attachment index was narrowed");
        }
        string bone=hierarchy.Pivots[animatedPivot].Name;
        var definition=aggregate.RenderDefinition with {Flags=forceChild?aggregate.RenderDefinition.Flags|1u:aggregate.RenderDefinition.Flags&~1u,
            Attachments=continuousOnly?[new(particlePrototype??"ms03a-pat",bone)]:[..aggregate.RenderDefinition.Attachments,new(streak,bone)]};
        if(modelSibling)definition=definition with {Attachments=[..definition.Attachments,new(modelPrototype,bone)]};
        if(primitiveSibling is "ring" or "both")definition=definition with {Attachments=[..definition.Attachments,
            new(assets.BaseAssetResolver.Resolve(1877).W3d!.CurrentRetailAggregate!.RenderDefinition.Attachments[0].SubobjectName,bone)]};
        if(primitiveSibling is "sphere" or "both")definition=definition with {Attachments=[..definition.Attachments,new("sk_clx01p2",bone)]};
        using var wrapperCache=new EnbW3dRenderResourceCache();
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? wrapperSources=null;
        if(modelPrototype=="ATTACHVOID")
        {
            byte[] bytes=new byte[56];BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x750);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),48);"ATTACHVOID"u8.CopyTo(bytes.AsSpan(24));
            if(!EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out var why)||
                !wrapperCache.TryLoadFile(new("attachvoid.w3d","ATTACHVOID",new("attachvoid","attachment-fixture",0,bytes.Length),file.Model,[]){PrototypeFile=file},out _,out why))throw new Exception(why);
            wrapperSources=wrapperCache.CapturePrototypeSources(out _);
        }
        if(wrapModel || Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_HMODEL_WRAPPED_LEAF")=="1")
        {
            if(!modelSibling)throw new Exception("Wrapped leaf requires model sibling");
            string nestedChild=wrappedChildPrototype??(Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_NESTED_LOD")=="1"?"RVC02B":"SPH13.SPH134");
            foreach(string name in new[]{modelPrototype,nestedChild})
            {
                if(wrapperCache.TryFindPrototypeSource(name,out _))continue;
                if(!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.RetailMixResources,name,out var leaf,out var why) ||
                    !wrapperCache.TryLoadFile(leaf,out _,out why))throw new Exception(why);
            }
            wrapperHasChildren &= Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_HMODEL_EMPTY_WRAPPER")!="1";
            byte[] bytes=Wrapper("LEAFWRAP",modelPrototype,wrapperHasChildren?[nestedChild]:[]);
            if(!EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out var error) ||
                !wrapperCache.TryLoadFile(new("leafwrap.w3d","LEAFWRAP",new("leafwrap","attachment-fixture",0,bytes.Length),file.Model,[]){PrototypeFile=file},out _,out error))throw new Exception(error);
            wrapperSources=wrapperCache.CapturePrototypeSources(out _);
            definition=definition with {Attachments=definition.Attachments.Select(a=>a.SubobjectName==modelPrototype?a with {SubobjectName="LEAFWRAP"}:a).ToArray()};
        }
        EnbCurrentRetailRObjEffectAsset Prepare(EnbRetailAssetBundle provider)=>EnbCurrentRetailRObjEffectAsset.LoadPrototype(provider,source with {CurrentRetailAggregate=new(definition,
            aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)},System.Numerics.Vector3.Zero,wrapperSources);
        EnbCurrentRetailRObjEffectAsset asset;
        if(Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_AGGREGATE_LOOSE_CHILD")=="1")
        {
            if(!modelSibling || !assets.FileResources.TryRead(modelPrototype+".w3d",out var originalChild,out _,out _))throw new Exception("Loose model child fixture unavailable");
            var altered=(byte[])originalChild.Clone();bool renamed=false;
            for(int offset=0;offset<altered.Length;)
            {
                uint kind=BinaryPrimitives.ReadUInt32LittleEndian(altered.AsSpan(offset));
                int size=checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(altered.AsSpan(offset+4))&0x7fffffff)+8);
                if(kind==0x700)
                {
                    altered.AsSpan(offset+24,16).Clear();Encoding.ASCII.GetBytes("LOOSE.CHILD").CopyTo(altered,offset+24);renamed=true;
                }
                offset+=size;
            }
            if(!renamed)throw new Exception("Loose model child fixture requires an HLOD");
            string directory=Path.Combine(Path.GetTempPath(),"EnbNext-AggregateChild-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string childPath=Path.Combine(directory,"LOOSE.CHILD.w3d");
                File.WriteAllBytes(Path.Combine(directory,"LOOSE.w3d"),originalChild);
                File.WriteAllBytes(childPath,altered);
                definition=definition with {Attachments=definition.Attachments.Select(a=>a.SubobjectName==modelPrototype?a with {SubobjectName="LOOSE.CHILD"}:a).ToArray()};
                string executable=Path.Combine(directory,"client.exe");
                //The missing-texture image is read from the controlling EXE;
                //the isolated file-provider fixture must retain that source.
                File.Copy(assets.Paths.ClientExecutablePath,executable);
                var provider=EnbRetailAssetBundle.Load(assets.Paths with {ClientExecutablePath=executable});
                asset=Prepare(provider);
                void RetainImages(EnbCurrentRetailRObjEffectAsset prepared)
                {
                    string prefix="retail-missing-image:"+executable+":";
                    foreach(var key in prepared.Resource?.Meshes.SelectMany(m=>m.MaterialPasses).SelectMany(p=>p.TextureStages)
                        .SelectMany(s=>s.Textures).Select(t=>t.LogicalName).Distinct()??[])
                        if(key.StartsWith(prefix,StringComparison.Ordinal))assets.Textures.ResolveCurrentRetailMissingTexture(key[prefix.Length..],executable);
                    foreach(var model in prepared.ModelAttachments)RetainImages(model.Asset);
                }
                RetainImages(asset);
                var child=asset.ModelAttachments.Single(a=>a.Name=="LOOSE.CHILD").Asset;
                if(child.Resource!.Key.ArchivePath!=childPath || child.Resource.HierarchicalLods.Single().Name!="LOOSE.CHILD")
                    throw new Exception("Aggregate model child bypassed its full-name loose fallback");
            }
            finally {Directory.Delete(directory,true);}
        }
        else asset=Prepare(assets);
        if(Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_MISSING_SPRITE_TEXTURE")=="1")
        {
            EnbCurrentRetailRObjParticleAsset Missing(EnbCurrentRetailRObjParticleAsset sprite)
            {
                var result=(sprite with {Definition=sprite.Definition.Copy().SetTextureFileName("missing-sprite-653.tga")}).PrepareTexture(assets);
                if(result.RuntimeTextureName is null || assets.Textures.Resolve(result.RuntimeTextureName).Image?.Format!=EnbTextureFormat.CurrentRetailMissingArgb8)
                    throw new Exception("Missing sprite did not retain the native failure image");
                return result;
            }
            asset=asset with {AggregateParticles=asset.AggregateParticles?.Select(Missing).ToArray(),
                Mixed=asset.Mixed is {} group?group with {Sprites=group.Sprites.Select(Missing).ToArray()}:null};
        }
        var resource=asset.Resource!.Copy().SetAnimations(animated.Resource.Animations);
        return asset with {Resource=resource,Animation=animated.Animation,AnimationFlags=animated.AnimationFlags,Sequence=animated.Sequence,
            Rings=asset.Rings is {} rings?rings with {Resource=resource,Animation=animated.Animation,AnimationFlags=animated.AnimationFlags}:null,
            Spheres=asset.Spheres is {} spheres?spheres with {Resource=resource,Animation=animated.Animation,AnimationFlags=animated.AnimationFlags}:null,
            Mixed=asset.Mixed is {} mixed ? mixed with {Resource=resource,Animation=animated.Animation,AnimationFlags=animated.AnimationFlags}:null};
    }

    public static EnbCurrentRetailRObjEffectAsset Load(EnbRetailAssetBundle assets,bool next=false,int baseId=230,bool reuseClip=false,bool cycle=false,bool singleFrameNext=false,bool inactiveReturn=false,bool inactiveFresh=false,bool deactivate=false,bool nextOnce=false,bool activateWithoutTarget=false,int? initializationArgument=null,bool startsEnabled=false,bool stop=true,bool stopOnce=false,bool initializeLink=false,bool directLink=false,bool messagePrefix=false,bool pivotMaps=false,bool copyOnlyFlags=false,string? animationFields=null)
    {
        animationFields ??= Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_RSL_ANIMATION_FIELDS");
        pivotMaps |= Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_RSL_PIVOT_MAPS")=="1";
        copyOnlyFlags |= Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_RSL_COPY_ONLY_FLAGS")=="1";
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,baseId);
        var source=assets.BaseAssetResolver!.Resolve(baseId);
        var resource=original.Resource;
        if(resource is null && !EnbW3dRenderResourceCompiler.TryCompile(source.W3d!,out resource,out var why))
            throw new InvalidDataException(why);
        var pivots=resource.HierarchyModels.Count==1
            ? resource.HierarchyModels.Single().Nodes.Select(n=>(int)n.PivotIndex)
                .Concat((original.AggregateParticles??[]).Select(p=>p.PivotIndex)).Where(p=>p>0).Distinct().ToArray()
            : resource.Hierarchies.Single().Pivots.Select((_,i)=>i).Where(i=>i>0).ToArray();
        byte[] clip=new byte[60+28*pivots.Length];
        void Word(int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(clip.AsSpan(offset),value);
        Word(0,0x200);Word(4,0x80000000u+(uint)clip.Length-8);Word(8,0x201);Word(12,44);Word(16,0x40002);
        Encoding.ASCII.GetBytes("MOVE").CopyTo(clip,20);
        Encoding.ASCII.GetBytes(resource.Hierarchies.Single().Name).CopyTo(clip,36);
        Word(52,2);Word(56,2);
        for(int i=0;i<pivots.Length;i++)
        {
            int offset=60+28*i;
            Word(offset,0x202);Word(offset+4,20);Word(offset+8,0x00010000);Word(offset+12,1);Word(offset+16,(uint)pivots[i]);
            Word(offset+20,BitConverter.SingleToUInt32Bits(0));Word(offset+24,BitConverter.SingleToUInt32Bits(80));
        }
        byte[] payload=clip;
        if(next)
        {
            byte[] second=(byte[])clip.Clone();second.AsSpan(20,16).Clear();Encoding.ASCII.GetBytes("MOVE2").CopyTo(second,20);
            for(int i=0;i<pivots.Length;i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(second.AsSpan(80+28*i),40);
                BinaryPrimitives.WriteSingleLittleEndian(second.AsSpan(84+28*i),120);
            }
            payload=[..clip,..second];
            if(singleFrameNext)
            {
                second=new byte[60+24*pivots.Length];
                clip.AsSpan(0,60).CopyTo(second);
                BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(4),0x80000000u+(uint)second.Length-8);
                second.AsSpan(20,16).Clear();Encoding.ASCII.GetBytes("MOVE2").CopyTo(second,20);
                BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(52),1);
                for(int i=0;i<pivots.Length;i++)
                {
                    int offset=60+24*i;
                    BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(offset),0x202);
                    BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(offset+4),16);
                    BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(offset+8),0);
                    BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(offset+12),1);
                    BinaryPrimitives.WriteUInt32LittleEndian(second.AsSpan(offset+16),(uint)pivots[i]);
                    BinaryPrimitives.WriteSingleLittleEndian(second.AsSpan(offset+20),40);
                }
                payload=[..clip,..second];
            }
        }
        if(Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_ANIMATION_KEYS")=="1")
        {
            payload=AnimationCurveFixture.ExpandClassicFixture(payload);
            if(!EnbW3dModel.TryDecode(payload,out var expanded) || expanded.Animations.Any(a=>
                a.CompressedChannels.Any(c=>c.TimeCodes.Count!=AnimationCurveFixture.LargeCount) ||
                a.CompressedBitChannels.Any(c=>c.TimeCodes.Count!=AnimationCurveFixture.LargeCount)))
                throw new InvalidDataException("Expanded sparse channels failed binary admission");
        }
        if(Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_ANIMATION_CHUNKS")=="1")
            payload=ExtendAnimationChunks(payload);
        if(Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_ANIMATION_DURATION")=="1")
        {
            for(int root=0;root<payload.Length;)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(root+52),1_000_001);
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(root+56),1_000_001);
                root+=8+(int)(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(root+4))&0x7fffffff);
            }
            if(!EnbW3dModel.TryDecode(payload,out var durationModel) || durationModel.Animations.Any(a=>a.NumFrames!=1_000_001 || a.FrameRate!=1_000_001))
                throw new InvalidDataException("Extended duration/rate animation did not survive binary decoding");
        }
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-hmodel-attachment-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);string package=Path.Combine(directory,"mixfile_art_clip.mix");
        try
        {
            const string file="fixture.w3d";
            using(var writer=new BinaryWriter(File.Create(package)))
            {
                writer.Write(0x3158494du);writer.Write(16);writer.Write(32);writer.Write(0);writer.Write(1);
                writer.Write(EnbCurrentRetailMixSource.ComputeNameHash(Encoding.ASCII.GetBytes(file),c=>c is >=97 and <=122?c-32:c));
                writer.Write(80);writer.Write(payload.Length);writer.Write(1);
                byte[] name=Encoding.ASCII.GetBytes(file+"\0");writer.Write((byte)name.Length);writer.Write(name);
                writer.BaseStream.Position=80;writer.Write(payload);
            }
            string objectName=source.W3d?.RequestedObjectName ?? source.Rsl!.ResourceGraph!.States.Single(s=>s.Definition.Type==EnbRslStateType.Object).Definition.Object!;
            var states=EnbRetailRenderStateCatalog.ParseText($"[0\\STATE0]\nID=0\nTYPE=object\nOBJECT={objectName}\nLINK=1\nRS_STARTS_ENABLED={startsEnabled}\n"+
                $"[0\\STATE1]\nID=1\nTYPE=anim\nFILE=fixture.w3d\nANIM=MOVE\nRS_STOP={stop}\nRS_STOP_ONCE={stopOnce}\n"+
                (animationFields??"")+
                (copyOnlyFlags?"RS_REMOVE_PROTOTYPE=true\nRS_STATIONARY=true\n":"")+
                (nextOnce?"RS_ACTIVATE_NEXT_ONCE=true\n":"")+
                (activateWithoutTarget?"RS_ACTIVATE_NEXT=true\n":"")+
                (next?$"NEXT=2\n{(nextOnce?"":"RS_ACTIVATE_NEXT=true\n")}[0\\STATE2]\nID=2\nTYPE=anim\nFILE=fixture.w3d\nANIM={(reuseClip?"MOVE":"MOVE2")}\nRS_STOP=true\n"+(cycle?"NEXT=1\nRS_ACTIVATE_NEXT=true\n":inactiveReturn?"NEXT=1\n":""):"")+
                (next?animationFields??"":"")+
                (copyOnlyFlags && next?"RS_REMOVE_PROTOTYPE=true\nRS_STATIONARY=true\n":"")+
                (deactivate?"RS_DEACTIVATE=true\n":"")+
                (inactiveFresh?$"NEXT={(next?3:2)}\n[0\\STATE{(next?3:2)}]\nID={(next?3:2)}\nTYPE=anim\nFILE=fixture.w3d\nANIM=INACTIVE.NEVER_BOUND\nRS_STOP=true\n":"")+
                (initializeLink || directLink || messagePrefix?$"[0\\STATE{(next?3:2)+(inactiveFresh?1:0)}]\nID=10\nTYPE=link\nLINK={(initializeLink?99999:0)}\n":"")+
                (messagePrefix?$"[0\\STATE{(next?4:3)+(inactiveFresh?1:0)}]\nID=11\nTYPE=message\nMESSAGE=99999\nLINK=10\n":"")+
                (pivotMaps?"[0\\STATE1\\PIVOTMAP0]\nBONE=ROOT\nWEIGHT=0\n[0\\STATE1\\PIVOTMAP1]\nBONE=MISSING\nWEIGHT=-2\n"+
                    (next?"[0\\STATE2\\PIVOTMAP0]\nBONE=ROOT\nWEIGHT=0\n":""):""));
            var catalog=new EnbRetailPresentationCatalog();catalog.LoadBaseAssetsText("[BASE-31000]\nRSLID=0\n"+(messagePrefix?(initializationArgument is int messageArgument?$"LINKID=11\nRSID={messageArgument}\n":"RSID=11\n"):directLink?"RSID=10\n":initializeLink?$"LINKID=10\nRSID={initializationArgument??0}\n":initializationArgument is int argument?$"LINKID=0\nRSID={argument}\n":"RSID=0\n"));
            var files=new EnbRetailRslResourceResolver(EnbMixResourceCatalog.Load(directory));
            var resolved=new EnbRetailBaseAssetResolver(catalog,assets.MixResources,states,files).Resolve(31000);
            return EnbCurrentRetailRObjEffectAsset.Load(assets,resolved);
        }
        finally {File.Delete(package);Directory.Delete(directory);}
    }

    private static byte[] ExtendAnimationChunks(byte[] source)
    {
        using var output=new MemoryStream();using var writer=new BinaryWriter(output);
        for(int root=0;root<source.Length;)
        {
            int size=(int)(BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(root+4))&0x7fffffff);
            using var body=new MemoryStream();using var children=new BinaryWriter(body);
            for(int offset=root+8;offset<root+8+size;)
            {
                uint type=BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset));
                int length=(int)(BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset+4))&0x7fffffff);
                children.Write(type);children.Write(0x80000000u|(uint)(length+(type==0x201?3:0)));
                children.Write(source,offset+8,length);
                if(type==0x201)
                {
                    children.Write(new byte[]{255,255,255});
                    children.Write(0x7fff0101u);children.Write(0x80000003u);children.Write(new byte[]{255,255,255});
                    children.Write(0x201u);children.Write(0x80000000u);
                }
                offset+=8+length;
            }
            writer.Write(0x200u);writer.Write((uint)body.Length);writer.Write(body.ToArray());root+=8+size;
        }
        return output.ToArray();
    }
}
