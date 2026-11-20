using System.Numerics;
using System.Reflection;
using EnbNext.Assets;
using EnbNext.Rendering;

internal static class WarpAggregateBindingFixture
{
    public static EnbBaseAssetResolution Resolve(EnbRetailAssetBundle assets,int baseId,bool forceChild=true,bool unusedClip=false)
    {
        var resolved=assets.BaseAssetResolver!.Resolve(baseId);
        var source=resolved.W3d!;var aggregate=source.CurrentRetailAggregate!;
        var definition=aggregate.RenderDefinition with {Flags=forceChild ? aggregate.RenderDefinition.Flags|1u : aggregate.RenderDefinition.Flags&~1u,
            Attachments=aggregate.RenderDefinition.Attachments.Select(a=>a with {BoneName="missing-emitter-bone"}).ToArray()};
        if(unusedClip)
        {
            byte[] clip=new byte[88];
            void Word(int offset,uint value)=>System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(clip.AsSpan(offset),value);
            Word(0,0x200);Word(4,0x80000050);Word(8,0x201);Word(12,44);Word(16,0x40002);
            System.Text.Encoding.ASCII.GetBytes("UNREQUESTED").CopyTo(clip,20);
            System.Text.Encoding.ASCII.GetBytes(source.Model.Hierarchies.Single().Name).CopyTo(clip,36);
            Word(52,2);Word(56,30);Word(60,0x202);Word(64,20);Word(68,0x00010000);Word(72,1);
            Word(76,source.Model.Hierarchies.Single().Pivots.Count>1?1u:0u);
            Word(80,BitConverter.SingleToUInt32Bits(12345));Word(84,BitConverter.SingleToUInt32Bits(23456));
            if(!assets.FileResources.TryRead(aggregate.BaseLogicalFileName,out var bytes,out _,out var why) ||
                !EnbW3dModel.TryDecode([..bytes,..clip],out var combined))throw new Exception(why??"Warp unused clip fixture did not decode");
            source=source with {Model=combined};
        }
        if(Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_MISSING_MESH_TEXTURE")=="1")
            source=MeshTextureFailureFixture.Resolve(assets,resolved).W3d!;
        return resolved with {W3d=source with {CurrentRetailAggregate=new(definition,
            aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)}};
    }

    public static EnbWarpPlumeAsset Plume(EnbRetailAssetBundle assets)
    {
        var load=typeof(EnbWarpPlumeAsset).GetMethod("LoadAttachEffect",BindingFlags.Static|BindingFlags.NonPublic)!;
        var asset=(EnbWarpPlumeAsset)load.Invoke(null,[assets,710,Vector3.Zero,Resolve(assets,assets.PresentationCatalog!.FindEffect(710)!.BaseAssetId!.Value,
            unusedClip:Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_WARP_UNUSED_CLIP")=="1")])!;
        if(asset.Particles.Count==0 || asset.Particles.Any(p=>p.Pivot!=0))throw new Exception("Warp plume missing-bone fixture lost root binding");
        return asset;
    }

    public static EnbWarpTunnelAsset Tunnel(EnbRetailAssetBundle assets)
    {
        var load=typeof(EnbWarpTunnelAsset).GetMethod("Load",BindingFlags.Static|BindingFlags.NonPublic)!;
        var asset=(EnbWarpTunnelAsset)load.Invoke(null,[assets,Resolve(assets,216,unusedClip:Environment.GetEnvironmentVariable("ENB_SCANNER_PROBE_WARP_UNUSED_CLIP")=="1")])!;
        if(asset.Particles.Count!=7 || asset.Particles.Any(p=>p.Pivot!=0))throw new Exception("Warp tunnel missing-bone fixture lost root binding");
        return asset;
    }
}
