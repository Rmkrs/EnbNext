using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed class StationNameDraw
    {
        public readonly EnbRetailText Text=new();
        public readonly EnbOpenGlBackdropRenderer Renderer=new();
        public float Opacity;
        public float FlashAlpha=1;
        public long LastTick;
        public void Destroy(EnbGlApi? gl){Text.Dispose();Renderer.Deinitialize(gl);}
    }
    private readonly Dictionary<(bool Player,uint Id,bool Guild),StationNameDraw> _stationNameDraws=[];
    private readonly Dictionary<string,EnbRetailFontFace> _stationNameFonts=[];
    private EnbInterfaceLayoutCatalog? _stationNameLayout;
    private EnbRgb _stationNameColor=new(1,1,0);
    private EnbRgb _stationPlayerNameColor;
    private EnbRgb _stationGuildNameColor;
    private float _stationNameFadeIn, _stationNameFadeOut;
    public int StationVisibleNameCount {get;private set;}
    private void DestroyStationNames(EnbGlApi? gl,bool preserveScene=false)
    {
        foreach(var draw in _stationNameDraws.Values)
            if(preserveScene)draw.Renderer.Deinitialize(gl);else draw.Destroy(gl);
        if(!preserveScene)_stationNameDraws.Clear();
        StationVisibleNameCount=0;
    }
    private void RenderStationNames(EnbGlApi gl,int fb,int width,int height,double uiScale)
    {
        StationVisibleNameCount=0;
        if(!StationArrivalComplete || _stationDepartureStarted is not null || _retailAssetsAccessor() is not {} assets)return;
        if(_stationNameLayout is null)
        {
            _stationNameLayout=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_sbase.ini"));
            var colors=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fontart.ini"));
            foreach(var section in colors.SectionNames)
                if(colors.TryGetSection(section,out var effect) &&
                    float.TryParse(effect.GetValueOrDefault("Speed"),System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,out var speed))
                {
                    if(effect.GetValueOrDefault("EffectID")=="FA_FADE_IN") _stationNameFadeIn=speed;
                    if(effect.GetValueOrDefault("EffectID")=="FA_FADE_OUT") _stationNameFadeOut=speed;
                }
            if(_stationNameLayout.TryResolve("NPCText_SBASE",out var native))
                foreach(var section in colors.SectionNames)
                    if(colors.TryGetSection(section,out var values) && values.GetValueOrDefault("ColorID")==native.Properties["Color"] &&
                        values.GetValueOrDefault("RGB")?.Split(',') is {Length:3} rgb)
                    { _stationNameColor=new(float.Parse(rgb[0],System.Globalization.CultureInfo.InvariantCulture)/255,
                        float.Parse(rgb[1],System.Globalization.CultureInfo.InvariantCulture)/255,float.Parse(rgb[2],System.Globalization.CultureInfo.InvariantCulture)/255);break; }
            if(_stationNameLayout.TryResolve("NameText_SBASE",out var playerNative))
                foreach(var section in colors.SectionNames)
                    if(colors.TryGetSection(section,out var values) && values.GetValueOrDefault("ColorID")==playerNative.Properties["Color"] &&
                        values.GetValueOrDefault("RGB")?.Split(',') is {Length:3} rgb)
                    {_stationPlayerNameColor=new(float.Parse(rgb[0],System.Globalization.CultureInfo.InvariantCulture)/255,float.Parse(rgb[1],System.Globalization.CultureInfo.InvariantCulture)/255,float.Parse(rgb[2],System.Globalization.CultureInfo.InvariantCulture)/255);break;}
            if(_stationNameLayout.TryResolve("GuildText_SBASE",out var guildNative))
                foreach(var section in colors.SectionNames)
                    if(colors.TryGetSection(section,out var values) && values.GetValueOrDefault("ColorID")==guildNative.Properties["Color"] &&
                        values.GetValueOrDefault("RGB")?.Split(',') is {Length:3} rgb)
                    {_stationGuildNameColor=new(float.Parse(rgb[0],System.Globalization.CultureInfo.InvariantCulture)/255,float.Parse(rgb[1],System.Globalization.CultureInfo.InvariantCulture)/255,float.Parse(rgb[2],System.Globalization.CultureInfo.InvariantCulture)/255);break;}
        }
        HashSet<(bool,uint,bool)> seen=[];
        foreach(var label in StationNameLabels)
        {
            if(!_stationNameLayout.TryResolve(label.Guild?"GuildText_SBASE":label.Player?"NameText_SBASE":"NPCText_SBASE",out var definition) ||
                !EnbRetailFontFactory.TryResolveDefinition(assets,definition.Properties["FontType"],width,height,out var fontDefinition) || fontDefinition.Resource is null)continue;
            if(!_stationNameFonts.TryGetValue(fontDefinition.Resource,out var font))
            {
                if(!EnbRetailFontFactory.TryCreate(assets,fontDefinition,out font))continue;
                _stationNameFonts.Add(fontDefinition.Resource,font);
            }
            var key=(label.Player,label.Slot,label.Guild);seen.Add(key);
            if(!_stationNameDraws.TryGetValue(key,out var draw))
                _stationNameDraws[key]=draw=new(){LastTick=_stationElapsedMilliseconds};
            double scale=EnbStationNamePresentation.Scale(label.CameraDistance)*uiScale;
            uint elapsed=unchecked((uint)Math.Max(0,_stationElapsedMilliseconds-draw.LastTick));
            float delta=elapsed*.001f;
            draw.LastTick=_stationElapsedMilliseconds;
            draw.Opacity=EnbStationNamePresentation.AdvanceOpacity(draw.Opacity,label.CameraDistance,label.Occluded,delta,_stationNameFadeIn,_stationNameFadeOut);
            if(!label.Flash)draw.FlashAlpha=1;
            else if(!label.Hidden)draw.FlashAlpha=EnbStationSpeakerLabel.AdvanceFlash(draw.FlashAlpha,elapsed);
            if(draw.Opacity<=0 || label.Name.Length==0 || label.Hidden)continue;
            draw.Text.SetText(label.Name,font,label.Color??(label.Guild?_stationGuildNameColor:label.Player?_stationPlayerNameColor:_stationNameColor));
            if(draw.Text.Raster is not {} raster)continue;
            if(!draw.Renderer.IsInitialized)draw.Renderer.Initialize(gl);
            // Retail 0063F490: min(2,25/distance), fade out below 0.5.
            int w=Math.Max(1,(int)Math.Round(draw.Text.Width*scale)),h=Math.Max(1,(int)Math.Round(draw.Text.Height*scale));
            // 6D6CED takes the lesser of effect opacity(+2FC) and flash alpha(+4A0).
            draw.Renderer.RenderOverlay(gl,fb,(int)Math.Round(label.X*width-w*.5),height-(int)Math.Round(label.Y*height+h*.5),w,h,raster,opacity:Math.Min(draw.Opacity,draw.FlashAlpha));
            StationVisibleNameCount++;
        }
        foreach(var key in _stationNameDraws.Keys.Where(k=>!seen.Contains(k)).ToArray())
        {_stationNameDraws[key].Destroy(gl);_stationNameDraws.Remove(key);}
        gl.Viewport(0,0,width,height);
    }
}
