using System.Reflection;
using System.Buffers.Binary;
using EnbNext.Assets;
using System.Text;
using EnbNext.App;
using EnbNext.Client;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Protocol;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class MultiboxInputTests
{
    [Fact]
    public void AdmissionRequiresEachOptInLeaderMembershipAndBothLifetimes()
    {
        var source=new EnbMultiboxContext(1,8,8,12,4,3);
        var recipient=new EnbMultiboxContext(2,7,7,21,4,8);
        var input=new EnbMultiboxInput(Guid.NewGuid(),"target",99,source);
        var leader=new MultiboxState(Guid.NewGuid(),"test",Guid.NewGuid(),1,"Pilot",8,null,false,true,[7],"Sector",null,false){Context=source};
        var command=new MultiboxMessage(MultiboxPeer.Version,"command",leader.Instance,input.Id,"target",null,input,recipient);
        var options=new EnbMultiboxOptions(true,false,false,true,false);
        Assert.True(command.Allows(leader,recipient,"test",options));
        Assert.False(command.Allows(leader,recipient,"test",options with {Enabled=false}));
        Assert.False(command.Allows(leader,recipient,"test",options with {FollowLeaderTarget=false,FollowLeaderFire=true}));
        Assert.False(command.Allows(leader with {Leader=false},recipient,"test",options));
        Assert.False(command.Allows(leader with {GroupMembers=[]},recipient,"test",options));
        Assert.False(command.Allows(leader with {Context=source with {Owner=13}},recipient,"test",options));
        Assert.False(command.Allows(leader,recipient with {Transition=9},"test",options));
        Assert.False(command.Allows(leader,recipient with {Session=Guid.NewGuid()},"test",options));
        Assert.False(command.Allows(leader,recipient,"other",options));
        Assert.False((command with {Input=Guid.NewGuid()}).Allows(leader,recipient,"test",options));
        var fire=command with {Action="fire",Command=input with {Action="fire"}};
        Assert.False(fire.Allows(leader,recipient,"test",options));
        Assert.True(fire.Allows(leader,recipient,"test",options with {FollowLeaderFire=true,FollowLeaderTarget=false}));
    }

    [Fact]
    public async Task ReceivedInputsUseManualWirePathsWithoutEchoAndRejectChangedContext()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        await using var client=new EnbClient(new());
        var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        typeof(EnbClient).GetField("_net7",flags)!.SetValue(client,session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session,7u);
        EnbR11FlightTests.FlightWorld(session.World);
        typeof(Net7GameplayState).GetProperty("PresentationMode")!.SetValue(session.Gameplay,0);
        session.World.Apply(Packet.ForOpcode(0x0042,new byte[Net7PresentationWire.ServerParametersPayloadSize]),1000);
        session.World.MaterializeCreate(new Net7CreateUpdate(99,1,0,1,0,0,0));
        void Set(string path,object value)
        {var p=session.World.LocalPlayerAuxData.Properties.Single(p=>p.Path=="LocalPlayerAuxData.GroupInfo."+path);p.Value=value;p.State=2;p.UpdateRevision++;}
        Set("Members.0.Name",new Net7AuxDataString(Encoding.Latin1.GetBytes("Leader")));Set("Members.0.GameID",8);Set("IsGroupLeader",(byte)0);
        void Publish()=>typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",flags)!.Invoke(client,[1000u]);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);using var lifetime=receiver;
        Publish();var context=Assert.IsType<EnbMultiboxContext>(client.MultiboxContext);
        int inputs=0;client.MultiboxInput+=(_,_)=>inputs++;
        var frames=new List<(ushort Opcode,byte[] Payload)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        var input=new EnbMultiboxInput(Guid.NewGuid(),"target",99,context with {Avatar=8});
        // A formation name or slot alone does not establish participation.
        Set("Formation",new byte[4]);Set("FormationName",new Net7AuxDataString(Encoding.Latin1.GetBytes("Pipe")));
        Assert.False(await client.ApplyMultiboxInputAsync(input,context));
        Assert.False(await client.ApplyMultiboxInputAsync(input with {Action="fire"},context));
        Assert.Empty(frames);
        Set("Formation",BitConverter.GetBytes(1u));Publish();
        Assert.True(await client.ApplyMultiboxInputAsync(input,context));
        Assert.Equal(99u,session.Gameplay.LocalSelectedTargetId);
        Assert.Equal(new ushort[]{0x17,0x5a},frames.Select(f=>f.Opcode));
        Assert.Equal(Net7GameplayWire.BuildTargetRequest(7,99),frames[0].Payload);
        Assert.True(await client.ApplyMultiboxInputAsync(input with {Action="fire"},context));
        Assert.Equal(Net7GameplayWire.BuildAction(7,Net7PlayerAction.FireAllWeapons,7,0),frames[^1].Payload);
        Assert.Equal(0,inputs);
        frames.Clear();
        Assert.False(await client.ApplyMultiboxInputAsync(input with {Action="fire",Target=100},context,requireTarget:true));
        Assert.Empty(frames);
        Assert.True(await client.ApplyMultiboxInputAsync(input with {Target=0},context));
        Assert.Null(session.Gameplay.LocalSelectedTargetId);
        Assert.Equal(new ushort[]{0x5a,0x17},frames.Select(f=>f.Opcode));
        frames.Clear();
        Set("Formation",new byte[4]);Publish();
        Assert.False(await client.ApplyMultiboxInputAsync(input,context));
        Assert.False(await client.ApplyMultiboxInputAsync(input with {Action="fire"},context));
        Set("IsGroupLeader",(byte)1);Publish();Assert.False(client.CanLeadMultibox(context,8));
        Set("Formation",BitConverter.GetBytes(1u));Publish();Assert.True(client.CanLeadMultibox(context,8));
        typeof(Net7GameplayState).GetProperty("PresentationMode")!.SetValue(session.Gameplay,2);
        Assert.False(client.CanLeadMultibox(context,8));
        Set("IsGroupLeader",(byte)0);Publish();
        Assert.False(await client.ApplyMultiboxInputAsync(input,context));
        Assert.False(await client.ApplyMultiboxInputAsync(input with {Action="fire"},context));
        typeof(Net7GameplayState).GetProperty("PresentationMode")!.SetValue(session.Gameplay,0);
        Publish();
        Assert.False(await client.ApplyMultiboxInputAsync(input with {Target=100},context));
        Assert.False(await client.ApplyMultiboxInputAsync(input,context with {Owner=context.Owner+1}));
        Assert.False(await client.ApplyMultiboxInputAsync(input with {Context=input.Context with {Sector=1}},context));
        Set("IsGroupLeader",(byte)1);Publish();Assert.True(client.CanLeadMultibox(context,8));Assert.False(client.CanLeadMultibox(context,9));Assert.False(await client.ApplyMultiboxInputAsync(input,context));
        Set("IsGroupLeader",(byte)0);Set("Members.0.GameID",9);Publish();Assert.False(await client.ApplyMultiboxInputAsync(input,context));
        Assert.Empty(frames);Assert.Equal(0,inputs);
        Assert.True(await client.RequestTargetAsync(99));Assert.Equal(1,inputs);
        Assert.True(await client.FireAllWeaponsAsync(context.Ship,context.Owner));Assert.Equal(2,inputs);
    }

    [InstalledRetailFact]
    public async Task RemoteSlotResolvesCurrentAssignmentAndUsesManualEquipmentBytes()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        await using var client=new EnbClient(new());
        var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        typeof(EnbClient).GetField("_net7",flags)!.SetValue(client,session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session,7u);
        EnbR11FlightTests.FlightWorld(session.World);
        typeof(Net7GameplayState).GetProperty("PresentationMode")!.SetValue(session.Gameplay,0);
        session.World.Apply(Packet.ForOpcode(0x0042,new byte[Net7PresentationWire.ServerParametersPayloadSize]),1000);
        void Set(Net7AuxDataSchema schema,string suffix,object value)
        {var p=schema.Properties.Single(p=>p.Path==schema.Name+"."+suffix);p.Value=value;p.State=2;}
        Set(session.World.LocalPlayerAuxData,"GroupInfo.Members.0.Name",new Net7AuxDataString(Encoding.Latin1.GetBytes("Leader")));
        Set(session.World.LocalPlayerAuxData,"GroupInfo.Members.0.GameID",8);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);using var lifetime=receiver;
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        typeof(EnbClient).GetProperty("RetailAssets")!.SetValue(client,assets);
        var saved=(Dictionary<uint,EnbRetailShortcuts>)typeof(EnbRetailAssetBundle).GetField("_shortcuts",flags)!.GetValue(assets)!;
        uint character=session.FixedSessionId;
        void Assign(int? inventorySlot)
        {
            string header="["+BinaryPrimitives.ReverseEndianness(character)+"_PDA_EQUIP]\nShortcutBeenCreated=yes\nNum=1\n";
            saved[character]=EnbRetailShortcuts.Resolve(EnbRetailIniDocument.Parse(header+(inventorySlot is int n?$"0_Bar=1\n0_Group=1\n0_Button=2\n0_Key=<{n}/>\n":"")),character);
        }
        var empty=new Net7ItemTemplateVector([],null);
        session.ItemTemplates.Materialize(new(101,0,0,0,new([],empty,empty),0,1,1,0,1,0,0,new(Encoding.Latin1.GetBytes("Device")),new([]),new([])));
        var ship=session.World.Objects.FindFirst(session.World.LocalShipObjectId!.Value)!.AuxData!;
        foreach(int slot in new[]{3,4}){Set(ship,$"Inventory.Equipped.{slot}.ItemTemplateID",101);Set(ship,$"Inventory.Equipped.{slot}.StackCount",1);}
        typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",flags)!.Invoke(client,[1000u]);
        var context=Assert.IsType<EnbMultiboxContext>(client.MultiboxContext);

        var frames=new List<(ushort Opcode,byte[] Payload)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        int echoes=0;client.MultiboxInput+=(_,_)=>echoes++;
        var input=new EnbMultiboxInput(Guid.NewGuid(),"shortcut",11,context with {Avatar=8});
        Assign(3);Assert.True(await client.ActivateRemoteShortcutAsync(input,context));
        var first=Assert.Single(frames);Assert.Equal(Net7GameplayWire.BuildUseInventoryItem(0,2,3),first.Payload);
        frames.Clear();session.World.MaterializeCreate(new Net7CreateUpdate(99,1,0,1,0,0,0));Assert.True(await client.RequestTargetAsync(99));frames.Clear();echoes=0;Assign(4);Assert.True(await client.ActivateRemoteShortcutAsync(input with {Id=Guid.NewGuid()},context));
        Assert.Equal(first.Opcode,Assert.Single(frames).Opcode);Assert.Equal(Net7GameplayWire.BuildUseInventoryItem(0,2,4),frames[0].Payload); // 0059FEFA: SClient+1130 (no 0047 here), not the target (INV-D102)
        frames.Clear();Assign(null);Assert.False(await client.ActivateRemoteShortcutAsync(input,context));
        Assign(3);Assert.False(await client.ActivateRemoteShortcutAsync(input,context with {Owner=context.Owner+1}));
        Assert.False(await client.ActivateRemoteShortcutAsync(input with {Target=12},context));
        Assert.Empty(frames);Assert.Equal(0,echoes);
        var skills=EnbRetailSkillCatalog.Parse(EnbRetailIniDocument.Parse("[All Skills]\n7=Cloak"),EnbRetailIniDocument.Parse(""),EnbRetailIniDocument.Parse("[Activated Abilities]\n3=Cloak Ability\n[Cloak Ability]\nSkill=Cloak"));
        typeof(EnbRetailAssetBundle).GetField("_shortcutSkills",flags)!.SetValue(assets,skills);
        typeof(EnbRetailAssetBundle).GetField("_shortcutSkillsLoaded",flags)!.SetValue(assets,true);
        saved[character]=EnbRetailShortcuts.Resolve(EnbRetailIniDocument.Parse($"[{BinaryPrimitives.ReverseEndianness(character)}_Skills]\nShortcutBeenCreated=yes\nNum=1\n0_Bar=1\n0_Group=1\n0_Button=2\n0_Key=<7/><Cloak/><Cloak Ability/>\n"),character);
        Assert.True(await client.ActivateRemoteShortcutAsync(input with {Id=Guid.NewGuid()},context));
        Assert.Equal((ushort)0x58,Assert.Single(frames).Opcode);Assert.Equal(Net7GameplayWire.BuildSkillAbility(7,3),frames[0].Payload);
        Assert.Equal(0,echoes);

    }
}
