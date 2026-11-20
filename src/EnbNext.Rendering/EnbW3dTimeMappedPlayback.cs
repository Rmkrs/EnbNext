using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Retained source references and per-track search cursors for 02C0.
/// Registry retirement drops its references; already acquired draw views retain
/// theirs. No GPU resource or filesystem access occurs during sampling.</summary>
public sealed class EnbW3dTimeMappedPlayback
{
    private readonly EnbW3dRenderHierarchyAnimation?[] _sources;
    private readonly EnbCurrentRetailAnimationTimeTrack[] _tracks;
    private readonly int[] _pivots;

    public EnbW3dTimeMappedPlayback(EnbW3dTimeMappedAnimation definition, int pivotCount,
        IReadOnlyList<EnbW3dRenderHierarchyAnimation?> sources)
    {
        _sources = sources.ToArray();
        _tracks = definition.Sources.Select(source => new EnbCurrentRetailAnimationTimeTrack(
            i => new(source.Keys[i].Time, source.Keys[i].SourceFrame), () => source.Keys.Count, 0)).ToArray();
        byte[] map = new byte[checked(pivotCount * 4)];
        foreach (var write in definition.PivotWrites)
        {
            if (write.Length > map.Length) throw new InvalidDataException("02C0 pivot write exceeds acquired hierarchy storage.");
            write.CopyTo(map, 0);
        }
        _pivots = new int[pivotCount];
        for (int i = 0; i < pivotCount; i++) _pivots[i] = BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(i * 4));
    }

    public bool TrySample(int pivot, float frame, out EnbW3dPivotAnimationSample sample, out string? diagnostic)
    {
        sample = default; diagnostic = null;
        if (!TrySelect(pivot,frame,out var source,out var selection,out diagnostic) ||
            !Rotation(source,pivot,selection,out var rotation,out diagnostic) ||
            !Translation(source,pivot,selection,out var translation,out diagnostic)) return false;
        if (!float.IsFinite(translation.X) || !float.IsFinite(translation.Y) || !float.IsFinite(translation.Z) ||
            !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
        { diagnostic = "02C0 produced nonfinite transform data."; return false; }
        sample = new(translation, rotation, true);
        return true;
    }

    private bool TrySelect(int pivot,float frame,out EnbW3dRenderHierarchyAnimation source,
        out EnbCurrentRetailAnimationTimeSelection selection,out string? diagnostic)
    {
        source=default!;selection=default;diagnostic=null;
        if ((uint)pivot >= _pivots.Length) { diagnostic = "02C0 pivot addresses outside its acquired hierarchy."; return false; }
        int track = _pivots[pivot];
        if ((uint)track >= _sources.Length || (uint)track >= _tracks.Length || _sources[track] is null)
        { diagnostic = "02C0 selected source is missing or outside its retained storage."; return false; }
        source=_sources[track]!;
        try { selection = _tracks[track].Select(frame); }
        catch (ArgumentOutOfRangeException) { diagnostic = "02C0 time lookup addresses outside its authored keys."; return false; }
        catch (IndexOutOfRangeException) { diagnostic = "02C0 time lookup addresses outside its authored keys."; return false; }
        return true;
    }

    private static bool Translation(EnbW3dRenderHierarchyAnimation source,int pivot,EnbCurrentRetailAnimationTimeSelection selection,
        out Vector3 value,out string? diagnostic)
    {
        value=default;
        if(!SourceTranslation(source,pivot,selection.First,out var a,out diagnostic) ||
            !SourceTranslation(source,pivot,selection.Second,out var b,out diagnostic))return false;
        value=new(EnbCurrentRetailClassicAnimationArithmetic.InterpolateScalar(a.X,b.X,selection.Fraction),
            EnbCurrentRetailClassicAnimationArithmetic.InterpolateScalar(a.Y,b.Y,selection.Fraction),
            EnbCurrentRetailClassicAnimationArithmetic.InterpolateScalar(a.Z,b.Z,selection.Fraction));return true;
    }
    private static bool Rotation(EnbW3dRenderHierarchyAnimation source,int pivot,EnbCurrentRetailAnimationTimeSelection selection,
        out Quaternion value,out string? diagnostic)
    {
        value=default;
        if(!SourceRotation(source,pivot,selection.First,out var a,out diagnostic) ||
            !SourceRotation(source,pivot,selection.Second,out var b,out diagnostic))return false;
        value=EnbW3dAnimationSampler.InterpolateQuaternionRetail(a,b,selection.Fraction);return true;
    }
    private static bool SourceTranslation(EnbW3dRenderHierarchyAnimation source,int pivot,float frame,out Vector3 value,out string? diagnostic)
    {
        value=default;
        if(source.TimeMappedPlayback is {} mapped)
            return mapped.TrySelect(pivot,frame,out var child,out var selection,out diagnostic) && Translation(child,pivot,selection,out value,out diagnostic);
        if(!EnbW3dAnimationSampler.TrySamplePivot(source,pivot,frame,out var sample,out diagnostic,sampleVisibility:false))return false;
        value=sample.Translation;return true;
    }
    private static bool SourceRotation(EnbW3dRenderHierarchyAnimation source,int pivot,float frame,out Quaternion value,out string? diagnostic)
    {
        value=default;
        if(source.TimeMappedPlayback is {} mapped)
            return mapped.TrySelect(pivot,frame,out var child,out var selection,out diagnostic) && Rotation(child,pivot,selection,out value,out diagnostic);
        if(!EnbW3dAnimationSampler.TrySamplePivot(source,pivot,frame,out var sample,out diagnostic,sampleVisibility:false))return false;
        value=sample.Rotation;return true;
    }
}
