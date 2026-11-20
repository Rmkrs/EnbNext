using System.Numerics;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
public sealed class EnbMeshFrustumAdmissionTests
{
    private static EnbW3dRenderMesh Mesh()=>new(0,"box","",2,[],[],[],[],[],[],[])
        {BoundingBoxMin=-Vector3.One,BoundingBoxMax=Vector3.One,GeometryVersion=0x40001};
    private static EnbW3dDrawTask Packet(EnbW3dRenderMesh mesh,Vector3 position)=>
        new(default,0,0,0,Matrix4x4.CreateTranslation(position),default,mesh,null,null!,null!,0,null,0,"box");
    private static EnbResolvedRenderCamera Camera(Vector3 position=default)
    {
        Assert.True(EnbResolvedRenderCamera.TryCreate(position,Quaternion.Identity,new(1920,1080,1),
            Math.PI/2,new(1,100),out var camera));return camera;
    }
    [Fact]
    public void DirectWorldBoxMatchesLiteralRetailStorageIncludingScaleAndShear()
    {
        var random=new Random(0x8c2f30);
        for(int sample=0;sample<2048;sample++)
        {
            float Next()=>(float)(random.NextDouble()*200-100);
            var world=new Matrix4x4(Next(),Next(),Next(),0,Next(),Next(),Next(),0,Next(),Next(),Next(),0,Next(),Next(),Next(),1);
            var center=new Vector3(Next(),Next(),Next());var extent=new Vector3(Next(),Next(),Next());
            var expected=EnbCurrentRetailRenderBoundsStorage.Calculate(world,Vector4.Zero,center,extent);
            var actual=EnbCurrentRetailMeshVisibility.TransformBox(world,center,extent);
            Assert.Equal(expected.BoxCenter,actual.Center);Assert.Equal(expected.BoxExtent,actual.Extent);
        }
    }
    [Fact]
    public void UsesCurrentCameraAndBoxInsteadOfOriginOrRootSphere()
    {
        var admission=new EnbCurrentRetailMeshVisibility();var mesh=Mesh();var camera=Camera();
        Assert.True(admission.Admits(Packet(mesh,new(10,0,0)),camera));
        Assert.False(admission.Admits(Packet(mesh,new(-10,0,0)),camera));
        Assert.False(admission.Admits(Packet(mesh,new(120,0,0)),camera));
        Assert.False(admission.Admits(Packet(mesh,new(10,30,0)),camera));
        Assert.True(admission.Admits(Packet(mesh,new(10,30,0)),Camera(new(0,30,0))));
        // Returning to an earlier view must use its own retained frustum.
        Assert.False(admission.Admits(Packet(mesh,new(10,30,0)),camera));
    }
    [Fact]
    public void SkinFlagIsInternalModelFlagNotAuthoredCollisionBits()
    {
        var admission=new EnbCurrentRetailMeshVisibility();var camera=Camera();
        Assert.False(admission.Admits(Packet((Mesh()).Copy().SetAttributes(0x400),new(-10,0,0)),camera));
        Assert.True(admission.Admits(Packet((Mesh()).Copy().SetAttributes(0x20000),new(-10,0,0)),camera));
        Assert.True(admission.Admits(Packet((Mesh()).Copy().SetBoneIndices(new ushort[]{0}),new(-10,0,0)),camera));
    }
    [Fact]
    public void MutableModelBoundsAndReplacementInvalidateCachedLocalBox()
    {
        var state=new EnbCurrentRetailMeshGeometryState([],[],[],[], -Vector3.One,Vector3.One,Vector3.Zero,2,0);
        var mesh=(Mesh()).Copy().SetCurrentRetailLoadedGeometry(state);var admission=new EnbCurrentRetailMeshVisibility();
        var camera=Camera();var packet=Packet(mesh,new(-10,0,0));
        Assert.False(admission.Admits(packet,camera));
        uint seed=1;state.Scale(new Vector3(20),ref seed);
        Assert.True(admission.Admits(packet,camera));
        Assert.False(admission.Admits((packet).ForkTask().SetMesh(Mesh()),camera));
    }
    [Theory]
    [InlineData(0x10000u)]
    [InlineData(0x60000u)]
    public void CameraFacingMeshesUseSphereEnclosingBoxBeforeCameraOrientation(uint attributes)
    {
        var mesh=(Mesh()).Copy().SetAttributes(attributes).SetBoundingBoxMin(new(-.01f,-10,-10)).SetBoundingBoxMax(new(.01f,10,10)).SetBoundingRadius(15);
        var packet=Packet(mesh,new(-5,0,0));var admission=new EnbCurrentRetailMeshVisibility();
        Assert.True(admission.Admits(packet,Camera()));
        Assert.False(admission.Admits((packet).ForkTask().SetMesh((mesh).Copy().SetAttributes(0)),Camera()));
        //Radius changes on the same prepared occurrence invalidate its world box.
        Assert.False(admission.Admits((packet).ForkTask().SetMesh((mesh).Copy().SetBoundingRadius(1)),Camera()));
        Assert.True(admission.Admits(packet,Camera()));
        Assert.False(admission.Admits((packet).ForkTask().SetWorldTransform(Matrix4x4.CreateTranslation(-50,0,0)),Camera()));
    }
    [Fact]
    public void CameraFacingSphereCenterMatchesRetailStorageAtBothPrecisions()
    {
        var random=new Random(0x940eb0);
        foreach(var precision in new[]{EnbRetailLinePrecision.Single24,EnbRetailLinePrecision.Double53})
        for(int sample=0;sample<1024;sample++)
        {
            float Next()=>(float)(random.NextDouble()*200-100);
            var m=new Matrix4x4(Next(),Next(),Next(),0,Next(),Next(),Next(),0,Next(),Next(),Next(),0,Next(),Next(),Next(),1);
            var c=new Vector3(Next(),Next(),Next());
            var expected=EnbCurrentRetailRenderBoundsStorage.Calculate(m,new(c,12),default,default,precision,true).Sphere;
            Assert.Equal(new Vector3(expected.X,expected.Y,expected.Z),EnbCurrentRetailMeshVisibility.TransformSphereCenter(m,c,precision));
        }
    }
    [Fact]
    public void OutsideDecisionsMatchRetailMachineCodeOnX87Hardware()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"tests/EnbNext.Net7.Tests/Fixtures/retail-mesh-frustum.bin")))root=root.Parent;
        Assert.NotNull(root);
        using var reader=new BinaryReader(File.OpenRead(Path.Combine(root!.FullName,"tests/EnbNext.Net7.Tests/Fixtures/retail-mesh-frustum.bin")));
        int count=0;
        while(reader.BaseStream.Position<reader.BaseStream.Length)
        {
            var precision=reader.ReadUInt32()==0?EnbRetailLinePrecision.Single24:EnbRetailLinePrecision.Double53;
            var planes=new Vector4[6];for(int i=0;i<6;i++)planes[i]=new(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
            var center=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
            var extent=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
            bool expected=reader.ReadUInt32()!=0;
            Assert.Equal(expected,EnbCurrentRetailMeshVisibility.OutsidePlanes(planes,center,extent,precision));count++;
        }
        Assert.Equal(4096,count);
    }
}
