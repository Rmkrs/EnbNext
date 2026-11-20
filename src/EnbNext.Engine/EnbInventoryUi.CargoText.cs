using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private sealed record CargoFont(EnbAttachedTextStyle Style,string Text,EnbRgb Color,EnbRgb Alternate,bool StayComplete);
    private readonly Dictionary<string,CargoFont> _cargoFonts=[];
    private readonly Dictionary<string,EnbAttachedLabelWipe> _cargoWipes=[];
    private readonly Dictionary<string,EnbAttachedLabelScroll> _cargoScroll=[];
    private readonly HashSet<string> _cargoFontsDrawn=[];

    private void LoadCargoFonts(EnbRetailAssetBundle assets,EnbAuthoredUiAsset asset,string file,string prefix)
    {
        string path=Path.Combine(assets.Paths.ClientIniDirectory,file);
        var ini=EnbRetailIniDocument.Load(path);var layout=EnbInterfaceLayoutCatalog.Load(path);
        var colors=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fontart.ini"));
        EnbRgb Color(string id)
        {
            var rgb=colors.EnumerateSections().First(s=>s.Value.GetValueOrDefault("ColorID")==id).Value["RGB"].Split(',');
            return new(byte.Parse(rgb[0])/255f,byte.Parse(rgb[1])/255f,byte.Parse(rgb[2])/255f);
        }
        foreach(var entry in ini.EnumerateSections())
            if(entry.Value.GetValueOrDefault("StringID") is {} id&&layout.TryResolve(id,out var label)&&
                asset.Hierarchy.Pivots.Any(p=>p.Name.Equals(label.Properties["Attach"],StringComparison.OrdinalIgnoreCase)))
                _cargoFonts[prefix+id]=new(EnbAttachedTextStyle.Load(assets,asset,label,Matrix4x4.Identity),
                    label.Properties.GetValueOrDefault("String0",""),Color(label.Properties["Color"]),Color(label.Properties["AltColor"]),
                    label.Properties.GetValueOrDefault("EffectOn","").Contains("FA_STAY_COMPLETE",StringComparison.Ordinal));
    }

    private void CargoLabel(string id,string cache,string? text=null,bool alternate=false,EnbRgb? color=null)
    {
        if(!_cargoFonts.TryGetValue(id,out var definition))return;
        //005CFE40/00657430/005D1250 create retained utility font collections.
        //Mode0 replacements preserve clocks. Cargo and vault Sort omit STAY;
        //other vault/equipment labels take their lifetime from the authored effect.
        if(!_cargoWipes.TryGetValue(id,out var wipe))_cargoWipes[id]=wipe=new(stayComplete:definition.StayComplete);
        if(!_cargoScroll.TryGetValue(id,out var scroll))_cargoScroll[id]=scroll=new();
        AttachedText(cache,text??definition.Text,definition.Style,scroll,color??(alternate?definition.Alternate:definition.Color),wipe,fixedLabel:true);
        _cargoFontsDrawn.Add(id);
    }
}
