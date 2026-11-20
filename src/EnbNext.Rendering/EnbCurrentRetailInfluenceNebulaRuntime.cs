using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public enum EnbCurrentRetailInfluenceNebulaRegion : byte
{
    Outside = 0,
    Between = 1,
    Room = 2,
    FailClosed = 3,
}

public sealed record EnbCurrentRetailInfluenceNebulaBoundarySource(
    EnbW3dRenderResource Resource,
    EnbW3dRenderHlod Hlod,
    Matrix4x4 WorldTransform,
    string Source)
{
    public float ModelScale { get; init; } = 1;
    public float? PendingHierarchyScale { get; init; }
    public IReadOnlyList<string>? AuthoredMemberNames { get; init; }
}

public sealed record EnbCurrentRetailInfluenceNebulaEvaluation(
    EnbCurrentRetailInfluenceNebulaRegion Region,
    int? RoomReferenceIndex,
    string? RoomName,
    EnbRetailNebulaEnvironmentState? Environment,
    string? Diagnostic)
{
    public bool IsEnvironmentActive => Region is
        EnbCurrentRetailInfluenceNebulaRegion.Between or
        EnbCurrentRetailInfluenceNebulaRegion.Room;
}

/// <summary>One constructed helper's selected record (retail owner +78).</summary>
public sealed class EnbCurrentRetailInfluenceNebulaState(EnbCurrentRetailInfluenceNebulaBoundarySource source)
{
    internal int? SelectedReferenceIndex;
    internal EnbRetailInfluenceSpatialTree? SpatialTree { get; } = EnbCurrentRetailInfluenceNebulaRuntime.CreateSpatialTree(source);
    public EnbCurrentRetailInfluenceNebulaBoundarySource Source { get; } = source;

    public EnbCurrentRetailInfluenceNebulaEvaluation Evaluate(Vector3 camera, EnbRetailNebulaEnvironmentCatalog? catalog)
        => EnbCurrentRetailInfluenceNebulaRuntime.Evaluate([Source], camera, catalog, this);
}

/// <summary>
/// Current-retail type 0x0A influence/nebula boundary evaluator.
///
/// ConstructInfluenceOrNebulaClientObject_Type0A @ 0x007E87C0 requires a
/// one-level HLOD, creates one room record per HLOD reference, and turns record
/// zero into the inverted-polarity NEBULA_ROOT sentinel. FUN_007E9560 leaves the
/// ordinary sector environment untouched while that sentinel is selected,
/// installs Between state when no record is selected inside the root, and Room
/// state when a non-sentinel room is selected.
///
/// FUN_0093EC20/FUN_0092AD70 transform the camera into each boundary mesh's
/// local space and classify it with six directional triangle tests. This modern
/// implementation executes the traced directional triangle classifier, including
/// exact projected-edge flags and the authored acceleration tree.
/// </summary>
public static class EnbCurrentRetailInfluenceNebulaRuntime
{
    public const byte ObjectType = 0x0A;
    public const int SpecialEnvironmentBaseAsset = 0x137;

    public static bool TryCreateObjectWorldTransform(
        IEnbRenderObject sceneObject,
        out Matrix4x4 worldTransform,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(sceneObject);
        worldTransform = default;
        diagnostic = null;
        if (sceneObject.Spatial?.Transform is not IEnbRenderTransform spatial)
        {
            diagnostic = $"type 0x0A object {sceneObject.GameId} has no immutable spatial transform.";
            return false;
        }
        if (!float.IsFinite(sceneObject.Scale) ||
            !IsFinite(spatial.Position) ||
            !IsFinite(spatial.Orientation))
        {
            diagnostic = $"type 0x0A object {sceneObject.GameId} has non-finite scale/position/orientation.";
            return false;
        }

        if (!EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(spatial,
                out Matrix4x4 rigidWorld,
                out string? transformDiagnostic))
        {
            diagnostic = $"type 0x0A object {sceneObject.GameId} could not build its current-retail rigid world transform: {transformDiagnostic}";
            return false;
        }

        worldTransform = rigidWorld;
        if (!IsFinite(worldTransform))
        {
            diagnostic = $"type 0x0A object {sceneObject.GameId} produced a non-finite world transform.";
            return false;
        }
        return true;
    }

