using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

/// <summary>
/// Test-side conveniences for wrappers the client no longer needs. Each one only
/// composes production code that the client itself calls, so the tests that use
/// them still exercise the real parsers, selectors and compilers.
/// </summary>
internal static class RetailTestShims
{
    extension(EnbRetailSoundCatalog)
    {
        public static EnbRetailSoundCatalog ParseText(string text) => EnbRetailSoundCatalog.Parse(EnbRetailIniDocument.Parse(text));
    }

    extension(EnbBloodCatalog)
    {
        /// <summary>The attachments the client selects, by bone; a later selection of a bone replaces an earlier tier.</summary>
        public static IReadOnlyDictionary<int, int> SelectBones(uint seed, int count, int level)
        {
            var result = new Dictionary<int, int>();
            foreach (var (bone, tier) in EnbBloodCatalog.SelectAttachments(seed, count, level)) result[bone] = tier;
            return result;
        }
    }

    extension(EnbRetailShortcuts)
    {
        public static EnbRetailShortcuts LoadOutput(string directory, uint character)
        {
            string path = Path.Combine(directory, "shortcut.ini");
            return EnbRetailShortcuts.Resolve(File.Exists(path) ? EnbRetailIniDocument.Load(path) : EnbRetailIniDocument.Parse(""), character);
        }
    }

    extension(EnbRetailMusicCatalog catalog)
    {
        public IReadOnlyList<EnbRetailMusicTheme> WanderPlaylist(int race, int level, int? previousId, Random random)
            => catalog.Playlist("Wander", race, level, previousId, random);
    }

    extension(EnbCurrentRetailStationInterior)
    {
        public static string EntryResourceName(uint definition, uint flags) => EnbCurrentRetailStationInterior.RoomResourceName(definition, 0, flags);
    }

    extension(EnbW3dModel model)
    {
        public EnbW3dMesh? FindMesh(string name)
            => model.Meshes.FirstOrDefault(mesh => string.Equals(mesh.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    extension(EnbW3dHierarchy hierarchy)
    {
        public EnbW3dPivot? FindPivot(string name)
            => hierarchy.Pivots.FirstOrDefault(pivot => string.Equals(pivot.Name, name, StringComparison.Ordinal));
    }

    extension(EnbW3dHierarchyModel model)
    {
        public IEnumerable<EnbW3dHierarchyModelNode> FindNodes(string renderObjectName)
            => model.Nodes.Where(node => string.Equals(node.RenderObjectName, renderObjectName, StringComparison.Ordinal));
    }

    /// <summary>The radar gate the client applies to a decoration's main render object.</summary>
    public static bool ShouldAttachDecorationRenderObject(IEnbRenderObject? observer, IEnbRenderObject obj)
        => obj.ObjectType != EnbCurrentRetailRadarRenderRangeRuntime.DecorationObjectType ||
           EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(observer, obj, scanRange: null);

    // Field checks on compiled mapper programs, describing what installed passes compile to.
    public static bool IsReflectionMapper(EnbW3dMapperRuntimeProgram program)
        => program.Kind == EnbW3dMapperRuntimeProgramKind.GridAtlasAxisProjection &&
           program.CoordinateSource == EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector &&
           program.TransformShape == EnbW3dMapperTransformShape.Count2 &&
           !program.ConfiguresBumpEnvironment;

    public static bool IsNormalAtlasMapper(EnbW3dMapperRuntimeProgram program)
        => program.Kind == EnbW3dMapperRuntimeProgramKind.GridAtlas &&
           program.CoordinateSource == EnbW3dMapperCoordinateSource.CameraSpaceNormal &&
           program.TransformShape == EnbW3dMapperTransformShape.Count2 &&
           !program.ConfiguresBumpEnvironment;

    public static bool IsReflectionSecondStage(EnbW3dRenderMaterialPass pass)
        => pass.SingleBindingSemantics is { } s && IsReflectionMapper(s.Mapper1Program);

    /// <summary>A whole terrain lit as the client lights its regions.</summary>
    public static EnbTextureImage ShadeTerrain(EnbRetailTerrain terrain, EnbRetailTerrainPrelight cache,
        EnbTextureImage color, EnbRetailTerrainLightState light, bool colorAlphaIsIllumination = false,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        int count = checked(terrain.Width * terrain.Width);
        var prepared = EnbRetailTerrainLighting.Prepare(cache, light, precision);
        var pixels = new EnbColor32[count];
        EnbRetailTerrainLighting.ShadeRegion(terrain, cache, color, light, colorAlphaIsIllumination, prepared, pixels, 0, 0, terrain.Width, terrain.Width, precision);
        return EnbTextureImage.CreateCurrentRetailRuntimeRgb8([(terrain.Width, terrain.Width, pixels)]);
    }

    public static bool TryCompileMaterial(EnbW3dRenderShaderRecord shader, EnbW3dRenderVertexMaterial material, int textureStageCount,
        out EnbW3dCompiledMaterialSemantics semantics, out string? diagnostic)
        => EnbW3dMaterialCompiler.TryCompileWithVertexLighting(shader, material, textureStageCount,
            EnbW3dVertexLightingSemantics.RetailDefault, out semantics, out diagnostic);
}

internal static class RetailCameraTestShims
{
    extension(EnbRetailCameraModeCatalog)
    {
        public static EnbRetailCameraModeCatalog ParseText(string text) => EnbRetailCameraModeCatalog.Parse(EnbRetailIniDocument.Parse(text));
    }
}

internal static class ShipEmoteTestShims
{
    extension(EnbShipEmoteRuntime)
    {
        /// <summary>The emote pose of a scene transform, through the same transform the client's SamplePose uses.</summary>
        public static EnbSceneTransformSnapshot Sample(EnbSceneTransformSnapshot physical, uint emote, float t, float radius)
        {
            System.Numerics.Matrix4x4 provider = physical.ProviderTransform ?? EnbRetailCameraQuaternionBasis.Create(physical.Orientation);
            if (physical.ProviderTransform is null) provider.Translation = physical.Position;
            System.Numerics.Matrix4x4 animated = EnbRetailShipEmoteTransform.Sample(provider, emote, t, radius);
            return physical with { Position = animated.Translation,
                Orientation = System.Numerics.Quaternion.CreateFromRotationMatrix(animated),
                OrientationColumn0 = new(animated.M11, animated.M12, animated.M13),
                ProviderTransform = animated };
        }
    }
}
