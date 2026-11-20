using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbEffectAssetCacheAdmissionTests
{
    [Fact]
    public void ActiveAndInFlightPlansSurviveWhileCompletedUnusedPlansCanRetire()
    {
        var pending=new TaskCompletionSource<string>();
        var cache=new Dictionary<int,Task<string>>{[1]=Task.FromResult("active"),[2]=pending.Task,[3]=Task.FromResult("old")};
        var active=cache[1];
        EnbEffectAssetCacheAdmission.TrimForAdmission(cache,new HashSet<int>{1},3);
        Assert.Same(active,cache[1]);Assert.Same(pending.Task,cache[2]);Assert.False(cache.ContainsKey(3));
        cache[4]=Task.FromResult("scheduled");
        EnbEffectAssetCacheAdmission.TrimForAdmission(cache,new HashSet<int>{1,4},3);
        Assert.Equal(3,cache.Count);
        pending.SetResult("completed");
        EnbEffectAssetCacheAdmission.TrimForAdmission(cache,new HashSet<int>{1,4},3);
        Assert.Equal(2,cache.Count);
        Assert.Same(active,cache[1]);Assert.True(cache.ContainsKey(4));
    }

    [Fact]
    public void HundredsOfSequentialVariantsRemainAdmissibleAtBoundedResidency()
    {
        var cache=new Dictionary<int,Task<object>>();
        for(int i=0;i<200;i++)
        {
            EnbEffectAssetCacheAdmission.TrimForAdmission(cache,new HashSet<int>{i},32);
            cache.Add(i,Task.FromResult(new object()));
            Assert.InRange(cache.Count,1,32);
        }
        Assert.True(cache.ContainsKey(199));
    }

    [Fact]
    public void SimultaneousRequiredVariantsExceedResidencyTargetAndTrimAfterRetirement()
    {
        var required=Enumerable.Range(0,65).ToHashSet();
        var pending=new TaskCompletionSource<object>();
        var cache=new Dictionary<int,Task<object>> {[-1]=pending.Task};
        for(int i=0;i<65;i++)
        {
            EnbEffectAssetCacheAdmission.TrimForAdmission(cache,required,32);
            cache.Add(i,Task.FromResult(new object()));
        }
        Assert.Equal(66,cache.Count);
        Assert.All(required,key=>Assert.True(cache.ContainsKey(key)));
        EnbEffectAssetCacheAdmission.TrimForAdmission(cache,new HashSet<int>{64},32);
        Assert.Equal(31,cache.Count);
        Assert.True(cache.ContainsKey(64));
        Assert.Same(pending.Task,cache[-1]);
    }
}
