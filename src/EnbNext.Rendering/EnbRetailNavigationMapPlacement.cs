using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>7C071B object placement and7C1815 camera placement.</summary>
public readonly record struct EnbRetailNavigationMapPlacement(Vector2 ScenePosition,Vector2 HitNormalized)
{
    public static EnbRetailNavigationMapPlacement Resolve(Vector2 world,float scale,Vector2 scaledCenter,
        Vector2 viewport,int displayWidth,int displayHeight)
    {
        var scene=EnbRetailScreenPixelGrid.Snap(world*scale,displayWidth,displayHeight);
        var relative=EnbRetailScreenPixelGrid.Snap(scene-scaledCenter,displayWidth,displayHeight);
        return new(scene,new(relative.X/viewport.X+.5f,.5f-relative.Y/viewport.Y));
    }

    public Vector2 RenderPoint(Vector2 scaledCenter,Vector2 viewport,int displayWidth,int displayHeight)
    {
        var camera=EnbRetailScreenPixelGrid.Snap(scaledCenter,displayWidth,displayHeight);
        var relative=ScenePosition-camera;
        return viewport*.5f+new Vector2(relative.X,-relative.Y);
    }
}
