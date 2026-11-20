using System.Collections.Immutable;
using System.Numerics;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>
/// Exact current-retail RenderObjClass flag predicates used by scene culling and
/// HLOD detail preparation. The bit names intentionally stay structural:
/// current client.exe proves their behavior, but does not justify prettier
/// gameplay names for the two +0x174 eligibility bits.
/// </summary>
public readonly record struct EnbCurrentRetailRenderObjectState(uint Flags)
{
    public const uint SceneVisibilityBit = 0x0000_0100u;
    public const uint ActivityBit200 = 0x0000_0200u;
    public const uint ActivityBit400 = 0x0000_0400u;
    public const uint ActivityMask = ActivityBit200 | ActivityBit400;
    public const uint SceneVisibleAndActiveMask = SceneVisibilityBit | ActivityMask;
    public const uint ForceSceneVisibleBit = 0x0000_0800u;
    public const uint SuppressDetailPreparationBit = 0x0000_8000u;

    /// <summary>RenderObjClass virtual +0x174 / FUN_00463060.</summary>
    public bool IsDetailActivityEligible => (Flags & ActivityMask) == ActivityMask;

    /// <summary>RenderObjClass virtual +0x170 / FUN_00463030.</summary>
    public bool IsSceneVisibleAndActive =>
        (Flags & SceneVisibleAndActiveMask) == SceneVisibleAndActiveMask;

    /// <summary>RenderObjClass virtual +0x190 / FUN_00463180.</summary>
    public bool ForcesSceneVisibility => (Flags & ForceSceneVisibleBit) != 0;

    /// <summary>FUN_00965F40 tests flags bit 15 before virtual +0x104.</summary>
    public bool SuppressesDetailPreparation =>
        (Flags & SuppressDetailPreparationBit) != 0;

    /// <summary>Virtual+198 /004631D0.</summary>
    public bool HasBit4000 => (Flags & 0x4000) != 0;

    /// <summary>Virtual+1A0 /00463220.</summary>
    public uint LowByteClassMask => Flags & 0xff;

    /// <summary>Virtual+1A4 /00463240: replace bits1..7 and always set bit0.</summary>
    public EnbCurrentRetailRenderObjectState WithLowByteClassMask(uint mask)
        => this with { Flags = (Flags & ~0xfeu) | (mask & 0xfeu) | 1u };

    /// <summary>
    /// RenderObjClass virtual +0x17C / FUN_004630B0. Current scene traversal
    /// rewrites only bit 0x100 from force-visible/frustum classification.
    /// </summary>
    public EnbCurrentRetailRenderObjectState WithSceneVisibility(bool visible)
        => visible
            ? this with { Flags = Flags | SceneVisibilityBit }
            : this with { Flags = Flags & ~SceneVisibilityBit };

    public bool ShouldPrepareDetail(bool sphereIntersectsFrustum)
    {
        bool visible = ForcesSceneVisibility || sphereIntersectsFrustum;
        EnbCurrentRetailRenderObjectState current = WithSceneVisibility(visible);
        return current.IsSceneVisibleAndActive && !current.SuppressesDetailPreparation;
    }
}

/// <summary>
/// Current-retail scene sphere/frustum admission used before RenderObjClass
/// virtual +0x104. FUN_008E1FB0 tests one sphere against six planes and rejects
/// it only when FUN_008E3470 classifies it outside any plane. This helper
/// expresses the same six-plane sphere intersection in EnbNext camera space.
/// </summary>
public static class EnbCurrentRetailSceneCulling
{
    public static bool SphereIntersectsFrustum(
        EnbResolvedRenderCamera camera,
        EnbW3dWorldBounds sphere,
        float unappliedModelScale = 1f)
    {
        // Retail scales MeshModel geometry/bounds before its rigid +0xF0 sphere
        // refresh. Immutable asset bounds need that radius scale explicitly;
        // their center has already been placed by the caller's scaled world.
        // Ship post-transform bounds already contain it and pass the default 1.
        if (!sphere.IsValid || !float.IsFinite(unappliedModelScale) || unappliedModelScale <= 0)
            return false;

        Vector3 relative = sphere.Center - camera.Position;
        float depth = Vector3.Dot(relative, camera.Basis.Forward);
        float horizontal = Vector3.Dot(relative, camera.Basis.Right);
        float vertical = Vector3.Dot(relative, camera.Basis.Up);
        float radius = sphere.Radius * unappliedModelScale;
        if (!float.IsFinite(radius)) return false;
        if (!float.IsFinite(depth) || !float.IsFinite(horizontal) || !float.IsFinite(vertical))
            return false;

        EnbRenderClipRange clip = camera.Projection.ClipRange;
        if (clip.NearPlane - depth > radius || depth - clip.FarPlane > radius)
            return false;

        if (camera.Projection.IsOrthographic)
        {
            return MathF.Abs(horizontal) <= camera.Projection.OrthographicHalfWidth + radius &&
                   MathF.Abs(vertical) <= camera.Projection.OrthographicHalfHeight + radius;
        }

        float xScale = camera.Projection.Matrix.M11;
        float yScale = camera.Projection.Matrix.M22;
        if (!float.IsFinite(xScale) || !float.IsFinite(yScale) || xScale <= 0f || yScale <= 0f)
            return false;

        float tanHalfHorizontal = 1f / xScale;
        float tanHalfVertical = 1f / yScale;
        if (OutsideSidePlane(horizontal, depth, radius, tanHalfHorizontal) ||
            OutsideSidePlane(vertical, depth, radius, tanHalfVertical))
        {
            return false;
        }

        return true;
    }

    private static bool OutsideSidePlane(float lateral, float depth, float radius, float tangent)
    {
        float normalizer = MathF.Sqrt(1f + tangent * tangent);
        if (!float.IsFinite(normalizer) || normalizer <= 0f)
            return true;

        // Normalized signed distance beyond either side plane. A sphere is
        // outside only when that distance is greater than its radius.
        float signedOutsideDistance =
            (MathF.Abs(lateral) - depth * tangent) / normalizer;
        return signedOutsideDistance > radius;
    }
}

