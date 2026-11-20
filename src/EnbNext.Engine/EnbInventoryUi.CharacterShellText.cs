using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private int _characterFontTab=-1;
    private long _characterFontStarted;
    private float _characterCycleSpeed;

    private void LoadCharacterShellFonts(EnbRetailAssetBundle assets,EnbAuthoredUiAsset asset)
    {
        LoadCargoFonts(assets,asset,"fa_cinfo.ini","character/");
        var palette=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fontart.ini"));
        _characterCycleSpeed=float.Parse(palette.EnumerateSections().First(s=>s.Value.GetValueOrDefault("EffectID")=="FA_CYCLE").Value["Speed"],CultureInfo.InvariantCulture);
        _characterFontTab=-1;
    }

    private void CharacterTabLabels()
    {
        //00573CE0 creates fresh fonts;00573E80 resets all clocks on a changed
        //selection, selected stage0 (CYCLE), others stage1 (REVERT/GLOW).
        if(_characterFontTab!=_characterTab)
        {
            _characterFontTab=_characterTab;_characterFontStarted=Environment.TickCount64;
        }
        string[] ids=["ATTRIBUTES_MENU","MISSIONS_MENU","FACTION_MENU"];
        for(int i=0;i<ids.Length;i++)
        {
            string key="character/"+ids[i];
            if(!_cargoFonts.TryGetValue(key,out var definition))continue;
            var color=definition.Color;
            if(i==_characterTab)
            {
                color=EnbAttachedLabelCycle.Color(color,definition.Alternate,Environment.TickCount64-_characterFontStarted,_characterCycleSpeed);
                _animatingText=true;
            }
            else if(_hover=="tab/"+i)
            {
                //006CA0C0 case11 takes per-channel maxima, not a glow sprite.
                color=new(Math.Max(color.R,definition.Alternate.R),Math.Max(color.G,definition.Alternate.G),Math.Max(color.B,definition.Alternate.B));
            }
            CargoLabel(key,"tabLabel"+i,color:color);
        }
    }
}
