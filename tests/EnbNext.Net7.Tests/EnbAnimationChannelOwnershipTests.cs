using System.Collections;
using System.Runtime.CompilerServices;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbAnimationChannelOwnershipTests
{
    private static EnbW3dRenderAnimationChannel X(int pivot,float end)=>new(0,1,1,0,
        EnbW3dAnimationChannelKind.TranslationX,(ushort)pivot,pivot,new[]{0f,end});
    private static EnbW3dRenderHierarchyAnimation Clip(IReadOnlyList<EnbW3dRenderAnimationChannel> channels)
        =>new(0,0x40001,"clip","tree",2,30,false,channels,[]);
    [Fact] public void RetainedChannelSelectionStillSamplesEveryFrameAndReplacementClip()
    {
        var channels=new Counted<EnbW3dRenderAnimationChannel>(new[]{X(1,10),X(2,50),X(1,20)});
        var clip=Clip(channels);
        for(int i=0;i<64;i++)
        {
            float frame=i/64f;
            Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip,1,frame,out var sample,out var error),error);
            Assert.Equal(frame*20,sample.Translation.X);
            Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip,2,frame,out sample,out error),error);
            Assert.Equal(frame*50,sample.Translation.X);
        }
        Assert.Equal(1,channels.Enumerations);
        var replacement=clip.Copy().SetChannels(new[]{X(1,80)});
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(replacement,1,.5f,out var changed,out _));
        Assert.Equal(40,changed.Translation.X);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip,1,.5f,out var original,out _));
        Assert.Equal(10,original.Translation.X);
    }
    [Fact] public void PreparedClipCanBeCollectedAfterRetirement()
    {
        var reference=CreateRetiredClip();
        for(int i=0;i<3&&reference.IsAlive;i++){GC.Collect();GC.WaitForPendingFinalizers();}
        Assert.False(reference.IsAlive);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateRetiredClip()
    {
        var clip=Clip(new[]{X(1,10)});
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip,1,.5f,out _,out _));
        return new WeakReference(clip);
    }
    private sealed class Counted<T>(T[] values):IReadOnlyList<T>
    {
        public int Enumerations;
        public int Count=>values.Length;
        public T this[int index]=>values[index];
        public IEnumerator<T> GetEnumerator(){Enumerations++;return ((IEnumerable<T>)values).GetEnumerator();}
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
}
