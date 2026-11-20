using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbR10ShipLegoHlodBoundsCacheTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void EffectQueryInputsRemainOwnedAndSeparateAcrossPasses()
    {
        var (resource,hlod)=TwoMemberResource();var admission=new EnbW3dHlodQueryOwner();var camera=new EnbW3dHlodQueryOwner();
        var first=admission.PreparePost("HIER",Matrix4x4.Identity,1,1);
        var second=camera.PreparePost("HIER",Matrix4x4.CreateTranslation(7,8,9),2,3);
        Assert.NotSame(first,second);
        Assert.True(camera.Constructor.TryCompute(resource,hlod,second.RigidWorldTransform,[],second,out var cameraBounds,out _));
        var preserved=(cameraBounds.LocalBox,cameraBounds.WorldSphere);
        long Run()
        {
            long start=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<1000;i++)
            {
                var post=admission.PreparePost("HIER",Matrix4x4.CreateTranslation(i,0,0),1+i*.001f,1);
                if(!ReferenceEquals(first,post)||!admission.Constructor.TryCompute(resource,hlod,post.RigidWorldTransform,admission.Samples(null,0),post,out _,out _))throw new InvalidOperationException();
            }
            return GC.GetAllocatedBytesForCurrentThread()-start;
        }
        for(int warmup=0;warmup<10;warmup++)Run();
        Assert.Equal(0,Run());Assert.Equal(preserved,(cameraBounds.LocalBox,cameraBounds.WorldSphere));
        Assert.Equal(new Vector3(7,8,9),second.RigidWorldTransform.Translation);
    }

    [Fact]
    public void RetainedMemberSourcesAllocateNothingWhileWorldTransformChanges()
    {
        var (resource,hlod)=TwoMemberResource();var owner=new EnbW3dHlodBoundsOwner();
        long Run()
        {
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<1000;i++)
                if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,Matrix4x4.CreateTranslation(i,2,3),[],out var actual,out _,owner)||!ReferenceEquals(owner,actual))
                    throw new InvalidOperationException("Owner bounds evaluation failed");
            return GC.GetAllocatedBytesForCurrentThread()-before;
        }
        Run();Assert.Equal(0,Run());
        var world=Matrix4x4.CreateTranslation(999,2,3);
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,world,[],out var expected,out _));EqualBounds(expected,owner);
        resource.Meshes[0].SetBoundingBoxMax(new Vector3(40));
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,world,[],out _,out _,owner));
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,world,[],out expected,out _));EqualBounds(expected,owner);
        var replaced=resource.Copy().SetMeshes([resource.Meshes[0].Copy().SetBoundingBoxMax(new Vector3(80)),resource.Meshes[1]]);
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(replaced,hlod,world,[],out _,out _,owner));
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(replaced,hlod,world,[],out expected,out _));EqualBounds(expected,owner);
    }

    [Fact]
    public void RecursiveCollectionAndHlodBoundsRetainStorageWithoutFreezingScale()
    {
        var (inner,hlod)=TwoMemberResource();
        var leaf=new EnbW3dRenderCompositeChild("leaf",0,default,0){MeshMinimum=new(-2),MeshMaximum=new(3)};
        var nested=new EnbW3dRenderCompositeChild("nested",0,default,0){HlodResource=inner,Hlod=hlod,HasConfiguredHierarchy=true};
        var composite=new EnbW3dRenderCompositeChild("A",23,default,0){CollectionChildren=[leaf,nested],HasConfiguredHierarchy=true};
        var resource=inner.Copy().SetCompositeRenderChildren([composite]);var owner=new EnbW3dHlodBoundsOwner();
        var post=new EnbW3dHierarchyState("HIER",Matrix4x4.Identity,2,new Dictionary<int,Matrix4x4>());
        for(int i=0;i<20;i++)
        {
            post.Update("HIER",Matrix4x4.CreateTranslation(i,3,4),1+i*.1f,post.Transforms,1+i*.02f);
            Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,post.RigidWorldTransform,[],post,out var actual,out var error,owner),error);
            Assert.True(TryCompute(resource,hlod,post,out var expected));EqualBounds(expected,actual);
        }
        long Run()
        {
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<1000;i++)
                if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,post.RigidWorldTransform,[],post,out _,out _,owner))throw new InvalidOperationException();
            return GC.GetAllocatedBytesForCurrentThread()-before;
        }
        Run();Assert.Equal(0,Run());
    }

    [Fact]
    public void CompilerMutatesOnlyItsSuppliedInvocationOwner()
    {
        var (resource,hlod)=TwoMemberResource();
        var owner=new EnbW3dHlodBoundsOwner();var other=new EnbW3dHlodBoundsOwner();
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,Matrix4x4.Identity,[],out var untouched,out _,other));
        var earlier=(untouched.LocalBox,untouched.LocalSphere,untouched.WorldSphere);
        for(int i=0;i<20;i++)
        {
            var world=Matrix4x4.CreateTranslation(i,2,3);
            Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,world,[],out var actual,out _,owner));
            Assert.Same(owner,actual);
            Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,world,[],out var expected,out _));
            EqualBounds(expected,actual);
            Assert.Equal(earlier,(other.LocalBox,other.LocalSphere,other.WorldSphere));
        }
    }

    [Fact]
    public void UnchangedConstructorBoundsDoNotRebuildHierarchyOrMemberStorage()
    {
        var (resource,hlod)=TwoMemberResource();var owner=new EnbW3dHlodConstructorBoundsOwner();
        var post=new EnbW3dHierarchyState("HIER",Matrix4x4.Identity,1,new Dictionary<int,Matrix4x4>());
        long Run(bool retained)
        {
            long start=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<1000;i++)
            {
                bool ok=retained?owner.TryCompute(resource,hlod,Matrix4x4.Identity,[],post,out _,out _)
                    :TryCompute(resource,hlod,post,out _);
                if(!ok)throw new InvalidOperationException();
            }
            return GC.GetAllocatedBytesForCurrentThread()-start;
        }
        Run(false);Run(true);
        long before=Run(false),after=Run(true);
        Assert.Equal(1,owner.PreparationCount);Assert.Equal(0,after);Assert.True(before>after);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {Calls=1000,BeforeBytes=before,AfterBytes=after}));
    }

    [Fact]
    public void ConstructorBoundsOwnerRetainsLocalWorkAndInvalidatesAtResourceOrScaleChange()
    {
        var (resource,hlod)=TwoMemberResource();var owner=new EnbW3dHlodConstructorBoundsOwner();
        var post=new EnbW3dHierarchyState("HIER",Matrix4x4.Identity,1,new Dictionary<int,Matrix4x4>());
        for(int i=0;i<100;i++)
        {
            var world=Matrix4x4.CreateRotationY(i*.03f)*Matrix4x4.CreateTranslation(i,2,3);
            post.Update(post.HierarchyName,world,post.RenderObjectScale,post.Transforms,post.PendingHierarchyScale);
            Assert.True(owner.TryCompute(resource,hlod,world,[],post,out var actual,out var error),error);
            Assert.True(TryCompute(resource,hlod,post,out var expected));EqualBounds(expected,actual);
        }
        Assert.Equal(1,owner.PreparationCount);
        var replacement=resource.Copy().SetMeshes([(resource.Meshes[0]).Copy().SetBoundingBoxMax(new Vector3(40)),resource.Meshes[1]]);
        Assert.True(owner.TryCompute(replacement,hlod,post.RigidWorldTransform,[],post,out var changed,out _));
        Assert.True(TryCompute(replacement,hlod,post,out var literal));EqualBounds(literal,changed);
        Assert.Equal(2,owner.PreparationCount);
        post.PendingHierarchyScale=2;
        Assert.True(owner.TryCompute(replacement,hlod,post.RigidWorldTransform,[],post,out changed,out _));
        Assert.True(TryCompute(replacement,hlod,post,out literal));EqualBounds(literal,changed);
        Assert.Equal(3,owner.PreparationCount);
        var transforms=(Dictionary<int,Matrix4x4>)post.Transforms;transforms[1]=Matrix4x4.CreateTranslation(5,6,7);
        Assert.True(owner.TryCompute(replacement,hlod,post.RigidWorldTransform,[],post,out changed,out _));
        Assert.True(TryCompute(replacement,hlod,post,out literal));EqualBounds(literal,changed);
        Assert.Equal(3,owner.PreparationCount);
    }

    [Fact]
    public void AnimationFreePostTransformLocalBoundsRemainInvariantAcrossRigidMovement()
    {
        (EnbW3dRenderResource resource, EnbW3dRenderHlod hlod) = TwoMemberResource();
        var pivotOverrides = new Dictionary<int, Matrix4x4>
        {
            [1] = Matrix4x4.CreateTranslation(0f, 5f, 0f),
        };
        var firstPostTransforms = new EnbW3dHierarchyState(
            "HIER",
            Matrix4x4.CreateTranslation(100f, 0f, 0f),
            RenderObjectScale: 1.25f,
            pivotOverrides);
        Matrix4x4 secondRigidWorld =
            Matrix4x4.CreateRotationZ(0.75f) * Matrix4x4.CreateTranslation(-20f, 40f, 3f);
        EnbW3dHierarchyState secondPostTransforms = CopyHierarchy(firstPostTransforms,world:secondRigidWorld);

        Assert.True(TryCompute(resource, hlod, firstPostTransforms, out EnbW3dHlodBoundsOwner first));
        Assert.True(TryCompute(resource, hlod, secondPostTransforms, out EnbW3dHlodBoundsOwner second));

        Assert.Equal(first.LocalBox, second.LocalBox);
        Assert.Equal(first.LocalSphere, second.LocalSphere);
        Vector4 nativeSphere=EnbCurrentRetailRenderBoundsStorage.Calculate(secondRigidWorld,new(first.LocalSphere.Center,first.LocalSphere.Radius),first.LocalBox.Center,first.LocalBox.Extents).Sphere;
        var refreshed = new EnbW3dHlodBoundsOwner(
            first.LocalBox,
            first.LocalSphere,
            new EnbW3dWorldBounds(
                new(nativeSphere.X,nativeSphere.Y,nativeSphere.Z),
                first.LocalSphere.Radius));
        EqualBounds(second,refreshed);
    }

    [Fact]
    public void PostTransformLocalBoundsChangeWhenAnExactCacheGuardInputChanges()
    {
        (EnbW3dRenderResource resource, EnbW3dRenderHlod hlod) = TwoMemberResource();
        var original = new EnbW3dHierarchyState(
            "HIER",
            Matrix4x4.Identity,
            RenderObjectScale: 1f,
            new Dictionary<int, Matrix4x4>
            {
                [1] = Matrix4x4.CreateTranslation(0f, 5f, 0f),
            });
        EnbW3dHierarchyState changedPivot = CopyHierarchy(original,transforms:new Dictionary<int, Matrix4x4>
            {
                [1] = Matrix4x4.CreateTranslation(0f, 9f, 0f),
            });
        EnbW3dHierarchyState changedScale = CopyHierarchy(original,scale:2f);

        Assert.True(TryCompute(resource, hlod, original, out EnbW3dHlodBoundsOwner baseline));
        Assert.True(TryCompute(resource, hlod, changedPivot, out EnbW3dHlodBoundsOwner pivotResult));
        Assert.True(TryCompute(resource, hlod, changedScale, out EnbW3dHlodBoundsOwner scaleResult));

        Assert.NotEqual(baseline.LocalBox, pivotResult.LocalBox);
        Assert.NotEqual(baseline.LocalBox, scaleResult.LocalBox);
    }

    [Fact]
    public void PendingHierarchyScaleMovesPivotsWithoutRescalingMemberGeometry()
    {
        var (resource,hlod)=TwoMemberResource();
        var original=new EnbW3dHierarchyState("HIER",Matrix4x4.Identity,1,
            new Dictionary<int,Matrix4x4>()) {PendingHierarchyScale=1};
        Assert.True(TryCompute(resource,hlod,original,out var first));
        Assert.True(TryCompute(resource,hlod,CopyHierarchy(original,pending:2),out var second));
        Assert.Equal(first.LocalBox.Center+new Vector3(5,0,0),second.LocalBox.Center);
        Assert.Equal(first.LocalBox.Extents+new Vector3(5,0,0),second.LocalBox.Extents);
        Assert.Equal(first.LocalBox.Extents.Y,second.LocalBox.Extents.Y);
        Assert.Equal(first.LocalBox.Extents.Z,second.LocalBox.Extents.Z);
    }

    private static EnbW3dHierarchyState CopyHierarchy(EnbW3dHierarchyState source,Matrix4x4? world=null,
        float? scale=null,IReadOnlyDictionary<int,Matrix4x4>? transforms=null,float? pending=null)
        =>new(source.HierarchyName,world??source.RigidWorldTransform,scale??source.RenderObjectScale,transforms??source.Transforms)
            {PendingHierarchyScale=pending??source.PendingHierarchyScale};

    private static void EqualBounds(EnbW3dHlodBoundsOwner expected,EnbW3dHlodBoundsOwner actual)
    {Assert.Equal(expected.LocalBox,actual.LocalBox);Assert.Equal(expected.LocalSphere,actual.LocalSphere);Assert.Equal(expected.WorldSphere,actual.WorldSphere);}

    private static bool TryCompute(
        EnbW3dRenderResource resource,
        EnbW3dRenderHlod hlod,
        EnbW3dHierarchyState postTransforms,
        out EnbW3dHlodBoundsOwner bounds)
        => EnbW3dHlodCompositeBoundsCompiler.TryCompute(
            resource,
            hlod,
            Matrix4x4.Identity,
            Array.Empty<EnbW3dResolvedWeightedAnimationSample>(),
            postTransforms,
            out bounds,
            out _);

    private static (EnbW3dRenderResource Resource, EnbW3dRenderHlod Hlod) TwoMemberResource()
    {
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            EnbR4W3dRenderResourceTests.BuildResolvedResource(shaderId: 0),
            out EnbW3dRenderResource compiled,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMesh template = Assert.Single(compiled.Meshes);
        EnbW3dRenderMesh first = (template).Copy().SetOrdinal(0).SetName("A").SetBoundingBoxMin(new Vector3(-1f)).SetBoundingBoxMax(new Vector3(1f)).SetBoundingSphereCenter(Vector3.Zero).SetBoundingRadius(MathF.Sqrt(3f)).SetCurrentRetailRenderPassCount(1);
        EnbW3dRenderMesh second = (first).Copy().SetOrdinal(1).SetName("B");
        var hierarchy = new EnbW3dRenderHierarchy(
            0x0003_0000u,
            "HIER",
            Vector3.Zero,
            HasSyntheticRetailRoot: false,
            new EnbW3dRenderPivot[]
            {
                new(0, "ROOT", uint.MaxValue, Vector3.Zero, Vector3.Zero, Quaternion.Identity, Matrix4x4.Identity),
                new(1, "BONE1", 0, new Vector3(10f, 0f, 0f), Vector3.Zero, Quaternion.Identity, Matrix4x4.CreateTranslation(10f, 0f, 0f)),
            },
            Array.Empty<EnbW3dRenderPivotFixup>());
        var hlod = new EnbW3dRenderHlod(
            "ROOTLOD",
            "HIER",
            new EnbW3dRenderHlodLevel[]
            {
                new(0, float.MaxValue, BitConverter.SingleToUInt32Bits(float.MaxValue), [new EnbW3dRenderHlodMember(0, "A")]),
                new(1, float.MaxValue, BitConverter.SingleToUInt32Bits(float.MaxValue), [new EnbW3dRenderHlodMember(1, "B")]),
            });

        return (compiled.Copy().SetMeshes([first, second]).SetHierarchies([hierarchy]).SetHierarchicalLods([hlod]), hlod);
    }

}
