using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Assets;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private readonly Dictionary<string,EnbAttachedTextStyle> _dialogueStyles=[];
    private readonly Dictionary<string,EnbAttachedLabelScroll> _dialogueScroll=[];
    private readonly Dictionary<string,EnbAttachedLabelWipe> _dialogueWipe=[];
    private readonly Dictionary<string,float> _dialogueToggleSpeeds=[];
    private readonly Dictionary<string,EnbAttachedLabelToggle> _dialogueToggle=[];
    private readonly HashSet<string> _dialogueDrawn=[];
    private object? _dialogueTree;
    private bool _dialogueDone,_dialogueHasText;
    private double _dialogueWrapWidth,_answerWrapWidth;
    private string _dialogueTextAsset="";

    private void LoadDialogueText(EnbAuthoredUiAsset asset,string assetName)
    {
        _dialogueTextAsset=assetName;
        _dialogueStyles.Clear();_dialogueScroll.Clear();_dialogueWipe.Clear();_dialogueToggleSpeeds.Clear();_dialogueToggle.Clear();
        var effects=EnbRetailIniDocument.Load(Path.Combine(_assets!.Paths.ClientIniDirectory,"fontart.ini"));
        foreach(string name in Enumerable.Range(0,6).Select(i=>$"TALK{i:00}")
            .Concat(Enumerable.Range(0,5).Select(i=>$"ANSWER{i:00}"))
            .Concat(new[]{"NAME","REPLY","MORE","TRADE","SKILL"}))
            if(_conversationLayouts["talk"].TryResolve(name+"_"+assetName,out var definition))
            {
                _dialogueStyles[name]=EnbAttachedTextStyle.Load(_assets!,asset,definition,Matrix4x4.Identity);
                foreach(var effect in effects.EnumerateSections().Select(s=>s.Value))
                    if(effect.GetValueOrDefault("EffectID")==definition.Properties.GetValueOrDefault("MouseOver") && effect.GetValueOrDefault("EffectType")=="TOGGLE")
                        _dialogueToggleSpeeds[name]=float.Parse(effect["Speed"],System.Globalization.CultureInfo.InvariantCulture);
            }
        // 0064C1CD/0064C235 use the authored mesh half-width, not the text clip.
        double Width(string name)
        {
            var mesh=asset.Resource.Meshes.First(m=>m.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            float width=(mesh.BoundingBoxMax.X-mesh.BoundingBoxMin.X)*.95f;
            return assetName=="tli01a_av"?width:(int)width;
        }
        _dialogueWrapWidth=Width("TALK00");_answerWrapWidth=Width("ANSWER00");
    }

    private string[] DialogueLines(string text,bool answer=false)
    {
        string name=answer?"ANSWER00":"TALK00";
        if(!_dialogueStyles.TryGetValue(name,out var style)||Font(style.Font) is not {} font)return [text];
        double unit=font.Scale*640/_size.Width;
        return EnbWrappedLabel.Lines(text,answer?_answerWrapWidth:_dialogueWrapWidth,c=>font.CharacterAdvance(c)*unit);
    }

    private void DialogueText(string name,string id,string text)
    {
        if(!_dialogueStyles.TryGetValue(name,out var style)||
            !_conversationLayouts["talk"].TryResolve(name+"_"+_dialogueTextAsset,out var definition))return;
        var color=_conversationColors[definition.Properties["Color"]];
        if(_dialogueToggleSpeeds.TryGetValue(name,out float speed))
        {
            if(!_dialogueToggle.TryGetValue(name,out var toggle))_dialogueToggle[name]=toggle=new();
            var alternate=_conversationColors[definition.Properties["AltColor"]];
            color=toggle.Update(Environment.TickCount64,_hover==id,color,alternate,speed);
            _animatingText|=_hover==id;
        }
        _dialogueDrawn.Add(name);
        if(!_dialogueScroll.TryGetValue(name,out var scroll))_dialogueScroll[name]=scroll=new();
        if(!_dialogueWipe.TryGetValue(name,out var wipe))_dialogueWipe[name]=wipe=new();
        AttachedText(id,text,style,scroll,color,wipe);
    }

    private async Task ContinueDialogue()
    {
        if(_conversationWaiting||_conversationOwner is not {} owner||Conversation is not {Tree:{Responses.Count:0}} c||ReplyRequested is not {} reply)return;
        _conversationWaiting=true;
        await Send(()=>reply(new(owner,c.Revision,0)));
    }
}
