using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Explicit animation sample chosen by presentation/runtime policy. This owns
/// how a W3D animation is sampled, but does not invent which animation should
/// play or derive that choice from RSL/HLOD state.
/// </summary>
public readonly record struct EnbW3dAnimationSample(string Name, float Frame);

/// <summary>
/// One current-retail HAnim aggregate entry after RSL state activation has
/// selected a concrete HAnim, frame, and global blend weight. The retail
/// aggregate normalizer operates on these global weights before the current
/// RenderObj evaluates its hierarchy. Per-pivot PIVOTMAP weighting is a
/// separate branch and remains outside this ordinary unmasked representation.
/// </summary>
// HierarchyName, when present, makes the entry the asset-manager registry
// identity "<hierarchy>.<name>" (EnbCurrentRetailRslHAnimResolver.BuildRegistryName).
// Two HAnims sharing a raw name on different hierarchies are distinct in retail.
public readonly record struct EnbW3dWeightedAnimationSample(
    string Name,
    float Frame,
    float Weight,
    string? HierarchyName = null);

/// <summary>
/// Rendering-internal resolved form of one weighted HAnim entry. Keeping the
/// animation object here lets a blend combine HAnims that retail resolved from
/// different W3D files while geometry remains owned by the current RenderObj.
/// </summary>
public readonly record struct EnbW3dResolvedWeightedAnimationSample(
    EnbW3dRenderHierarchyAnimation Animation,
    float Frame,
    float Weight);

/// <summary>
/// One resolved hierarchy pose. World transforms use System.Numerics row-vector
/// convention. Visibility is the current-retail per-pivot animation result.
/// </summary>
public readonly record struct EnbW3dPivotAnimationSample(
    Vector3 Translation,
    Quaternion Rotation,
    bool Visible);

