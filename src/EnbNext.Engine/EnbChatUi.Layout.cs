using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using EnbNext.Presentation;
using System.Numerics;
using System.Collections.Immutable;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbChatUi
{
    private readonly Dictionary<int, EnbRetailChatLayoutPose> _layoutPoses = [];
    private IReadOnlyDictionary<string, Vector3> _layoutAnchors = new Dictionary<string, Vector3>();
    private readonly Dictionary<int, EnbAuthoredUiAsset> _layoutShells = [];
    private readonly List<(EnbAuthoredUiAsset Asset, Rect Bounds)> _layoutShellParts = [];
    private (int Mode, int Size, int Split)? _layoutShellKey;
    private bool _authoredShell;

    private void ResolveLayoutAnchors()
    {
        var layout = _layouts[_mode];
        int size = Math.Min(360,layout.HeightFrame(_height));
        int split = layout.SplitFrame(_split,_mode==2);
        // Native mode 2 is the horizontal split (local mode 1).
        // 00571F00 +118 and 005732A0 constrain its split to the size frame.
        if (_mode==1) split=Math.Min(split,size);
        _height=layout.Height(size); _split=layout.Split(split,_mode==2);
        if (!_layoutPoses.TryGetValue(_mode,out var pose))
            _layoutPoses[_mode]=pose=new(layout);
        _layoutAnchors=pose.Evaluate(size,_mode==0?null:split);
        var key=(_mode,size,split);
        if(_layoutShellKey!=key)
        {
            _layoutShellKey=key;_layoutShellParts.Clear();
            if(_layoutShells.TryGetValue(_mode,out var asset))
            {
                var evaluated=pose.EvaluatePose(size,_mode==0?null:split);
                foreach(var mesh in asset.Resource.Meshes.Where(m=>m.Name is "NEW_FRAME01" or "CENTER BAR" || m.Name.StartsWith("BAR_CARGO",StringComparison.Ordinal))
                    .OrderByDescending(m=>m.StaticSort.RuntimeLevel).ThenBy(m=>m.Ordinal))
                {
                    EnbW3dDeformedGeometry geometry;
                    if(mesh.BoneIndices.Count>0)
                    {
                        if(!EnbW3dSkinDeformer.TryDeform(mesh,evaluated,out geometry,out _))continue;
                    }
                    else
                    {
                        var pivot=asset.Hierarchy.Pivots.FirstOrDefault(p=>p.Name==mesh.Name);
                        if(pivot is null)continue;
                        var transform=evaluated.WorldTransforms[pivot.Ordinal];
                        geometry=new(mesh.Positions.Select(p=>Vector3.Transform(p,transform)).ToImmutableArray(),
                            mesh.Normals.Select(n=>Vector3.TransformNormal(n,transform)).ToImmutableArray());
                    }
                    if(geometry.Positions.Count==0)continue;
                    var min=geometry.Positions.Aggregate(Vector3.Min);var max=geometry.Positions.Aggregate(Vector3.Max);
                    var bounds=new Rect(min.X,-max.Y,max.X-min.X,max.Y-min.Y);
                    if(bounds.Width<=0 || bounds.Height<=0)continue;
                    _layoutShellParts.Add((asset with{Resource=asset.Resource.Copy().SetMeshes([mesh]),Geometry=geometry},bounds));
                }
            }
        }
        _authoredShell=_layoutShellParts.Any(p=>p.Asset.Resource.Meshes[0].Name=="NEW_FRAME01");
    }

    private void PublishLayoutShell()
    {
        if(_hidden || !_authoredShell)return;
        // 0056DB20 retains the installed model; 005732A0 updates its masked
        // size/split pose. Preserve its geometry/materials, before pane text.
        foreach(var part in _layoutShellParts)
            _draws.Add(new(part.Asset,0,(ComboScreen(part.Bounds.Translate(320,240))),false)
                {LayoutGroup=EnbUiGroup.Chat,DesignBounds=(part.Bounds),BehindHud=true});
    }

    private Point LayoutPoint(string name)
    {
        var point=_layoutAnchors[name];
        return new((point.X+320)*_size.Width/640,(240-point.Y)*_size.Height/480);
    }

    private Rect LayoutViewport(int nativePane)
    {
        var tl=LayoutPoint($"VP{nativePane}_TL");var br=LayoutPoint($"VP{nativePane}_BR");
        return new(tl.X,tl.Y,Math.Max(0,br.X-tl.X),Math.Max(0,br.Y-tl.Y));
    }

    private Rect LayoutControl(string art, Point origin)
    {
        var mesh=_art[art].Asset.Resource.Meshes.Single();
        return new(origin.X+mesh.BoundingBoxMin.X*_size.Width/640,
            origin.Y-mesh.BoundingBoxMax.Y*_size.Height/480,
            (mesh.BoundingBoxMax.X-mesh.BoundingBoxMin.X)*_size.Width/640,
            (mesh.BoundingBoxMax.Y-mesh.BoundingBoxMin.Y)*_size.Height/480);
    }

    private void PublishPaneSlider(int pane)
    {
        if (_paneRowCounts[pane]==0 || !_art.ContainsKey("up") || !_art.ContainsKey("down")) return;
        int nativePane=_mode==0?0:1-pane;
        var top=LayoutPoint($"SLIDER_{nativePane:00}_MIN");
        var bottom=LayoutPoint($"SLIDER_{nativePane:00}_MAX");
        double ux=_size.Width/640,uy=_size.Height/480;
        var mesh=_art["up"].Asset.Resource.Meshes.Single();
        double extent=mesh.BoundingBoxMax.Y-mesh.BoundingBoxMin.Y-2;
        double length=(bottom.Y-top.Y)/uy-2*extent;
        // 0066FD80 collapses travel below one; 0066EE20 hides arrows at zero,
        // and the thumb below 16-2. These are design-space distances.
        if (length<1) return;
        Button("up"+pane,"^",LayoutControl("up",top),()=>Scroll(pane,1));
        Button("down"+pane,"v",LayoutControl("down",bottom),()=>Scroll(pane,-1));
        Hit("up"+pane,new(top.X-extent*.5*ux,top.Y,extent*ux,extent*uy),()=>Scroll(pane,1),wheel:d=>Scroll(pane,d>0?1:-1));
        Hit("down"+pane,new(top.X-extent*.5*ux,bottom.Y-extent*uy,extent*ux,extent*uy),()=>Scroll(pane,-1),wheel:d=>Scroll(pane,d>0?1:-1));
        var track=new Rect(top.X-extent*.5*ux,top.Y+extent*uy,extent*ux,length*uy);
        if (length<14) return;
        if(!_authoredShell)Box(track,Pixel(0,0,0,230));
        Hit("track"+pane,track,()=>SetHistorySlider(pane,_historySliderValues[pane]+
            (_last.Y<_historyThumbs[pane].Top?-_visibleLines[pane]:_visibleLines[pane])),
            wheel:delta=>Scroll(pane,delta>0?1:-1));
        PublishHistoryThumb(pane,track);
    }
}
