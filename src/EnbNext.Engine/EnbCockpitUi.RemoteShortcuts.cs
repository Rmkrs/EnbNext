using EnbNext.Presentation;
using EnbNext.Assets;
using EnbNext.Rendering;
namespace EnbNext.Engine;

internal sealed partial class EnbCockpitUi
{
    public EnbRemoteShortcut[] CaptureRemoteShortcuts(EnbRemoteShortcut[]? previous=null)
    {
        if(_shortcuts is null||!_interactive)return [];
        var result=new List<EnbRemoteShortcut>();
        foreach(var slot in _shortcuts.Slots)
        {
            if(slot.Resolution!=EnbSceneShortcutResolution.Resolved)continue;
            var cached=previous?.FirstOrDefault(s=>s.Slot==EnbRemoteShortcut.Index(slot.Address)&&s.Name==(slot.Name??""));
            string? icon=cached?.Icon??slot.IconResource;
            if(icon is null&&slot.IconBaseAsset is int id)icon=_assets?.PresentationCatalog?.ResolveBaseAsset(id).Properties?.GetValueOrDefault("Icon");
            var feedback=_weaponFeedback.TryGetValue(slot.Address,out var current)?current:new EnbNext.Rendering.EnbRetailWeaponFeedback(-1,0,0,false,false);
            // Remote bindings include the hidden bank. Re-evaluate its presentation
            // against the alt's current target/time without running sound/input callbacks.
            if(slot.SkillFeedback is {} state && slot.AbilityId is int ability)
                feedback=EnbRetailSkillFeedback.Evaluate(state,ability,_combatTarget?.SurfaceDistance,unchecked((uint)_combatTime),feedback.Angle,0).Range;
            else if(slot.Assignment is {Source:EnbRetailShortcutSource.Equipment,SavedIndex:{} index} && _inventory?.Find(2,index) is {State:{} itemState} item)
                feedback=EnbRetailWeaponFeedback.UsesTargetRange(item.ItemType)
                    ?EnbRetailWeaponFeedback.Evaluate(itemState,_combatTarget?.SurfaceDistance,item.TargetRange,item.ReadyTime,_combatTime,_combatTarget?.ObjectType==0,feedback.Angle,0,feedback.Cue)
                    :EnbRetailWeaponFeedback.UsesEffectRange(item.ItemType)
                        ?EnbRetailWeaponFeedback.EvaluateEffect(itemState,_combatTarget?.SurfaceDistance,item.EffectRange,item.EffectUsage,item.EffectTargets,item.EffectValidity,item.ReadyTime,_combatTime,_combatTarget?.ObjectType==0,feedback.Angle,0,feedback.RangeValue,feedback.Cue)
                        :feedback;
            bool usable=EnbShortcutSkillActivation.Capture(_shortcuts,slot,_shortcutShipOwner) is {} skill
                ?EnbShortcutSkillActivation.TryResolve(skill,_shortcuts,_shortcutShipOwner,out _)
                :EnbShortcutEquipmentActivation.Capture(_shortcuts,slot,_shortcutShipOwner) is {} equipment&&EnbShortcutEquipmentActivation.TryResolve(equipment,_shortcuts,_shortcutShipOwner,null,out _);
            int frame=feedback.Frame<0?-1:feedback.Frame==15?15:0;
            if(cached is not null)
                result.Add(cached.Usable==usable&&cached.Cooling==feedback.Cooling&&cached.Active==feedback.Active&&cached.RangeFrame==frame&&cached.RangeAngle==0
                    ?cached:cached with {Usable=usable,Cooling=feedback.Cooling,Active=feedback.Active,RangeFrame=frame,RangeAngle=0});
            else result.Add(new(EnbRemoteShortcut.Index(slot.Address),slot.Name??"",DescribeShortcut(slot)??slot.Name??"",icon,
                slot.IconTint.X,slot.IconTint.Y,slot.IconTint.Z,usable,feedback.Cooling,feedback.Active){RangeFrame=frame,RangeAngle=0});
        }
        return result.ToArray();
    }
}
