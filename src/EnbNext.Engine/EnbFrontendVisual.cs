using EnbNext.Assets;
using EnbNext.Presentation;
namespace EnbNext.Engine;
internal sealed class EnbFrontendVisual
{
    public EnbUiRect Bounds;
    public bool IsVisible;
    public double Opacity=1;
    public string Text="";
    public EnbTextureImage? Texture;
}
