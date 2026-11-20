using EnbNext.Engine;
using EnbNext.Presentation;
namespace EnbNext.Net7.Tests;

public sealed class DiagnosticUiTests
{
    [Fact]
    public void PointerCancellationAndClampingPreserveWindowAccess()
    {
        using var ui=new EnbDiagnosticUi(()=>"report",_=>{},()=> (360,60),(_,_)=>{});
        var captures=new List<bool>();ui.CaptureRequested+=captures.Add;ui.Resize(new(800,600));ui.UpdatePerformance(true,null);
        Assert.True(ui.Press(new(20,270)));Assert.True(ui.Captured);
        ui.Move(new(900,900));Assert.Equal(410,ui.PerformanceBounds.X);Assert.Equal(410,ui.PerformanceBounds.Y);
        ui.Cancel();Assert.False(ui.Captured);Assert.False(ui.Release(new(500,500)));
        Assert.Equal(new[]{true,false},captures);
        var r=ui.ActionBounds(1);ui.Press(new(r.X+2,r.Y+2));ui.Release(new(r.X+2,r.Y+2));
        Assert.True(ui.Details);Assert.True(ui.PerformanceBounds.Bottom<=600);
        ui.Resize(new(200,150));Assert.Equal(0,ui.PerformanceBounds.Y);Assert.True(ui.PerformanceBounds.Right<=200);
    }
    [Fact]
    public void IssuePopupCopiesCurrentReportAndCancelsUnfinishedClick()
    {
        string report="first",copied="";int suspended=0;
        using var ui=new EnbDiagnosticUi(()=>report,s=>copied=s,()=> (360,60),(_,_)=>{});
        ui.Resize(new(800,600));ui.IssueCount=1;ui.GameplaySuspended+=()=>suspended++;
        Click(2);Assert.True(ui.IssuesOpen);Assert.Equal("first",ui.IssueText);Assert.Equal(1,suspended);
        report="second";Click(3);Assert.Equal("second",copied);
        var close=ui.ActionBounds(4);ui.Press(new(close.X+1,close.Y+1));ui.Cancel();ui.Release(new(close.X+1,close.Y+1));Assert.True(ui.IssuesOpen);
        Click(4);Assert.False(ui.IssuesOpen);Assert.False(ui.Press(new(0,0)));
        void Click(int action){var r=ui.ActionBounds(action);var point=new EnbUiPoint(r.X+1,r.Y+1);Assert.True(ui.Press(point));Assert.True(ui.Release(point));}
    }
    [Fact]
    public void IssuePressSuspendsGameplayBeforeTakingPointerCapture()
    {
        using var ui=new EnbDiagnosticUi(()=>"asset failure",_=>{},()=> (360,60),(_,_)=>{});
        ui.Resize(new(800,600));ui.IssueCount=1;
        var order=new List<string>();
        ui.GameplaySuspended+=()=>order.Add("suspend");
        ui.CaptureRequested+=value=>order.Add(value?"capture":"release");
        var point=ui.ActionBounds(2).Center;
        Assert.True(ui.Press(point));Assert.False(ui.IssuesOpen);
        Assert.Equal(new[]{"suspend","capture"},order);
        Assert.True(ui.Release(point));Assert.True(ui.IssuesOpen);
        Assert.Equal(new[]{"suspend","capture","release"},order);
    }

}