/// <summary>
/// Current-retail HAnim sampler. This owns classic 0x0200 channels and adds
/// bounded 0x0280 flavor-0 sparse translation, quaternion and visibility
/// execution. Scalar rotation channels 3..5 remain preserved but unused because
/// the active retail pivot samplers do not consume them.
/// </summary>
public static class EnbW3dAnimationSampler
{
    internal struct ClassicChannels
    {
        public EnbW3dRenderAnimationChannel? X,Y,Z,Rotation;
        public EnbW3dRenderAnimationBitChannel? Visibility;
    }
    internal struct CompressedChannels
    {
        public EnbW3dRenderCompressedAnimationChannel? X,Y,Z,Rotation;
        public EnbW3dRenderCompressedAnimationBitChannel? Visibility;
    }
    // 009C3320 indexes the loaded clip's +50 table at pivot*20; it does
    // not search the authored chunks again while sampling. Keep equivalent
    // channel selection on the immutable decoded clip, not on a frame/owner.
    // A replacement clip gets a new weak key; channel data and clocks remain
    // the originals. Duplicate decoded channels retain last-entry selection.
    internal sealed class ChannelIndex
    {
        public readonly Dictionary<int,ClassicChannels> Classic=[];
        public readonly Dictionary<int,CompressedChannels> Compressed=[];
        public ChannelIndex(EnbW3dRenderHierarchyAnimation animation)
        {
            foreach(var channel in animation.Channels)
            {
                Classic.TryGetValue(channel.PivotIndex,out var node);
                switch(channel.KnownKind)
                {
                    case EnbW3dAnimationChannelKind.TranslationX:node.X=channel;break;
                    case EnbW3dAnimationChannelKind.TranslationY:node.Y=channel;break;
                    case EnbW3dAnimationChannelKind.TranslationZ:node.Z=channel;break;
                    case EnbW3dAnimationChannelKind.Quaternion:node.Rotation=channel;break;
                }
                Classic[channel.PivotIndex]=node;
            }
            foreach(var channel in animation.BitChannels)
                if(channel.KnownKind==EnbW3dAnimationBitChannelKind.Visibility)
                {Classic.TryGetValue(channel.PivotIndex,out var node);node.Visibility=channel;Classic[channel.PivotIndex]=node;}
            foreach(var channel in animation.CompressedChannels)
            {
                Compressed.TryGetValue(channel.PivotIndex,out var node);
                switch(channel.KnownKind)
                {
                    case EnbW3dAnimationChannelKind.TranslationX:node.X=channel;break;
                    case EnbW3dAnimationChannelKind.TranslationY:node.Y=channel;break;
                    case EnbW3dAnimationChannelKind.TranslationZ:node.Z=channel;break;
                    case EnbW3dAnimationChannelKind.Quaternion:node.Rotation=channel;break;
                }
                Compressed[channel.PivotIndex]=node;
            }
            foreach(var channel in animation.CompressedBitChannels)
                if(channel.KnownKind==EnbW3dAnimationBitChannelKind.Visibility)
                {Compressed.TryGetValue(channel.PivotIndex,out var node);node.Visibility=channel;Compressed[channel.PivotIndex]=node;}
        }
    }
    public static bool TrySamplePivot(
        EnbW3dRenderHierarchyAnimation animation,
        int pivotIndex,
        float frame,
        out EnbW3dPivotAnimationSample sample,
        out string? diagnostic,
        bool sampleVisibility = true)
    {
        ArgumentNullException.ThrowIfNull(animation);
        sample = default;
        diagnostic = null;

        if (animation.Family == EnbW3dHAnimFamily.Adaptive02C0)
        {
            if (animation.TimeMappedPlayback is {} playback)
                return playback.TrySample(pivotIndex, frame, out sample, out diagnostic);
            diagnostic = $"02C0 animation '{animation.Name}' has not acquired its sources.";
            return false;
        }

        // Loaded NEXT may publish a frame beyond the clip's duration.
        // Concrete HAnim channels resolve their own ranges; do not clamp or
        // reject that frame before their native sampling semantics run.
        if (!float.IsFinite(frame) || frame <= int.MinValue)
        {
            diagnostic = $"Hierarchy animation '{animation.Name}' frame {frame} is not a finite samplable frame.";
            return false;
        }

        if (animation.Family == EnbW3dHAnimFamily.Compressed0280)
        {
            if (animation.CompressionFlavor != 0)
            {
                diagnostic = $"Hierarchy animation '{animation.Name}' uses compressed 0x0280 flavor {animation.CompressionFlavor}; R6Z21 executes only current-retail flavor 0.";
                return false;
            }

            return TrySampleCompressedFlavor0Pivot(animation, pivotIndex, frame, out sample, out diagnostic, sampleVisibility);
        }

        if (animation.Family != EnbW3dHAnimFamily.Classic0200)
        {
            diagnostic = $"Hierarchy animation '{animation.Name}' family 0x{(uint)animation.Family:X4} is not executable at the current sampler boundary.";
            return false;
        }

        var selectedFrames=EnbCurrentRetailClassicAnimationArithmetic.SelectFrames(frame,animation.NumFrames);
        int currentFrame=selectedFrames.Current,nextFrame=selectedFrames.Next;
        float fraction=selectedFrames.Fraction;

        OwnerCache.Get(ref animation.DerivedChannelIndex,animation,static clip=>new ChannelIndex(clip)).Classic.TryGetValue(pivotIndex,out var channels);
        var tx=channels.X;var ty=channels.Y;var tz=channels.Z;var quaternion=channels.Rotation;

        if (!TrySampleScalar(tx, currentFrame, out float x0, out diagnostic) ||
            !TrySampleScalar(ty, currentFrame, out float y0, out diagnostic) ||
            !TrySampleScalar(tz, currentFrame, out float z0, out diagnostic) ||
            !TrySampleQuaternion(quaternion, currentFrame, out Quaternion q0, out diagnostic))
        {
            diagnostic = $"Hierarchy animation '{animation.Name}' pivot {pivotIndex}: {diagnostic}";
            return false;
        }

        Vector3 translation;
        Quaternion rotation;
        if (fraction == 0f)
        {
            translation = new Vector3(x0, y0, z0);
            rotation = q0;
        }
        else
        {
            if (!TrySampleScalar(tx, nextFrame, out float x1, out diagnostic) ||
                !TrySampleScalar(ty, nextFrame, out float y1, out diagnostic) ||
                !TrySampleScalar(tz, nextFrame, out float z1, out diagnostic) ||
                !TrySampleQuaternion(quaternion, nextFrame, out Quaternion q1, out diagnostic))
            {
                diagnostic = $"Hierarchy animation '{animation.Name}' pivot {pivotIndex}: {diagnostic}";
                return false;
            }

            translation = new Vector3(
                EnbCurrentRetailClassicAnimationArithmetic.InterpolateScalar(x0,x1,fraction),
                EnbCurrentRetailClassicAnimationArithmetic.InterpolateScalar(y0,y1,fraction),
                EnbCurrentRetailClassicAnimationArithmetic.InterpolateScalar(z0,z1,fraction));
            rotation = InterpolateQuaternionRetail(q0, q1, fraction);
        }

        if (!IsFinite(translation) || !IsFinite(rotation))
        {
            diagnostic = $"Hierarchy animation '{animation.Name}' pivot {pivotIndex} produced non-finite sampled transform data.";
            return false;
        }

        bool visible = true;
        var visibility=channels.Visibility;
        if (sampleVisibility && visibility is not null && !TrySampleVisibility(visibility, unchecked((int)(long)frame), out visible, out diagnostic))
        {
            diagnostic = $"Hierarchy animation '{animation.Name}' pivot {pivotIndex}: {diagnostic}";
            return false;
        }

        sample = new EnbW3dPivotAnimationSample(translation, rotation, visible);
        return true;
    }

