using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Numerics;
namespace EnbNext.Engine;
internal sealed partial class EnbInventoryUi
{
    private void SyncCustomizationPalette(string texture)
    {
        Vector3 color;
        if(_terminal?.Type==6 && _customAvatar is {} a)
        {
            var rgb=_customColor switch {0=>a.HairColor,1=>a.EyeColor,2=>a.SkinColor,3=>a.ShirtPrimaryColor,4=>a.ShirtSecondaryColor,5=>a.PantsPrimaryColor,6=>a.PantsSecondaryColor,_=>null};
            if(rgb is null || rgb.Length!=3)return;
            color=new(rgb[0],rgb[1],rgb[2]);
        }
        else if(_customShip is {} ship)color=_customShipPage==3?ship.ShipNameColor:ship.PartAttributes[_customColor].Color;
        else return;
        var selection=(texture,_customColor,color);
        if(_customPaletteSelection==selection)return;
        _customPaletteSelection=selection;
        if(Texture(texture) is not {} image || !_art.TryGetValue(_terminalArt+"/COLOR_PALETTE",out var art) || art.Asset.Geometry is not {} geometry)return;
        var mesh=art.Asset.Resource.Meshes[0];var uv=mesh.MaterialPasses[0].TextureStages[0].RuntimeTexCoords;
        float best=float.PositiveInfinity;Vector2 bestPoint=_customPalettePoint;
        for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++)
        {
            int offset=(y*image.Width+x)*4;
            var sample=new Vector3(image.BgraPixels[offset+2],image.BgraPixels[offset+1],image.BgraPixels[offset])/255f;
            float error=Vector3.DistanceSquared(sample,color);if(error>=best)continue;
            var texel=new Vector2((x+.5f)/image.Width,(y+.5f)/image.Height);
            for(int i=0;i+2<mesh.Indices.Count;i+=3)
            {
                int ia=(int)mesh.Indices[i],ib=(int)mesh.Indices[i+1],ic=(int)mesh.Indices[i+2];
                var b=uv[ib]-uv[ia];var c=uv[ic]-uv[ia];var d=texel-uv[ia];float det=b.X*c.Y-b.Y*c.X;
                if(Math.Abs(det)<1e-6f)continue;
                float u=(d.X*c.Y-d.Y*c.X)/det,v=(b.X*d.Y-b.Y*d.X)/det;
                if(u<0||v<0||u+v>1)continue;
                var position=geometry.Positions[ia]*(1-u-v)+geometry.Positions[ib]*u+geometry.Positions[ic]*v;
                bestPoint=new((float)((position.X-art.Design.X)/art.Design.Width),(float)((-position.Y-art.Design.Y)/art.Design.Height));
                best=error;break;
            }
        }
        _customPalettePoint=bestPoint;
    }
}
