using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public enum EnbCurrentRetailShipDecalOutcome : byte
{
    NotDecalRenderObject = 0,
    NoSlotAssignment = 1,
    AppliedTexture = 2,
    AppliedTextureAndTint = 3,
    MissingCatalog = 4,
    MissingDefinition = 5,
    UnsupportedSwatch = 6,
    InvalidColor = 7,
    MissingStage0Texture = 8,
    Hidden = 9,
    OmittedDescriptorPass = 10,
    UnsupportedDescriptor = 11,
}

public readonly record struct EnbCurrentRetailShipDecalReceipt(
    EnbCurrentRetailShipDecalOutcome Outcome,
    int Slot,
    int DecalId,
    string? TextureName,
    Vector4 Color,
    string? Diagnostic)
{
    public bool IsTarget => Outcome != EnbCurrentRetailShipDecalOutcome.NotDecalRenderObject;

    public bool Applied => Outcome is
        EnbCurrentRetailShipDecalOutcome.AppliedTexture or
        EnbCurrentRetailShipDecalOutcome.AppliedTextureAndTint or
        EnbCurrentRetailShipDecalOutcome.Hidden or
        EnbCurrentRetailShipDecalOutcome.OmittedDescriptorPass;
}

/// <summary>
/// Current-retail 0x0010 instance mutation for ordinary texture decals.
/// Retail recursively targets RenderObjs whose post-container name starts with
/// DECALdd, uses dd as one of sixteen slots, resolves the signed decal id through
/// decal_textures/decal_swatches/secret_decal_textures, and replaces the descriptor.
/// The decal manager forces normal and secret textures to clamp U/V after loading
/// their INI definitions. Negative swatches copy an authored W3D material
/// descriptor through the retained packet-group owner; this single-pass API reports them to that owner.
/// </summary>
public static class EnbCurrentRetailShipDecalRuntime
{
    private const float RetailColorEpsilon = 1.0f / 256.0f;
    /// <summary>0075BE00 retains this descriptor when an authored swatch is empty,
    /// missing or not a mesh. This is the manager's positive default, not hidden.</summary>
    public static EnbW3dRenderMaterialPass CreateSwatchDefaultDescriptor()
    {
        const uint shader = 0x985b3; // 0093B270's 984B3, then &~200 |100.
        var material = new EnbW3dRenderVertexMaterial(-1, "retail-decal-default", 0,
            new(255,255,255,0), new(255,255,255,0), new(0,0,0,0), new(0,0,0,0),
            0, 1, 0, null, null);
        var binding = new EnbW3dRenderVertexMaterialBinding(uint.MaxValue, material)
        {
            SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState,
            CurrentRetailRuntimeCoordinates = new(0, 1),
        };
        var lighting = new EnbW3dVertexLightingSemantics(true, EnbW3dMaterialColorSource.Material,
            EnbW3dMaterialColorSource.Material, EnbW3dMaterialColorSource.Material, EnbW3dMaterialColorSource.Material);
        if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(shader, material, 2, lighting, out var semantics, out var diagnostic))
            throw new InvalidOperationException(diagnostic);
        return new(0, [binding], EnbW3dRenderBindingCardinality.Uniform,
            [new(shader, default, semantics.FixedFunction) { SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState }],
            EnbW3dRenderBindingCardinality.Uniform, [], [], [], [], [], semantics)
        {
            CurrentRetailUniformMaterial = binding,
            CurrentRetailUniformLighting = lighting,
            CurrentRetailUniformShader = shader,
        };
    }

    public static EnbW3dDrawTask Apply(
        EnbW3dDrawTask packet,
        IReadOnlyList<EnbSceneDecalSnapshot> entries,
        EnbDecalDefinitionCatalog? catalog,
        out EnbCurrentRetailShipDecalReceipt receipt,EnbCurrentRetailSwatchCatalog? retainedTextures=null,
        bool hasDecalUpdate=false)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(entries);

        if (!TryParseSlot(packet.RenderObjectName, out int targetSlot))
        {
            receipt = new EnbCurrentRetailShipDecalReceipt(
                EnbCurrentRetailShipDecalOutcome.NotDecalRenderObject,
                -1,
                0,
                null,
                Vector4.One,
                null);
            return packet;
        }

        EnbSceneDecalSnapshot? selected = null;
        foreach (EnbSceneDecalSnapshot entry in entries)
        {
            int slot = (int)Math.Min(entry.Field0, 15u);
            if (slot == targetSlot)
                selected = entry; // Retail's fixed slot array retains the last assignment.
        }

        if (selected is null)
        {
            //0080E8C0 initializes all sixteen slots to None before copying
            //this packet's assignments. An empty received packet also clears
            //every slot; absence of any packet leaves the authored model alone.
            if (hasDecalUpdate || entries.Count != 0)
            {
                receipt = new(EnbCurrentRetailShipDecalOutcome.Hidden,targetSlot,0x07FFFFFF,null,Vector4.Zero,null);
                return packet;
            }
            receipt = new EnbCurrentRetailShipDecalReceipt(
                EnbCurrentRetailShipDecalOutcome.NoSlotAssignment,
                targetSlot,
                0,
                null,
                Vector4.One,
                null);
            return packet;
        }

        int decalId = unchecked((int)selected.Field1);
        // 00621390 / 0061A4D0 use this sentinel for the editor's None choice.
        if (decalId == 0x07FFFFFF)
        {
            receipt = new(EnbCurrentRetailShipDecalOutcome.Hidden,targetSlot,decalId,null,Vector4.Zero,null);
            return packet;
        }
        // 0075CB10 branches to the retained swatch descriptor before reading RGBA.
        // Signed swatch selection does not consume the positive decal tint payload.
        Vector4 color = Vector4.One;
        if (decalId >= 0 && !TryDecodeClampedColor(selected.Data, out color))
        {
            receipt = Failure(
                EnbCurrentRetailShipDecalOutcome.InvalidColor,
                targetSlot,
                decalId,
                $"Decal slot {targetSlot} has {selected.Data.Length} color bytes; current retail requires four little-endian floats.");
            return packet;
        }

        if (catalog is null)
        {
            receipt = Failure(
                EnbCurrentRetailShipDecalOutcome.MissingCatalog,
                targetSlot,
                decalId,
                "The installed retail decal catalog is unavailable.",
                color);
            return packet;
        }

        if (!catalog.TryResolve(decalId, out EnbDecalDefinition definition))
        {
            receipt = Failure(
                EnbCurrentRetailShipDecalOutcome.MissingDefinition,
                targetSlot,
                decalId,
                $"Retail decal id {decalId} is absent from the installed catalog.",
                color);
            return packet;
        }

        if (definition.Kind == EnbDecalDefinitionKind.Swatch)
        {
            receipt = Failure(
                EnbCurrentRetailShipDecalOutcome.UnsupportedSwatch,
                targetSlot,
                decalId,
                $"Retail decal id {decalId} is swatch '{definition.Resource}'; its W3D material-descriptor copy is not implemented by this slice.",
                color);
            return packet;
        }

        EnbW3dRenderMaterialPass source = packet.Material.Source;
        // 0075CB10 copies manager+30, whose descriptor has exactly one pass.
        if (source.Ordinal != 0)
        {
            receipt = new(EnbCurrentRetailShipDecalOutcome.OmittedDescriptorPass,
                targetSlot, decalId, definition.Resource, color, null);
            return packet;
        }
        bool tint = NeedsRetailTint(color);
        var descriptor = CreateSwatchDefaultDescriptor();
        var material = descriptor.CurrentRetailUniformMaterial!.Value;
        if (tint) material = material with { Material = ApplyTint(material.Material, color) };
        var texture = ReplaceTexture(new(0, definition.Resource, null), definition);
        if(retainedTextures?.TryGetTexture(decalId,out var textureState)==true)
            texture=texture with {CurrentRetailTextureState=textureState};
        descriptor = (descriptor).Copy().SetCurrentRetailUniformMaterial(material).SetTextureStages([new(0, [texture], [], EnbW3dRenderBindingCardinality.Uniform)
                { CurrentRetailUniformTexture = texture }]);
        if (!EnbW3dRenderResourceCompiler.TryReplaceUniformDescriptor(
                (packet.Mesh).Copy().SetMaterialPasses([source]), [descriptor], out var passes, out var diagnostic))
        {
            receipt = Failure(EnbCurrentRetailShipDecalOutcome.UnsupportedDescriptor,
                targetSlot, decalId, diagnostic ?? "Positive decal descriptor compilation failed.", color);
            return packet;
        }
        var effectivePass = passes[0];
        EnbW3dDrawTask effectivePacket = (packet).ForkTask().SetMaterial(new(effectivePass, effectivePass.SingleBindingSemantics)).SetCurrentRetailLoadedMaterials([material.Material]).SetCurrentRetailLoadedTextures(texture.CurrentRetailTextureState is {} retained ? [retained] : null).SetCurrentRetailSharedMaterialMappers(effectivePass.VertexMaterialCardinality ==
                EnbW3dRenderBindingCardinality.PerVertex ? packet.CurrentRetailSharedMaterialMappers : null).SetOrderKey(packet.OrderKey with { BlendClass = EnbW3dDrawPacketCompiler.ClassifyBlend(effectivePass) });
        receipt = new EnbCurrentRetailShipDecalReceipt(
            tint
                ? EnbCurrentRetailShipDecalOutcome.AppliedTextureAndTint
                : EnbCurrentRetailShipDecalOutcome.AppliedTexture,
            targetSlot,
            decalId,
            definition.Resource,
            color,
            null);
        return effectivePacket;
    }

    public static bool TryParseSlot(string renderObjectName, out int slot)
    {
        slot = -1;
        if (string.IsNullOrWhiteSpace(renderObjectName))
            return false;

        string name = renderObjectName;
        int separator = name.IndexOf('.');
        if (separator >= 0 && separator + 1 < name.Length)
            name = name[(separator + 1)..];

        if (name.Length <= 6 ||
            !name.StartsWith("DECAL", StringComparison.OrdinalIgnoreCase) ||
            !char.IsAsciiDigit(name[5]) ||
            !char.IsAsciiDigit(name[6]))
        {
            return false;
        }

        slot = ((name[5] - '0') * 10) + (name[6] - '0');
        slot = Math.Min(slot, 15);
        return true;
    }

    private static bool TryDecodeClampedColor(ImmutableArray<byte> bytes, out Vector4 color)
    {
        color = Vector4.One;
        if (bytes.Length != 16)
            return false;

        ReadOnlySpan<byte> span = bytes.AsSpan();
        Vector4 decoded = new(
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(0, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(8, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(12, 4)));
        //0080E972..0080EA36: unordered/below-zero takes the zero branch;
        //strictly above one takes one, otherwise the original word survives.
        static float ClampRetail(float value) => !(value >= 0f) ? 0f : value > 1f ? 1f : value;
        color = new(ClampRetail(decoded.X), ClampRetail(decoded.Y), ClampRetail(decoded.Z), ClampRetail(decoded.W));
        return true;
    }

    private static bool NeedsRetailTint(Vector4 color)
        => MathF.Abs(1f - color.X) > RetailColorEpsilon ||
           MathF.Abs(1f - color.Y) > RetailColorEpsilon ||
           MathF.Abs(1f - color.Z) > RetailColorEpsilon ||
           MathF.Abs(1f - color.W) > RetailColorEpsilon;

    private static EnbW3dRenderTextureBinding ReplaceTexture(
        EnbW3dRenderTextureBinding source,
        EnbDecalDefinition definition)
    {
        return source with
        {
            LogicalName = definition.Resource,
            CurrentRetailTextureState = null,
            Location = null,
            // DecalDefinitionManagerLoadFiles forces texture +0x64/+0x68 to one
            // for both normal and secret entries after the generic texture loader
            // has parsed TextureWrap. At the D3D boundary, one means clamp.
            Sampling = source.Sampling with
            {
                AddressU = EnbRenderTextureAddressMode.Clamp,
                AddressV = EnbRenderTextureAddressMode.Clamp,
            },
            SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState,
            ResolveLogicalNameAtRuntime = true,
        };
    }

    private static EnbW3dRenderVertexMaterial ApplyTint(
        EnbW3dRenderVertexMaterial material,
        Vector4 color)
    {
        EnbW3dRenderMaterialUniformOverride exact = material.CurrentRetailUniformOverride ??
            new EnbW3dRenderMaterialUniformOverride(
                ToVector3(material.Ambient),
                ToVector3(material.Diffuse),
                ToVector3(material.Specular),
                ToVector3(material.Emissive),
                material.Power,
                material.Opacity);
        Vector3 rgb = new(color.X, color.Y, color.Z);
        return material with
        {
            CurrentRetailUniformOverride = exact with
            {
                Ambient = rgb,
                Diffuse = rgb,
                DiffuseAlpha = color.W,
            },
        };
    }

    private static Vector3 ToVector3(EnbRenderPackedRgb8 color)
        => EnbW3dMaterialCompiler.DecodeMaterialRgb(color);

    private static EnbCurrentRetailShipDecalReceipt Failure(
        EnbCurrentRetailShipDecalOutcome outcome,
        int slot,
        int decalId,
        string diagnostic,
        Vector4? color = null)
        => new(outcome, slot, decalId, null, color ?? Vector4.One, diagnostic);
}
