using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Collections.Immutable;
using EnbNext.Net7.Social;
using EnbNext.Presentation;

namespace EnbNext.Engine;
internal sealed partial class EnbInventoryUi
{
    public Action<Net7GroupAttentionPresentation>? GroupAttentionPublished {get;set;}
    private Guid _groupAttentionStream;
    private long _groupAttentionSequence;
    private void ReceiveGroupAttention(EnbSceneGroupSnapshot group)
    {
        if(_groupAttentionStream!=group.AttentionStream)
        {
            _groupAttentionStream=group.AttentionStream;_groupAttentionSequence=0;
            _spaceGroupVisual.FormationAttention.Clear();_spaceGroupVisual.MemberAttention.Clear();
        }
        var members=StationGroup?_spaceGroupVisual.Members:_groupMembers;
        foreach(var e in group.AttentionEvents.Where(e=>e.Sequence>_groupAttentionSequence))
        {
            _groupAttentionSequence=e.Sequence;
            if(e.Owner!=_spaceGroupVisual.Identity)continue;
            switch(e.Request)
            {
                case 14:_spaceGroupVisual.FormationAttention.Clear();_groupFormationOpen=false;break;
                case 15:
                    if(e.Recipient!=0)_spaceGroupVisual.FormationAttention.Request();
                    _groupFormationOpen=false;break;
                case 17:
                    if(!members.Items.Any(m=>m.PresentationLifetime==e.Recipient))break;
                    if(!_spaceGroupVisual.MemberAttention.TryGetValue(e.Recipient,out var flash))
                        _spaceGroupVisual.MemberAttention[e.Recipient]=flash=new();
                    flash.Request();break;
            }
        }
    }
    private void RebuildGroupAttentionWidgets()
    {
        if(StationGroup)return;
        foreach(var flash in _spaceGroupVisual.MemberAttention.Values)flash.RebuildWidget();
    }
    private void AdvanceGroupAttention(uint now)
    {
        if(StationGroup)return;
        uint elapsed=unchecked(now-_spaceGroupVisual.AttentionTime);_spaceGroupVisual.AttentionTime=now;
        _spaceGroupVisual.FormationAttention.Advance(elapsed);
        foreach(var (id,flash) in _spaceGroupVisual.MemberAttention.ToArray())
        {
            if(!_groupMembers.Items.Any(m=>m.PresentationLifetime==id))_spaceGroupVisual.MemberAttention.Remove(id);
            else flash.Advance(elapsed);
        }
    }
    private void ApplyGroupAttention(EnbGroupAttentionFlash flash,string hoverId,uint now)
    {
        if(StationGroup || !flash.Forced || _draws.Count==0)return;
        flash.UpdateMaterial(_hover==hoverId,now);
        _draws[^1]=_draws[^1] with {Hovered=true,HighlightEmissive=flash.Emissive,SuppressHoverEvent=_hover!=hoverId};
        _animatingText=true;
    }
    private void PublishGroupAttention()
    {
        if(Group is not {} group)return;
        var members=StationGroup?_spaceGroupVisual.Members:_groupMembers;
        GroupAttentionPublished?.Invoke(new(group.AttentionStream,_groupAttentionSequence,
            FormationArt("tsi03a_form0") is not null,members.Items.Select(m=>new Net7GroupAttentionRecipient(
                m.PresentationLifetime,m.GameId,_art.ContainsKey("groupRow/P_INTARGET01"))).ToImmutableArray(),_spaceGroupVisual.Identity));
    }
}
