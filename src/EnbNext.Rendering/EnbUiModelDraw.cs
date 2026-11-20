using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed record EnbUiModelDraw(EnbAuthoredUiAsset Asset, uint Verb, EnbUiRect Bounds, bool Hovered)
{
    public EnbUiGroup LayoutGroup {get;init;}
    // The owning input gadget survives layout/translation changes.6609A0
    // moves its existing clone without emitting another FlashingGadgetEvent.
    public object? GadgetOwner {get;init;}
    public bool BehindHud {get;init;}
    /// <summary>Next Modern UI: drawn with the Next panels, under the game windows (the map included).</summary>
    public bool NextPanel {get;init;}
    public bool ClearBackground {get;init;}
    public bool OpaqueBlackBacking {get;init;}
    public Vector3? OpaqueBackingColor {get;init;}
    public Action<float>? PreviewDistance {get;init;}
    public bool FlashHighlight {get;init;}
    public float? HighlightEmissive {get;init;}
    public bool SuppressHoverEvent {get;init;}
    public uint? HighlightTime {get;init;}
    public bool SpinSwatch {get;init;}
    public float SwatchAngle {get;init;}
    public Action<float>? PreviewPanOrigin {get;init;}
    public bool AnimateMaterials {get;init;}
    public int? ShortcutRangeFrame {get;init;}
    public float ShortcutRangeAngle {get;init;}
    public uint? MaterialTime {get;init;}
    public Vector3? EmissiveOverride {get;init;}
    // Material alpha controls RGB blending, not the opacity of an already drawn
    // panel in the host's separate HUD surface. Preserve accumulated coverage
    // for every retained UI mesh, including buttons layered over opaque frames.
    public bool CompositeOpaqueCoverage {get;init;} = true;
    /// <summary>Draw only <see cref="Images"/>; the asset is not rendered.</summary>
    public bool ImagesOnly {get;init;}
    public EnbUiRect DesignBounds { get; init; } = new(-12,-12,24,24);
    public ImmutableArray<EnbUiTextDraw> Images { get; init; } = [];
    public Vector2? Stage0UvOffset { get; init; }
    public IEnbSceneFrame? EquipmentPreview { get; init; }
    public EnbUiRect? EquipmentBounds {get;init;}
    public EnbUiRect? EquipmentClipBounds {get;init;}
    public bool EquipmentTransparent {get;init;}
    public Vector2 EquipmentPan {get;init;}
    public float EquipmentSpin {get;init;}
    public Quaternion? EquipmentRotation {get;init;}
    public EnbSceneAvatarSnapshot? Portrait {get;init;}
    /// <summary>Modern UI target portrait: the scanner's live target model is drawn here, in draw order.</summary>
    public bool ScannerModel {get;init;}
    public EnbAvatarPortraitAsset? PortraitPose {get;init;}
    public bool PortraitSharedActor {get;init;}
    public bool PortraitStationLocal {get;init;}
    public uint? PortraitStationRemoteId {get;init;}
    public EnbDialogueSpeechPage? PortraitSpeech {get;init;}
    public EnbSceneEnvironmentSnapshot? PortraitEnvironment {get;init;}
    public EnbUiRect PortraitBounds {get;init;}
    public double? PortraitAspect {get;init;}
    public string PortraitCacheKey {get;init;} = "default";
    public bool PortraitFullBody {get;init;}
    public float PortraitHeading {get;init;}
    public Vector3? CustomizationView {get;init;}
    public Vector3 EquipmentView { get; init; } = new(0,0,1);
}

