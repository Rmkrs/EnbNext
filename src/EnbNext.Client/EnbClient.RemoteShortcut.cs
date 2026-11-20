using EnbNext.Presentation;
namespace EnbNext.Client;

public sealed partial class EnbClient
{
    public Task<bool> ActivateRemoteShortcutAsync(EnbMultiboxInput input,EnbMultiboxContext expected)
    {
        if(input.Action!="shortcut"||input.Target>=12||input.Id==Guid.Empty||_net7 is not {} session||RetailAssets is not {} assets)return Task.FromResult(false);
        return session.ReadPresentationFrame((world,_)=>
        {
            var group=session.PlayerSemantics.GetGroupState();
            if(!ReferenceEquals(session,_net7)||MultiboxContext!=expected||world.IsTransitioning||
                session.Gameplay.PresentationMode is not (0 or 1 or 5)||input.Context.Sector!=expected.Sector||
                !group.IsInGroup||group.IsGroupLeader||!group.Members.Any(m=>m.GameId==input.Context.Avatar)||
                world.Objects.FindFirst(expected.Ship)?.AuxData is not {} ship)return Task.FromResult(false);
            var current=EnbSceneShortcutSnapshotBuilder.Build(assets.ResolveShortcuts(session.FixedSessionId),assets.ResolveShortcutSkills(),ship,world.LocalPlayerAuxData,session.ItemTemplates);
            var slot=current.Slots.FirstOrDefault(s=>s.Address==EnbRemoteShortcut.Address((int)input.Target));
            if(EnbShortcutSkillActivation.Capture(current,slot,expected.Owner) is {} skill)return ActivateShortcutSkillAsync(skill);
            if(EnbShortcutEquipmentActivation.Capture(current,slot,expected.Owner) is {} equipment)return ActivateShortcutEquipmentAsync(equipment);
            return Task.FromResult(false);
        });
    }
}
