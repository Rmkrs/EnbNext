using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// Immutable references to an accepted GPU submission, for on-demand body picking.
/// No per-frame projected-triangle expansion or screen-bounds acceptance.
/// </summary>
public readonly record struct EnbSubmittedMeshPick
{
    private readonly EnbW3dIndexedSubmissionPlan _submission;
    private readonly IReadOnlyList<Vector3>? _positions;
    private readonly IReadOnlyList<uint>? _indices;
    public EnbW3dIndexedSubmissionPlan Submission
    {
        get=>_submission;
        init
        {
            value.Packet?.RetainAfterDrain();
            _submission=value;
            // Diagnostic picks are independent of the next owner mutation.
            _indices=value.Packet is null?null:value.Indices is EnbW3dMutableIndexList mutable?mutable.ToArray():value.Indices;
            // UI hit testing runs after the render invocation has drained.
            // Share authored geometry; detach a generated stream once at this
            // publication boundary, not once for every material pass or query.
            var positions=value.Packet is null?null:value.Positions;
            var skin=value.Packet?.DeformedGeometry;
            _positions=skin?.PublicationPositions is {} prepared && ReferenceEquals(positions,skin.Positions)
                ?(skin!.HasMutablePickPose?prepared.ToArray():prepared):positions is EnbW3dFrameStream<Vector3> stream
                    ?stream.RetainForPublication():positions;
        }
    }
    public Matrix4x4 ClipFromModel {get;init;}
    public EnbSubmittedMeshPick(EnbW3dIndexedSubmissionPlan Submission,Matrix4x4 ClipFromModel)
    {this=default;this.Submission=Submission;this.ClipFromModel=ClipFromModel;}
    public EnbSubmittedPixelBounds ProjectBounds(int width,int height)
    {
        var positions=_positions??Submission.Positions;
        var packet=Submission.Packet;
        bool retained=packet.DeformedGeometry is null && packet.Mesh.CurrentRetailUsesMeshRenderer &&
            !packet.CurrentRetailParticleBuffer && !packet.CurrentRetailTerrainSea && !packet.CurrentRetailTerrainDisplacement;
        if(!retained)return EnbSubmittedMeshProjection.Project(positions,ClipFromModel,width,height);
        var definition=packet.PreparedState;
        var projection=definition.QueryProjection;
        if(projection is null)definition.QueryProjection=projection=new();
        return projection.Project(positions,ClipFromModel,width,height);
    }

    public bool Contains(double normalizedX, double normalizedY)
        => Contains(_positions ?? Submission.Positions, _indices ?? Submission.Indices, Submission.Runs, ClipFromModel, normalizedX, normalizedY);

    internal static bool Contains(IReadOnlyList<Vector3> positions, IReadOnlyList<uint> indices, System.Collections.Immutable.ImmutableArray<EnbW3dIndexedSubmissionRun> runs, Matrix4x4 ClipFromModel, double normalizedX, double normalizedY)
    {
        if (!double.IsFinite(normalizedX) || !double.IsFinite(normalizedY) ||
            normalizedX < 0 || normalizedX > 1 || normalizedY < 0 || normalizedY > 1 ||
            !Matrix4x4.Invert(ClipFromModel, out Matrix4x4 inverse))
            return false;
        float x = (float)(normalizedX * 2 - 1), y = (float)(1 - normalizedY * 2);
        if (!Unproject(x, y, 0, inverse, out Vector3 origin) ||
            !Unproject(x, y, 0.5f, inverse, out Vector3 middle))
            return false;
        Vector3 direction = Vector3.Normalize(middle - origin);
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z))
            return false;
        foreach (var run in runs)
        {
            int end = Math.Min(indices.Count, run.StartIndex + run.IndexCount);
            for (int i = run.StartIndex; i + 2 < end; i += 3)
            {
                uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
                if (a >= positions.Count || b >= positions.Count || c >= positions.Count)
                    continue;
                Vector3 v0 = positions[(int)a], e1 = positions[(int)b] - v0, e2 = positions[(int)c] - v0;
                Vector3 p = Vector3.Cross(direction, e2);
                float determinant = Vector3.Dot(e1, p);
                if (MathF.Abs(determinant) < 1e-8f) continue;
                float reciprocal = 1f / determinant;
                Vector3 relative = origin - v0;
                float u = Vector3.Dot(relative, p) * reciprocal;
                if (u < 0 || u > 1) continue;
                Vector3 q = Vector3.Cross(relative, e1);
                float v = Vector3.Dot(direction, q) * reciprocal;
                if (v < 0 || u + v > 1) continue;
                float t = Vector3.Dot(e2, q) * reciprocal;
                if (!float.IsFinite(t) || t < 0) continue;
                Vector4 clip = Vector4.Transform(new Vector4(origin + t * direction, 1), ClipFromModel);
                if (clip.W > 0 && clip.Z >= 0 && clip.Z <= clip.W)
                    return true;
            }
        }
        return false;
    }

    private static bool Unproject(float x, float y, float z, Matrix4x4 inverse, out Vector3 point)
    {
        Vector4 h = Vector4.Transform(new Vector4(x, y, z, 1), inverse);
        point = new Vector3(h.X, h.Y, h.Z) / h.W;
        return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    }
}
