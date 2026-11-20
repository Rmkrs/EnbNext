using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public enum EnbCurrentRetailShipNameDecalOutcome : byte
{
    NotNameDecalRenderObject = 0,
    MissingNameDecalState = 1,
    InvalidRgb = 2,
    MissingGeneratedTexture = 3,
    MissingStage0Texture = 4,
    Applied = 5,
    ExpandedBindings = 6,
    AdditionalMaterialPass = 7,
}

public readonly record struct EnbCurrentRetailShipNameDecalReceipt(
    EnbCurrentRetailShipNameDecalOutcome Outcome,
    string? Name,
    Vector3 Rgb,
    string? TextureName,
    string? Diagnostic)
{
    public bool IsTarget => Outcome != EnbCurrentRetailShipNameDecalOutcome.NotNameDecalRenderObject;
    public bool Applied => Outcome == EnbCurrentRetailShipNameDecalOutcome.Applied;
}

public sealed record EnbCurrentRetailShipNameDecalTexture(
    string LogicalName,
    EnbTextureImage Image,
    int TextWidth,
    int GlyphHeight)
{
    //006D8B90/006D8DC0 allocate a private format7, single-level TextureClass;
    //00810075/00810078 set its MIN/MAG words to2. It is not name-registered.
    public EnbCurrentRetailTextureObjectState TextureState { get; } = new(
        new EnbW3dTextureCreationRequest(EnbW3dTextureSamplingSemantics.RetailDefault with
        {
            RetailCreationSelector=1,
            CurrentRetailMinMagOverride=EnbRenderTextureMinMagFilter.Linear,
        },7));
}

/// <summary>
/// Current-retail 0x00B2 NAMEDECAL mutation. Retail refines the installed
/// ship_font.tga 16x16 glyph grid by its A4R4G4B4 alpha, renders the packet's
/// raw one-byte name with one transparent pixel after every glyph, and packs
/// four horizontal 64x16 strips into one 64x64 A4R4G4B4 texture. Every
/// post-container RenderObj name beginning with NAMEDECAL receives that stage-0
/// texture and the packet RGB as both ambient and diffuse material state.
/// </summary>
public static class EnbCurrentRetailShipNameDecalRuntime
{
    public const string RetailFontTextureName = "ship_font.tga";
    public const int RetailGlyphColumns = 16;
    public const int RetailGlyphRows = 16;
    public const int RetailGlyphHeight = 16;
    public const int RetailStripWidth = 64;
    public const int RetailStripCount = 4;
    public const int RetailTextureSize = 64;
    public const int RetailGlyphSpacing = 1;

    public static bool IsTarget(string renderObjectName)
    {
        if (string.IsNullOrWhiteSpace(renderObjectName))
            return false;

        ReadOnlySpan<char> name = renderObjectName.AsSpan();
        int separator = name.IndexOf('.');
        if (separator >= 0 && separator + 1 < name.Length)
            name = name[(separator + 1)..];

        return name.Length > 8 &&
               name.StartsWith("NAMEDECAL", StringComparison.OrdinalIgnoreCase);
    }

    public static string CreateLogicalTextureName(ulong renderIdentity, ulong visualGeneration)
        => $"__enbnext_namedecal_{renderIdentity:X16}_{visualGeneration:X16}.argb4444";

    public static bool TryBuildTexture(
        EnbSceneNameDecalSnapshot nameDecal,
        EnbTextureImage fontAtlas,
        string logicalName,
        out EnbCurrentRetailShipNameDecalTexture texture,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(nameDecal);
        ArgumentNullException.ThrowIfNull(fontAtlas);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        texture = default!;
        diagnostic = null;

        if (fontAtlas.Width % RetailGlyphColumns != 0 ||
            fontAtlas.Height % RetailGlyphRows != 0)
        {
            diagnostic =
                $"Retail font atlas '{RetailFontTextureName}' is {fontAtlas.Width}x{fontAtlas.Height}; the current client requires a 16x16 glyph grid.";
            return false;
        }

        int cellWidth = fontAtlas.Width / RetailGlyphColumns;
        int cellHeight = fontAtlas.Height / RetailGlyphRows;
        if (cellWidth <= 0 || cellHeight <= 0)
        {
            diagnostic = $"Retail font atlas '{RetailFontTextureName}' has empty glyph cells.";
            return false;
        }

        var glyphs = new GlyphBounds[256];
        int commonTop = int.MaxValue;
        int commonBottom = -1;
        for (int value = 0; value < glyphs.Length; value++)
        {
            int cellX = (value & 0x0F) * cellWidth;
            int cellY = (value >> 4) * cellHeight;
            int left = int.MaxValue;
            int right = -1;
            int top = int.MaxValue;
            int bottom = -1;

            // FUN_006BC120 gives the horizontal alpha-bound scan one inclusive
            // cell-width endpoint. Clamp it at the decoded atlas edge.
            int scanRight = Math.Min(fontAtlas.Width, cellX + cellWidth + 1);
            int scanBottom = Math.Min(fontAtlas.Height, cellY + cellHeight);
            for (int y = cellY; y < scanBottom; y++)
            {
                for (int x = cellX; x < scanRight; x++)
                {
                    if ((fontAtlas.GetPixel(x, y).A >> 4) == 0)
                        continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y - cellY);
                    bottom = Math.Max(bottom, y - cellY);
                }
            }

            glyphs[value] = right < left
                ? GlyphBounds.Empty
                : new GlyphBounds(left, right);
            if (bottom >= top)
            {
                commonTop = Math.Min(commonTop, top);
                commonBottom = Math.Max(commonBottom, bottom);
            }
        }

