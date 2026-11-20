using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7JobsAdminStateTests
{
    [Fact]
    public void I07FamiliesMaterializeJobsNotesReferralAndGmState()
    {
        var state = new Net7GameplayState();

        state.Apply(Packet.ForOpcode(0x0093, Net7UiPresentationWireTests.Build(w =>
        {
            w.U32(2);
            Net7JobsAdminWireTests.WriteJob(w, 10, 20, 30, 40, "A", "B", "C");
            Net7JobsAdminWireTests.WriteJob(w, 11, 21, 31, 41, "D", "E", "F");
        })));
        Assert.Equal(2, state.JobList.Count);

        state.Apply(Packet.ForOpcode(0x0094, Net7UiPresentationWireTests.Build(w => { w.U32(10); w.U8(1); w.CString("Title"); w.CString("Description"); })));
        Assert.Equal("Title", state.JobDescription!.Title);

        state.Apply(Packet.ForOpcode(0x0096, Convert.FromHexString("0A000000020000003C000000")));
        Assert.Equal(60u, state.LastJobAcceptReply!.SecondsRemaining);

        byte[] noteList = Net7JobsAdminWireTests.NoteListRecord(1, 2, "Alice").Concat(new byte[3]).ToArray();
        state.Apply(Packet.ForOpcode(0x00B7, noteList));
        Assert.Equal("Alice", Assert.Single(state.NoteList!.Entries).From);

        state.Apply(Packet.ForOpcode(0x00B8, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(3); w.Bytes(Net7JobsAdminWireTests.FixedFrom("Bob")); w.CString("Hello"); })));
        Assert.Equal("Hello", state.NoteContent!.Content);

        state.Apply(Packet.ForOpcode(0x00E9, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(9); w.NetworkU32(4); })));
        Assert.Equal(9u, state.ReferralPoints!.PointsTotal);

        state.Apply(Packet.ForOpcode(0x00EA, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(77); w.NetworkU32(1); w.CString("Cargo"); })));
        Assert.Equal(77u, state.GmInventoryEditor!.PlayerPackTargetValue);

        state.Apply(Packet.ForOpcode(0x00EB, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(88); w.NetworkU32(1); w.CString("GM Mission"); })));
        Assert.Equal("GM Mission", Assert.Single(state.GmMissionLog!.Entries));
    }

    [Fact]
    public void JobDeleteRemovesMatchingCurrentUiIdWithoutInventingOtherConsequences()
    {
        var state = new Net7GameplayState();
        state.Apply(Packet.ForOpcode(0x0093, Net7UiPresentationWireTests.Build(w =>
        {
            w.U32(2);
            Net7JobsAdminWireTests.WriteJob(w, 10, 20, 30, 40, "A", "B", "C");
            Net7JobsAdminWireTests.WriteJob(w, 11, 21, 31, 41, "D", "E", "F");
        })));
        state.Apply(Packet.ForOpcode(0x0094, Net7UiPresentationWireTests.Build(w => { w.U32(10); w.U8(1); w.CString("Title"); w.CString("Description"); })));

        state.Apply(Packet.ForOpcode(0x0095, Convert.FromHexString("0A000000")));

        Assert.Equal(10u, state.LastDeletedJobId);
        Net7JobListEntry remaining = Assert.Single(state.JobList);
        Assert.Equal(11u, remaining.Field0);
        Assert.Equal(10u, state.JobDescription!.JobId);
    }

    [Fact]
    public void JobDeleteAndAcceptRemoveOnlyTheFirstEntryCarryingADuplicatedId()
    {
        // 005A3BC0 appends every batch unchanged; 005A4430/005A3FB0 unlink the first match only.
        var state = new Net7GameplayState();
        state.Apply(Packet.ForOpcode(0x0093, Net7UiPresentationWireTests.Build(w =>
        {
            w.U32(3);
            Net7JobsAdminWireTests.WriteJob(w, 10, 0, 30, 40, "A", "B", "C");
            Net7JobsAdminWireTests.WriteJob(w, 10, 0, 31, 41, "D", "E", "F");
            Net7JobsAdminWireTests.WriteJob(w, 10, 0, 32, 42, "G", "H", "I");
        })));
        state.Apply(Packet.ForOpcode(0x0095, Convert.FromHexString("0A000000")));
        Assert.Equal(new[] { "D", "G" }, state.JobList.Select(j => j.Text0));
        state.BeginStationJobRequest(10, true);
        state.Apply(Packet.ForOpcode(0x0096, Convert.FromHexString("0A000000000000000000000000")[..12]));
        Assert.Equal(new[] { "G" }, state.JobList.Select(j => j.Text0));
    }

    [Fact]
    public void DeletedJobsAreWithdrawnUntilListedAgainAndForgottenWhenTheListOpens()
    {
        // The server lists a job twice, then deletes it once (its refresh): the second copy stays (005A4430), but the
        // job is withdrawn, which Next's Modern Jobs window leaves out.
        var state = new Net7GameplayState();
        void List(uint id, string name) => state.Apply(Packet.ForOpcode(0x0093, Net7UiPresentationWireTests.Build(w => { w.U32(1); Net7JobsAdminWireTests.WriteJob(w, id, 0, 30, 40, name, "B", "C"); })));
        List(10, "A"); List(11, "B"); List(10, "A");
        state.Apply(Packet.ForOpcode(0x0095, Convert.FromHexString("0A000000")));
        Assert.Equal(new[] { 11u, 10u }, state.JobList.Select(j => j.Field0));
        Assert.Equal(new[] { 10u }, state.WithdrawnJobIds);
        var modern = EnbNext.Engine.EnbInventoryUi.DistinctJobs(state.JobList.ToArray(), [.. state.WithdrawnJobIds]);
        Assert.Equal(new[] { 11u }, modern.Select(j => j.Field0));
        List(10, "A");
        Assert.Empty(state.WithdrawnJobIds);
        state.Apply(Packet.ForOpcode(0x0095, Convert.FromHexString("0B000000")));
        state.BeginStationJobs();
        Assert.Empty(state.WithdrawnJobIds);
        Assert.Empty(state.JobList);
    }

    [Fact]
    public void TheModernJobsListShowsEachJobOnceInTheListsOrder()
    {
        static Net7JobListEntry Job(uint id, string name) => new(id, 0, 0, 10, name, "S", "R");
        Net7JobListEntry[] rows = [Job(1, "A"), Job(2, "B"), Job(1, "A again"), Job(3, "C")];
        Assert.Equal(new[] { "A", "B", "C" }, EnbNext.Engine.EnbInventoryUi.DistinctJobs(rows, []).Select(j => j.Text0));
        Assert.Equal(new[] { "A", "C" }, EnbNext.Engine.EnbInventoryUi.DistinctJobs(rows, [2]).Select(j => j.Text0));
        Net7JobListEntry[] distinct = [Job(1, "A"), Job(2, "B")];
        Assert.Same(distinct, EnbNext.Engine.EnbInventoryUi.DistinctJobs(distinct, []));
    }

    [Fact]
    public void GmMissionLogRemainsSeparateFromNormalLocalPlayerAuxDataMissions()
    {
        var gameplay = new Net7GameplayState();
        var world = new Net7WorldState();
        var before = world.LocalPlayerAuxData.Properties
            .Where(p => p.Path.StartsWith("LocalPlayerAuxData.Missions", StringComparison.Ordinal))
            .Select(p => (p.Id, p.State, p.Value))
            .ToArray();

        gameplay.Apply(Packet.ForOpcode(0x00EB, Net7UiPresentationWireTests.Build(w =>
        {
            w.NetworkU32(123);
            w.NetworkU32(2);
            w.CString("GM Mission A");
            w.CString("GM Mission B");
        })));

        var after = world.LocalPlayerAuxData.Properties
            .Where(p => p.Path.StartsWith("LocalPlayerAuxData.Missions", StringComparison.Ordinal))
            .Select(p => (p.Id, p.State, p.Value))
            .ToArray();
        Assert.Equal(before, after);
        Assert.Equal(2, gameplay.GmMissionLog!.Entries.Count);
    }

    [Fact]
    public void I07StateClearsAtCharacterSelectionWithoutAddingUnprovenPacketSemantics()
    {
        var state = new Net7GameplayState();
        Populate(state);
        Assert.NotEmpty(state.JobList);
        Assert.NotNull(state.GmMissionLog);

        state.ResetForCharacterSelection();
        Assert.Empty(state.JobList);
        Assert.Null(state.JobDescription);
        Assert.Null(state.NoteContent);
        Assert.Null(state.LastJobAcceptReply);
        Assert.Null(state.GmMissionLog);
    }


    private static void Populate(Net7GameplayState state)
    {
        state.Apply(Packet.ForOpcode(0x0093, Net7UiPresentationWireTests.Build(w => { w.U32(1); Net7JobsAdminWireTests.WriteJob(w, 1, 2, 3, 4, "A", "B", "C"); })));
        state.Apply(Packet.ForOpcode(0x0094, Net7UiPresentationWireTests.Build(w => { w.U32(1); w.U8(1); w.CString("T"); w.CString("D"); })));
        state.Apply(Packet.ForOpcode(0x0096, new byte[12]));
        state.Apply(Packet.ForOpcode(0x00B7, Net7JobsAdminWireTests.NoteListRecord(1, 2, "A").Concat(new byte[3]).ToArray()));
        state.Apply(Packet.ForOpcode(0x00B8, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.Bytes(Net7JobsAdminWireTests.FixedFrom("A")); w.CString("C"); })));
        state.Apply(Packet.ForOpcode(0x00E9, new byte[8]));
        state.Apply(Packet.ForOpcode(0x00EA, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.NetworkU32(0); })));
        state.Apply(Packet.ForOpcode(0x00EB, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.NetworkU32(0); })));
    }
}
