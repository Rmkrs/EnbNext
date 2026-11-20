using EnbNext.Assets;
using EnbNext.Rendering;
using System.Numerics;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailAvatarSpeechTests
{
    private static EnbRetailAvatarSpeech Parser()
    {
        string[] tags=["begin","middle","end","alone","wave","breathe1","talk1","talk2","talk3","talk4","talk5","idle_talk1","idle_talk2","idle_talk3","idle_talk4","idle_talk5"];
        string personality="[HEADER]\nSpeechDelay=1\n[MOOD-0]\nMoodName=happy\nMoodNumber=2\n"+
            string.Join("\n",tags.Select((t,i)=>$"[BANIM-{i}]\nTag={t}\nAnimNumber={i+1}"));
        return new(EnbRetailIniDocument.Parse(personality),EnbRetailIniDocument.Parse("""
            [LEXICON-0]
            Phrase0=yes
            ContextBegin=begin
            ContextMiddle=middle
            ContextEnd=end
            ContextAlone=alone
            ContextAlways=wave
            """));
    }
    [Fact]
    public void AbsentSpeechTagStopsAndKeepsProducedRecords()
    {
        // 0078EEF0: an absent talk/idle_talk tag asserts and reads NULL+4; Next keeps the prefix and stops.
        string personality="[HEADER]\nSpeechDelay=1\n[BANIM-0]\nTag=wave\nAnimNumber=7\n[BANIM-1]\nTag=talk1\nAnimNumber=8";
        var parser=new EnbRetailAvatarSpeech(EnbRetailIniDocument.Parse(personality),EnbRetailIniDocument.Parse(""));
        var records=parser.Produce("/wave hello. more words",(_,_)=>10);
        Assert.Equal([new EnbRetailAvatarNlpRecord(2,7)],records);
        Assert.Equal(new EnbRetailAvatarNlpRecord(1,8,"hi."),parser.Produce("hi. more",(_,_)=>0)[0]);
        Assert.Equal([new EnbRetailAvatarSpeechCommand("wave","",-1)],parser.Parse("/wave hello. more",(_,_)=>10));
    }
    [Fact]
    public void ActionWhoseAnimationByteIsZeroIsSkipped()
    {
        // 00784700 cases 1/2 skip a record whose action byte is zero and continue with the next.
        string personality="[HEADER]\nSpeechDelay=1\n[BANIM-0]\nTag=wave\nAnimNumber=0\n[BANIM-1]\nTag=nod\nAnimNumber=3";
        var parser=new EnbRetailAvatarSpeech(EnbRetailIniDocument.Parse(personality),EnbRetailIniDocument.Parse(""));
        Assert.Equal([new EnbRetailAvatarSpeechCommand("nod","",-1)],parser.Parse("/wave /nod",(_,_)=>10));
    }
    [Theory]
    [InlineData("yes","alone")]
    [InlineData("yes later","begin")]
    [InlineData("before yes later","middle")]
    [InlineData("before yes","end")]
    [InlineData("/happy yes","end")]
    public void NativeContextsUseOriginalInputStart(string text,string expected)
    {Assert.Contains(Parser().Parse(text,(_,_)=>10),c=>c.AnimationTag==expected);}
    [Fact]
    public void CommandsSplitPunctuationAndCarryMoodToNextAction()
    {
        var commands=Parser().Parse("/happy /wave yes, plain! /unknown",(_,_)=>10);
        Assert.Equal(new("wave","",2),commands[0]);
        Assert.Equal("end",commands[1].AnimationTag);
        Assert.Contains(commands,c=>c.Text.EndsWith("!"));
        Assert.EndsWith("/unknown",commands[^1].Text);
        Assert.Equal(new("breathe1","",2),Parser().Parse("/happy").Single());
        Assert.DoesNotContain(Parser().Parse("yesterday"),c=>c.AnimationTag=="alone");
        Assert.Contains(Parser().Parse("yesDAY"),c=>c.AnimationTag=="alone"); // native original-byte suffix test
    }
    [Theory]
    [InlineData(6,"talk1")][InlineData(7,"talk2")][InlineData(15,"talk3")]
    [InlineData(30,"talk4")][InlineData(45,"talk5")][InlineData(600,"talk5")]
    public void TalkThresholdsAndInclusiveChoice(int frames,string expected)
    {Assert.Equal(expected,Parser().Talk(frames,(_,_)=>2));Assert.Equal("idle_"+expected,Parser().Talk(frames,(_,_)=>3));}
    [Fact]
    public void ParserPublishesFinalMoodAndExplicitTerminalAction()
    {
        var parser=Parser();
        var commands=parser.Parse("before /happy /WAVE",(_,_)=>10,true);
        Assert.Equal(2,parser.FinalMood);
        Assert.Equal(-1,commands[0].Mood);
        Assert.Equal(new("WAVE","",2),commands[1]);
        Assert.Equal(new("breathe1","",-1),commands[^1]);
        Assert.Single(parser.Parse("/WAVE")); // Active narrator state 1.
        Assert.Equal(new("breathe1","",2),Assert.Single(parser.Parse("/happy")));
        parser.Parse("plain");Assert.Equal(-1,parser.FinalMood);
    }
    [Fact]
    public void EmptyTextActionUsesCommandBlinkRangeAndLoadedEmptyIniOverridesProcedural()
    {
        var ranges=new List<(int,int)>();
        int Random(int a,int b){ranges.Add((a,b));return a;}
        _=new EnbRetailAvatarFacialMap(60,3,60,0,"",null,0,Random,commandActive:true);
        Assert.Equal((0,1),ranges[1]);
        ranges.Clear();
        var authored=new EnbRetailAvatarFacialMap(60,3,60,2,"",EnbRetailIniDocument.Parse(""),4,Random);
        Assert.Empty(ranges);Assert.Equal(4,authored.Sample(0).Brow);Assert.Equal(10,authored.Sample(59).Brow);
    }
    [Fact]
    public void AuthoredMapsStopAtMissingIndexAndReturnToOwnerMood()
    {
        var ini=EnbRetailIniDocument.Parse("""
            [Index-0000]
            Frame=0
            Brow=Close1
            [Index-0001]
            Frame=5
            Mood=Happy
            Brow=Wink
            Eye=Fullright
            [Index-0003]
            Frame=10
            Brow=Open1
            """);
        var map=new EnbRetailAvatarFacialMap(21,2,30,1,"a",ini,3);
        var sampled=map.Sample(5);
        Assert.Equal((17.25f,14f,4f),(sampled.Mouth,sampled.Brow,sampled.Eye));
        Assert.Equal(new EnbRetailAvatarFaceKeys(3,14,.5f),map.Sample(2.5f).BrowKeys);
        Assert.Equal(new EnbRetailAvatarFaceKeys(0,4,.5f),map.Sample(2.5f).EyeKeys);
        Assert.Equal(8.5f,map.Sample(2.5f).Brow);
        Assert.Equal(5,map.Sample(20).Brow);Assert.Equal(0,map.Sample(20).Eye);
        Assert.Equal(17,map.Sample(100).Mouth);
        Assert.Equal(0,new EnbRetailAvatarFacialMap(21,2,30,3,"",ini).Sample(100).Mouth);
    }
    [Fact]
    public void ProceduralEyeAndBlinkMapsUseNativeOrderedRanges()
    {
        var ranges=new List<(int,int)>();
        int Random(int a,int b){ranges.Add((a,b));return Math.Min(a,b);}
        var map=new EnbRetailAvatarFacialMap(60,1,30,2,"hello",null,0,Random);
        Assert.Equal(new[]{(1,2),(0,1),(2,55),(0,1),(2,15),(1,54)},ranges);
        Assert.Equal(11,map.Sample(4).Brow);Assert.Equal(1,map.Sample(3).Eye);
        Assert.Equal(10,map.Sample(59).Brow);Assert.Equal(0,map.Sample(59).Eye);
    }
    [Fact]
    public void HeadUsesTwentyHzAndCountsCallsRatherThanTicks()
    {
        var head=new EnbRetailAvatarHeadMotion();int draws=0;
        int Random(int a,int b){draws++;return b;}
        head.Advance(.025f,Random);Assert.Equal(0u,head.Calls);Assert.Equal(Matrix4x4.Identity,head.Matrix);
        head.Advance(.025f,Random);Assert.Equal(1u,head.Calls);Assert.Equal(.5f,head.Yaw);Assert.Equal(.25f,head.Pitch);Assert.Equal(3,draws);
        head.Advance(.25f,Random);Assert.Equal(2u,head.Calls);
        float yaw=.5f;for(int i=0;i<5;i++)yaw=.95f*yaw+.5f;
        Assert.Equal(yaw,head.Yaw);Assert.Equal(3,draws);
        Assert.InRange(MathF.Abs(head.Matrix.GetDeterminant()-1),0,.000001f);
        Assert.True(head.Matrix.M13<0);Assert.True(head.Matrix.M12>0);
        for(int i=2;i<30;i++)head.Advance(.05f,Random);
        Assert.Equal(3,draws);head.Advance(.05f,Random);Assert.Equal(6,draws);
        Assert.Equal(60u,head.NextTarget);
        var look=new EnbRetailAvatarHeadMotion();look.Advance(.05f,(_,_)=>0,10,-5);
        Assert.Equal(.5f,look.Yaw);Assert.Equal(-.25f,look.Pitch);
        Assert.InRange(look.Matrix.M12,.0087264f,.0087266f);
        Assert.InRange(look.Matrix.M13,.0043632f,.0043634f);
    }
}