    private static bool TrySampleCompressedFlavor0Pivot(
        EnbW3dRenderHierarchyAnimation animation,
        int pivotIndex,
        float frame,
        out EnbW3dPivotAnimationSample sample,
        out string? diagnostic, bool sampleVisibility)
    {
        sample = default;
        diagnostic = null;

        OwnerCache.Get(ref animation.DerivedChannelIndex,animation,static clip=>new ChannelIndex(clip)).Compressed.TryGetValue(pivotIndex,out var channels);
        var tx=channels.X;var ty=channels.Y;var tz=channels.Z;var quaternion=channels.Rotation;

        if (!TrySampleCompressedScalar(tx, frame, out float x, out diagnostic) ||
            !TrySampleCompressedScalar(ty, frame, out float y, out diagnostic) ||
            !TrySampleCompressedScalar(tz, frame, out float z, out diagnostic) ||
            !TrySampleCompressedQuaternion(quaternion, frame, out Quaternion rotation, out diagnostic))
        {
            diagnostic = $"Compressed hierarchy animation '{animation.Name}' pivot {pivotIndex}: {diagnostic}";
            return false;
        }

        bool visible = true;
        var visibility=channels.Visibility;
        if (sampleVisibility && visibility is not null && !TrySampleCompressedVisibility(visibility, frame, out visible, out diagnostic))
        {
            diagnostic = $"Compressed hierarchy animation '{animation.Name}' pivot {pivotIndex}: {diagnostic}";
            return false;
        }

        Vector3 translation = new(x, y, z);
        if (!IsFinite(translation) || !IsFinite(rotation))
        {
            diagnostic = $"Compressed hierarchy animation '{animation.Name}' pivot {pivotIndex} produced non-finite sampled transform data.";
            return false;
        }

        sample = new EnbW3dPivotAnimationSample(translation, rotation, visible);
        return true;
    }

    private static bool TrySampleCompressedScalar(
        EnbW3dRenderCompressedAnimationChannel? channel,
        float frame,
        out float value,
        out string? diagnostic)
    {
        value = 0f;
        diagnostic = null;
        if (channel is null)
            return true;
        if (channel.VectorLength != 1)
        {
            diagnostic = $"compressed scalar channel has vector length {channel.VectorLength}, expected 1.";
            return false;
        }

        if (!TryFindCompressedSegment(channel.TimeCodes, frame, out int currentIndex, out int nextIndex, out bool hold, out float fraction, out diagnostic))
            return false;
        if ((uint)currentIndex >= (uint)channel.Data.Count)
        {
            diagnostic = "compressed scalar channel current key exceeds decoded sample storage.";
            return false;
        }

        float current = channel.Data[currentIndex];
        if (nextIndex < 0 || hold)
        {
            value = current;
            if (!float.IsFinite(value))
            {
                diagnostic = "compressed scalar key contains a non-finite value.";
                return false;
            }
            return true;
        }
        if ((uint)nextIndex >= (uint)channel.Data.Count)
        {
            diagnostic = "compressed scalar channel next key exceeds decoded sample storage.";
            return false;
        }

        float next = channel.Data[nextIndex];
        value = EnbCurrentRetailSparseAnimationArithmetic.InterpolateScalar(current,next,fraction);
        if (!float.IsFinite(value))
        {
            diagnostic = "compressed scalar channel produced a non-finite interpolated value.";
            return false;
        }
        return true;
    }

