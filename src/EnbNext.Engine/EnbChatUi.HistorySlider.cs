using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using EnbNext.Presentation;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbChatUi
{
    private readonly Rect[] _historyTracks = new Rect[2], _historyThumbs = new Rect[2];
    private readonly double[] _historySliderValues = new double[2];
    private bool _historyCaptured;
    private double _historyGrab, _historyGrabValue;
    private int _historyRepeat, _historyDragPane;
    private long _historyRepeatAt;

    private static bool IsHistorySlider(string id) => id.Length > 1 && id[^1] is '0' or '1' &&
        id[..^1] is "up" or "down" or "track" or "thumb";

    private void PublishHistoryThumb(int pane, Rect track)
    {
        if (_privateMemberThumb is not { } thumb || _paneRowCounts[pane] == 0) return;
        // 680790 supplies total=count+visible, page=visible. 66FA90/670300
        // size the authored animation at height-16 and position it over count.
        double unitY = _size.Height / 480;
        double height = Math.Max(16, track.Height / unitY * _visibleLines[pane] /
            (_paneRowCounts[pane] + _visibleLines[pane]));
        var animation = thumb.Resource.Animations.FirstOrDefault(a => a.Name.Equals("slider_vert-1", StringComparison.OrdinalIgnoreCase));
        if (animation is null || thumb.Resource.Hierarchies.Count != 1 || thumb.Resource.Meshes.Count != 1) return;
        var hierarchy = thumb.Resource.Hierarchies.Single();
        var transforms = new Matrix4x4[hierarchy.Pivots.Count];
        foreach (var pivot in hierarchy.Pivots)
        {
            if (!EnbW3dAnimationSampler.TrySamplePivot(animation, pivot.Ordinal, (float)Math.Max(0,height-16), out var sample, out _)) return;
            var local = Matrix4x4.CreateTranslation(sample.Translation) * pivot.LocalTransform;
            transforms[pivot.Ordinal] = pivot.IsRoot ? local : local * transforms[pivot.ParentIndex];
        }
        var pose = new EnbW3dHierarchyPoseOwner(transforms.ToImmutableArray(), Enumerable.Repeat(true,transforms.Length).ToImmutableArray(),null,null);
        if (!EnbW3dSkinDeformer.TryDeform(thumb.Resource.Meshes.Single(),pose,out var geometry,out _)) return;
        var min = geometry.Positions.Aggregate(Vector3.Min); var max = geometry.Positions.Aggregate(Vector3.Max);
        var source = new Rect(min.X,-max.Y,max.X-min.X,max.Y-min.Y);
        int first = Math.Max(0,_paneRowCounts[pane]-_shownLines[pane]-_offset[pane]);
        if (Math.Round(_historySliderValues[pane]) != first) _historySliderValues[pane] = first;
        double pixelHeight = height * unitY;
        var bounds = new Rect(track.X,track.Y + Math.Max(0,track.Height-pixelHeight)*
            _historySliderValues[pane]/_paneRowCounts[pane],track.Width,pixelHeight);
        _historyTracks[pane] = track; _historyThumbs[pane] = bounds;
        string id = "thumb"+pane;
        Hit(id,bounds,()=>{},wheel:delta=>Scroll(pane,delta>0?1:-1));
        var visual=new Rect(track.Center.X+source.X*_size.Width/640,
            bounds.Y+source.Y*unitY,source.Width*_size.Width/640,source.Height*unitY);
        _draws.Add(new(thumb with {Geometry=geometry},0,(visual),_hover==id)
            {LayoutGroup=EnbUiGroup.Chat,DesignBounds=(source),GadgetOwner=_hits[id]});
    }

    private void SetHistorySlider(int pane, double value)
    {
        value = Math.Clamp(value,0,_paneRowCounts[pane]);
        int current = Math.Max(0,_paneRowCounts[pane]-_shownLines[pane]-_offset[pane]);
        int next = (int)Math.Round(value);
        _historySliderValues[pane] = value;
        if (next != current) InteractionSoundRequested?.Invoke("slider_bar_1.wav");
        Scroll(pane,current-next);
    }

    private void BeginHistorySlider(string id)
    {
        _drag=id; _historyDragPane=id[^1]-'0'; _historyCaptured=true;
        int pane=_historyDragPane;
        _historyGrab=_last.Y-_historyThumbs[pane].Top;
        _historyGrabValue=_historySliderValues[pane];
        _historyRepeat=id.StartsWith("up") ? -1 : id.StartsWith("down") ? 1 :
            id.StartsWith("track") ? (_last.Y<_historyThumbs[pane].Top ? -_visibleLines[pane] : _visibleLines[pane]) : 0;
        _historyRepeatAt=CommandsClock()+500;
        CaptureRequested?.Invoke(id);
        if (_historyRepeat!=0) SetHistorySlider(pane,_historySliderValues[pane]+_historyRepeat);
    }

    private void DragHistoryThumb(Point point)
    {
        int pane=_historyDragPane;
        var bounds=Region("up"+pane).Union(Region("down"+pane));
        if (!bounds.Inflate(32*_size.Width/640,32*_size.Height/480).Contains(point))
        { SetHistorySlider(pane,_historyGrabValue);return; }
        var track=_historyTracks[pane];
        double travel=track.Height-_historyThumbs[pane].Height;
        SetHistorySlider(pane,travel>0 ? (point.Y-_historyGrab-track.Top)/travel*_paneRowCounts[pane] : 0);
    }

    private bool AdvanceHistorySlider()
    {
        if (_drag is null || !IsHistorySlider(_drag) || _historyRepeat==0 || CommandsClock()<_historyRepeatAt) return false;
        if (!TryRegion(_drag,out var region) || !region.Contains(_last)) return false;
        var thumb=_historyThumbs[_historyDragPane];
        if (_drag.StartsWith("track") && (_historyRepeat<0 ? _last.Y>=thumb.Top : _last.Y<=thumb.Bottom)) return false;
        _historyRepeatAt+=100;
        SetHistorySlider(_historyDragPane,_historySliderValues[_historyDragPane]+_historyRepeat);
        return true;
    }

    private void EndHistorySlider()
    {
        bool captured=_historyCaptured; _historyCaptured=false; _historyRepeat=0;
        if (_drag is not null && IsHistorySlider(_drag)) _drag=null;
        if (captured) CaptureRequested?.Invoke(null);
    }
}