public readonly record struct EnbW3dHlodMemberKey(uint ReferenceIndex, string Name)
{
    public static EnbW3dHlodMemberKey From(EnbW3dRenderHlodMember member)
        => new(member.ReferenceIndex, member.Name);
}

public readonly record struct EnbW3dAxisAlignedBounds(Vector3 Center, Vector3 Extents)
{
    public bool IsValid =>
        IsFinite(Center) && IsFinite(Extents) &&
        Extents.X >= 0f && Extents.Y >= 0f && Extents.Z >= 0f;

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>Mutable bounds fields of one render invocation. Consumers read the
/// owner in traversal order; diagnostic history must copy the value fields.</summary>
public sealed class EnbW3dHlodBoundsOwner
{
    public EnbW3dAxisAlignedBounds LocalBox {get;private set;}
    public EnbW3dWorldBounds LocalSphere {get;private set;}
    public EnbW3dWorldBounds WorldSphere {get;private set;}
    internal readonly EnbW3dHierarchyPoseOwner HierarchyPose=new();
    internal EnbW3dRenderResource? BindingResource;
    internal EnbW3dRenderHlod? BindingHlod;
    internal EnbW3dRenderHierarchy? BindingHierarchy;
    internal EnbW3dRenderHlodMember? BoundingBoxMember;
    internal EnbW3dExternalRenderObject? BoundingBoxObject;
    internal EnbW3dHlodMemberBoundsOwner[] Members=[];
    private EnbW3dRenderHierarchy? queryHierarchy;
    private int queryScale;
    internal Matrix4x4[] QueryLocals=[];
    internal int[] QueryParents=[];
    internal Matrix4x4[] QuerySamples=[];
    internal readonly Func<int,Matrix4x4> SampleAtIndex;
    public EnbW3dHlodBoundsOwner() {SampleAtIndex=ReadSample;}
    private Matrix4x4 ReadSample(int index)=>QuerySamples[index];
    internal void PrepareSpecialQuery(EnbW3dRenderHierarchy hierarchy,float scale,bool sampled)
    {
        int count=hierarchy.Pivots.Count;
        if(!ReferenceEquals(queryHierarchy,hierarchy)||queryScale!=BitConverter.SingleToInt32Bits(scale))
        {
            if(QueryLocals.Length!=count){QueryLocals=new Matrix4x4[count];QueryParents=new int[count];}
            for(int i=0;i<count;i++)
            {
                var pivot=hierarchy.Pivots[i];
                QueryLocals[i]=EnbRetailParticleHierarchyTransform.ScaleTranslation(pivot.LocalTransform,scale);
                QueryParents[i]=pivot.IsRoot?-1:checked((int)pivot.ParentIndex);
            }
            queryHierarchy=hierarchy;queryScale=BitConverter.SingleToInt32Bits(scale);
        }
        if(sampled&&QuerySamples.Length!=count)QuerySamples=new Matrix4x4[count];
    }
    public EnbW3dHlodBoundsOwner(EnbW3dAxisAlignedBounds box,EnbW3dWorldBounds local,EnbW3dWorldBounds world) : this()
        =>Set(box,local,world);
    public EnbW3dHlodBoundsOwner Set(EnbW3dAxisAlignedBounds box,EnbW3dWorldBounds local,EnbW3dWorldBounds world)
    {LocalBox=box;LocalSphere=local;WorldSphere=world;return this;}
}


/// <summary>One retained render/query invocation's HLOD inputs and bounds.
/// Instances and view/query passes must not share mutable evaluation storage.</summary>
public sealed class EnbW3dHlodQueryOwner
{
    public readonly EnbW3dHlodBoundsOwner Bounds=new();
    public readonly EnbW3dHlodConstructorBoundsOwner Constructor=new();
    private readonly EnbW3dResolvedWeightedAnimationSample[] sample=new EnbW3dResolvedWeightedAnimationSample[1];
    private static readonly IReadOnlyDictionary<int,Matrix4x4> NoOverrides=new Dictionary<int,Matrix4x4>();
    private EnbW3dHierarchyState? post;
    public EnbW3dHierarchyState PreparePost(string name,Matrix4x4 world,float scale,float pending)
    {
        post??=new(name,world,scale,NoOverrides);
        post.Update(name,world,scale,NoOverrides,pending);return post;
    }
    public IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> Samples(EnbW3dRenderHierarchyAnimation? animation,float frame)
    {
        if(animation is null)return Array.Empty<EnbW3dResolvedWeightedAnimationSample>();
        sample[0]=new(animation,frame,1);return sample;
    }
}

// The HLOD owns attached member bindings; lookup is a load/change operation.
internal sealed class EnbW3dHlodMemberBoundsOwner(EnbW3dRenderHlodMember member)
{
    internal readonly EnbW3dRenderHlodMember Member=member;
    internal EnbW3dRenderCompositeChild? Composite;
    internal EnbW3dCompositeBoundsOwner? CompositeOwner;
    internal EnbW3dExternalRenderObject? External;
    internal EnbW3dRenderMesh? Mesh;
    internal bool HasStaticBox;
    internal Vector3 Minimum,Maximum;
    internal string? Diagnostic;
}
internal sealed class EnbW3dCompositeBoundsOwner
{
    internal readonly EnbW3dRenderCompositeChild Definition;
    internal readonly EnbW3dCompositeBoundsOwner[] Children;
    internal readonly (Vector3 Center,Vector3 Extent)[] Boxes;
    internal readonly Func<int>? Count;
    internal readonly Func<int,(Vector3 Center,Vector3 Extent)>? At;
    internal readonly EnbW3dHlodBoundsOwner? HlodBounds;
    internal EnbW3dHierarchyState? Post;
    private static readonly IReadOnlyDictionary<int,Matrix4x4> NoOverrides=new Dictionary<int,Matrix4x4>();
    internal EnbW3dCompositeBoundsOwner(EnbW3dRenderCompositeChild definition)
    {
        Definition=definition;
        int count=definition.CollectionChildren?.Count??0;
        Children=count==0?[]:new EnbW3dCompositeBoundsOwner[count];Boxes=count==0?[]:new (Vector3,Vector3)[count];
        for(int i=0;i<count;i++)Children[i]=new(definition.CollectionChildren![i]);
        if(definition.CollectionChildren is not null){Count=ReadCount;At=ReadAt;}
        if(definition.Hlod is not null)HlodBounds=new();
    }
    private int ReadCount()=>Boxes.Length;
    private (Vector3 Center,Vector3 Extent) ReadAt(int i)=>Boxes[i];
    internal EnbW3dHierarchyState PreparePost(string name,float scale,float? pending)
    {
        Post??=new(name,Matrix4x4.Identity,scale,NoOverrides);
        Post.Update(name,Matrix4x4.Identity,scale,NoOverrides,pending);return Post;
    }
}

/// <summary>
/// Current HLOD composite-bound path. FUN_0098F1D0 unions every attached
/// ordinary-level child and every 0x0705 aggregate child after its hierarchy
/// state-record transform. HLOD virtual +0xF8 / FUN_0098D0B0 then derives a
/// sphere from that composite box. HLOD +0x1BC / 009421B0 owns the cached
/// world-bounds refresh, while +0xF0 / FUN_0098D110 transforms the local sphere
/// center and copies the radius unchanged. A final-level class-1B box named
/// BOUNDINGBOX overrides the aggregate AABB exactly as current retail does.
///
/// 0x0706 is a proxy-query array rather than attached render children, so it
/// does not participate in bounds. HLOD references index the loaded tree
/// directly, including the synthetic root of pre30000 hierarchies.
/// </summary>
public static class EnbW3dHlodCompositeBoundsCompiler
{
    public static bool TryGetMemberBoundingBox(EnbW3dRenderResource resource,string memberName,
        out Vector3 minimum,out Vector3 maximum,out string? diagnostic)
    {
        diagnostic=null;
        if(EnbCurrentRetailRenderObjectSemantics.TryGetHlodBoundingBox(resource,memberName,
            out minimum,out maximum))return true;
        if(!TryResolveUniqueMesh(resource,memberName,out var mesh,out diagnostic))return false;
        minimum=mesh.BoundingBoxMin;
        maximum=mesh.BoundingBoxMax;
        return true;
    }

    /// <summary>0098CF90 evaluates a special BOUNDINGBOX member in the live
    /// hierarchy; ordinary HLODs use the base-pose aggregate from 0098F1D0.</summary>
    public static bool HasBoundingBoxOverride(EnbW3dRenderResource resource,EnbW3dRenderHlod hlod)
        => FindRetailBoundingBoxMember(resource,hlod) is not null;

    public static bool TryCompute(
        EnbW3dRenderResource resource,
        EnbW3dRenderHlod hlod,
        Matrix4x4 hlodWorldTransform,
        EnbW3dRenderHierarchyAnimation? animation,
        float animationFrame,
        out EnbW3dHlodBoundsOwner bounds,
        out string? diagnostic, EnbW3dHlodBoundsOwner? owner=null, bool localBoundsQuery=false)
        => TryComputeCore(
            resource,
            hlod,
            hlodWorldTransform,
            animation is null
                ? null
                : new EnbW3dResolvedWeightedAnimationSample[]
                {
                    new(animation, animationFrame, 1f),
                },
            hierarchyPostTransforms: null,
            singleAnimationQuery: true,
            out bounds,
            out diagnostic, owner:owner,localBoundsQuery:localBoundsQuery);

    public static bool TryCompute(
        EnbW3dRenderResource resource,
        EnbW3dRenderHlod hlod,
        Matrix4x4 hlodWorldTransform,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> animationBlend,
        out EnbW3dHlodBoundsOwner bounds,
        out string? diagnostic, EnbW3dHlodBoundsOwner? owner=null, bool localBoundsQuery=false)
    {
        ArgumentNullException.ThrowIfNull(animationBlend);
        return TryComputeCore(
            resource,
            hlod,
            hlodWorldTransform,
            animationBlend,
            hierarchyPostTransforms: null,
            singleAnimationQuery: false,
            out bounds,
            out diagnostic, owner:owner,localBoundsQuery:localBoundsQuery);
    }

    public static bool TryCompute(
        EnbW3dRenderResource resource,
        EnbW3dRenderHlod hlod,
        Matrix4x4 hlodWorldTransform,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> animationBlend,
        EnbW3dHierarchyState hierarchyPostTransforms,
        out EnbW3dHlodBoundsOwner bounds,
        out string? diagnostic, EnbW3dHlodBoundsOwner? owner=null, bool localBoundsQuery=false)
    {
        ArgumentNullException.ThrowIfNull(animationBlend);
        ArgumentNullException.ThrowIfNull(hierarchyPostTransforms);
        return TryComputeCore(
            resource,
            hlod,
            hlodWorldTransform,
            animationBlend,
            hierarchyPostTransforms,
            singleAnimationQuery: false,
            out bounds,
            out diagnostic, owner:owner,localBoundsQuery:localBoundsQuery);
    }

    private static bool TryComputeCore(
        EnbW3dRenderResource resource,
        EnbW3dRenderHlod hlod,
        Matrix4x4 hlodWorldTransform,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample>? animationBlend,
        EnbW3dHierarchyState? hierarchyPostTransforms,
        bool singleAnimationQuery,
        out EnbW3dHlodBoundsOwner bounds,
        out string? diagnostic,
        float? pendingDazzleScale=null, EnbW3dHlodBoundsOwner? owner=null, bool localBoundsQuery=false)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(hlod);
        bounds = default!;
        diagnostic = null;

        if (!IsFinite(hlodWorldTransform))
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' received a non-finite scene transform.";
            return false;
        }

        owner??=new();
        PrepareMemberBindings(owner,resource,hlod);
        EnbW3dRenderHierarchy? hierarchy=owner.BindingHierarchy;
        if (hierarchy is null)
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' requires missing hierarchy '{hlod.HierarchyName}'.";
            return false;
        }

        //0098BAD0 stores raw0704 indices;0098C420 indexes the loaded tree
        //directly. A pre30000 hierarchy's inserted root does not shift them.
        if (hierarchyPostTransforms is not null &&
            !string.Equals(
                hierarchyPostTransforms.HierarchyName,
                hierarchy.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"W3D HLOD '{hlod.Name}' received state-record overrides for hierarchy '{hierarchyPostTransforms.HierarchyName}', not '{hierarchy.Name}'.";
            return false;
        }

        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> selectedAnimations=animationBlend??[];
        for(int i=0;i<selectedAnimations.Count;i++)
        {
            var entry=selectedAnimations[i];
            if(string.Equals(entry.Animation.HierarchyName,hierarchy.Name,StringComparison.OrdinalIgnoreCase))continue;
            diagnostic=$"W3D HLOD '{hlod.Name}' received animation '{entry.Animation.Name}' for hierarchy '{entry.Animation.HierarchyName}', not '{hierarchy.Name}'.";
            return false;
        }

        EnbW3dRenderHlodMember? boundingBoxMember = owner.BoundingBoxMember;
        // 0098F1D0 explicitly calls 009895B0 with identity: evaluate the
        // hierarchy's base transforms, not the current HAnim sample. The
        // ordinary aggregate stays fixed while its child meshes rotate.
        // Only 0098CF90's special BOUNDINGBOX path uses the live pose.
        if (boundingBoxMember is null)
            selectedAnimations = [];

        owner??=new();
        EnbW3dHierarchyPoseOwner localPose;
        Matrix4x4 boundsWorldTransform = localBoundsQuery?Matrix4x4.Identity:hierarchyPostTransforms?.RigidWorldTransform??hlodWorldTransform;
        Matrix4x4 poseRoot=boundingBoxMember is null?Matrix4x4.Identity:boundsWorldTransform;
        bool poseReady;
        if (hierarchyPostTransforms is not null)
        {
            poseReady = EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchyPose(
                hierarchy,
                poseRoot,
                hierarchyPostTransforms.HierarchyTranslationScale,
                selectedAnimations,
                hierarchyPostTransforms.Transforms,
                out localPose,
                out diagnostic,storage:owner.HierarchyPose);
            if (poseReady&&boundingBoxMember is null)
            {
                boundsWorldTransform = localBoundsQuery?Matrix4x4.Identity:hierarchyPostTransforms.RigidWorldTransform;
            }
        }
        else
        {
            poseReady = selectedAnimations.Count == 0
                ? EnbW3dHierarchyPose.TryEvaluate(
                    hierarchy,
                    poseRoot,
                    animation: null,
                    frame: 0f, postWorldTransforms:null,
                    out localPose,
                    out diagnostic,storage:owner.HierarchyPose)
                : EnbW3dHierarchyPose.TryEvaluate(
                    hierarchy,
                    poseRoot,
                    selectedAnimations,postWorldTransforms:null,
                    out localPose,
                    out diagnostic,storage:owner.HierarchyPose);
        }
        if (!poseReady)
        {
            return false;
        }

        bool haveBox = false;
        Vector3 minimum = default;
        Vector3 maximum = default;

        // FUN_0098F1D0 scans the final level from the end for a class-1B box whose
        // name tail is BOUNDINGBOX and stores that member index at +0xF0. The
        // HLOD +0xFC virtual (0098CF90) then uses only that member's transformed
        // AABB. If no such member exists, +0xFC falls back to the aggregate AABB
        // cached by FUN_0098F1D0.
        (Vector3 Center,Vector3 Extent)? selectedBox=null;
        if (boundingBoxMember is EnbW3dRenderHlodMember specialMember)
        {
            int pivot=checked((int)specialMember.ReferenceIndex);
            if(pivot>=hierarchy.Pivots.Count)
            {diagnostic=$"W3D HLOD '{hlod.Name}' selected box pivot {pivot} is outside '{hierarchy.Name}'.";return false;}
            Matrix4x4 queriedMatrix;
            if(selectedAnimations.Count==0||singleAnimationQuery)
            {
                float scale=hierarchyPostTransforms?.HierarchyTranslationScale??1f;
                owner.PrepareSpecialQuery(hierarchy,scale,selectedAnimations.Count!=0);
                var locals=owner.QueryLocals;var parents=owner.QueryParents;
                Matrix4x4[]? sampled=null;
                if(selectedAnimations.Count!=0)
                {
                    sampled=owner.QuerySamples;
                    for(int n=1;n<sampled.Length;n++)
                    {
                        var entry=selectedAnimations[0];
                        if(!EnbW3dAnimationSampler.TrySamplePivot(entry.Animation,n,entry.Frame,out var sample,out diagnostic))return false;
                        Matrix4x4 matrix=EnbRetailParticleHierarchyTransform.QuaternionBasis(sample.Rotation);
                        matrix.M41=sample.Translation.X;matrix.M42=sample.Translation.Y;matrix.M43=sample.Translation.Z;sampled[n]=matrix;
                    }
                }
                queriedMatrix=EnbRetailHierarchyPivotQuery.Query(locals,parents,pivot,boundsWorldTransform,
                    hierarchy.CurrentRetailAnimationTranslationScale*scale,sampled is null?null:owner.SampleAtIndex);
            }
            else queriedMatrix=localPose.WorldTransforms[pivot]; //9C60E0 mode4 copies the refreshed record.
            var authored=owner.BoundingBoxObject!.BoxDefinition!;
            selectedBox=EnbRetailHlodBounds.TransformSelectedBox(boundsWorldTransform,queriedMatrix,authored.Center,authored.Extents);
            haveBox=true;
        }
        else
        {
            // Same level/member order followed by the 0705 aggregate; 0706
            // remains excluded. Stored bindings replace repeated name lookup.
            foreach(var member in owner.Members)
                if(!TryExpandMemberBox(resource,hlod,hierarchy,localPose,member,
                    hierarchyPostTransforms?.RenderObjectScale??1f,
                    pendingDazzleScale??hierarchyPostTransforms?.HierarchyTranslationScale??1f,
                    ref haveBox,ref minimum,ref maximum,out diagnostic))return false;
        }

        if (!haveBox)
        {
            var zeroBox = new EnbW3dAxisAlignedBounds(Vector3.Zero, Vector3.Zero);
            var zeroSphere = new EnbW3dWorldBounds(Vector3.Zero, 0f);
            Vector4 transformedZero=EnbCurrentRetailRenderBoundsStorage.Calculate(boundsWorldTransform,Vector4.Zero,Vector3.Zero,Vector3.Zero).Sphere;
            bounds = (owner??new()).Set(zeroBox,zeroSphere,new EnbW3dWorldBounds(new(transformedZero.X,transformedZero.Y,transformedZero.Z),0f));
            return bounds.WorldSphere.IsValid;
        }

        using var boundsPrecision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        Vector3 localCenter=selectedBox?.Center??default,localExtents=selectedBox?.Extent??default;
        for(int axis=0;selectedBox is null&&axis<3;axis++)
        {
            R sum=(R)maximum[axis]+minimum[axis],difference=(R)maximum[axis]-minimum[axis];
            if(axis!=0){sum=(float)sum;difference=(float)difference;}
            localCenter[axis]=(float)(sum*.5);localExtents[axis]=(float)(difference*.5);
        }
        float localRadius = EnbRetailHlodBounds.BoxSphereRadius(localExtents);
        Vector4 localSphereWords=new(localCenter,localRadius);
        Vector4 worldSphereWords=boundingBoxMember is not null
            ? EnbRetailHlodBounds.TransformSelectedLocalSphere(boundsWorldTransform,localSphereWords)
            : EnbCurrentRetailRenderBoundsStorage.Calculate(boundsWorldTransform,localSphereWords,localCenter,localExtents).Sphere;
        Vector3 worldCenter=new(worldSphereWords.X,worldSphereWords.Y,worldSphereWords.Z);
        if (!IsFinite(worldCenter) || !float.IsFinite(localRadius))
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' produced non-finite current-retail composite bounds.";
            return false;
        }

        var localBox = new EnbW3dAxisAlignedBounds(localCenter, localExtents);
        var localSphere = new EnbW3dWorldBounds(localCenter, localRadius);
        // FUN_0098D110 copies the radius from +0xF8 unchanged after transforming
        // the sphere center. Do not apply EnbNext's generic max-axis scale rule.
        var worldSphere = new EnbW3dWorldBounds(worldCenter, localRadius);
        if (!localBox.IsValid || !localSphere.IsValid || !worldSphere.IsValid)
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' produced invalid current-retail composite bounds.";
            return false;
        }

        bounds = (owner??new()).Set(localBox, localSphere, worldSphere);
        return true;
    }


    private static void PrepareMemberBindings(EnbW3dHlodBoundsOwner owner,EnbW3dRenderResource resource,EnbW3dRenderHlod hlod)
    {
        if(ReferenceEquals(owner.BindingResource,resource)&&ReferenceEquals(owner.BindingHlod,hlod))return;
        RebindMembers(owner,resource,hlod);
    }
    // Keep cold lookup closures out of the unchanged-owner entry point.
    private static void RebindMembers(EnbW3dHlodBoundsOwner owner,EnbW3dRenderResource resource,EnbW3dRenderHlod hlod)
    {
        owner.BindingResource=resource;owner.BindingHlod=hlod;
        owner.BindingHierarchy=resource.Hierarchies.FirstOrDefault(h=>string.Equals(h.Name,hlod.HierarchyName,StringComparison.OrdinalIgnoreCase));
        owner.BoundingBoxMember=FindRetailBoundingBoxMember(resource,hlod);
        owner.BoundingBoxObject=owner.BoundingBoxMember is {} special
            ?resource.ExternalRenderObjects.First(e=>string.Equals(e.Name,special.Name,StringComparison.OrdinalIgnoreCase)&&e.BoxDefinition?.RetailClassId==0x1b):null;
        if(owner.BoundingBoxMember is not null){owner.Members=[];return;}
        int count=hlod.Aggregate?.Members.Count??0;
        for(int i=0;i<hlod.Levels.Count;i++)count+=hlod.Levels[i].Members.Count;
        var bindings=new EnbW3dHlodMemberBoundsOwner[count];int slot=0;
        for(int i=0;i<hlod.Levels.Count;i++)
            for(int j=0;j<hlod.Levels[i].Members.Count;j++)bindings[slot++]=BindMember(resource,hlod.Levels[i].Members[j]);
        if(hlod.Aggregate is {} aggregate)
            for(int i=0;i<aggregate.Members.Count;i++)bindings[slot++]=BindMember(resource,aggregate.Members[i]);
        owner.Members=bindings;
    }
    private static EnbW3dHlodMemberBoundsOwner BindMember(EnbW3dRenderResource resource,EnbW3dRenderHlodMember member)
    {
        var binding=new EnbW3dHlodMemberBoundsOwner(member);
        binding.Composite=resource.CompositeRenderChildren.FirstOrDefault(c=>string.Equals(c.Name,member.Name,StringComparison.OrdinalIgnoreCase));
        binding.External=resource.ExternalRenderObjects.FirstOrDefault(e=>string.Equals(e.Name,member.Name,StringComparison.OrdinalIgnoreCase));
        if(binding.Composite is {} composite)binding.CompositeOwner=new(composite);
        else if(binding.External?.DazzleDefinition is not null || binding.External?.BoxDefinition is not null ||
            EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(member.Name) || binding.External?.Kind==EnbW3dExternalRenderObjectKind.NullObject){}
        else if(EnbCurrentRetailRenderObjectSemantics.TryGetHlodBoundingBox(resource,member.Name,out binding.Minimum,out binding.Maximum))binding.HasStaticBox=true;
        else TryResolveUniqueMesh(resource,member.Name,out binding.Mesh,out binding.Diagnostic);
        return binding;
    }

    private static EnbW3dRenderHlodMember? FindRetailBoundingBoxMember(EnbW3dRenderResource resource,EnbW3dRenderHlod hlod)
    {
        if (hlod.Levels.Count == 0)
            return null;

        IReadOnlyList<EnbW3dRenderHlodMember> members = hlod.Levels[^1].Members;
        for (int index = members.Count - 1; index >= 0; index--)
        {
            EnbW3dRenderHlodMember member = members[index];
            if(!resource.ExternalRenderObjects.Any(value=>string.Equals(value.Name,member.Name,StringComparison.OrdinalIgnoreCase)&&value.BoxDefinition?.RetailClassId==0x1b))continue;
            string name = member.Name;
            int separator = name.IndexOf('.');
            string tail = separator >= 0 ? name[(separator + 1)..] : name;
            if (string.Equals(tail, "BOUNDINGBOX", StringComparison.OrdinalIgnoreCase))
                return member;
        }

        return null;
    }

    private static bool TryComputeCompositeChildBox(EnbW3dCompositeBoundsOwner owner,float modelScale,float pendingDazzleScale,
        out Vector3 center,out Vector3 extent,out string? diagnostic)
    {
        var child=owner.Definition;
        center=child.ConstructorBounds.BoxCenter;extent=child.ConstructorBounds.BoxExtent;diagnostic=null;
        if(child.DazzleDefinitionRadius is {} radius)
        {center=Vector3.Zero;extent=new(radius*(child.DazzleInstanceScale*pendingDazzleScale));return true;}
        if(modelScale==1f && pendingDazzleScale==1f && !child.HasConfiguredHierarchy)return true;
        if(child.MeshMinimum is {} low&&child.MeshMaximum is {} high)
        {var box=ScaledMeshBox(low,high,modelScale);center=box.Center;extent=box.Extent;return true;}
        if(child.CollectionChildren is {} children)
        {
            var boxes=owner.Boxes;
            for(int index=0;index<children.Count;index++)
            {
                if(!TryComputeCompositeChildBox(owner.Children[index],modelScale,pendingDazzleScale,out var childCenter,out var childExtent,out diagnostic))return false;
                boxes[index]=(childCenter,childExtent);
            }
            var box=EnbCurrentRetailCollectionBounds.CalculateBox(owner.Count!,owner.At!);
            center=box.Center;extent=box.Extent;return true;
        }
        if(child.HlodResource is {} resource&&child.Hlod is {} hlod)
        {
            var post=owner.PreparePost(hlod.HierarchyName,modelScale,child.HasConfiguredHierarchy?pendingDazzleScale:null);
            if(!TryComputeCore(resource,hlod,Matrix4x4.Identity,Array.Empty<EnbW3dResolvedWeightedAnimationSample>(),post,false,out var bounds,out diagnostic,pendingDazzleScale,owner:owner.HlodBounds))return false;
            center=bounds.LocalBox.Center;extent=bounds.LocalBox.Extents;
        }
        return true;
    }

    private static bool TryExpandMemberBox(
        EnbW3dRenderResource resource,
        EnbW3dRenderHlod hlod,
        EnbW3dRenderHierarchy hierarchy,
        EnbW3dHierarchyPoseOwner localPose,
        EnbW3dHlodMemberBoundsOwner binding,
        float modelScale,
        float pendingDazzleScale,
        ref bool haveBox,
        ref Vector3 minimum,
        ref Vector3 maximum,
        out string? diagnostic)
    {
        var member=binding.Member;
        diagnostic = null;
        if (member.ReferenceIndex >= (uint)localPose.WorldTransforms.Length)
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' member '{member.Name}' references hierarchy state record {member.ReferenceIndex}, outside '{hierarchy.Name}' ({localPose.WorldTransforms.Length} records).";
            return false;
        }

        Vector3 center,extent;
        var composite=binding.Composite;
        var external=binding.External;
        if(composite is not null)
        {
            if(!TryComputeCompositeChildBox(binding.CompositeOwner!,modelScale,pendingDazzleScale,out center,out extent,out diagnostic))return false;
        }
        else if(external?.DazzleDefinition is {} dazzle)
        {center=Vector3.Zero;extent=new(dazzle.Radius*(external.DazzleInstanceScale*pendingDazzleScale));}
        else if(external?.BoxDefinition is {} authoredBox){center=authoredBox.Center;extent=authoredBox.Extents;}
        else if(EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(member.Name)||external?.Kind==EnbW3dExternalRenderObjectKind.NullObject)
        {center=Vector3.Zero;extent=new(.1f);}
        else if(binding.HasStaticBox)
        {var box=ScaledMeshBox(binding.Minimum,binding.Maximum,modelScale);center=box.Center;extent=box.Extent;}
        else
        {
            if(binding.Mesh is not {} mesh){diagnostic=binding.Diagnostic;return false;}
            var box=ScaledMeshBox(mesh.BoundingBoxMin,mesh.BoundingBoxMax,modelScale);center=box.Center;extent=box.Extent;
        }
        Matrix4x4 memberTransform=localPose.WorldTransforms[(int)member.ReferenceIndex];
        if(!TryExpandTransformedBox(center,extent,memberTransform,ref haveBox,ref minimum,ref maximum))
        {diagnostic=$"W3D HLOD '{hlod.Name}' member '{member.Name}' produced invalid transformed bounds.";return false;}

        return true;
    }

    //0093EDD0→0092D460 scales stored mesh min/max before its local-box
    //getter. Null/Box scale is a no-op; hierarchy translation scales separately.
    private static (Vector3 Center,Vector3 Extent) ScaledMeshBox(Vector3 low,Vector3 high,float scale)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        for(int axis=0;axis<3;axis++){low[axis]=(float)((R)scale*low[axis]);high[axis]=(float)((R)scale*high[axis]);}
        return EnbCurrentRetailRenderBoundsStorage.MeshLocalBox(low,high);
    }

    private static bool TryResolveUniqueMesh(
        EnbW3dRenderResource resource,
        string name,
        out EnbW3dRenderMesh mesh,
        out string? diagnostic)
    {
        mesh = default!;
        diagnostic = null;
        EnbW3dRenderMesh? resolved = null;
        foreach (EnbW3dRenderMesh candidate in resource.Meshes)
        {
            if (!EnbW3dRenderObjectNameSemantics.MatchesMesh(candidate, name))
                continue;
            if (resolved is not null)
            {
                diagnostic = $"W3D HLOD member '{name}' resolves to more than one mesh while current retail owns one attached render-object pointer per member.";
                return false;
            }
            resolved = candidate;
        }

        if (resolved is null)
        {
            diagnostic = $"W3D HLOD member '{name}' is not represented by an EnbNext mesh render object or a proven current-retail no-op RenderObj; bounds for other retail render-object classes remain fail-closed.";
            return false;
        }

        mesh = resolved;
        return true;
    }

    private static bool TryExpandTransformedBox(Vector3 center,Vector3 extent,Matrix4x4 transform,
        ref bool haveBox,ref Vector3 minimum,ref Vector3 maximum)
    {
        if(!IsFinite(center)||!IsFinite(extent)||!IsFinite(transform))return false;
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        Span<float> rows=stackalloc float[] {transform.M11,transform.M21,transform.M31,transform.M41,transform.M12,transform.M22,transform.M32,transform.M42,transform.M13,transform.M23,transform.M33,transform.M43};
        if(!haveBox)
        {
            Vector3 low=default,high=default;
            for(int axis=0;axis<3;axis++){low[axis]=(float)((R)center[axis]-extent[axis]);high[axis]=(float)((R)extent[axis]+center[axis]);}
            (minimum,maximum)=EnbCurrentRetailAffineBounds.MinMax(rows,low,high);haveBox=true;
        }
        else
        {
            var box=EnbCurrentRetailAffineBounds.CenterExtent(rows,center,extent);
            if(box.Extent!=Vector3.Zero)
                for(int axis=0;axis<3;axis++)
                {
                    R low=(R)box.Center[axis]-box.Extent[axis],high=(R)box.Extent[axis]+box.Center[axis];
                    if(axis!=0){low=(float)low;high=(float)high;}
                    if(low<minimum[axis])minimum[axis]=(float)low;
                    if(high>maximum[axis])maximum[axis]=(float)high;
                }
        }
        return IsFinite(minimum)&&IsFinite(maximum);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}