    private static bool TrySampleCompressedQuaternion(
        EnbW3dRenderCompressedAnimationChannel? channel,
        float frame,
        out Quaternion value,
        out string? diagnostic)
    {
        value = Quaternion.Identity;
        diagnostic = null;
        if (channel is null)
            return true;
        if (channel.VectorLength != 4)
        {
            diagnostic = $"compressed quaternion channel has vector length {channel.VectorLength}, expected 4.";
            return false;
        }

        if (!TryFindCompressedSegment(channel.TimeCodes, frame, out int currentKey, out int nextKey, out bool hold, out float fraction, out diagnostic))
            return false;
        int currentOffset = checked(currentKey * 4);
        if (currentOffset < 0 || currentOffset > channel.Data.Count - 4)
        {
            diagnostic = "compressed quaternion current key exceeds decoded sample storage.";
            return false;
        }

        Quaternion current = new(
            channel.Data[currentOffset],
            channel.Data[currentOffset + 1],
            channel.Data[currentOffset + 2],
            channel.Data[currentOffset + 3]);
        if (nextKey < 0 || hold)
        {
            value = current;
            if (!IsFinite(value))
            {
                diagnostic = "compressed quaternion key contains a non-finite value.";
                return false;
            }
            return true;
        }

        int nextOffset = checked(nextKey * 4);
        if (nextOffset < 0 || nextOffset > channel.Data.Count - 4)
        {
            diagnostic = "compressed quaternion next key exceeds decoded sample storage.";
            return false;
        }

        Quaternion next = new(
            channel.Data[nextOffset],
            channel.Data[nextOffset + 1],
            channel.Data[nextOffset + 2],
            channel.Data[nextOffset + 3]);
        value = InterpolateQuaternionRetail(current, next, fraction);
        if (!IsFinite(value))
        {
            diagnostic = "compressed quaternion interpolation produced a non-finite value.";
            return false;
        }
        return true;
    }

    private static bool TrySampleCompressedVisibility(
        EnbW3dRenderCompressedAnimationBitChannel channel,
        float frame,
        out bool visible,
        out string? diagnostic)
    {
        visible = true;
        diagnostic = null;
        if (channel.TimeCodes.Count == 0)
        {
            diagnostic = "compressed visibility channel contains no time codes.";
            return false;
        }

        // Compressed samplers call __ftol (00A264EC), truncating toward zero;
        // loaded NEXT may supply a signed fraction before frame zero.
        int integerFrame=unchecked((int)(long)frame),current=0;
        EnbCurrentRetailSparseAnimationArithmetic.SelectVisibilityKey(channel.TimeCodes,integerFrame,ref current);
        if ((uint)current >= (uint)channel.TimeCodes.Count)
        {
            diagnostic = "compressed visibility channel key search escaped decoded storage.";
            return false;
        }

        visible = (channel.TimeCodes[current] & 0x8000_0000u) != 0;
        return true;
    }

    private static bool TryFindCompressedSegment(
        IReadOnlyList<uint> timeCodes,
        float frame,
        out int currentIndex,
        out int nextIndex,
        out bool hold,
        out float fraction,
        out string? diagnostic)
    {
        currentIndex = 0;
        nextIndex = -1;
        hold = true;
        fraction = 0f;
        diagnostic = null;
        if (timeCodes.Count == 0)
        {
            diagnostic = "compressed channel contains no time codes.";
            return false;
        }

        uint integerFrame = unchecked((uint)(long)frame);
        currentIndex = FindCompressedKeyIndex(timeCodes, integerFrame);
        if (currentIndex >= timeCodes.Count - 1)
            return true;

        nextIndex = currentIndex + 1;
        uint currentFrame = timeCodes[currentIndex] & 0x7FFF_FFFFu;
        uint nextFrame = timeCodes[nextIndex] & 0x7FFF_FFFFu;
        hold = (timeCodes[nextIndex] & 0x8000_0000u) != 0;
        if (hold)
            return true;
        if (nextFrame <= currentFrame)
        {
            diagnostic = $"compressed channel has non-increasing time codes {currentFrame} -> {nextFrame}.";
            return false;
        }

        fraction = EnbCurrentRetailSparseAnimationArithmetic.Fraction(frame,currentFrame,nextFrame);
        if (!float.IsFinite(fraction))
        {
            diagnostic = "compressed channel produced a non-finite interpolation fraction.";
            return false;
        }
        return true;
    }

