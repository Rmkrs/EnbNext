using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Installed frontend preparation, owned by the retail asset session.</summary>
public sealed class EnbFrontendResources
{
    private readonly EnbRetailAssetBundle assets;
    private readonly Dictionary<string,EnbFrontendModel> models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,EnbRgb> colors = new(StringComparer.OrdinalIgnoreCase);
    public int BlinkPeriodMilliseconds { get; }
    internal EnbFrontendResources(EnbRetailAssetBundle assets)
    {
        this.assets=assets;
        // 006BFD00 loads FA_BLINK's authored Speed; 006CA169 divides the
        // client.exe 00AE7848 500-ms base period by that speed.
        var fontArt=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fontart.ini"));
        foreach (var palette in new[] { fontArt, EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fontcode.ini")) })
            foreach (var values in palette.EnumerateSections().Select(s=>s.Value))
                if (values.TryGetValue("ColorID",out var colorId) && values.TryGetValue("RGB",out var rgb))
                {
                    var components=rgb.Split(',').Select(v=>int.Parse(v,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    colors[colorId]=new(components[0]/255f,components[1]/255f,components[2]/255f);
                }
        string? blinkSpeed=fontArt.EnumerateSections().Select(s=>s.Value)
            .Where(s=>s.GetValueOrDefault("EffectID","").Equals("FA_BLINK",StringComparison.OrdinalIgnoreCase))
            .Select(s=>s.GetValueOrDefault("Speed")).FirstOrDefault();
        BlinkPeriodMilliseconds=float.TryParse(blinkSpeed,System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,out float speed) && speed>0
            ?Math.Max(1,(int)(500f/speed)):500;
    }
    public EnbFrontendModel Model(string name)
    {
        if(!models.TryGetValue(name,out var model))models[name]=model=new(assets,name);
        return model;
    }
    public EnbRgb Color(string id)=>colors[id];
    public bool TryColor(string id,out EnbRgb color)=>colors.TryGetValue(id,out color);
    private readonly Dictionary<string,EnbAuthoredTooltipFrame> _dialogFrames=[];
    private EnbInterfaceLayoutCatalog? _dialogLabels;
    private EnbRetailIniDocument? _dialogDefinitions;
    public EnbInterfaceLayoutCatalog DialogLabels=>OwnerCache.Get(ref _dialogLabels,assets,
        static assets=>EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fc_dialogbox.ini")));
    public EnbRetailIniDocument DialogDefinitions=>OwnerCache.Get(ref _dialogDefinitions,assets,
        static assets=>EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"dialogbox.ini")));
    public EnbAuthoredTooltipFrame DialogFrame(string name)
    {
        if(!_dialogFrames.TryGetValue(name,out var asset))
        {
            var ini=assets.Paths.ClientIniDirectory;
            var definition=EnbRetailScannerTooltipDefinition.Parse(EnbInterfaceLayoutCatalog.Load(Path.Combine(ini,"fc_default.ini")),
                EnbRetailIniDocument.Load(Path.Combine(ini,"tooltip.ini")),EnbRetailIniDocument.Load(Path.Combine(ini,"dynawin.ini")),name)
                ??throw new InvalidDataException("Missing dialog frame: "+name);
            _dialogFrames[name]=asset=EnbAuthoredTooltipFrame.Load(assets,definition)??throw new InvalidDataException("Unavailable dialog mesh");
        }
        return asset;
    }
}
