namespace EnbNext.Net7.World;

public enum Net7LegoControllerMutationKind { Create, Rebuild, ReplaceModel, Remove }

/// <summary>Ordered732880/6FEF70 notifications. RenderObject construction and
/// its failure outcome belong to the presentation consumer, not packet decode.</summary>
public sealed record Net7LegoControllerMutation(
    ulong Sequence,Net7LegoControllerMutationKind Kind,string Source,int Ordinal,
    ulong MaterializationRevision,string BoneName,string? PreviousBoneName,int Type,int BaseAsset)
{
    // Packet handlers mutate the same live RenderObj synchronously. A Lego
    // notification must bind to the model selected at this packet boundary,
    // not to whichever model the next presentation frame eventually selects.
    public int? RenderStateActionCount { get; init; }
}
