using System.Buffers.Binary;
using EnbNext.Assets;
using EnbNext.Rendering;

internal static class RingMeshLevelsFixture
{
    public static EnbCurrentRetailRObjEffectAsset Load(EnbRetailAssetBundle assets,bool proxyMetadata=false)
        => EnbCurrentRetailRObjEffectAsset.Load(assets,Resolve(assets,proxyMetadata));

    public static EnbBaseAssetResolution Resolve(EnbRetailAssetBundle assets,bool proxyMetadata=false)
    {
        var resolved=assets.BaseAssetResolver!.Resolve(1877);
        var source=resolved.W3d!;
        if(!EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out var why))throw new InvalidDataException(why);
        var hlod=resource.HierarchicalLods.Single();
        if(!EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(resource,hlod,_=>true,out var costs,out why))throw new InvalidDataException(why);
        int copies=checked((int)Math.Ceiling(18000d/costs.Single()));
        if(!assets.FileResources.TryRead(source.CurrentRetailAggregate!.BaseLogicalFileName,out var raw,out _,out why))throw new InvalidDataException(why);
        static uint Type(byte[] chunk)=>BinaryPrimitives.ReadUInt32LittleEndian(chunk);
        static byte[][] Chunks(byte[] bytes,int begin)
        {
            var result=new List<byte[]>();
            for(int offset=begin;offset<bytes.Length;)
            {
                int length=checked(8+(int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+4))&0x7fffffffu));
                result.Add(bytes.AsSpan(offset,length).ToArray());offset+=length;
            }
            return result.ToArray();
        }
        static byte[] Container(uint type,IEnumerable<byte[]> children)
        {
            byte[] payload=children.SelectMany(c=>c).ToArray();byte[] result=new byte[8+payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(result,type);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4),0x80000000u+(uint)payload.Length);
            payload.CopyTo(result,8);return result;
        }
        var top=Chunks(raw,0);int at=Array.FindIndex(top,c=>Type(c)==0x700);
        var children=Chunks(top[at],8);var header=children.Single(c=>Type(c)==0x701);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12),2);
        var level=Chunks(children.Single(c=>Type(c)==0x702),8);
        var members=level.Where(c=>Type(c)==0x704).ToArray();
        byte[] Level(byte[][] entries)
        {
            var count=(byte[])level.Single(c=>Type(c)==0x703).Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(count.AsSpan(8),(uint)entries.Length);
            return Container(0x702,new[]{count}.Concat(entries));
        }
        top[at]=Container(0x700,[header,Level([members[0]]),Level(Enumerable.Range(0,copies).SelectMany(_=>members).ToArray())]);
        byte[] updated=top.SelectMany(c=>c).ToArray();
        if(!EnbW3dModel.TryDecode(proxyMetadata?HlodProxyMetadataFixture.Append(updated):updated,out var model))throw new InvalidDataException("Synthetic ring HLOD levels did not decode");
        return resolved with {W3d=source with {Model=model}};
    }
}