    private static int FindCompressedKeyIndex(IReadOnlyList<uint> timeCodes, uint frame)
    {
        int low = 0;
        int high = timeCodes.Count - 1;
        if ((timeCodes[high] & 0x7FFF_FFFFu) <= frame)
            return high;

        while (low < high - 1)
        {
            int middle = low + ((high - low) >> 1);
            uint middleFrame = timeCodes[middle] & 0x7FFF_FFFFu;
            if (middleFrame <= frame)
                low = middle;
            else
                high = middle;
        }

        return low;
    }

    public static Quaternion InterpolateQuaternionRetail(Quaternion first, Quaternion second, float fraction)
        => EnbCurrentRetailQuaternionBlendRuntime.Interpolate(first,second,fraction,
            EnbCurrentRetailTrigonometricTables.InverseCosineEntry,EnbCurrentRetailTrigonometricTables.SineEntry,
            EnbCurrentRetailTrigonometricTables.InverseCosineSinglePrecision);

    private static bool TrySampleScalar(
        EnbW3dRenderAnimationChannel? channel,
        int frame,
        out float value,
        out string? diagnostic)
    {
        value = 0f;
        diagnostic = null;
        if (channel is null || frame < channel.FirstFrame || frame > channel.LastFrame)
            return true;

        if (channel.VectorLength != 1)
        {
            diagnostic = $"scalar channel has vector length {channel.VectorLength}, expected 1.";
            return false;
        }

        int index = frame - channel.FirstFrame;
        if ((uint)index >= (uint)channel.Data.Count)
        {
            diagnostic = $"scalar channel frame {frame} exceeds its decoded sample storage.";
            return false;
        }

        value = channel.Data[index];
        if (!float.IsFinite(value))
        {
            diagnostic = $"scalar channel frame {frame} contains a non-finite value.";
            return false;
        }
        return true;
    }

    private static bool TrySampleQuaternion(
        EnbW3dRenderAnimationChannel? channel,
        int frame,
        out Quaternion value,
        out string? diagnostic)
    {
        value = Quaternion.Identity;
        diagnostic = null;
        if (channel is null || frame < channel.FirstFrame || frame > channel.LastFrame)
            return true;

        if (channel.VectorLength != 4)
        {
            diagnostic = $"quaternion channel has vector length {channel.VectorLength}, expected 4.";
            return false;
        }

        int index = checked((frame - channel.FirstFrame) * 4);
        if (index < 0 || index > channel.Data.Count - 4)
        {
            diagnostic = $"quaternion channel frame {frame} exceeds its decoded sample storage.";
            return false;
        }

        value = new Quaternion(
            channel.Data[index],
            channel.Data[index + 1],
            channel.Data[index + 2],
            channel.Data[index + 3]);
        if (!IsFinite(value))
        {
            diagnostic = $"quaternion channel frame {frame} contains a non-finite value.";
            return false;
        }
        return true;
    }

