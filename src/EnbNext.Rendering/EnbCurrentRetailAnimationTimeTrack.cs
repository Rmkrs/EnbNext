using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public readonly record struct EnbCurrentRetailAnimationTimeKey(uint Time,int SourceFrame);
public readonly record struct EnbCurrentRetailAnimationTimeSelection(int First,int Second,float Fraction);

/// <summary>00986CD0/00986DA0/00986E70's retained 0x02C0 time track.
/// The key reader supplies actual indexed storage, including incoming bytes
/// when the native path addresses outside the logical key count.</summary>
public sealed class EnbCurrentRetailAnimationTimeTrack(
    Func<int,EnbCurrentRetailAnimationTimeKey> readKey,Func<int> count,int cachedIndex)
{
    public int CachedIndex {get;private set;}=cachedIndex;

    public EnbCurrentRetailAnimationTimeSelection Select(float frame)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        if(float.IsNaN(frame)||frame<0)return Same(readKey(0).SourceFrame);
        var last=readKey(unchecked(count()-1));
        if((double)last.Time<=frame)return Same(last.SourceFrame);
        int index=SelectIndex(frame);
        int first=readKey(index).SourceFrame,second=readKey(unchecked(index+1)).SourceFrame;
        uint firstTime=readKey(index).Time,secondTime=readKey(unchecked(index+1)).Time;
        int difference=unchecked((int)(secondTime-firstTime));
        return new(first,second,(float)(((R)frame-(double)firstTime)/(double)difference));
    }

    private static EnbCurrentRetailAnimationTimeSelection Same(int value)=>new(value,value,0);

    private int SelectIndex(float frame)
    {
        int index=CachedIndex;
        if((double)readKey(index).Time<=frame)
        {
            int last=unchecked(count()-1);
            if(index==last)return index;
            int next=unchecked(index+1);
            if((double)readKey(next).Time>frame)return index;
            CachedIndex=next;
            if(next==last)return next;
            if((double)readKey(unchecked(next+1)).Time>frame)return next;
        }
        return CachedIndex=Search(frame);
    }

    private int Search(float frame)
    {
        if((double)readKey(0).Time>frame)return 0;
        int upper=count();
        if((double)readKey(unchecked(upper-1)).Time<=frame)return unchecked(upper-1);
        int lower=0,width=upper;
        while(width!=1)
        {
            int middle=unchecked((width>>1)+lower);
            if((double)readKey(middle).Time>frame)upper=middle;else lower=middle;
            width=unchecked(upper-lower);
        }
        return lower;
    }
}