    public static EnbCurrentRetailInfluenceNebulaEvaluation Evaluate(
        IReadOnlyList<EnbCurrentRetailInfluenceNebulaBoundarySource> sources,
        Vector3 cameraWorldPosition,
        EnbRetailNebulaEnvironmentCatalog? environmentCatalog)
        => Evaluate(sources, cameraWorldPosition, environmentCatalog, null);

    internal static EnbCurrentRetailInfluenceNebulaEvaluation Evaluate(
        IReadOnlyList<EnbCurrentRetailInfluenceNebulaBoundarySource> sources,
        Vector3 cameraWorldPosition,
        EnbRetailNebulaEnvironmentCatalog? environmentCatalog,
        EnbCurrentRetailInfluenceNebulaState? state)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (!IsFinite(cameraWorldPosition))
            return Reject("camera position is non-finite");
        if (sources.Count != 1)
        {
            return Reject(sources.Count == 0
                ? "no materialized type 0x0A boundary HLOD is available"
                : $"{sources.Count} active type 0x0A boundary roots are ambiguous");
        }

        EnbCurrentRetailInfluenceNebulaBoundarySource source = sources[0];
        if (!TryBuildBoundaries(source, out Boundary sentinel, out Boundary[] rooms, out string? diagnostic))
            return Reject(diagnostic ?? "boundary materialization failed");

        //7E96D7..7E9704 tests the retained mesh before querying any boxes.
        //A selected room keeps ownership even after entering another room.
        if (state?.SelectedReferenceIndex is int selectedReference)
        {
            Boundary? retained = selectedReference == 0 ? sentinel : rooms.FirstOrDefault(room => room.ReferenceIndex == selectedReference);
            if (retained is not null)
            {
                if (!TryContains(retained, cameraWorldPosition, out bool retainedInside, out diagnostic))
                    return Reject(diagnostic ?? "retained boundary containment failed");
                if (retainedInside != (selectedReference == 0))
                    return Selected(retained);
            }
            state.SelectedReferenceIndex = null;
        }

        //7E9704..7E9784 consumes 8DA110's linked candidates in order,
        //selecting the first record whose mesh classification differs from
        //its polarity. An empty list selects record zero without testing it.
        Boundary[] records = [sentinel, .. rooms];
        var tree = state?.SpatialTree ?? BuildSpatialTree(records);
        int[] candidates = tree.Query(cameraWorldPosition);
        if (candidates.Length == 0) return Selected(sentinel);
        foreach (int index in candidates)
        {
            Boundary candidate = records[index];
            if (!TryContains(candidate, cameraWorldPosition, out bool inside, out diagnostic))
                return Reject(diagnostic ?? $"room {candidate.ReferenceIndex} containment failed");
            if (inside != (candidate.ReferenceIndex == 0)) return Selected(candidate);
        }
        if (environmentCatalog is null)
            return Reject("nebula.ini is unavailable from the selected retail installation");
        return new EnbCurrentRetailInfluenceNebulaEvaluation(
            EnbCurrentRetailInfluenceNebulaRegion.Between,
            null,
            null,
            environmentCatalog.Between,
            null);