        int glyphHeight = commonBottom >= commonTop
            ? commonBottom - commonTop + 1
            : 0;
        if (glyphHeight != RetailGlyphHeight)
        {
            diagnostic =
                $"Retail font atlas '{RetailFontTextureName}' refined to height {glyphHeight}; the 0x00B2 handler requires exactly {RetailGlyphHeight}.";
            return false;
        }

        var output = new EnbColor32[RetailTextureSize * RetailTextureSize];
        int renderedX = 0;
        foreach (byte value in nameDecal.RawName)
        {
            if (value == 0)
                break;

            GlyphBounds glyph = glyphs[value];
            int cellY = (value >> 4) * cellHeight;
            for (int glyphX = 0; glyphX < glyph.Width; glyphX++)
            {
                int sourceTextX = renderedX + glyphX;
                int strip = sourceTextX / RetailStripWidth;
                if (strip >= RetailStripCount)
                    continue;

                int destinationX = sourceTextX % RetailStripWidth;
                int destinationY = strip * RetailGlyphHeight;
                for (int glyphY = 0; glyphY < RetailGlyphHeight; glyphY++)
                {
                    output[((destinationY + glyphY) * RetailTextureSize) + destinationX] =
                        fontAtlas.GetPixel(glyph.Left + glyphX, cellY + commonTop + glyphY);
                }
            }

            // Retail includes positive spacing after the final glyph too.
            renderedX += glyph.Width + RetailGlyphSpacing;
        }

