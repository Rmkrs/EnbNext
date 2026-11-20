using System.Collections.Immutable;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>006FCB70 initialization and006FE1E0's independent five damage caches.</summary>
public sealed class EnbRetailShipDamageRuntime
{
    public ImmutableArray<EnbRetailMovieDamage> Quadrants { get; private set; } = [new(1,1),new(1,1),new(1,1),new(1,1)];
    public EnbRetailMovieDamage Overall { get; private set; } = new(1,1);
    private uint _nextSound;
    public void ResetVisuals()
    {
        //006FCB70 resets104..114, but leaves the object's154 deadline alone.
        Quadrants=[new(1,1),new(1,1),new(1,1),new(1,1)];Overall=new(1,1);
    }

    //006FE208..006FE334 precedes this frame's Aux sampling. Constructor+154
    //is zero; the comparison is unsigned and strict, with no catch-up loop.
    public string? TakeSound(uint now)
    {
        if(now<=_nextSound)return null;
        int level=Math.Max(Overall.Level,Quadrants.Max(q=>q.Level));
        if(level<=1)return null;
        _nextSound=unchecked(now+(level==4?4000u:level==3?5000u:6000u));
        return $"Level_{level-1}_Ship_Damage";
    }

    public void Update(EnbSceneShipPresentationSnapshot? ship,EnbSceneTargetVitalsSnapshot? vitals)
    {
        var next=Quadrants.ToBuilder();
        for(int i=0;i<4;i++)
            next[i]=Advance(next[i],Tier(ship?.VisibleQuadrantHealth is {Length:4} health?health[i]:1));
        Quadrants=next.ToImmutable();
        float remaining=ship?.IsOrganic==true? vitals?.ShieldPercent??1:
            vitals is {HullPoints:float hull,MaximumHullPoints:float maximum}&&maximum!=0? hull/maximum:1;
        Overall=Advance(Overall,Tier(remaining));
    }

    private static EnbRetailMovieDamage Advance(EnbRetailMovieDamage prior,int level)
        =>prior.Level==level?prior:new(level,unchecked(prior.Generation+1));

    //006FE6B3..006FE78F; constants3F000000 and3E800000, inclusive lower bounds.
    private static int Tier(float remaining)
    {
        float damage=1-Math.Clamp(remaining,0,1);
        return damage>=.5f?4:damage>=.25f?3:damage>0?2:1;
    }
}