    private static bool TrySampleVisibility(
        EnbW3dRenderAnimationBitChannel channel,
        int frame,
        out bool visible,
        out string? diagnostic)
    {
        diagnostic = null;
        if (frame < channel.FirstFrame || frame > channel.LastFrame)
        {
            visible = channel.DefaultValue == 1;
            return true;
        }

        int relativeFrame = frame - channel.FirstFrame;
        int byteIndex = relativeFrame >> 3;
        int bitIndex = relativeFrame & 7;
        if ((uint)byteIndex >= (uint)channel.PackedData.Count)
        {
            visible = false;
            diagnostic = $"visibility frame {frame} exceeds its decoded packed-bit storage.";
            return false;
        }

        visible = (channel.PackedData[byteIndex] & (1 << bitIndex)) != 0;
        return true;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}

/// <summary>
/// CPU-side deformed geometry produced by current-retail one-index-per-vertex
/// skinning. Positions are already in resolved hierarchy/world space, therefore
/// a backend submits this geometry with an identity world transform.
/// </summary>
public sealed record EnbW3dDeformedGeometry(
    IReadOnlyList<Vector3> Positions,
    IReadOnlyList<Vector3> Normals)
{
    // A skin pick can retain immutable source/pose inputs rather than taking
    // ownership of the transient GPU vertex array. Other generated geometry
    // continues to publish its completed positions.
    private readonly IReadOnlyList<Vector3>? _publicationSource, _publicationPositions;
    internal IReadOnlyList<Vector3>? PublicationPositions
    {
        get => ReferenceEquals(Positions,_publicationSource) ? _publicationPositions : null;
        init { _publicationSource=Positions; _publicationPositions=value; }
    }
    internal bool HasMutablePickPose {get;init;}
    internal bool IsActive => (Positions is not EnbW3dFrameStream<Vector3> p || p.IsActive) &&
        (Normals is not EnbW3dFrameStream<Vector3> n || n.IsActive);
}

public static class EnbW3dSkinDeformer
{
    internal sealed class SkinBindings
    {
        internal readonly Vector3[] Positions,Normals;
        internal readonly ushort[] Bones;
        internal readonly (ushort Bone,int[] Vertices)[] Groups;
        internal readonly int MaximumBone;
        internal SkinBindings(EnbW3dRenderMesh mesh)
        {
            Positions=mesh.Positions.ToArray();Normals=mesh.Normals.ToArray();Bones=mesh.BoneIndices.ToArray();
            var groups=new Dictionary<ushort,List<int>>();
            for(int i=0;i<mesh.BoneIndices.Count;i++)
            {
                ushort bone=mesh.BoneIndices[i];
                if(!groups.TryGetValue(bone,out var vertices))groups.Add(bone,vertices=[]);
                vertices.Add(i);MaximumBone=Math.Max(MaximumBone,bone);
            }
            Groups=groups.Select(pair=>(pair.Key,pair.Value.ToArray())).ToArray();
        }
    }
    private sealed class SkinPickPositions(SkinBindings source,EnbW3dHierarchyPoseOwner pose) : IReadOnlyList<Vector3>
    {
        public int Count => source.Positions.Length;
        public Vector3 this[int index] => Vector3.Transform(source.Positions[index],pose.WorldTransforms[source.Bones[index]]);
        public IEnumerator<Vector3> GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private static SkinBindings BindingsOf(EnbW3dRenderMesh mesh)=>OwnerCache.Get(ref mesh.DerivedSkinBindings,mesh,static source=>new SkinBindings(source));

    internal static bool SameOwnedBoneVersions(EnbW3dRenderMesh mesh,EnbW3dHierarchyPoseOwner pose,long[]? versions)
    {
        if(versions is null||pose.TransformRevisions is not {} current)return false;
        var bindings=BindingsOf(mesh);
        if(versions.Length!=bindings.Groups.Length||bindings.MaximumBone>=current.Length)return false;
        for(int i=0;i<versions.Length;i++)if(versions[i]!=current[bindings.Groups[i].Bone])return false;
        return true;
    }
    internal static long[]? RetainOwnedBoneVersions(EnbW3dRenderMesh mesh,EnbW3dHierarchyPoseOwner pose,long[]? versions)
    {
        if(pose.TransformRevisions is not {} current)return null;
        var bindings=BindingsOf(mesh);
        if(bindings.MaximumBone>=current.Length)return null;
        if(versions is null||versions.Length!=bindings.Groups.Length)versions=new long[bindings.Groups.Length];
        for(int i=0;i<versions.Length;i++)versions[i]=current[bindings.Groups[i].Bone];
        return versions;
    }

    // A hierarchy publication may change bones this mesh never reads. Compare
    // the exact matrix words of its authored influences, not animation clocks
    // or pose object identity. Signed zero is deliberately significant.
    internal static bool UsesSameTransforms(EnbW3dRenderMesh mesh,
        EnbW3dHierarchyPoseOwner before,EnbW3dHierarchyPoseOwner after)
    {
        var bindings=BindingsOf(mesh);
        if(bindings.MaximumBone>=before.WorldTransforms.Length ||
            bindings.MaximumBone>=after.WorldTransforms.Length)return false;
        foreach(var group in bindings.Groups)
            if(!System.Runtime.InteropServices.MemoryMarshal.AsBytes(before.WorldTransforms.AsSpan().Slice(group.Bone,1))
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(after.WorldTransforms.AsSpan().Slice(group.Bone,1))))
                return false;
        return true;
    }

