using System.Collections.Immutable;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// GPU-independent pass skeleton. This preserves the current retail ordering
/// constraints and depth-range transitions without assigning later
/// asset families to GPU implementation details prematurely.
/// </summary>
public enum EnbRenderPassKind
{
    FrameClear,
    SharedScene,
    SharedWorld,
    SpecializedDepthRange,
    OrdinaryWorld,
    WorldSceneComplete,
    LegacyInterfaceBoundary,
    ModernOverlayBoundary,
    Present,
}

public sealed record EnbRenderPassDescriptor(
    EnbRenderPassKind Kind,
    EnbRenderClipRange? ClipRange,
    int SpecializedDepthRangeIndex = -1);

public sealed record EnbRenderPassGraph(ImmutableArray<EnbRenderPassDescriptor> Passes)
{
    public static EnbRenderPassGraph CreateSpaceSkeleton(
        EnbSceneCameraStateSnapshot cameraState,
        bool swapBackdrop)
    {
        ArgumentNullException.ThrowIfNull(cameraState);

        EnbRenderClipRange normal = ToClip(cameraState.NormalClipRange);
        // MainViewRenderSharedScenePass 0050D060 overrides the camera clip
        // planes with 00AECF38/00AECF34, then restores the caller's range.
        EnbRenderClipRange backdrop = new(20f, 100000f);
        var builder = ImmutableArray.CreateBuilder<EnbRenderPassDescriptor>();
        builder.Add(new EnbRenderPassDescriptor(EnbRenderPassKind.FrameClear, null));

        if (!swapBackdrop)
        {
            builder.Add(new EnbRenderPassDescriptor(EnbRenderPassKind.SharedScene, backdrop));
            builder.Add(new EnbRenderPassDescriptor(EnbRenderPassKind.SharedWorld, normal));
        }
        else
        {
            builder.Add(new EnbRenderPassDescriptor(EnbRenderPassKind.SharedWorld, normal));
            builder.Add(new EnbRenderPassDescriptor(EnbRenderPassKind.SharedScene, backdrop));
        }

        for (int i = 0; i < cameraState.TemporaryPassClipRanges.Length; i++)
        {
            builder.Add(new EnbRenderPassDescriptor(
                EnbRenderPassKind.SpecializedDepthRange,
                ToClip(cameraState.TemporaryPassClipRanges[i]),
                i));
        }

        builder.Add(new EnbRenderPassDescriptor(EnbRenderPassKind.OrdinaryWorld, normal));
        builder.Add(new EnbRenderPassDescriptor(EnbRenderPassKind.WorldSceneComplete, normal));
        builder.Add(new EnbRenderPassDescriptor(EnbRenderPassKind.LegacyInterfaceBoundary, null));
        builder.Add(new EnbRenderPassDescriptor(EnbRenderPassKind.ModernOverlayBoundary, null));
        builder.Add(new EnbRenderPassDescriptor(EnbRenderPassKind.Present, null));
        return new EnbRenderPassGraph(builder.ToImmutable());
    }

    private static EnbRenderClipRange ToClip(EnbSceneClipRangeSnapshot clip)
    {
        var result = new EnbRenderClipRange(clip.NearPlane, clip.FarPlane);
        if (!result.IsValid)
            throw new InvalidDataException($"Invalid presentation clip range {clip.NearPlane}..{clip.FarPlane}.");
        return result;
    }
}
