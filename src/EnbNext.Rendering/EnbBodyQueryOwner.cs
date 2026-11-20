using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Completed-view body queries owned by a render occurrence. UI markers
/// explicitly retain a completed set; the next invocation uses another free set.
/// No draw task or submission plan is retained by the normal query path.</summary>
public sealed class EnbBodyQueryOwner
{
    private readonly List<MeshQuery> meshes = [];
    private readonly List<EnbSubmittedMeshPick> diagnosticPicks = [];
    private int users;
    private readonly List<List<Vector3>> positionCopies=[];
    private readonly List<List<uint>> indexCopies=[];
    private int positionCopyCount,indexCopyCount;
    private IReadOnlyList<Vector3> RetainPositions(EnbW3dFrameStream<Vector3> source)
    {
        if(positionCopyCount==positionCopies.Count)positionCopies.Add([]);
        var target=positionCopies[positionCopyCount++];
        System.Runtime.InteropServices.CollectionsMarshal.SetCount(target,source.Count);
        source.ReadSpan.CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(target));
        return target;
    }
    private IReadOnlyList<uint> RetainIndices(EnbW3dMutableIndexList source)
    {
        if(indexCopyCount==indexCopies.Count)indexCopies.Add([]);
        var target=indexCopies[indexCopyCount++];target.Clear();target.AddRange(source);return target;
    }
    private bool projected;
    private EnbSubmittedPixelBounds projectedBounds;
    private int width, height;
    public int Count => meshes.Count;
    public bool IsRetained => users != 0;
    public void Retain() => users++;
    public void Release()
    {
        if(users == 0) throw new InvalidOperationException("Unbalanced body query release");
        if(--users == 0) Clear();
    }
    public void Clear()
    {
        if(IsRetained) throw new InvalidOperationException("Cannot overwrite retained body queries");
        meshes.Clear(); diagnosticPicks.Clear(); projected=false;positionCopyCount=0;indexCopyCount=0;
    }
    public void Add(EnbW3dIndexedSubmissionPlan submission, Matrix4x4 clip, int pixelWidth, int pixelHeight, bool diagnosticCapture=false)
    {
        if(IsRetained) throw new InvalidOperationException("Cannot mutate completed body queries");
        var packet=submission.Packet;
        var positions=submission.Positions;
        var skin=packet.DeformedGeometry;
        var source=skin?.PublicationPositions is {} recipe && ReferenceEquals(positions,skin.Positions) ? recipe :
            positions is EnbW3dFrameStream<Vector3> stream ? RetainPositions(stream) : positions;
        EnbSubmittedProjectionBinding? projection=null;
        if(skin is null && packet.Mesh.CurrentRetailUsesMeshRenderer && !packet.CurrentRetailParticleBuffer &&
            !packet.CurrentRetailTerrainSea && !packet.CurrentRetailTerrainDisplacement)
            projection=packet.PreparedState.QueryProjection ??= new();
        var indices=submission.Indices is EnbW3dMutableIndexList mutable?RetainIndices(mutable):submission.Indices;
        meshes.Add(new(source,indices,submission.Runs,clip,projection));
        if(diagnosticCapture) diagnosticPicks.Add(new(submission,clip));
        width=pixelWidth; height=pixelHeight; projected=false;
    }
    public EnbSubmittedPixelBounds Bounds
    {
        get
        {
            if(projected) return projectedBounds;
            var result=EnbSubmittedPixelBounds.Empty;
            foreach(var mesh in meshes) result=result.Include(mesh.Projection is {} cached
                ? cached.Project(mesh.Positions,mesh.Clip,width,height)
                : EnbSubmittedMeshProjection.Project(mesh.Positions,mesh.Clip,width,height));
            projectedBounds=result; projected=true; return result;
        }
    }
    public bool Contains(double x,double y)
    {
        foreach(var mesh in meshes)
            if(EnbSubmittedMeshPick.Contains(mesh.Positions,mesh.Indices,mesh.Runs,mesh.Clip,x,y))return true;
        return false;
    }
    // Explicit diagnostic export, never used by production hover or projection.
    public ImmutableArray<EnbSubmittedMeshPick> CaptureDiagnostics()
    {
        if(diagnosticPicks.Count != meshes.Count) throw new InvalidOperationException("Body-query diagnostic capture was not enabled before rendering");
        return diagnosticPicks.ToImmutableArray();
    }
    private readonly record struct MeshQuery(IReadOnlyList<Vector3> Positions,IReadOnlyList<uint> Indices,
        ImmutableArray<EnbW3dIndexedSubmissionRun> Runs,Matrix4x4 Clip,EnbSubmittedProjectionBinding? Projection);
}
