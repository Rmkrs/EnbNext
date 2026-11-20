using System.Buffers.Binary;
using System.Text.Json;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbMissileModelDecodeTests
{
    [Theory]
    [InlineData("MISSILE.W3D",2)]
    [InlineData("MS02A.W3D",4)]
    [InlineData("MS03A.W3D",1)]
    public void InstalledUnhintedModelsMatchHintedCopiesAndRejectTruncatedChildren(string name,int meshes)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        Assert.True(assets.RetailMixResources!.TryReadResource(name,out var raw,out _));
        Assert.True(EnbW3dModel.TryDecode(raw,out var original));
        Assert.Equal(meshes,original.Meshes.Count);
        Assert.Single(original.Hierarchies);Assert.Single(original.HierarchyModels);
        var hinted=raw.ToArray();
        void Hint(int start,int end,int depth)
        {
            for(int at=start;at<end;)
            {
                uint type=BinaryPrimitives.ReadUInt32LittleEndian(hinted.AsSpan(at));
                int length=(int)(BinaryPrimitives.ReadUInt32LittleEndian(hinted.AsSpan(at+4))&0x7fffffffu);
                if((depth==0 && type is 0 or 0x100 or 0x300) ||
                    (depth>0 && type is 0x2a or 0x2b or 0x30 or 0x31 or 0x38 or 0x48))
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(hinted.AsSpan(at+4),(uint)length|0x80000000u);
                    Hint(at+8,at+8+length,depth+1);
                }
                at+=8+length;
            }
        }
        Hint(0,hinted.Length,0);
        Assert.True(EnbW3dModel.TryDecode(hinted,out var copy));
        var options=new JsonSerializerOptions {IncludeFields=true};
        Assert.Equal(JsonSerializer.Serialize(original,options),JsonSerializer.Serialize(copy,options));
        // A missing hint never permits a child body to escape its parent.
        var corrupt=raw.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(corrupt.AsSpan(12),0x7fffffffu);
        Assert.False(EnbW3dModel.TryDecode(corrupt,out _));
        Assert.False(EnbW3dModel.TryDecode(raw.AsSpan(0,raw.Length-1),out _));
        // Also reject an overflowing nested texture record, past valid geometry.
        corrupt=raw.ToArray();bool damagedTexture=false;
        for(int at=0;at<raw.Length && !damagedTexture;)
        {
            uint type=BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at));
            int length=(int)(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at+4))&0x7fffffffu);
            if(type==0)
                for(int child=at+8;child<at+8+length;)
                {
                    uint childType=BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(child));
                    int childLength=(int)(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(child+4))&0x7fffffffu);
                    if(childType==0x30)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(corrupt.AsSpan(child+12),0x7fffffffu);
                        damagedTexture=true;break;
                    }
                    child+=8+childLength;
                }
            at+=8+length;
        }
        Assert.True(damagedTexture);
        Assert.False(EnbW3dModel.TryDecode(corrupt,out _));
    }
}
