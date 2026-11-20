using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbCockpitUi
{
    public event Action<string>? CombatSoundRequested;
    public event Action<int,bool>? WeaponRangeSoundChanged;
    private EnbRadarContact? _combatTarget;
    private ulong _combatOwner, _combatLastTarget;
    private long _combatTime;
    private readonly Dictionary<EnbRetailShortcutAddress,(EnbRetailShortcutEntry Assignment,EnbRetailEquipmentNotification Notification)> _equipmentNotifications=[];
    private readonly Dictionary<EnbRetailShortcutAddress,EnbRetailWeaponFeedback> _weaponFeedback=[];
    private readonly Dictionary<EnbRetailShortcutAddress,EnbRetailShortcutEntry> _weaponIdentities=[];
    private (ulong,ulong) _equipmentFeedbackRevision, _skillFeedbackRevision;
    private static int RangeSoundSlot(EnbRetailShortcutAddress address)=>address.Bar*6+address.Group*3+address.Button;
    private ImmutableArray<EnbCombatIcon> _combatIcons=[];
    public void SetCombatTarget(EnbRadarContact? target) => _combatTarget=target;

    private void UpdateCombat(long now)
    {
        if(_combatOwner!=_shortcutShipOwner || !_interactive)
        {
            for(int i=0;i<12;i++)WeaponRangeSoundChanged?.Invoke(i,false);
            _weaponFeedback.Clear();_weaponIdentities.Clear();_equipmentNotifications.Clear();_combatLastTarget=0;
            _combatTime=now;_combatOwner=_shortcutShipOwner;
        }
        uint elapsed=unchecked((uint)(now-_combatTime));_combatTime=now;
        var icons=ImmutableArray.CreateBuilder<EnbCombatIcon>();
        Array.Clear(_modernFeedback);
        // 68E360 -> 43ED30 -> 43E360 -> 9EFEC0 allocates a voice per notification.
        var cues=new List<string>();
        if(!_interactive){_combatIcons=[];return;}
        ulong target=_combatTarget?.RenderIdentity??0;
        if(target!=_combatLastTarget && target!=0)
        {
            cues.Add("targety1.wav");
            // RefreshSelectedTargetUi 005127AC -> 00563910 -> 00564030:
            // selecting another player opens communications with these two cues.
            if(_combatTarget?.ObjectType==1 && target!=_combatOwner)
            {
                cues.Add("TextClick01.wav");
                cues.Add("vidscreen_1.wav");
            }
        }
        _combatLastTarget=target;
        // 611F60 retains each group's gadget; hiding a bank does not construct a new renderer.
        // Saved entry instances survive snapshot publication. A successful new assignment
        // replaces the entry, even for the same source slot (5A0740 -> 614360).
        foreach(var key in _equipmentNotifications.Keys.ToArray())
            if(_shortcuts?.Slots.FirstOrDefault(s=>s.Address==key)?.Assignment is not
                {Source:EnbRetailShortcutSource.Equipment,SavedIndex:{} } assignment ||
                !ReferenceEquals(assignment,_equipmentNotifications[key].Assignment))
                _equipmentNotifications.Remove(key);
        // 630490 subscribes the shortcut section to SlotData; 6309F0 refreshes
        // every retained gadget, including those not attached to the visible bank.
        foreach(var equipment in _shortcuts?.Slots ?? [])
        {
            if(equipment.Resolution!=EnbSceneShortcutResolution.Resolved || equipment.Assignment is not
                {Source:EnbRetailShortcutSource.Equipment,SavedIndex:{} equipmentSource})continue;
            var equipmentItem=_inventory?.Find(2,equipmentSource);
            if(!_equipmentNotifications.TryGetValue(equipment.Address,out var notification))
                _equipmentNotifications[equipment.Address]=notification=(equipment.Assignment,new());
            if(notification.Notification.Update(equipmentItem?.ItemType??uint.MaxValue,equipmentItem?.State??0) is {} cue)cues.Add(cue);
        }
        bool equipmentChanged=_shortcuts?.EquipmentRevision!=_equipmentFeedbackRevision;
        bool skillsChanged=_shortcuts?.SkillRevision!=_skillFeedbackRevision;
        _equipmentFeedbackRevision=_shortcuts?.EquipmentRevision??default;
        _skillFeedbackRevision=_shortcuts?.SkillRevision??default;
        foreach(var key in _weaponIdentities.Keys.ToArray())
            if(!ReferenceEquals(_shortcuts?.Slots.FirstOrDefault(s=>s.Address==key)?.Assignment,_weaponIdentities[key]))
            {
                WeaponRangeSoundChanged?.Invoke(RangeSoundSlot(key),false);
                _weaponFeedback.Remove(key);_weaponIdentities.Remove(key);
            }
        foreach(var slot in _shortcuts?.Slots ?? [])
        {
            if(slot.Assignment is not {SavedIndex:{} source} assignment ||
                slot.Resolution!=EnbSceneShortcutResolution.Resolved)continue;
            int bar=slot.Address.Bar,button=slot.Address.Button;
            bool visible=Shown(slot.Address);
            bool constructed=!_weaponIdentities.ContainsKey(slot.Address);
            // 611F60/66B690 only detach the proxy: hidden owners retain their state.
            // 6309F0 refreshes them on their SlotData notification, not target motion.
            if(!visible && !constructed && !(assignment.Source==EnbRetailShortcutSource.Equipment?equipmentChanged:
                assignment.Source==EnbRetailShortcutSource.Skill&&skillsChanged))continue;
            EnbRetailWeaponFeedback feedback;
            EnbRetailSkillFeedback? skill = null;
            _weaponFeedback.TryGetValue(slot.Address,out var previous);
            var item = assignment.Source==EnbRetailShortcutSource.Equipment ? _inventory?.Find(2,source)
                : assignment.Source==EnbRetailShortcutSource.Cargo ? _inventory?.Find(1,source) : null;
            if(constructed){previous=default;WeaponRangeSoundChanged?.Invoke(RangeSoundSlot(slot.Address),false);}
            if(assignment.Source==EnbRetailShortcutSource.Skill && slot.SkillFeedback is {} stateSkill && slot.AbilityId is int ability)
            {
                skill=EnbRetailSkillFeedback.Evaluate(stateSkill,ability,_combatTarget?.SurfaceDistance,
                    unchecked((uint)now),previous.Angle,elapsed);
                feedback=skill.Value.Range;
            }
            else if(assignment.Source==EnbRetailShortcutSource.Equipment && item is {State:{} state} &&
                (EnbRetailWeaponFeedback.UsesTargetRange(item.ItemType)||EnbRetailWeaponFeedback.UsesEffectRange(item.ItemType)))
                feedback=EnbRetailWeaponFeedback.UsesTargetRange(item.ItemType)
                    ?EnbRetailWeaponFeedback.Evaluate(state,_combatTarget?.SurfaceDistance,item.TargetRange,
                        item.ReadyTime,now,_combatTarget?.ObjectType==0,previous.Angle,elapsed,previous.Cue)
                    :EnbRetailWeaponFeedback.EvaluateEffect(state,_combatTarget?.SurfaceDistance,item.EffectRange,item.EffectUsage,item.EffectTargets,item.EffectValidity,
                        item.ReadyTime,now,_combatTarget?.ObjectType==0,previous.Angle,elapsed,previous.RangeValue,previous.Cue);
            else if(item is not null)feedback=new(-1,0,0,false,false);
            else continue;
            _weaponIdentities[slot.Address]=assignment;
            WeaponRangeSoundChanged?.Invoke(RangeSoundSlot(slot.Address),feedback.Cue==2);
            _weaponFeedback[slot.Address]=feedback;
            if(Modern)
            {
                int modernSlot=slot.Address.Group*6+bar*3+button;
                if(modernSlot<12)_modernFeedback[modernSlot]=new(modernSlot,default,feedback,skill?.Invalid ?? feedback.Invalid,skill?.WrongTarget ?? feedback.WrongTarget,skill?.Powerup,feedback.NoAmmo,
                    item is not null && _inventory is not null && EnbItemInformation.HasUnmetRequirements(item,_inventory));
            }
            if(!visible || _shortcutDragging && assignment==_shortcutDrag?.Source)continue;
            if(_shortcutAsset is {} asset)
            {
                var pivot=asset.Hierarchy.Pivots.First(p=>p.Name==$"BTTN_{bar}_0{button}");
                icons.Add(new(bar*3+button,_shortcutTransforms[pivot.Ordinal].Translation,feedback,
                    skill?.Invalid ?? feedback.Invalid,skill?.WrongTarget ?? feedback.WrongTarget,skill?.Powerup,feedback.NoAmmo,
                    item is not null && _inventory is not null && EnbItemInformation.HasUnmetRequirements(item,_inventory)));
            }
        }
        _combatIcons=icons.ToImmutable();
        foreach(string cue in cues)CombatSoundRequested?.Invoke(cue);
    }

    private void ArmEquipmentNotification(EnbShortcutEquipmentRequest? request)
    {
        if(request is null)return;
        var address=request.Assignment.Address;
        if(!_equipmentNotifications.TryGetValue(address,out var notification)||!ReferenceEquals(notification.Assignment,request.Assignment))
            _equipmentNotifications[address]=notification=(request.Assignment,new());
        notification.Notification.Arm();
    }

    private void ResetShortcutFeedback(EnbRetailShortcutAddress address)
    {
        WeaponRangeSoundChanged?.Invoke(RangeSoundSlot(address),false);
        _equipmentNotifications.Remove(address);
        _weaponFeedback.Remove(address);
        _weaponIdentities.Remove(address);
    }

    private string? EquipmentCombatTooltip(EnbSceneItemSnapshot item,float retainedRange)
    {
        string text=ItemTooltipDescription(item)+EnbRetailEquipmentTooltip.Suffix(
            _combatTarget?.SurfaceDistance??-1,retainedRange,EnbRetailEquipmentTooltip.Remaining(item.ReadyTime,_combatTime));
        // All action renderers use this template, including non-weapon equipment (68F510).
        return SlotTooltip("WEAPON_ON",text);
    }
}
