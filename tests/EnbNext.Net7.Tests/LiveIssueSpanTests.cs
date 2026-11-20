using EnbNext.Engine;
using System.Text.RegularExpressions;
using EnbNext.App;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class LiveIssueSpanTests
{
    private static bool Prior(string text)
    {
        string counters=Regex.Replace(text,@"\b(?<labels>[a-z][a-z0-9-]*(?:/[a-z][a-z0-9-]*)+) (?<values>\d+(?:/\d+)+)\b",match=>
        {
            string[] labels=match.Groups["labels"].Value.Split('/'),values=match.Groups["values"].Value.Split('/');
            return labels.Length==values.Length?string.Join("; ",labels.Zip(values,(a,b)=>$"{a} {b}")):match.Value;
        },RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        return Regex.IsMatch(counters,@"render failure|requires missing|not implemented|not the bounded|sky renderer:.*unavailable|Interior unavailable|\brejected\b(?!\s+0\b)|\b(?:fail|blocked|unresolved|recovered) [1-9]\d*\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    }
    [Fact]
    public void MatchesPreviousNormalizationAcrossMalformedAndUnicodeGroups()
    {
        string[] atoms=["target/applied/unresolved 3/3/0","unresolved/target 0/9","target/unresolved 0/9",
            "fail/blocked/recovered 0/1","fail/blocked 0/1/2","FAIL/a-b 1/0","rejected 0", "rejected 01",
            "sky renderer:\nunavailable","render failure","a/fail ١/٢","a/fail 1/2", "αfail/a 2/0", "a/fail 0/0", "no issue"];
        var random=new Random(15291);
        for(int i=0;i<5000;i++)
        {
            string text=string.Join(i%2==0?"; ":"\n",Enumerable.Range(0,random.Next(1,20)).Select(_=>atoms[random.Next(atoms.Length)]));
            Assert.Equal(Prior(text),LiveIssueLog.IsPresentationProblem(text.AsSpan()));
        }
    }
    [Fact]
    public void WarmDetectionDoesNotAllocate()
    {
        string text=string.Join("; ",Enumerable.Repeat("target/applied/unresolved 3/3/0",100));
        for(int i=0;i<100;i++)LiveIssueLog.IsPresentationProblem(text.AsSpan());
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++)LiveIssueLog.IsPresentationProblem(text.AsSpan());
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
    }
}