        EnbTextureImage image = EnbTextureImage.CreateCurrentRetailRuntimeArgb4444(
            RetailTextureSize,
            RetailTextureSize,
            output);
        texture = new EnbCurrentRetailShipNameDecalTexture(
            logicalName,
            image,
            renderedX,
            glyphHeight);
        return true;
    }

    public static EnbW3dDrawTask Apply(
        EnbW3dDrawTask packet,
        EnbSceneNameDecalSnapshot? nameDecal,
        EnbCurrentRetailShipNameDecalTexture? generatedTexture,
        out EnbCurrentRetailShipNameDecalReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!IsTarget(packet.RenderObjectName))
        {
            receipt = new EnbCurrentRetailShipNameDecalReceipt(
                EnbCurrentRetailShipNameDecalOutcome.NotNameDecalRenderObject,
                null,
                Vector3.Zero,
                null,
                null);
            return packet;
        }

        if (nameDecal is null)
            return Fail(packet, EnbCurrentRetailShipNameDecalOutcome.MissingNameDecalState, null, Vector3.Zero, "The target object has no retained 0x00B2 NAMEDECAL state.", out receipt);
        if (generatedTexture is null)
            return Fail(packet, EnbCurrentRetailShipNameDecalOutcome.MissingGeneratedTexture, nameDecal.Name, nameDecal.Rgb, "The exact current-retail ship-font texture was not generated.", out receipt);

        EnbW3dRenderMaterialPass source = packet.Material.Source;
        // 00810130 only writes pass 0. Raw00810291..00810302 returns
        // before any material write when pass 0 has an expanded material,
        // stage-0 texture, or shader array. Ordinary DECALdd differs here.
        if (source.Ordinal != 0)
            return Fail(packet, EnbCurrentRetailShipNameDecalOutcome.AdditionalMaterialPass,
                nameDecal.Name, nameDecal.Rgb, "NAMEDECAL only mutates material pass 0.", out receipt);
        if (source.VertexMaterialCardinality == EnbW3dRenderBindingCardinality.PerVertex ||
            source.ShaderCardinality == EnbW3dRenderBindingCardinality.PerTriangle ||
            source.TextureStages.Any(stage => stage.Ordinal == 0 && stage.BindingCardinality == EnbW3dRenderBindingCardinality.PerTriangle))
            return Fail(packet, EnbCurrentRetailShipNameDecalOutcome.ExpandedBindings,
                nameDecal.Name, nameDecal.Rgb, "Current retail rejects expanded NAMEDECAL pass-0 bindings.", out receipt);
        if (source.TextureStages.Count == 0 ||
            source.TextureStages[0].Textures.Count == 0 ||
            source.TriangleBindings.Any(static binding =>
                binding.Textures.Count == 0 || binding.Textures[0] is null || binding.Textures[0]!.IsUnbound))
        {
            return Fail(
                packet,
                EnbCurrentRetailShipNameDecalOutcome.MissingStage0Texture,
                nameDecal.Name,
                nameDecal.Rgb,
                $"RenderObj '{packet.RenderObjectName}' has no replaceable stage-0 texture binding.",
                out receipt);
        }

        // 0093B240 initializes C01140=9441B;0081034F..00810370 writes
        // C01140|40000 directly, without running authored shader optimization.
        if (!EnbW3dMaterialCompiler.TryDecodePackedRetailState(0xD441B, out var nameShaderState, out var shaderError))
            throw new InvalidOperationException(shaderError);
        var nameShader = new EnbW3dRenderShaderBinding(uint.MaxValue, default, nameShaderState)
            { SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState };
        var materials=new Dictionary<EnbW3dRenderVertexMaterial,EnbW3dRenderVertexMaterial>(ReferenceEqualityComparer.Instance);
        EnbW3dRenderVertexMaterial Recolor(EnbW3dRenderVertexMaterial material)
        {
            if(!materials.TryGetValue(material,out var changed))
            {
                changed=ApplyRgb(material,nameDecal.Rgb);
                EnbCurrentRetailMeshModelOwner.PreserveMaterialRuntimeWord64(material,changed);
                materials.Add(material,changed);
            }
            return changed;
        }
        var textures=new Dictionary<EnbW3dRenderTextureBinding,EnbW3dRenderTextureBinding>(ReferenceEqualityComparer.Instance);
        EnbW3dRenderTextureBinding Replace(EnbW3dRenderTextureBinding texture)
        {
            if(!textures.TryGetValue(texture,out var changed))textures.Add(texture,changed=ReplaceTexture(texture,generatedTexture));
            return changed;
        }
        EnbW3dRenderTextureStage[] stages = source.TextureStages
            .Select((stage, ordinal) => ordinal == 0
                ? (stage).Copy().SetCurrentRetailUniformTexture(Replace(stage.CurrentRetailUniformTexture??stage.Textures[0])).SetTextures(stage.Textures
                        .Select(Replace)
                        .ToArray())
                : stage)
            .ToArray();
        EnbW3dRenderVertexMaterialBinding[] vertexMaterials = source.VertexMaterials
            .Select(binding => binding with
            {
                Material = Recolor(binding.Material),
            })
            .ToArray();
        EnbW3dRenderTriangleBinding[] triangles = source.TriangleBindings
            .Select(binding =>
            {
                EnbW3dRenderTextureBinding?[] triangleTextures = binding.Textures.ToArray();
                triangleTextures[0] = Replace(triangleTextures[0]!);
                return binding with
                {
                    VertexMaterial = binding.VertexMaterial is EnbW3dRenderVertexMaterialBinding vertexMaterial
                        ? vertexMaterial with { Material = Recolor(vertexMaterial.Material) }
                        : binding.VertexMaterial,
                    Textures = triangleTextures,
                    Shader = nameShader,
                    Semantics = binding.Semantics is EnbW3dCompiledMaterialSemantics semantics
                        ? ApplyNameShader(ApplyRgb(semantics, nameDecal.Rgb), nameShaderState)
                        : binding.Semantics,
                };
            })
            .ToArray();
        EnbW3dCompiledMaterialSemantics? single = source.SingleBindingSemantics is EnbW3dCompiledMaterialSemantics singleSemantics
            ? ApplyNameShader(ApplyRgb(singleSemantics, nameDecal.Rgb), nameShaderState)
            : source.SingleBindingSemantics;
        EnbW3dRenderMaterialPass effectivePass = (source).Copy().SetVertexMaterials(vertexMaterials).SetCurrentRetailUniformMaterial(source.CurrentRetailUniformMaterial is {} uniform?uniform with {Material=Recolor(uniform.Material)}:null).SetCurrentRetailUniformShader(nameShaderState.PackedRetailState).SetShaders([nameShader]).SetShaderCardinality(EnbW3dRenderBindingCardinality.Uniform).SetTextureStages(stages).SetTriangleBindings(EnbW3dTriangleBindingPool.Freeze(triangles)).SetSingleBindingSemantics(single);
        EnbW3dDrawTask effectivePacket = (packet).ForkTask().SetMaterial(new EnbW3dDrawMaterialBinding(effectivePass, single)).SetCurrentRetailLoadedMaterials(effectivePass.CurrentRetailUniformMaterial is {} retained ? [retained.Material] : []).SetCurrentRetailLoadedTextures([generatedTexture.TextureState]);

        receipt = new EnbCurrentRetailShipNameDecalReceipt(
            EnbCurrentRetailShipNameDecalOutcome.Applied,
            nameDecal.Name,
            nameDecal.Rgb,
            generatedTexture.LogicalName,
            null);
        return effectivePacket;
    }

    private static EnbW3dRenderTextureBinding ReplaceTexture(
        EnbW3dRenderTextureBinding source,
        EnbCurrentRetailShipNameDecalTexture texture)
        => source with
        {
            LogicalName = texture.LogicalName,
            CurrentRetailTextureState = texture.TextureState,
            CurrentRetailResolvedTexture = new(texture.LogicalName,EnbTextureResolutionStatus.Decoded,Image:texture.Image),
            Location = null,
            Sampling = new EnbW3dTextureSamplingSemantics(
                AuthoredInfoPresent: false,
                AuthoredAttributes: 0,
                AuthoredWord1: 0,
                AuthoredWord2: 0,
                RetailCreationSelector: 1,
                AddressU: EnbRenderTextureAddressMode.Wrap,
                AddressV: EnbRenderTextureAddressMode.Wrap,
                LegacyCapabilityFormatRequested: false)
            {
                CurrentRetailMinMagOverride = EnbRenderTextureMinMagFilter.Linear,
            },
            SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState,
            ResolveLogicalNameAtRuntime = true,
        };

    private static EnbW3dRenderVertexMaterial ApplyRgb(
        EnbW3dRenderVertexMaterial material,
        Vector3 rgb)
    {
        EnbW3dRenderMaterialUniformOverride exact = material.CurrentRetailUniformOverride ??
            new EnbW3dRenderMaterialUniformOverride(
                ToVector3(material.Ambient),
                ToVector3(material.Diffuse),
                ToVector3(material.Specular),
                ToVector3(material.Emissive),
                material.Power,
                material.Opacity);
        return material with
        {
            CurrentRetailUniformOverride = exact with
            {
                Ambient = rgb,
                Diffuse = rgb,
            },
        };
    }

    private static EnbW3dCompiledMaterialSemantics ApplyRgb(
        EnbW3dCompiledMaterialSemantics semantics,
        Vector3 rgb)
        => semantics with
        {
            Uniforms = semantics.Uniforms with
            {
                Ambient = rgb,
                Diffuse = rgb,
            },
        };

    private static EnbW3dCompiledMaterialSemantics ApplyNameShader(
        EnbW3dCompiledMaterialSemantics source, EnbW3dFixedFunctionState shader)
        => source with
        {
            FixedFunction = shader,
            PermutationKey = source.PermutationKey with
            {
                DepthCompare = shader.DepthCompare,
                DepthWriteEnabled = shader.DepthWriteEnabled,
                CullMode = shader.CullMode,
                SourceBlend = shader.SourceBlend,
                DestinationBlend = shader.DestinationBlend,
                BlendEnabled = shader.BlendEnabled,
                AlphaTest = shader.AlphaTest,
                SpecularEnabled = shader.SpecularEnabled,
                FogMode = shader.FogMode,
                TextureCombinerSemantics = shader.TextureCombinerSemantics
            }
        };
    private static Vector3 ToVector3(EnbRenderPackedRgb8 color)
        => EnbW3dMaterialCompiler.DecodeMaterialRgb(color);

    private static EnbW3dDrawTask Fail(
        EnbW3dDrawTask packet,
        EnbCurrentRetailShipNameDecalOutcome outcome,
        string? name,
        Vector3 rgb,
        string diagnostic,
        out EnbCurrentRetailShipNameDecalReceipt receipt)
    {
        receipt = new EnbCurrentRetailShipNameDecalReceipt(
            outcome,
            name,
            rgb,
            null,
            diagnostic);
        return packet;
    }

    private readonly record struct GlyphBounds(int Left, int Right)
    {
        public static GlyphBounds Empty => new(0, -1);
        public int Width => Right < Left ? 0 : Right - Left + 1;
    }
}
