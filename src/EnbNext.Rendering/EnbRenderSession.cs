using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Render-side state of one retail asset session. The bundle's
/// <see cref="EnbRetailAssetBundle.RenderPrepared"/> slot owns it; each member is created once on first use, as the former
/// bundle-keyed side tables did.</summary>
public sealed class EnbRenderSession : EnbRenderPrepared
{
    private readonly EnbRetailAssetBundle _assets;
    private EnbW3dRenderResourceCache? _resourceCache;
    private EnbW3dTimeMappedAcquisition? _movieAnimations;
    private EnbCurrentRetailClientViewLightingState? _clientViewLighting;
    private Dictionary<int, EnbCurrentRetailRslEffectPassLibrary>? _rslPassTemplates;
    private HashSet<(int Library, int State)>? _rslAccessedObjectTemplates;
    private Lazy<EnbCurrentRetailSwatchCatalog>? _decals;
    private EnbRetailIniDocument? _fontEffects;
    private EnbTooltipResources? _tooltipResources;
    private EnbOptionsPanelResources? _optionsPanels;
    private EnbConfirmationResources? _confirmations;
    private EnbVerticalScrollbarResources? _verticalScrollbar;
    private EnbControlsResources? _controls;
    private EnbHorizontalSliderResources? _horizontalSlider;
    private EnbSliderPanelResources? _sliderPanels;
    private EnbSettingsFontMetrics? _settingsFontMetrics;
    private EnbUtilityHudResources? _utilityHud;
    private EnbInformationResources? _information;
    private EnbBrowserPanelResources? _browserPanel;
    private EnbShipNameResources? _shipName;
    private EnbGutterResources? _gutter;
    private EnbFrontendResources? _frontend;

    private EnbRenderSession(EnbRetailAssetBundle assets) => _assets = assets;

    public static EnbRenderSession Of(EnbRetailAssetBundle assets)
        => (EnbRenderSession)OwnerCache.Get(ref assets.RenderPrepared, assets,
            static a => (EnbRenderPrepared)new EnbRenderSession(a));

    private long _textureRevision;
    public EnbW3dRenderResourceCache ResourceCache
    {
        get
        {
            if(_textureRevision!=_assets.TextureRevision)
            {
                _resourceCache?.Dispose();_resourceCache=null;_decals=null;
                _textureRevision=_assets.TextureRevision;
            }
            return OwnerCache.Get(ref _resourceCache, _assets, EnbW3dRenderResourceCache.CreateForAssetNamespace);
        }
    }
    public EnbW3dTimeMappedAcquisition MovieAnimations
        => OwnerCache.Get(ref _movieAnimations, _assets, static a => new(a.Paths, a.MixResources, a.RetailMixResources));
    // Frontend and world are one retail client: ClientView lighting state is per session.
    public EnbCurrentRetailClientViewLightingState ClientViewLighting
        => OwnerCache.Get(ref _clientViewLighting, _assets, static _ => new());
    public Dictionary<int, EnbCurrentRetailRslEffectPassLibrary> RslPassTemplates
        => OwnerCache.Get(ref _rslPassTemplates, _assets, static _ => []);
    public HashSet<(int Library, int State)> RslAccessedObjectTemplates
        => OwnerCache.Get(ref _rslAccessedObjectTemplates, _assets, static _ => []);
    public EnbOptionsPanelResources OptionsPanels
        => OwnerCache.Get(ref _optionsPanels, _assets, static a => new(a));
    public EnbUtilityHudResources UtilityHud
        => OwnerCache.Get(ref _utilityHud, _assets, static a => new(a));
    public EnbInformationResources Information
        => OwnerCache.Get(ref _information, _assets, static a => new(a));
    public EnbFrontendResources Frontend
        => OwnerCache.Get(ref _frontend, _assets, static a => new(a));
    public EnbGutterResources Gutter
        => OwnerCache.Get(ref _gutter, _assets, static a => new(a));
    public EnbShipNameResources ShipName
        => OwnerCache.Get(ref _shipName, _assets, static a => new(a));
    public EnbBrowserPanelResources BrowserPanel
        => OwnerCache.Get(ref _browserPanel, _assets, static a => new(a));
    public EnbConfirmationResources Confirmations
        => OwnerCache.Get(ref _confirmations, _assets, static a => new(a));
    public EnbVerticalScrollbarResources VerticalScrollbar
        => OwnerCache.Get(ref _verticalScrollbar, _assets, static a => new(a));
    public EnbSettingsFontMetrics SettingsFontMetrics
        => OwnerCache.Get(ref _settingsFontMetrics, _assets, static a => new(a));
    public EnbSliderPanelResources SliderPanels
        => OwnerCache.Get(ref _sliderPanels, _assets, static a => new(a));
    public EnbHorizontalSliderResources HorizontalSlider
        => OwnerCache.Get(ref _horizontalSlider, _assets, static a => new(a));
    public EnbControlsResources Controls
        => OwnerCache.Get(ref _controls, _assets, static a => new(a));
    public EnbTooltipResources TooltipResources
        => OwnerCache.Get(ref _tooltipResources, _assets, static a => new(a));
    public EnbRetailIniDocument FontEffects
        => OwnerCache.Get(ref _fontEffects, _assets, static a =>
            EnbRetailIniDocument.Load(Path.Combine(a.Paths.ClientIniDirectory, "fontart.ini")));
    /// <summary>The first caller's loader is retained; the catalog is loaded once.</summary>
    public EnbCurrentRetailSwatchCatalog Decals(Func<EnbCurrentRetailSwatchCatalog> load)
        => OwnerCache.Get(ref _decals, load, static l => new Lazy<EnbCurrentRetailSwatchCatalog>(l)).Value;
}
