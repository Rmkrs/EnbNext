namespace EnbNext.Assets;

/// <summary>Render-prepared data owned by an asset definition (option A).
/// The renderer is the only writer and supplies the concrete subclass; Assets only
/// knows that such data exists. One instance per definition object, same lifetime,
/// no side table. Copy() of the owning definition starts without it.</summary>
public abstract class EnbRenderPrepared
{
}
