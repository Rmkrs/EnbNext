using System.Collections.Immutable;

namespace EnbNext.Net7.Social;

// The packet handler addresses retained MainView widgets, not current Aux slots.
public sealed record Net7GroupAttentionRecipient(long Lifetime, uint GameId, bool HasWidget);
public sealed record Net7GroupAttentionPresentation(Guid Stream, long ConsumedSequence,
    bool FormationWidget, ImmutableArray<Net7GroupAttentionRecipient> Members,Guid Owner=default);
public sealed record Net7GroupAttentionEvent(long Sequence, uint Request, long Recipient,Guid Owner=default);

public sealed partial class Net7SocialState
{
    public Guid GroupAttentionStream {get;private set;}=Guid.NewGuid();
    private long _groupAttentionSequence;
    private readonly List<Net7GroupAttentionEvent> _groupAttentionEvents=[];
    public IReadOnlyList<Net7GroupAttentionEvent> GroupAttentionEvents=>_groupAttentionEvents;
    private Net7GroupAttentionPresentation? _groupAttentionPresentation;

    // Called under the session's world-state lock, like packet dispatch. The
    // acknowledgement prunes delivered events without a time/count cutoff.
    public void PublishGroupAttention(Net7GroupAttentionPresentation presentation)
    {
        if(presentation.Stream!=GroupAttentionStream)return;
        _groupAttentionPresentation=presentation;
        _groupAttentionEvents.RemoveAll(e=>e.Sequence<=presentation.ConsumedSequence);
    }
    private void ObserveGroupAttention(Net7CtaResponseUpdate update,bool leader)
    {
        if(_groupAttentionPresentation is not {} owner)return;
        switch(update.RequestType)
        {
            case 14 when !leader:
                _groupAttentionEvents.Add(new(++_groupAttentionSequence,14,owner.FormationWidget?1:0,owner.Owner));
                break;
            case 15 when !leader:
                // Even without the formation widget, 598200 closes its menu.
                _groupAttentionEvents.Add(new(++_groupAttentionSequence,15,owner.FormationWidget?1:0,owner.Owner));
                break;
            case 17:
                // 5982A0 stops at the first ID, even if that widget is absent.
                var member=owner.Members.FirstOrDefault(m=>m.GameId==update.AvatarId);
                if(member is {HasWidget:true})
                    _groupAttentionEvents.Add(new(++_groupAttentionSequence,17,member.Lifetime,owner.Owner));
                break;
        }
    }
    private void ResetGroupAttention()
    {
        GroupAttentionStream=Guid.NewGuid();_groupAttentionSequence=0;
        _groupAttentionPresentation=null;_groupAttentionEvents.Clear();
    }
}
