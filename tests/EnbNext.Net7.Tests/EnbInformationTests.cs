using System.Reflection;
using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class EnbInformationTests
{
    [Fact]
    public void LogRoundTripsRetailSectionsEscapingPagesAndCharacterSelection()
    {
        string directory = Path.Combine(Path.GetTempPath(), "enb-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var log = new EnbRetailCaptainLog(directory,123);
            var other = new EnbRetailCaptainLog(directory,124);
            Assert.Equal(1,log.ReadCurrentPage());Assert.Equal("",log.ReadPage(1));
            log.SavePage(999,"Alpha; beta\n\nGamma\n");
            Assert.Equal("Alpha; beta\n\nGamma\n",log.ReadPage(999));
            Assert.Equal("",other.ReadPage(999));
            string options = Path.Combine(directory,$"player{EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(123)}_options.ini");
            File.WriteAllText(options,"[BOOL-000]\nOptionName=Camera_Warp\nOptionValue=yes\n");
            log.SaveCurrentPage(999);Assert.Equal(999,log.ReadCurrentPage());
            Assert.True(EnbRetailPlayerOptionsCatalog.ParseText(File.ReadAllText(options)).CameraWarp);
            Assert.Contains("LINES=4",File.ReadAllText(Directory.GetFiles(directory,"*_log-999.ini").Single()));
            Assert.Contains("TEXT=Alpha/:, beta",File.ReadAllText(Directory.GetFiles(directory,"*_log-999.ini").Single()));
            log.SavePage(999,"");Assert.Equal("",log.ReadPage(999));
            Assert.Equal("",log.ReadPage(999,out bool initialized));Assert.True(initialized);
            log.ReadPage(998,out initialized);Assert.False(initialized);
            log.SavePage(2,"Café");Assert.Equal("Café",log.ReadPage(2));
            Assert.Contains((byte)0xe9,File.ReadAllBytes(Directory.GetFiles(directory,"*_log-2.ini").Single()));
            string pagePath = Directory.GetFiles(directory,"*_log-999.ini").Single();
            File.WriteAllText(pagePath,"[NUM_LINES]\nLINES=2147483647\n[LINE-0000]\nTEXT="+new string('x',2000));
            Assert.Equal(new string('x',1024),log.ReadPage(999));
            Assert.Throws<ArgumentOutOfRangeException>(()=>log.SavePage(0,"x"));
            Assert.Throws<ArgumentOutOfRangeException>(()=>log.ReadPage(1000));
            Assert.Empty(Directory.GetFiles(directory,"*.tmp"));
        }
        finally { Directory.Delete(directory,true); }
    }

    [Fact]
    public async Task PetitionUsesCurrentTargetAndCategoryAndRejectsRetiredOwners()
    {
        await using var client = new EnbClient(new());
        var session = new Net7Session(new Net7ServerOptions { Host="127.0.0.1" });
        typeof(EnbClient).GetField("_net7",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(client,session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        EnbR11FlightTests.FlightWorld(session.World);
        var (_,channel,receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var lifetime = receiver;
        var sent = new List<byte[]>();
        channel.FrameSent += (frame,_) => { Assert.Equal((ushort)0x88,frame.Opcode);sent.Add(frame.Payload.ToArray()); };
        session.Gameplay.SetLocalSelectedTarget(23);
        ulong handoff = session.World.PresentationHandoffVersion;
        Assert.False(await client.SubmitPetitionAsync(8,handoff,1,"Subject","Details",""));
        Assert.False(await client.SubmitPetitionAsync(7,handoff+1,1,"Subject","Details",""));
        Assert.False(await client.SubmitPetitionAsync(7,handoff,8,"Subject","Details",""));
        Assert.Empty(sent);
        Assert.True(await client.SubmitPetitionAsync(7,handoff,3,"Player issue","Details; unchanged","Pilot One; Pilot Two"));
        Assert.Equal(Net7GameplayWire.BuildPetitionStuck(23,3,"Player issue","Details; unchanged","Pilot One; Pilot Two"),Assert.Single(sent));
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.Disconnected);
        Assert.False(await client.SubmitPetitionAsync(7,handoff,1,"Subject","Details",""));
        Assert.Single(sent);
    }
}
