using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbRslSoundAdmissionTests
{
    [Fact]
    public void RslAcquisitionSharesDefinitionDeadlineAndRetainsExistingHandles()
    {
        uint now=1000;
        var scope=new LiveWorldOpenAlAudio.SpeechScope();
        scope.Calls.Clock=()=>now;
        var renderer=new EnbWorldRenderer(()=>null,()=>null,()=>null,enableAudio:false);
        renderer.UseSpeechScope(scope);
        int choices=0;
        renderer.RslSoundChoiceSample=()=>{choices++;return 0;};
        renderer.RslSoundModifierSample=()=>0.5f;
        var definition=EnbRetailSoundCatalog.ParseText("[voice]\nSoundFilename=voice.wav\nSoundSecsBetweenCalls=4").GetDefinitionOrFile("voice");
        var obj=new EnbRenderObjectState {RenderIdentity=1};
        var first=renderer.SelectRslSound(obj,1,definition);
        Assert.NotNull(first.Definition);
        Assert.Equal(0f,first.Definition.EffectiveSecondsBetweenCalls);
        now=2000;
        Assert.Same(first,renderer.SelectRslSound(obj,1,definition));
        Assert.Null(renderer.SelectRslSound(obj,2,definition).Definition);
        Assert.False(scope.Calls.TryCall(definition));
        now=5000;
        Assert.NotNull(renderer.SelectRslSound(obj,3,definition).Definition);
        Assert.Null(renderer.SelectRslSound(obj,2,definition).Definition);
        Assert.False(scope.Calls.TryCall(definition));
    }
}
