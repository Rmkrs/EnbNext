using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>007B99C0 reset and007B9DC0 per-segment preview progression.</summary>
public sealed class EnbNavigationMapRouteAnimation
{
    public int CompletedSegments { get; private set; }
    public float SegmentProgress { get; private set; }

    public void Reset() { CompletedSegments=0; SegmentProgress=0; }

    public ImmutableArray<Vector2> Sample(Vector2 start,int count,Func<int,Vector2?> resolve,
        uint elapsedMilliseconds,bool immediate,Action<string>? sound=null)
    {
        if (count==0) return [];
        var result=ImmutableArray.CreateBuilder<Vector2>();
        result.Add(start);
        for(int i=0;i<count;i++)
        {
            // Retail validates even lines not yet attached to the scene.
            if(resolve(i) is not {} end) return [];
            if(!immediate && i>CompletedSegments)continue;
            var target=end;
            if(!immediate && i==CompletedSegments)
            {
                float next=(float)elapsedMilliseconds*.002f+SegmentProgress;
                SegmentProgress=next;
                if(next<1)
                    end=new((end.X-start.X)*next+start.X,(end.Y-start.Y)*next+start.Y);
                else
                {
                    CompletedSegments++;
                    SegmentProgress=0;
                    sound?.Invoke(CompletedSegments<count ? "starmap_build_1.wav" : "starmap_build_destination_1.wav");
                }
            }
            result.Add(end);
            start=target;
        }
        return result.ToImmutable();
    }
}