    // Retail 00958B90 reuses one hierarchy matrix for vertices with the same
    // bone. Resolve immutable influences at model preparation, retaining each
    // output at its original index. Current pose values are always consumed.
    internal static bool TryDeformPrepared(EnbW3dRenderMesh mesh,EnbW3dHierarchyPoseOwner pose,
        out EnbW3dDeformedGeometry geometry,out string? diagnostic, EnbW3dTransientGeometryArena.Frame? frame=null)
    {
        if(mesh.BoneIndices.Count!=mesh.Positions.Count || mesh.BoneIndices.Count==0 ||
            mesh.Normals.Count!=0 && mesh.Normals.Count!=mesh.Positions.Count)
            return TryDeform(mesh,pose,out geometry,out diagnostic);
        var prepared=BindingsOf(mesh);
        if(prepared.MaximumBone>=pose.WorldTransforms.Length)
            return TryDeform(mesh,pose,out geometry,out diagnostic);
        var positionLease=frame?.RentPositions(prepared.Positions.Length);
        var normalLease=prepared.Normals.Length==0?null:frame?.RentPositions(prepared.Normals.Length);
        var positionArray=positionLease is null?new Vector3[prepared.Positions.Length]:null;
        var normalArray=normalLease is null?(prepared.Normals.Length==0?Array.Empty<Vector3>():new Vector3[prepared.Normals.Length]):null;
        Span<Vector3> positions=positionLease is null?positionArray.AsSpan():positionLease.WriteSpan;
        Span<Vector3> normals=normalLease is null?normalArray.AsSpan():normalLease.WriteSpan;
        foreach(var group in prepared.Groups)
        {
            var transform=pose.WorldTransforms[group.Bone];
            foreach(int index in group.Vertices)
            {
                positions[index]=Vector3.Transform(prepared.Positions[index],transform);
                if(normals.Length!=0)normals[index]=Vector3.TransformNormal(prepared.Normals[index],transform);
            }
        }
        // Preserve failure order and diagnostics of the literal path.
        for(int i=0;i<positions.Length;i++)
            if(!IsFinite(positions[i]) || normals.Length!=0 && !IsFinite(normals[i]))
                return TryDeform(mesh,pose,out geometry,out diagnostic);
        positionLease?.SealForPublication();
        normalLease?.SealValidatedVector3();
        geometry=new(positionLease is not null?positionLease:
            System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(positionArray!),
            normalLease is not null?normalLease:
            System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(normalArray!))
        { PublicationPositions = positionLease is null ? null : new SkinPickPositions(prepared,pose), HasMutablePickPose=pose.IsOwnerHeld };
        diagnostic=null;return true;
    }

    public static bool TryDeform(
        EnbW3dRenderMesh mesh,
        EnbW3dHierarchyPoseOwner pose,
        out EnbW3dDeformedGeometry geometry,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(pose);
        geometry = default!;
        diagnostic = null;

        if (mesh.BoneIndices.Count != mesh.Positions.Count || mesh.BoneIndices.Count == 0)
        {
            diagnostic = $"Skin mesh '{mesh.Name}' requires exactly one decoded u16 bone index for every vertex.";
            return false;
        }

        if (mesh.Normals.Count != 0 && mesh.Normals.Count != mesh.Positions.Count)
        {
            diagnostic = $"Skin mesh '{mesh.Name}' has {mesh.Normals.Count} normals for {mesh.Positions.Count} positions.";
            return false;
        }

        var positions = ImmutableArray.CreateBuilder<Vector3>(mesh.Positions.Count);
        var normals = ImmutableArray.CreateBuilder<Vector3>(mesh.Normals.Count);
        for (int index = 0; index < mesh.Positions.Count; index++)
        {
            ushort boneIndex = mesh.BoneIndices[index];
            if (boneIndex >= pose.WorldTransforms.Length)
            {
                diagnostic = $"Skin mesh '{mesh.Name}' vertex {index} references missing hierarchy pivot {boneIndex}.";
                return false;
            }

            Matrix4x4 transform = pose.WorldTransforms[boneIndex];
            Vector3 position = Vector3.Transform(mesh.Positions[index], transform);
            if (!IsFinite(position))
            {
                diagnostic = $"Skin mesh '{mesh.Name}' vertex {index} produced a non-finite deformed position.";
                return false;
            }
            positions.Add(position);

            if (mesh.Normals.Count != 0)
            {
                Vector3 normal = Vector3.TransformNormal(mesh.Normals[index], transform);
                if (!IsFinite(normal))
                {
                    diagnostic = $"Skin mesh '{mesh.Name}' vertex {index} produced a non-finite deformed normal.";
                    return false;
                }
                normals.Add(normal);
            }
        }

        geometry = new EnbW3dDeformedGeometry(positions.MoveToImmutable(), normals.MoveToImmutable());
        return true;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