public sealed record EnbW3dHlodProxySample(string Name, Matrix4x4 WorldTransform);

/// <summary>
/// Current-retail HLOD proxy-array accessors from virtual +0x20C / 0098DAA0
/// and +0x210 / 0098DAC0. Proxy records are not render children. Querying one
/// returns its authored name together with the current 3x4 hierarchy state
/// transform selected by the record's 0x0704 ReferenceIndex.
/// </summary>
public static class EnbW3dHlodProxyResolver
{
    public static int GetCount(EnbW3dRenderHlod hlod)
    {
        ArgumentNullException.ThrowIfNull(hlod);
        return hlod.ProxyArray?.Members.Count ?? 0;
    }

    public static bool TryResolve(
        EnbW3dRenderResource resource,
        EnbW3dRenderHlod hlod,
        int proxyOrdinal,
        Matrix4x4 hlodWorldTransform,
        EnbW3dRenderHierarchyAnimation? animation,
        float animationFrame,
        out EnbW3dHlodProxySample proxy,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(hlod);
        proxy = new EnbW3dHlodProxySample(string.Empty, Matrix4x4.Identity);
        diagnostic = null;

        EnbW3dRenderHlodProxyArray? array = hlod.ProxyArray;
        if (array is null)
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' has no current-retail 0x0706 proxy array.";
            return false;
        }