        EnbCurrentRetailInfluenceNebulaEvaluation Selected(Boundary boundary)
        {
            if (state is not null) state.SelectedReferenceIndex = boundary.ReferenceIndex;
            if (boundary.ReferenceIndex == 0)
                return new(EnbCurrentRetailInfluenceNebulaRegion.Outside, null, null, null, null);
            return environmentCatalog is null ? Reject("nebula.ini is unavailable from the selected retail installation")
                : new(EnbCurrentRetailInfluenceNebulaRegion.Room, boundary.ReferenceIndex, boundary.Name, environmentCatalog.Room, null);
        }
    }

    private static bool TryBuildBoundaries(
        EnbCurrentRetailInfluenceNebulaBoundarySource source,
        out Boundary sentinel,
        out Boundary[] rooms,
        out string? diagnostic)
    {
        sentinel = default!;
        rooms = [];
        diagnostic = null;
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Resource);
        ArgumentNullException.ThrowIfNull(source.Hlod);

        EnbW3dRenderHlod hlod = source.Hlod;
        if (hlod.Levels.Count != 1)
        {
            diagnostic = $"influence HLOD '{hlod.Name}' has {hlod.Levels.Count} levels; current retail requires exactly one.";
            return false;
        }

        EnbW3dRenderHierarchy? hierarchy = source.Resource.Hierarchies.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, hlod.HierarchyName, StringComparison.OrdinalIgnoreCase));
        if (hierarchy is null)
        {
            diagnostic = $"influence HLOD '{hlod.Name}' requires missing hierarchy '{hlod.HierarchyName}'.";
            return false;
        }
        //7E8C6C obtains each configured pivot's initial transform. The
        //record AABBs are captured here;7E9560 later composes the spatial
        //owner transform into the cloned room meshes without rewriting them.
        if (!EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchy(
                hierarchy, Matrix4x4.Identity, source.PendingHierarchyScale??source.ModelScale,
                out var initialTransforms, out diagnostic)) return false;
        IReadOnlyList<EnbW3dRenderHlodMember> members = hlod.Levels[0].Members;
        if (members.Count == 0)
        {
            diagnostic = $"influence HLOD '{hlod.Name}' has no level-0 members.";
            return false;
        }

        var byReference = new Dictionary<uint, Boundary>();
        for (int memberIndex = 0; memberIndex < members.Count; memberIndex++)
        {
            var member = members[memberIndex];
            string authoredName = source.AuthoredMemberNames?[memberIndex] ?? member.Name;
            if (member.ReferenceIndex >= (uint)initialTransforms.Length)
            {
                diagnostic = $"influence HLOD '{hlod.Name}' member '{member.Name}' references hierarchy state {member.ReferenceIndex}, outside '{hierarchy.Name}'.";
                return false;
            }
            //7E8A5A..7E8AB3 compares the cloned member's name with the
            //hierarchy/pivot name and hierarchy/NEBULA_ROOT. Other members
            //belong to the visible room group, not its collision boundary.
            string pivotBoundaryName = $"{hierarchy.RuntimeName}.{hierarchy.Pivots[(int)member.ReferenceIndex].Name}";
            string rootBoundaryName = $"{hierarchy.RuntimeName}.NEBULA_ROOT";
            if (!string.Equals(authoredName, pivotBoundaryName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(authoredName, rootBoundaryName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryResolveUniqueMesh(source.Resource, member.Name, out EnbW3dRenderMesh mesh, out diagnostic))
                return false;
            if (byReference.ContainsKey(member.ReferenceIndex))
            {
                diagnostic = $"influence HLOD '{hlod.Name}' has multiple boundary meshes for reference {member.ReferenceIndex}; retail expects one BoundaryMesh pointer per room record.";
                return false;
            }

            byReference.Add(member.ReferenceIndex, new Boundary(
                (int)member.ReferenceIndex,
                authoredName,
                mesh,
                EnbRetailParticleHierarchyTransform.Compose(initialTransforms[(int)member.ReferenceIndex], source.WorldTransform),
                initialTransforms[(int)member.ReferenceIndex]));
        }

        if (!byReference.TryGetValue(0u, out Boundary? rootBoundary))
        {
            diagnostic = $"influence HLOD '{hlod.Name}' has no reference-0 sentinel boundary.";
            return false;
        }
        if (byReference.Count != hierarchy.Pivots.Count)
        {
            diagnostic = $"influence HLOD '{hlod.Name}' has {byReference.Count} named boundaries for {hierarchy.Pivots.Count} room records.";
            return false;
        }

        sentinel = rootBoundary;
        rooms = byReference
            .Where(static pair => pair.Key != 0u)
            .OrderBy(static pair => pair.Key)
            .Select(static pair => pair.Value)
            .ToArray();
        return true;
    }

    private static bool TryResolveUniqueMesh(
        EnbW3dRenderResource resource,
        string renderObjectName,
        out EnbW3dRenderMesh mesh,
        out string? diagnostic)
    {
        mesh = default!;
        diagnostic = null;
        EnbW3dRenderMesh? resolved = null;
        foreach (EnbW3dRenderMesh candidate in resource.Meshes)
        {
            if (!MatchesMesh(candidate, renderObjectName))
                continue;
            if (resolved is not null)
            {
                diagnostic = $"influence boundary '{renderObjectName}' resolves to multiple meshes.";
                return false;
            }
            resolved = candidate;
        }
        if (resolved is null)
        {
            diagnostic = $"influence boundary '{renderObjectName}' is not represented by a decoded mesh RenderObject.";
            return false;
        }

        mesh = resolved;
        return true;
    }

    private static bool TryContains(
        Boundary boundary,
        Vector3 cameraWorldPosition,
        out bool inside,
        out string? diagnostic)
    {
        inside = false;
        diagnostic = null;
        EnbW3dRenderMesh mesh = boundary.Mesh;
        if (mesh.Indices.Count < 3 || mesh.Indices.Count % 3 != 0)
        {
            diagnostic = $"boundary '{boundary.Name}' has invalid triangle topology ({mesh.Indices.Count} indices).";
            return false;
        }

        try
        {
            var classifier = OwnerCache.Get(ref mesh.DerivedInfluenceClassifier, mesh, static source =>
            {
                //93EC20 reads the retained MeshModel at +8C, including its
                //configured vertices and generated/authored acceleration tree.
                if (source.CurrentRetailLoadedGeometry is {} loaded)
                    return new EnbRetailInfluenceContainment(loaded);
                uint flags = EnbCurrentRetailMeshGeometryLoader.TranslateInitialFlags(source.GeometryVersion, source.Attributes, 1);
                if (source.BoneIndices.Count != 0) flags |= 0x400;
                var tree = (flags & 0x400) == 0 && source.AuthoredAabTreePayload is {} payload
                    ? EnbCurrentRetailMeshAabTree.LoadOwnedAuthored(payload.ToArray()) : null;
                var geometry = new EnbCurrentRetailMeshGeometryState(source.Positions.ToArray(), new Vector3[source.Positions.Count],
                    source.Indices.Select(index => checked((ushort)index)).ToArray(), [], source.BoundingBoxMin, source.BoundingBoxMax,
                    source.BoundingSphereCenter, source.BoundingRadius, flags, tree);
                return new EnbRetailInfluenceContainment(geometry);
            });
            inside = classifier.Contains(boundary.WorldTransform, cameraWorldPosition);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or OverflowException)
        {
            diagnostic = $"boundary '{boundary.Name}' has invalid collision geometry: {error.Message}";
            return false;
        }
    }

    internal static EnbRetailInfluenceSpatialTree? CreateSpatialTree(EnbCurrentRetailInfluenceNebulaBoundarySource source)
        => TryBuildBoundaries(source, out var sentinel, out var rooms, out _)
            ? BuildSpatialTree([sentinel, .. rooms]) : null;

    private static EnbRetailInfluenceSpatialTree BuildSpatialTree(Boundary[] records)
        => new(records.Select(Bounds).ToArray(), EnbRetailCrtRandomSource.Current);

    private static (Vector3 Center, Vector3 Extent) Bounds(Boundary boundary)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        Vector3 center = default, extent = default;
        for (int axis = 0; axis < 3; axis++)
        {
            var minimum = (EnbRetailLineArithmetic.Register)(boundary.Mesh.CurrentRetailLoadedGeometry?.Minimum ?? boundary.Mesh.BoundingBoxMin)[axis];
            var maximum = (EnbRetailLineArithmetic.Register)(boundary.Mesh.CurrentRetailLoadedGeometry?.Maximum ?? boundary.Mesh.BoundingBoxMax)[axis];
            center[axis] = (float)((minimum + maximum) * .5f);
            extent[axis] = (float)((maximum - minimum) * .5f);
        }
        var world = EnbRetailHlodBounds.TransformSelectedBox(Matrix4x4.Identity, boundary.QueryTransform, center, extent);
        return (world.Center, world.Extent);
    }
    private static bool MatchesMesh(EnbW3dRenderMesh mesh, string renderObjectName)
    {
        if (string.Equals(mesh.Name, renderObjectName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(mesh.ContainerName))
            return false;
        return string.Equals(
            $"{mesh.ContainerName}.{mesh.Name}",
            renderObjectName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static EnbCurrentRetailInfluenceNebulaEvaluation Reject(string diagnostic)
        => new(
            EnbCurrentRetailInfluenceNebulaRegion.FailClosed,
            null,
            null,
            null,
            diagnostic);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private sealed record Boundary(
        int ReferenceIndex,
        string Name,
        EnbW3dRenderMesh Mesh,
        Matrix4x4 WorldTransform,
        Matrix4x4 QueryTransform);
}
