using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private EnbWorldRenderer? _graphicsOptionsOwner;
    private EnbRenderDetailOptionState _graphicsOptions = new();
    private EnbRetailAssetBundle? _graphicsOptionsAssets;
    private uint? _graphicsOptionsSession;
    private EnbRetailRenderBudgetSourceSettings? _graphicsOptionsSource;
    private readonly object _graphicsOptionsGate = new();
    internal EnbRenderDetailRuntimeSettings? PerformanceDetailSettings =>
        _retailAssetsAccessor() is {} assets ? ResolveGraphicsOptions(assets) : null;
    // Offline replay only: consume the captured resolved policy without reading
    // a different character's options or the probe's default machine budget.
    internal EnbRenderDetailRuntimeSettings? RecordedDetailSettingsOverride { get; set; }

    internal void ShareGraphicsOptions(EnbWorldRenderer owner) => _graphicsOptionsOwner = owner;

    internal void ApplyGraphicsOptionChange(Action change)
    {
        // Consume every UI publication, including several changes between frames.
        lock (_graphicsOptionsGate)
        {
            if (_retailAssetsAccessor() is { } before) ResolveGraphicsOptions(before);
            change();
            if (_retailAssetsAccessor() is { } after) ResolveGraphicsOptions(after);
        }
    }

    private EnbRenderDetailRuntimeSettings ResolveGraphicsOptions(EnbRetailAssetBundle assets)
    {
        if(RecordedDetailSettingsOverride is {} recorded)return recorded;
        if (_graphicsOptionsOwner is { } owner) return owner.ResolveGraphicsOptions(assets);
        lock (_graphicsOptionsGate)
        {
            uint? session = _fixedSessionIdAccessor();
            if (!ReferenceEquals(assets, _graphicsOptionsAssets) || session != _graphicsOptionsSession)
            {
                _graphicsOptions = new();
                _graphicsOptionsAssets = assets;
                _graphicsOptionsSession = session;
                _graphicsOptionsSource = null;
            }
            var source = RenderBudgetSourceSettingsOverride ?? EnbNextRenderSettings.Current;
            bool force = source != _graphicsOptionsSource;
            _graphicsOptionsSource = source;
            return _graphicsOptions.Update(source, assets.ResolvePlayerOptions(session).Options, force);
        }
    }
}
