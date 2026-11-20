using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;

internal static class MeshTextureFailureFixture
{
    public const string MissingName="~.tga";
    public static EnbBaseAssetResolution Resolve(EnbRetailAssetBundle assets,EnbBaseAssetResolution resolved)
    {
        if(assets.Textures.Resolve(MissingName).Image is not null)throw new Exception("Missing texture fixture unexpectedly exists");
        var source=resolved.W3d!;
        if(!assets.FileResources.TryRead(source.CurrentRetailAggregate!.BaseLogicalFileName,out var raw,out _,out var why))throw new Exception(why);
        raw=(byte[])raw.Clone();int changed=0;
        void Visit(int start,int end)
        {
            for(int offset=start;offset<end;)
            {
                uint kind=BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset));
                uint size=BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset+4));
                int length=checked((int)(size&0x7fffffffu));int payload=offset+8,next=checked(payload+length);
                if(next>end)throw new Exception("Truncated mesh texture fixture");
                if(kind is 0x32 or 0x1a)
                {
                    if(length<MissingName.Length+1)throw new Exception("Texture fixture name field too short");
                    raw.AsSpan(payload,length).Clear();Encoding.ASCII.GetBytes(MissingName).CopyTo(raw,payload);changed++;
                }
                else if(kind is 0 or 0x15 or 0x16 or 0x19 or 0x1e or 0x23 or 0x24 or 0x25 or 0x26 or 0x30 or 0x31)
                    Visit(payload,next);
                offset=next;
            }
        }
        Visit(0,raw.Length);
        if(changed==0 || !EnbW3dModel.TryDecode(raw,out var model))throw new Exception("Missing mesh texture fixture did not decode");
        return resolved with {W3d=source with {Model=model}};
    }
}