        if ((uint)proxyOrdinal >= (uint)array.Members.Count)
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' proxy ordinal {proxyOrdinal} is outside [0,{array.Members.Count}).";
            return false;
        }

        if (!IsFinite(hlodWorldTransform) || !float.IsFinite(animationFrame))
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' proxy query received non-finite scene transform/animation data.";
            return false;
        }

        EnbW3dRenderHierarchy? hierarchy = resource.Hierarchies.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, hlod.HierarchyName, StringComparison.OrdinalIgnoreCase));
        if (hierarchy is null)
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' requires missing hierarchy '{hlod.HierarchyName}' for proxy resolution.";
            return false;
        }

        //0098B7D0 retains the raw proxy index;0098DB02..17 uses it directly
        //against the evaluated tree, including legacy inserted-root trees.

        EnbW3dRenderHierarchyAnimation? selectedAnimation = animation is not null &&
            string.Equals(animation.HierarchyName, hierarchy.Name, StringComparison.OrdinalIgnoreCase)
                ? animation
                : null;
        if (animation is not null && selectedAnimation is null)
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' received animation '{animation.Name}' for hierarchy '{animation.HierarchyName}', not '{hierarchy.Name}'.";
            return false;
        }

        if (!EnbW3dHierarchyPose.TryEvaluate(
                hierarchy,
                hlodWorldTransform,
                selectedAnimation,
                selectedAnimation is null ? 0f : animationFrame,
                out EnbW3dHierarchyPoseOwner pose,
                out diagnostic))
        {
            return false;
        }

        EnbW3dRenderHlodMember member = array.Members[proxyOrdinal];
        if (member.ReferenceIndex >= (uint)pose.WorldTransforms.Length)
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' proxy '{member.Name}' references hierarchy state record {member.ReferenceIndex}, outside '{hierarchy.Name}' ({pose.WorldTransforms.Length} records).";
            return false;
        }

        proxy = new EnbW3dHlodProxySample(
            member.Name,
            pose.WorldTransforms[(int)member.ReferenceIndex]);
        return true;
    }

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}

