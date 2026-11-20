using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Net7.Protocol;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbNavigationMapUi
{
    private sealed class GalaxyLabel
    {
        public Vector3 Position;
        public float Angle, Scale;
        public int Vertical, Horizontal;
        public EnbRgb Color;
        public string Text="", Font="", FadeOn="", FadeOff="";
        public bool Visible, Attached;
        public long FadeTime;
        public float Alpha;
        public int AtlasSize=32;
        public EnbUiRaster? Source, Atlas;
        public void Phase(bool visible)
        {
            Visible=visible; FadeTime=0;
            if(visible) Attached=true;
        }
    }
    private readonly Dictionary<string,GalaxyLabel> _galaxyLabels=[];
    private EnbInterfaceLayoutCatalog? _galaxyLabelCatalog;
    private EnbRetailIniDocument? _galaxyFontCode;
    private (uint? System,uint? Sector,int? Gate)? _galaxyLabelFocus;

    private GalaxyLabel GalaxyLabelOwner(string key,string text,bool system)
    {
        if(_galaxyLabels.TryGetValue(key,out var owner)) return owner;
        _galaxyLabelCatalog ??= EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets!.Paths.ClientIniDirectory,"fc_radar.ini"));
        string id=system ? "GALAXY_MAP_SYSTEM_NAME" : "GALAXY_MAP_SECTOR_NAME";
        if(!_galaxyLabelCatalog.TryResolve(id,out var style)) throw new InvalidDataException("Missing "+id);
        float scale=.1f;
        if(system)
        {
            scale=.3f; // 007B2B30's optional authored override.
            if(_galaxyIni!.TryGetSection("GalaxyMap",out var settings) && settings.TryGetValue("SystemNameScale",out var value))
                scale=float.Parse(value,CultureInfo.InvariantCulture);
        }
        owner=new() { Text=text, Font=style.Properties["FontType"], Scale=scale,
            FadeOn=style.Properties["EffectOn"].Split(',')[0].Trim(), FadeOff=style.Properties["EffectOff"].Split(',')[0].Trim() };
        _galaxyLabels.Add(key,owner); return owner;
    }

    private void ResolveGalaxyLabels(Net7GalaxySectorRecord[] sectors)
    {
        var focus=(_galaxyFocusedSystem,_galaxyFocusedSector,_galaxyFocusedGate);
        bool changed=_galaxyLabelFocus!=focus;
        foreach(var system in Galaxy.Systems)
        {
            var owner=GalaxyLabelOwner("galaxy:system:"+system.SystemHashId,system.Name,true);
            var (min,max)=_galaxySystemBounds[system.SystemHashId];
            owner.Position=system.TailValue switch
            {
                0=>new(min.X,(min.Y+max.Y)*.5f,min.Z), 1=>new(max.X,(min.Y+max.Y)*.5f,min.Z),
                3=>new((min.X+max.X)*.5f,min.Y,min.Z), _=>new((min.X+max.X)*.5f,max.Y,min.Z)
            };
            owner.Horizontal=system.TailValue switch {0=>4,1=>3,_=>0};
            owner.Vertical=system.TailValue switch {2=>2,3=>1,_=>0};
            uint phase=_galaxySystemPulse%1000; if(phase>500) phase=1000-phase;
            float brightness=_galaxyFocusedSystem==system.SystemHashId ? 1-phase*.002f*.75f : 1;
            owner.Color=new(system.VectorA[0]*brightness,system.VectorA[1]*brightness,system.VectorA[2]*brightness);
            bool visible=!_galaxyFocusedSector.HasValue;
            if(!owner.Attached || owner.Visible!=visible) owner.Phase(visible);
        }
        GalaxyLabel Sector(Net7GalaxySectorRecord sector)=>GalaxyLabelOwner("galaxy:sector:"+sector.SectorHashId,sector.Name,false);
        foreach(var sector in sectors.Where(s=>changed || _galaxyFocusedGate is int gate &&
            (Galaxy.Gates[gate].SectorReferenceA==s.SectorHashId || Galaxy.Gates[gate].SectorReferenceB==s.SectorHashId)))
            Sector(sector).Color=EnbGalaxyMapPresentation.SectorColor(Galaxy,sector,GalaxySectorHighlighted(sector.SectorHashId,sector.Field2));
        if(!changed) return;
        _galaxyLabelFocus=focus;

        void SetSector(Net7GalaxySectorRecord sector,bool highlight)
        {
            var owner=Sector(sector);
            owner.Color=EnbGalaxyMapPresentation.SectorColor(Galaxy,sector,highlight);
            var outgoing=_galaxyGateConstruction.Select(key=>Galaxy.Gates.First(g=>g.GateHashId==key)).Where(g=>g.SectorReferenceA==sector.SectorHashId && _galaxySectors.ContainsKey(g.SectorReferenceB)).ToArray();
            var angles=new List<float>();
            foreach(var gate in outgoing)
            {
                var target=sectors.First(s=>s.SectorHashId==gate.SectorReferenceB);
                var label=Sector(target);
                if(highlight)
                {
                    var delta=_galaxySectors[target.SectorHashId]-_galaxySectors[sector.SectorHashId];
                    float angle=MathF.Atan2(delta.Y,delta.X); angles.Add(angle);
                    var direction=delta.LengthSquared()==0 ? Vector3.Zero : Vector3.Normalize(delta);
                    label.Position=_galaxySectors[sector.SectorHashId]+direction*2;
                    label.Angle=Math.Abs(angle)<=MathF.PI*.5f ? angle : angle-MathF.PI;
                    label.Horizontal=Math.Abs(angle)<=MathF.PI*.5f ? 3 : 4; label.Vertical=0;
                    label.Color=EnbGalaxyMapPresentation.SectorColor(Galaxy,target);
                }
                label.Phase(highlight);
            }
            if(highlight)
            {
                if(angles.Count==0)
                {
                    owner.Position=_galaxySectors[sector.SectorHashId]+new Vector3(0,2,0);
                    owner.Angle=0; owner.Vertical=2; owner.Horizontal=0;
                }
                else
                {
                    angles.Sort(); angles.Insert(0,angles[^1]-MathF.Tau); angles.Add(angles[1]+MathF.Tau);
                    float largest=0, start=angles[0];
                    for(int i=1;i<angles.Count;i++)
                        if(angles[i]-angles[i-1]>largest) {largest=angles[i]-angles[i-1];start=angles[i-1];}
                    float angle=start+largest*.5f;
                    if(angle>=MathF.PI) angle-=MathF.Tau;
                    if(angle< -MathF.PI) angle+=MathF.Tau;
                    owner.Position=_galaxySectors[sector.SectorHashId]+new Vector3(MathF.Cos(angle)*2,MathF.Sin(angle)*2,0);
                    owner.Angle=Math.Abs(angle)<=MathF.PI*.5f ? angle : angle-MathF.PI;
                    owner.Vertical=0; owner.Horizontal=Math.Abs(angle)<=MathF.PI*.5f ? 3 : 4;
                }
            }
            if(highlight || owner.Attached) owner.Phase(highlight);
        }
        foreach(var sector in _galaxySectorOrder.Keys.Select(key=>sectors.First(s=>s.SectorHashId==key))) SetSector(sector,false);
        if(_galaxyFocusedSector is uint selected) SetSector(sectors.First(s=>s.SectorHashId==selected),true);
        else if(_galaxyFocusedSystem is uint system)
            foreach(var sector in _galaxySectorConstruction.Select(key=>sectors.First(s=>s.SectorHashId==key)).Where(s=>s.Field2==system)) SetSector(sector,true);
        else if(_galaxyFocusedGate is int index)
        {
            var gate=Galaxy.Gates[index];
            var from=Sector(sectors.First(s=>s.SectorHashId==gate.SectorReferenceA));
            var to=Sector(sectors.First(s=>s.SectorHashId==gate.SectorReferenceB));
            var a=_galaxySectors[gate.SectorReferenceA]; var b=_galaxySectors[gate.SectorReferenceB];
            var delta=new Vector2(b.X-a.X,b.Y-a.Y);
            float length=delta.LengthSquared();
            float t=length==0 ? 0 : Vector2.Dot(new(_galaxyGateEntry.X-a.X,_galaxyGateEntry.Y-a.Y),delta)/length;
            from.Position=to.Position=new(a.X+t*delta.X,a.Y+t*delta.Y,_galaxyGateEntry.Z);
            float angle=MathF.Atan2(delta.Y,delta.X); bool flip=Math.Abs(angle)>MathF.PI*.5f;
            from.Angle=to.Angle=flip ? angle+MathF.PI : angle;
            to.Vertical=flip ? 2 : 1; to.Horizontal=flip ? 4 : 3;
            from.Vertical=flip ? 1 : 2; from.Horizontal=flip ? 3 : 4;
            from.Phase(true); to.Phase(true);
        }
    }

    private ImmutableArray<EnbMapQuad> DrawGalaxyLabels()
    {
        _galaxyFontCode ??= EnbRetailIniDocument.Load(Path.Combine(_assets!.Paths.ClientIniDirectory,"fontcode.ini"));
        var output=ImmutableArray.CreateBuilder<EnbMapQuad>();
        foreach(var (key,label) in _galaxyLabels)
        {
            if(!label.Attached || Font(label.Font) is not {} font) continue;
            string id=label.Visible ? label.FadeOn : label.FadeOff;
            var effect=_galaxyFontCode.EnumerateSections().Select(s=>s.Value).First(s=>s.GetValueOrDefault("EffectID")==id);
            float Number(string name)=>float.Parse(effect[name].TrimEnd('f','F'),CultureInfo.InvariantCulture);
            float initial=Number("Initial"), target=Number("Intensity");
            float amount=Number("Speed")*.0005f*label.FadeTime;
            label.Alpha=Math.Clamp(initial<target ? Math.Min(target,initial+amount) : Math.Max(target,initial-amount),0,1);
            label.FadeTime+=_galaxyRenderElapsed;
            if(label.Alpha<=0) continue;
            if(!_text.TryGetValue(key,out var text)) _text[key]=text=new();
            text.SetText(label.Text,font,label.Color);
            if(text.Raster is not {} raster) continue;
            if(!ReferenceEquals(label.Source,raster))
            {
                label.AtlasSize=EnbGalaxyMapTextLayout.AtlasSize(raster.Width,raster.Height,label.AtlasSize);
                int size=label.AtlasSize;
                byte[] pixels=new byte[checked(size*size*4)];
                for(int offset=0,row=0;offset<raster.Width;offset+=size,row++)
                {
                    int count=Math.Min(size,raster.Width-offset);
                    for(int y=0;y<raster.Height;y++)
                        raster.BgraPixels.AsSpan((y*raster.Width+offset)*4,count*4)
                            .CopyTo(pixels.AsSpan(((row*raster.Height+y)*size)*4,count*4));
                }
                label.Atlas=new(size,size,ImmutableArray.CreateRange(pixels)); label.Source=raster;
            }
            float sx=(float)(640/(_size.Width*_scaling))*font.Scale*label.Scale;
            float sy=(float)(480/(_size.Height*_scaling))*font.Scale*label.Scale;
            // 006D64C0 truncates centered pixel dimensions before scaling.
            float left=-(label.Horizontal==3 ? 0 : label.Horizontal==4 ? raster.Width : raster.Width/2)*sx;
            float top=(label.Vertical==1 ? 0 : label.Vertical==2 ? raster.Height : raster.Height/2)*sy;
            // 006D72C7/006D72FE clamp each text quad to .5 world units;
            // 006D5170 initializes both reference-resolution divisors to one.
            float height=Math.Max(.5f,raster.Height*sy);
            var transform=Matrix4x4.CreateRotationZ(label.Angle)*Matrix4x4.CreateTranslation(EnbGalaxyMapTextLayout.RenderOrigin(label.Position,
                (float)(640/(_size.Width*_scaling)),(float)(480/(_size.Height*_scaling))));
            Vector3 At(float x,float y)=>Vector3.Transform(new(x,y,0),transform);
            // 006C2340 maps ADDITIVE to 2; 006D5F40 selects 0x98437:
            // source-alpha / one, unlike the galaxy's one / one patches.
            int chunkIndex=0;
            foreach(var chunk in EnbGalaxyMapTextLayout.Chunks(raster.Width,label.AtlasSize,sx,left))
            {
                float u=(float)chunk.PixelWidth/label.AtlasSize;
                float v=(float)(chunkIndex*raster.Height)/label.AtlasSize;
                float bottom=(float)((++chunkIndex)*raster.Height)/label.AtlasSize;
                output.Add(new(label.Atlas!,At(chunk.Left,top),At(chunk.Left+chunk.Width,top),At(chunk.Left,top-height),At(chunk.Left+chunk.Width,top-height),
                    new(0,v),new(u,v),new(0,bottom),new(u,bottom),label.Alpha,SourceAlpha:true));
            }
        }
        return output.ToImmutable();
    }
}
