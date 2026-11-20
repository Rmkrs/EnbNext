using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbSpecialMaterialTests
{
    [Theory]
    [InlineData(1u)] [InlineData(2u)] [InlineData(3u)] [InlineData(4u)] [InlineData(5u)]
    public void FilePublishesClass24AndRetainsEachResourceSelector(uint selector)
    {
        byte[] bytes = Root("special", Member(selector));
        Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes, out var file, out _));
        var entry = Assert.Single(file.Model.PrototypeEntries);
        Assert.Equal(EnbW3dPrototypeKind.SpecialMaterial, entry.Kind);
        var root = Assert.Single(file.Model.SpecialMaterials);
        Assert.Equal("special", root.Name);
        Assert.Equal(new Vector3(-1,-2,-3), root.Minimum);
        Assert.Equal(new Vector3(1,2,3), root.Maximum);
        var member = Assert.Single(root.Members);
        Assert.True(member.Complete);
        Assert.Equal(selector, member.Resource!.Selector);
        Assert.Equal("diffuse.tga", member.Resource.Texture);
        Assert.Equal("resource", member.Resource.Name);
        Assert.Equal(7u, member.Resource.Flags);
        Assert.Equal(new Vector3(.2f,.3f,.4f), member.Resource.Ambient);
        Assert.Equal(36, member.Streams[0xb43].Length);
        Assert.Equal(6, member.Streams[0xb45].Length);
        Assert.Single(file.Entries);
    }

    [Fact]
    public void ShortMemberReaderRetainsPrefixAndStopsOnlyItsRemainingReaders()
    {
        var member = Member(1, [Chunk(0xb43, Floats(1,2,3)), Chunk(0xb44,Floats(9,9,9))]);
        Assert.True(EnbCurrentRetailSpecialMaterialDefinition.TryDecode(Root("partial", member, Member(2)), out var root));
        Assert.Equal(2, root.Members.Length);
        Assert.False(root.Members[0].Complete);
        Assert.Equal(12, root.Members[0].Streams[0xb43].Length);
        Assert.False(root.Members[0].Streams.ContainsKey(0xb44));
        Assert.True(root.Members[1].Complete);
    }

    [Fact]
    public void UnknownChildrenAndRepeatedStreamsFollowNativeDispatch()
    {
        byte[] positions = Floats(1,2,3,4,5,6,7,8,9);
        var member = Member(4, [Chunk(0xb42,[1]),Chunk(0xb43,new byte[36]),Chunk(0xb43,positions),Chunk(0xb46,new byte[12])]);
        Assert.True(EnbCurrentRetailSpecialMaterialDefinition.TryDecode(Root("repeat",member),out var root));
        Assert.Equal(positions, root.Members[0].Streams[0xb43].ToArray());
        Assert.Equal(12, root.Members[0].Streams[0xb46].Length);
    }

    [Fact]
    public void RootChecksNameIdButHeaderAndMemberIdsAreNotAdmissionRules()
    {
        byte[] bytes = Root("ids", Member(3));
        int headerAt = 8 + Chunk(0xb01,Text("ids")).Length;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerAt),0xdead);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerAt+88),0xbeef);
        Assert.True(EnbCurrentRetailSpecialMaterialDefinition.TryDecode(bytes,out var root));
        Assert.Equal(3u,root.Members[0].Resource!.Selector);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8),0xdead);
        Assert.False(EnbCurrentRetailSpecialMaterialDefinition.TryDecode(bytes,out _));
    }

    [Theory]
    [InlineData(0u,false,1)] [InlineData(0u,true,2)] [InlineData(0x100u,true,2)]
    [InlineData(0x101u,false,3)] [InlineData(0x104u,true,3)] [InlineData(0x201u,true,2)]
    public void CapabilitySelectionUsesTheNativeMajorByteEquality(uint version,bool dot3,int mode)
    {
        var caps=new EnbRetailSpecialMaterialCapabilities(version,dot3,true);
        Assert.Equal(mode,caps.Mode(4));
        Assert.Equal(1,caps.Mode(3));
        foreach(uint fixedSelector in new uint[]{1,2,5})Assert.Equal(0,caps.Mode(fixedSelector));
    }

    [Fact]
    public void VertexFillPreservesAuxiliaryAndUvBitsAndUsesTheActualColorField()
    {
        var member=Member(4,[Chunk(0xb43,Floats(1,2,3,4,5,6,7,8,9)),
            Chunk(0xb44,Floats(3,0,0,0,4,0,0,0,5)),Chunk(0xb47,Floats(.25f,.75f,0,1,1,0)),
            Chunk(0xb49,Floats(2,3,4,5,6,7,8,9,10)),Chunk(0xb46,new byte[12])]);
        Assert.True(EnbCurrentRetailSpecialMaterialDefinition.TryDecode(Root("streams",member),out var root));
        Assert.True(EnbRetailSpecialMaterialGeometry.TryCreate(root.Members[0],out var geometry,out _));
        var bytes=geometry.Fill(4,3).ToArray();
        Assert.Equal(3*0x48,bytes.Length);
        Assert.Equal(3,BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(12)));
        Assert.Equal(uint.MaxValue,BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(.25f,BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(28)));
        Assert.Equal(2,BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(36)));
        Assert.All(bytes.AsSpan(48,24).ToArray(),b=>Assert.Equal(0,b));
        Assert.Equal(3*0x1c,geometry.Fill(5,0).Length);
        Assert.Equal(3*0x24,geometry.Fill(3,1).Length);
        Assert.Throws<ArgumentOutOfRangeException>(()=>geometry.Fill(3,2));
    }

    [Fact]
    public void CloneSharesDefinitionButNotDelegatesOrProjectionTransition()
    {
        Assert.True(EnbCurrentRetailSpecialMaterialDefinition.TryDecode(Root("owner",Member(4)),out var root));
        var owner=new EnbRetailSpecialMaterialInstance(root);
        Assert.True(owner.TryGetGeometry(0,out var first,out _));
        var projection=new object();owner.ObserveContext(8,projection);owner.ObserveContext(0,null);
        Assert.True(owner.AdmitsPass(4,3,1));Assert.False(owner.AdmitsPass(4,3,0));
        var clone=owner.Clone();Assert.Same(owner.Definition,clone.Definition);
        Assert.Equal(0,clone.CachedDelegateCount);Assert.False(clone.ProjectionPhase);
        Assert.True(clone.TryGetGeometry(0,out var second,out _));Assert.NotSame(first,second);
        owner.ObserveContext(0,null);Assert.False(owner.ProjectionPhase);Assert.Same(projection,owner.RetainedProjection);
    }

    [Theory]
    [InlineData(21u)] [InlineData(22u)] [InlineData(23u)] [InlineData(24u)] [InlineData(25u)] [InlineData(26u)]
    public void CubeLoadsAllPackedFormatsWithTheirOwnMipChain(uint format)
    {
        var bytes=Cube(format,4,4,3);
        Assert.True(EnbRetailCubeTexture.TryDecode(bytes,out var cube));
        Assert.Equal(6,cube.Faces.Length);
        foreach(var face in cube.Faces)
        {
            Assert.Equal(4,face.Width);Assert.Equal(4,face.Height);
            Assert.Equal(new[]{4,2,1},face.MipLevels.Select(m=>m.Width));
            Assert.All(face.MipLevels,m=>Assert.All(m.Pixels.ToArray(),pixel=>Assert.True(pixel.R>0)));
        }
        // Native DDS admission checks neither header size nor the cube flag.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(112),0xfc00);
        Assert.True(EnbRetailCubeTexture.TryDecode(bytes,out _));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(112),0xf800);
        Assert.False(EnbRetailCubeTexture.TryDecode(bytes,out _));
        Assert.False(EnbRetailCubeTexture.TryDecode(bytes.AsSpan(0,130),out _));
    }

    [Theory]
    [InlineData(21u)] [InlineData(24u)] [InlineData(0x31545844u)] [InlineData(0x35545844u)]
    public void CubeWithoutSquareFacesOrAllMipsIsMissingStorage(uint format)
    {
        // The D3DX resize that would complete such a cube is not carried (no
        // installed file is a cube DDS); it is treated as retail's NULL cube.
        Assert.False(EnbRetailCubeTexture.TryDecode(Cube(format,4,4,1),out _));
        Assert.False(EnbRetailCubeTexture.TryDecode(Cube(format,4,2,3),out _));
    }

    [Theory]
    [InlineData(0x31545844u)] [InlineData(0x32545844u)] [InlineData(0x33545844u)]
    [InlineData(0x34545844u)] [InlineData(0x35545844u)]
    public void CubeCompressedMipChainRetainsNativeBlocks(uint format)
    {
        Assert.True(EnbRetailCubeTexture.TryDecode(Cube(format,4,4,3),out var cube));
        Assert.Equal(6,cube.Faces.Length);
        foreach(var face in cube.Faces)
        {Assert.Equal(3,face.MipLevels.Count);Assert.Equal(new[]{4,2,1},face.MipLevels.Select(m=>m.Width));}
    }

    [Fact]
    public void CloneKeepsTextureLeasesAfterFileOwnerRetires()
    {
        Assert.True(EnbCurrentRetailSpecialMaterialDefinition.TryDecode(Root("lease",Member(4)),out var root));
        var registry=new EnbCurrentRetailTextureRegistry();
        var material=root.Members[0].Resource!;
        var textureOwner=new EnbRetailSpecialMaterialTextureOwner(material,
            name=>registry.Acquire(name,EnbW3dTextureSamplingSemantics.RetailDefault,null),Environment.CurrentDirectory);
        var original=new EnbRetailSpecialMaterialInstance(root);original.RetainTextures(_=>textureOwner);
        var clone=original.Clone();var state=clone.Textures(material)!.Base!.CurrentRetailTextureState;
        textureOwner.Dispose();original.Dispose();Assert.Equal(0,registry.CollectUnused());
        Assert.Same(state,clone.Textures(material)!.Base!.CurrentRetailTextureState);
        clone.Dispose();Assert.Equal(2,registry.CollectUnused());Assert.Throws<ObjectDisposedException>(()=>textureOwner.Retain());
        using var empty=new EnbRetailSpecialMaterialTextureOwner(material.Copy().SetTexture("").SetSecondTexture(""),
            _=>throw new Exception("Empty names must return NULL"),Environment.CurrentDirectory);
        Assert.Null(empty.Base);Assert.Null(empty.Normal);
    }

    [Fact]
    public void ObjectLightConstantsPreserveRawDirectionsAndUnusedVectorRegisters()
    {
        Assert.True(EnbCurrentRetailSpecialMaterialDefinition.TryDecode(Root("lights",Member(4)),out var root));
        var instance=new EnbRetailSpecialMaterialInstance(root);
        var lights=EnbW3dLightEnvironment.BlackNoLights with {SpecialMaterialObjectLights=new(new(.2f,.3f,.4f),[(new(2,3,4),new(.4f)),(new(5,6,7),new(.5f))])};
        var packet=EnbRetailSpecialMaterialDrawPacket.Build(instance,new("x","x",0,0),Matrix4x4.Identity,lights,0,1,1,1,out _).Single();
        Assert.True(EnbCurrentRetailScannerCameraRuntime.TryCreate(new(-4,0,0),Vector3.Zero,Vector3.Zero,1,new(800,600,1),4d/3,out var camera));
        var constants=new EnbRetailSpecialMaterialConstants();constants.SetDraw(packet,camera,EnbW3dLightEnvironment.BlackNoLights,Vector3.One,3,default);
        Assert.Equal(new Vector4(2,3,4,1),constants.Registers[16]);Assert.Equal(new Vector4(5,6,7,1),constants.Registers[17]);
        Assert.Equal(new Vector4(.04f,.09f,.16f,0),constants.Registers[24],new Vector4Comparer());
        packet=(packet).ForkTask().SetLightEnvironment(lights with {SpecialMaterialObjectLights=new(Vector3.Zero,[(Vector3.UnitX,Vector3.One)])});
        constants.SetDraw(packet,camera,EnbW3dLightEnvironment.BlackNoLights,Vector3.One,3,default);
        Assert.Equal(new Vector4(5,6,7,1),constants.Registers[17]);Assert.Equal(Vector4.Zero,constants.Registers[21]);
    }
    [Theory]
    [InlineData(3u,1)] [InlineData(4u,1)] [InlineData(4u,2)] [InlineData(4u,3)]
    public void AmbientAlphaComesFromLightOwnerNotShaderVariant(uint selector,int mode)
    {
        Assert.True(EnbCurrentRetailSpecialMaterialDefinition.TryDecode(Root("alpha",Member(selector)),out var root));
        var instance=new EnbRetailSpecialMaterialInstance(root);
        var lights=EnbW3dLightEnvironment.BlackNoLights with {SpecialMaterialObjectLights=new(Vector3.One,[(Vector3.UnitX,Vector3.Zero)])};
        var packet=EnbRetailSpecialMaterialDrawPacket.Build(instance,new("x","x",0,0),Matrix4x4.Identity,lights,0,1,1,1,out _).Single();
        Assert.True(EnbCurrentRetailScannerCameraRuntime.TryCreate(new(-4,0,0),Vector3.Zero,Vector3.Zero,1,new(800,600,1),4d/3,out var camera));
        var constants=new EnbRetailSpecialMaterialConstants();
        constants.SetDraw(packet,camera,EnbW3dLightEnvironment.BlackNoLights,Vector3.One,mode,default);
        Assert.Equal(0,constants.Registers[24].W);
        Assert.Equal(selector==4 && mode==3?0:1,constants.Registers[25].W);
        constants.SetDraw((packet).ForkTask().SetLightEnvironment(lights with {SpecialMaterialObjectLights=new(Vector3.One,[])}),camera,
            EnbW3dLightEnvironment.BlackNoLights,Vector3.One,mode,default);
        Assert.Equal(1,constants.Registers[24].W);
    }
    private sealed class Vector4Comparer : IEqualityComparer<Vector4>
    {public bool Equals(Vector4 a,Vector4 b)=>Vector4.Distance(a,b)<1e-6f;public int GetHashCode(Vector4 value)=>0;}

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompositeSpecialChildScalesPivotTranslationButNotGeometry(bool hlod)
    {
        Assert.True(EnbCurrentRetailSpecialMaterialDefinition.TryDecode(Root("child",Member(1)),out var definition));
        var hierarchy=new EnbW3dRenderHierarchy(1,"tree",Vector3.Zero,true,
            [new(0,"root",uint.MaxValue,Vector3.Zero,Vector3.Zero,Quaternion.Identity,Matrix4x4.Identity),
             new(1,"pivot",0,Vector3.UnitY,Vector3.Zero,Quaternion.Identity,Matrix4x4.CreateTranslation(Vector3.UnitY))],[]);
        var resource=new EnbW3dRenderResource(new("file","file",0,0),"file",[],[hierarchy],[],
            hlod?[]:[new(0,1,"model","tree",[new(0,EnbW3dRenderHierarchyNodeKind.RenderObject,"child",1)])],
            hlod?[new("model","tree",[new(0,0,0,[new(1,"child")])]){IsConvertedHierarchyModel=true}]:[])
            {SpecialMaterials=[definition],ExternalRenderObjects=[new("child",EnbW3dExternalRenderObjectKind.SpecialMaterial,"file","file"){SpecialMaterialDefinition=definition}]};
        var obj=new EnbSceneObjectSnapshot(93,1,43,false,false,7,0,1,0,0,0,0,false,"owner",null!,null,null,null,[],[],[],[],[],[],null,[],null,0,0);
        Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(obj,resource,Matrix4x4.CreateScale(7),0,
            EnbRenderPassKind.OrdinaryWorld,hlod?new("model",0):null,EnbW3dLightEnvironment.BlackNoLights,out var list,out var error,
            new(Matrix4x4.Identity,7)),error);
        var packet=Assert.Single(list.Packets);Assert.NotNull(packet.SpecialMaterial);
        Assert.Equal(Matrix4x4.CreateTranslation(0,7,0),packet.WorldTransform);
        Assert.True(packet.SpecialMaterial!.BindRetainedInstance);
    }

    private static byte[] Cube(uint format,int width,int height,int mips)
    {
        bool bc=format>26;int stride=bc?format==0x31545844?8:16:format<=22?4:2;
        int faceLength=0;
        for(int level=0,w=width,h=height;level<mips;level++,w=Math.Max(1,w>>1),h=Math.Max(1,h>>1))
            faceLength+=bc?((w+3)/4)*((h+3)/4)*stride:w*h*stride;
        byte[] bytes=new byte[128+6*faceLength];
        void W(int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
        W(0,0x20534444);W(12,(uint)height);W(16,(uint)width);W(28,(uint)mips);W(112,0xfe00);
        W(80,format is 21 or 25 or 26?0x41u:0x40u);W(84,bc?format:0);W(88,format<=22?32u:16u);
        W(96,format==23?0x7e0u:0x3e0u);W(104,format==21?0xff000000u:format==25?0x8000u:0xf000u);
        for(int i=128;i<bytes.Length;i++)bytes[i]=255;
        return bytes;
    }

    [Fact]
    public void IndexBeyondTheMemberVertexBufferStopsOnlyThatMember()
    {
        // 94F330/9512C0 would draw through an index outside the allocated vertex
        // buffer (undefined); the member stops locally and the FILE stays.
        byte[] indices=[0,0,1,0,3,0];
        var member=Member(4,[Chunk(0xb43,Floats(1,2,3,4,5,6,7,8,9)),Chunk(0xb45,indices)]);
        Assert.True(EnbCurrentRetailSpecialMaterialDefinition.TryDecode(Root("indices",member),out var root));
        Assert.False(EnbRetailSpecialMaterialGeometry.TryCreate(root.Members[0],out _,out string? why));
        Assert.Contains("outside its retained vertex buffer",why);
    }

    internal static byte[] Root(string name, params byte[][] members)
    {
        byte[] header = new byte[0x50];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x10),(uint)members.Length);
        Floats(-1,-2,-3,1,2,3,0,0,0,4).CopyTo(header,0x28);
        return Chunk(0xb00,Join([Chunk(0xb01,Text(name)),Chunk(0xb02,header),..members]));
    }
    internal static byte[] Member(uint selector,byte[][]? streams=null)
    {
        byte[] header = new byte[0x38];
        BinaryPrimitives.WriteUInt32LittleEndian(header,1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4),3);
        uint derivedId=selector switch {5=>0x16490470,2=>0x16490460,_=>0x16490450};
        var fields=new List<byte[]> {Micro(0,Text("diffuse.tga"))};
        if(selector is 3 or 4) fields.Add(Micro(1,Text("normal.tga")));
        fields.Add(Micro((byte)(selector is 3 or 4?2:1),Floats(.2f,.3f,.4f)));
        byte[] resource = Chunk(0xb40,Join([Chunk(0,UInt(selector)),Chunk(1,Join([
            Chunk(0x16490430,Join([Micro(0,Text("resource")),Micro(1,UInt(7))])),Chunk(derivedId,Join(fields.ToArray()))]))]));
        return Chunk(0xb20,Join([Chunk(0,header),resource,..streams??[Chunk(0xb43,new byte[36]),Chunk(0xb45,new byte[6])]]));
    }
    internal static byte[] Chunk(uint id,byte[] payload)=>Join([UInt(id),UInt((uint)payload.Length|0x80000000),payload]);
    private static byte[] Micro(byte id,byte[] payload)=>[(byte)id,(byte)payload.Length,..payload];
    private static byte[] UInt(uint value) {var b=new byte[4];BinaryPrimitives.WriteUInt32LittleEndian(b,value);return b;}
    private static byte[] Text(string value)=>Encoding.Latin1.GetBytes(value+'\0');
    private static byte[] Floats(params float[] values)=>Join(values.Select(v=>UInt(BitConverter.SingleToUInt32Bits(v))).ToArray());
    private static byte[] Join(byte[][] parts)=>parts.SelectMany(x=>x).ToArray();
}