public sealed record EnbW3dHlodSceneInstance(
    EnbW3dRenderResource Resource,
    EnbW3dRenderHlod Hlod,
    int CurrentLevelOrdinal,
    Matrix4x4 WorldTransform,
    EnbCurrentRetailRenderObjectState RuntimeState,
    IReadOnlyDictionary<EnbW3dHlodMemberKey, EnbCurrentRetailRenderObjectState> MemberRuntimeStates,
    EnbW3dRenderHierarchyAnimation? Animation = null,
    float AnimationFrame = 0f);

public sealed record EnbW3dHlodSceneGatherEntry(
    string Name,
    EnbW3dWorldBounds? Bounds,
    bool? FrustumVisible,
    bool PreparedForDetail,
    bool IsFixedCost);

public sealed record EnbW3dHlodSceneGatherResult(
    float FixedRenderCost,
    ImmutableArray<EnbHlodDetailOwner> Candidates,
    ImmutableArray<EnbW3dHlodSceneGatherEntry> Entries);

/// <summary>
/// Scene-runtime seam matching FUN_00965F40 -> HLOD +0x104 / FUN_0098E9A0.
/// Scene visibility owns whether an HLOD reaches detail preparation; member
/// +0x174 activity owns per-level cost independently of per-frame visibility.
/// One-level HLODs contribute fixed cost, while multi-level HLODs enter the
/// shared allocator candidate set.
/// </summary>
public static class EnbW3dHlodSceneGatherer
{
    public static bool TryGather(
        EnbResolvedRenderCamera camera,
        IReadOnlyList<EnbW3dHlodSceneInstance> instances,
        float lodScale,
        out EnbW3dHlodSceneGatherResult result,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(instances);
        result = default!;
        diagnostic = null;
        if (!float.IsFinite(lodScale))
        {
            diagnostic = "W3D HLOD scene gather received a non-finite LOD scale.";
            return false;
        }

        float fixedRenderCost = 0f;
        var candidates = ImmutableArray.CreateBuilder<EnbHlodDetailOwner>();
        var entries = ImmutableArray.CreateBuilder<EnbW3dHlodSceneGatherEntry>(instances.Count);

        foreach (EnbW3dHlodSceneInstance instance in instances)
        {
            if (instance is null)
            {
                diagnostic = "W3D HLOD scene gather contains a null instance.";
                return false;
            }

            EnbW3dHlodBoundsOwner? compositeBounds = null;
            bool? frustumVisible = null;
            if (!instance.RuntimeState.ForcesSceneVisibility)
            {
                if (!EnbW3dHlodCompositeBoundsCompiler.TryCompute(
                        instance.Resource,
                        instance.Hlod,
                        instance.WorldTransform,
                        instance.Animation,
                        instance.AnimationFrame,
                        out compositeBounds,
                        out diagnostic))
                {
                    return false;
                }

                frustumVisible = EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(
                    camera,
                    compositeBounds.WorldSphere);
            }

            bool prepareDetail = instance.RuntimeState.ShouldPrepareDetail(frustumVisible ?? false);
            bool isFixedCost = instance.Hlod.Levels.Count == 1;

            if (!prepareDetail)
            {
                entries.Add(new EnbW3dHlodSceneGatherEntry(
                    instance.Hlod.Name,
                    compositeBounds?.WorldSphere,
                    frustumVisible,
                    PreparedForDetail: false,
                    isFixedCost));
                continue;
            }

            string? memberDiagnostic = null;
            bool IsMemberActive(EnbW3dRenderHlodMember member)
            {
                if (!TryGetMemberActivity(instance, member, out bool active, out string? stateDiagnostic))
                {
                    memberDiagnostic ??= stateDiagnostic;
                    return false;
                }
                return active;
            }

            if (!EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(
                    instance.Resource,
                    instance.Hlod,
                    IsMemberActive,
                    out ImmutableArray<int> levelCosts,
                    out string? costDiagnostic))
            {
                diagnostic = memberDiagnostic ?? costDiagnostic;
                return false;
            }
            if (memberDiagnostic is not null)
            {
                diagnostic = memberDiagnostic;
                return false;
            }

            if (!EnbW3dHlodBudgetCompiler.TryComputeW3dAggregateFixedRenderCost(
                    instance.Resource,
                    instance.Hlod,
                    IsMemberActive,
                    out float aggregateFixedCost,
                    out string? aggregateDiagnostic))
            {
                diagnostic = memberDiagnostic ?? aggregateDiagnostic;
                return false;
            }
            if (memberDiagnostic is not null)
            {
                diagnostic = memberDiagnostic;
                return false;
            }
            fixedRenderCost += aggregateFixedCost;

            if (isFixedCost)
            {
                fixedRenderCost += EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(levelCosts[0]);
                entries.Add(new EnbW3dHlodSceneGatherEntry(
                    instance.Hlod.Name,
                    compositeBounds?.WorldSphere,
                    frustumVisible,
                    PreparedForDetail: true,
                    IsFixedCost: true));
                continue;
            }

            if (compositeBounds is null &&
                !EnbW3dHlodCompositeBoundsCompiler.TryCompute(
                    instance.Resource,
                    instance.Hlod,
                    instance.WorldTransform,
                    instance.Animation,
                    instance.AnimationFrame,
                    out compositeBounds,
                    out diagnostic))
            {
                return false;
            }

            if (!EnbW3dHlodBudgetCompiler.TryCreateCandidate(
                    instance.Hlod,
                    instance.CurrentLevelOrdinal,
                    camera,
                    compositeBounds.WorldSphere,
                    levelCosts,
                    lodScale,
                    out EnbHlodDetailOwner candidate,
                    out diagnostic))
            {
                return false;
            }
            candidates.Add(candidate);
            entries.Add(new EnbW3dHlodSceneGatherEntry(
                instance.Hlod.Name,
                compositeBounds.WorldSphere,
                frustumVisible,
                PreparedForDetail: true,
                IsFixedCost: false));
        }

        result = new EnbW3dHlodSceneGatherResult(
            fixedRenderCost,
            candidates.ToImmutable(),
            entries.ToImmutable());
        return true;
    }

    private static bool TryGetMemberActivity(
        EnbW3dHlodSceneInstance instance,
        EnbW3dRenderHlodMember member,
        out bool active,
        out string? diagnostic)
    {
        diagnostic = null;
        active = false;
        EnbW3dHlodMemberKey key = EnbW3dHlodMemberKey.From(member);
        if (!instance.MemberRuntimeStates.TryGetValue(key, out EnbCurrentRetailRenderObjectState state))
        {
            diagnostic = $"W3D HLOD '{instance.Hlod.Name}' has no current runtime +0x174 state for member '{member.Name}' at reference {member.ReferenceIndex}.";
            return false;
        }
        active = state.IsDetailActivityEligible;
        return true;
    }
}
