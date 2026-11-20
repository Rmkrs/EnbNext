using EnbNext.Client;
using EnbNext.Engine;
using EnbNext.Host.Sdl;
using EnbNext.Presentation;
using SDL;
using System.Security.Cryptography;
using System.Text;
namespace EnbNext.App;
internal sealed partial class SdlApplication
{
    private EnbCombatMeter combatMeter=new();
    private EnbDamageMeterPanel? damagePanel;
    private long combatNext;
    private Task combatPoll=Task.CompletedTask;
    private uint combatTarget,combatSector;
    private uint? combatCharacter;
    private bool combatWatching;
    private object? damageToggleOwner;
    /// <summary>The Modern Local damage columns' own record of this character's hits (never group or multibox numbers).</summary>
    private EnbLocalCombatLedger? localCombat;
    private long localCombatNext;
    private const int LocalFightPauseSeconds=30;
    private static Guid CombatIdentity(string server,string name)=>new(SHA256.HashData(Encoding.UTF8.GetBytes((server+"/"+name).ToUpperInvariant())).AsSpan(0,16));
    private void TickCombatMeter()
    {
        var client=_client;
        var inventory=worldScreen?.Owner.Ui.Inventory;
        bool live=worldScreen is not null&&client is not null&&client.State is not (ClientLifecycleState.Disconnected or ClientLifecycleState.Disconnecting or ClientLifecycleState.Faulted or ClientLifecycleState.CharacterSelection);
        bool localColumns=live&&EffectiveQualityOfLife.ModernUi&&inventory is {ModernLocalOpen:true}&&(inventory.ModernLocalColumns&EnbInventoryUi.LocalDamageColumns)!=0;
        bool collect=live&&(multiboxOptions.Enabled||multiboxOptions.DamageMeterVisible||combatCharacter is not null||localColumns);
        client?.CollectCombat(collect);
        if(localColumns)
        {
            long at=Environment.TickCount64;
            if(at>=localCombatNext)
            {
                localCombatNext=at+250;
                // Local keeps a fight going for at least 30 s without a hit (the meter's own pause if longer): a mob or a
                // weapon landing only now and then stays in one fight.
                localCombat??=new();localCombat.PauseSeconds=Math.Max(LocalFightPauseSeconds,multiboxOptions.EncounterPauseSeconds);inventory!.ModernLocalCombat=localCombat;
                Guid localJournal=client!.CombatJournalId;
                for(int read=0;read<16;read++){var batch=client.ReadCombat(localJournal,localCombat.Cursor(localJournal));localCombat.Accept(batch);if(batch.Hits.Length<128)break;}
            }
        }
        if(worldScreen?.Owner.Ui.Cockpit is {} cockpit)
        {
            if(!ReferenceEquals(damageToggleOwner,cockpit)){damageToggleOwner=cockpit;cockpit.DamageMeterToggleRequested+=()=>SaveMultiboxOptions(multiboxOptions with {ShowDamageMeter=!multiboxOptions.ShowDamageMeter});}
            cockpit.DamageMeterButtonEnabled=multiboxOptions.EnableDamageMeter&&multiboxCharacter is not null;cockpit.DamageMeterShown=multiboxOptions.ShowDamageMeter;
        }
        if(multibox is {} bus){if(collect&&bus.CombatRead is null){bus.CombatRead=client!.ReadCombat;bus.CombatTargetLife=client.CombatTargetLife;}else if(!collect){bus.CombatRead=null;bus.CombatTargetLife=null;}}
        bool preview=worldScreen?.Owner.Ui.Layout.Editing==true&&client?.RetailAssets is not null;
        if(preview)
        {
            damagePanel??=new(client!.RetailAssets!);
            damagePanel.Layout=worldScreen!.Owner.Ui.Layout;worldScreen.Owner.Hud.DamagePanel=damagePanel;
        }
        if(!collect)
        {
            if(!preview){if(worldScreen is {} world)world.Owner.Hud.DamagePanel=null;damagePanel?.Dispose();damagePanel=null;}
            if(combatCharacter is not null){combatCharacter=null;combatMeter=new();combatWatching=false;}
            return;
        }
        if(combatCharacter!=multiboxCharacter){combatCharacter=multiboxCharacter;combatMeter=new();combatWatching=false;}
        var scene=client!.LatestPresentationSnapshot;if(scene is null)return;
        uint target=scene.Hud.LocalSelectedTargetId??0,sector=scene.Environment?.SectorNum??0;
        if(target!=combatTarget||sector!=combatSector){combatTarget=target;combatSector=sector;combatMeter.ClearTargets();combatNext=0;}
        if(multiboxOptions.DamageMeterVisible)
        {
            if(!combatWatching){combatWatching=true;combatMeter.Reset(Environment.TickCount64);}
            damagePanel??=new(client.RetailAssets!);
            damagePanel.Layout=worldScreen!.Owner.Ui.Layout;worldScreen.Owner.Hud.DamagePanel=damagePanel;
        }
        else if(!preview){worldScreen!.Owner.Hud.DamagePanel=null;damagePanel?.Dispose();damagePanel=null;}
        if(!combatWatching)return;
        long now=Environment.TickCount64;if(now<combatNext)return;combatNext=now+250;
        combatMeter.PauseSeconds=multiboxOptions.EncounterPauseSeconds;
        Guid journal=client.CombatJournalId;
        Guid self=CombatIdentity(_savedAccountServer??"",_selectedCharacter?.FullName??"");
        combatMeter.Accept(self,_selectedCharacter?.FullName??"",client.ReadCombat(journal,combatMeter.Cursor(self,journal),combatMeter.Since));
        var localTarget=client.CombatTargetLife(target);combatMeter.SetTarget(self,localTarget.Life,localTarget.Epoch);
        var group=scene.Hud.Group;
        var peers=multibox?.Peers.Where(p=>p.Server==(_savedAccountServer??"")&&group?.Members.Any(m=>m.GameId==p.Avatar)==true).ToArray()??[];
        if(combatPoll.IsCompleted&&peers.Length>0){combatPoll=PollCombat(peers,target,sector,combatMeter);dispatch.Observe(combatPoll);}
        combatMeter.Advance(now,peers.Select(p=>CombatIdentity(p.Server,p.Name)).Prepend(self).ToArray());
        damagePanel?.Update(combatMeter,combatSector,now);
    }
    private async Task PollCombat(MultiboxState[] peers,uint target,uint sector,EnbCombatMeter owner)
    {
        if(multibox is not {} bus)return;
        foreach(var peer in peers)
        {
            Guid identity=CombatIdentity(peer.Server,peer.Name);
            Guid journal=owner.Journal(identity);
            var reply=await bus.ReadCombatAsync(peer,journal,owner.Cursor(identity,journal),target,sector,owner.Since);
            if(!ReferenceEquals(owner,combatMeter)||!ReferenceEquals(bus,multibox))return;
            if(reply?.Combat is not {} batch)continue;
            owner.Accept(identity,peer.Name,batch);
            if(target==combatTarget&&sector==combatSector)owner.SetTarget(identity,reply.CombatTargetLife.Life,reply.CombatTargetLife.Epoch);
        }
    }
    // Game windows have precedence over the multibox and damage meter panels.
    private static bool PointerUnderGameUi(EnbNext.Host.Sdl.SdlWorldScreen world,SDL_Event e)
    {
        var type=(SDL_EventType)e.type;
        EnbUiPoint? point=type switch
        {
            SDL_EventType.SDL_EVENT_MOUSE_MOTION=>new(e.motion.x,e.motion.y),
            SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP=>new(e.button.x,e.button.y),
            SDL_EventType.SDL_EVENT_MOUSE_WHEEL=>new(e.wheel.mouse_x,e.wheel.mouse_y),
            _=>null
        };
        return point is {} p && world.Owner.Ui.GameUiCovers(p);
    }
    private bool HandleDamagePanel(SDL_Event e)
    {
        if(damagePanel is not {} panel||worldScreen is not {} world||world.Owner.Ui.Layout.Editing)return false;
        var type=(SDL_EventType)e.type;
        if(type is SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST or SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED){panel.EndDrag();world.Capture.Release(SdlPointerOwner.DamagePanel);return false;}
        if(!panel.Dragging&&PointerUnderGameUi(world,e)){if(type==SDL_EventType.SDL_EVENT_MOUSE_MOTION)panel.Pointer(new(-100000,-100000));return false;}
        if(type==SDL_EventType.SDL_EVENT_MOUSE_MOTION){var p=world.Owner.Ui.Layout.Inverse(EnbUiGroup.DamageMeter,new(e.motion.x,e.motion.y));panel.Pointer(p);return panel.Dragging||panel.Contains(p);}
        // Next Modern UI: the meter's title drags it.
        if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP&&e.button.button==1&&panel.Dragging){panel.EndDrag();world.Capture.Release(SdlPointerOwner.DamagePanel);return true;}
        if(type is SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP)
        {
            var p=world.Owner.Ui.Layout.Inverse(EnbUiGroup.DamageMeter,new(e.button.x,e.button.y));
            bool handled=type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN?(e.button.button==1?panel.Click(p):e.button.button==3?panel.RightClick(p):panel.Contains(p)):panel.Contains(p);
            if(panel.Dragging)world.Capture.Acquire(SdlPointerOwner.DamagePanel);
            return handled;
        }
        if(type==SDL_EventType.SDL_EVENT_MOUSE_WHEEL)return panel.Contains(world.Owner.Ui.Layout.Inverse(EnbUiGroup.DamageMeter,new(e.wheel.mouse_x,e.wheel.mouse_y)));
        return false;
    }
}
