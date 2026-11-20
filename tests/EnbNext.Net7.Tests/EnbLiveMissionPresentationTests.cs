using System.Reflection;
using System.Text;
using EnbNext.Client;
using EnbNext.Client.Models;
using EnbNext.Net7;
using EnbNext.Net7.Social;
using EnbNext.Net7.World;

namespace EnbNext.Net7.Tests;

public sealed class EnbLiveMissionPresentationTests
{
    [Fact]
    public async Task LiveFrameEmitsAcceptedMissionLineAndCueOnceAfterSilentInitialBatch()
    {
        await using var client=new EnbClient(new());
        var session=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"});
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(EnbClient).GetField("_net7",flags)!.SetValue(client,session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        var publish=typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",flags)!.CreateDelegate<Action<uint>>(client);
        session.PresentationStateChanged+=publish;publish(0);
        void Set(params (string Field,object Value)[] values)
        {
            using var bytes=new MemoryStream();using var writer=new BinaryWriter(bytes);
            writer.Write((byte)0);writer.Write(values.Length);
            foreach(var (field,value) in values)
            {
                writer.Write(session.World.LocalPlayerAuxData.Properties.Single(p=>p.Path=="LocalPlayerAuxData.Missions.0."+field).Id);
                if(value is int number)writer.Write(number);
                else {var text=Encoding.Latin1.GetBytes((string)value);writer.Write((ushort)text.Length);writer.Write(text);}
            }
            writer.Write(0);writer.Write(0);
            new Net7AuxDataCodec().Apply(session.World.LocalPlayerAuxData,bytes.ToArray());
        }
        Net7ChatPresentationEvent[] Sample()
        {
            var frame=client.SampleLivePresentationFrame()!;
            try{return frame.Hud.Chat!.Events.ToArray();}finally{frame.Release();}
        }
        Set(("Name",""),("Stage",0));Assert.Empty(Sample());
        Set(("Name","Terminal job"),("Stage",1),("StageCount",2),("Stages.0.Text","Deliver <color>cargo</color>."));
        var accepted=Sample();
        Assert.Equal("Mission_New",Assert.Single(accepted,e=>e.Kind==Net7ChatPresentationKind.InterfaceSound).Text);
        var line=Assert.Single(accepted,e=>e.ScreenLine is not null).ScreenLine!;
        Assert.Equal("(1/2) Deliver cargo.",line.Text);
        Assert.Equal(accepted,Sample());
        client.SamplePresentationSnapshot();Assert.Equal(accepted,Sample());
        session.PlayerSemantics.ResetForSectorBoundary();
        Set(("Stage",1));Assert.Equal(accepted,Sample());
    }
}
