using System.Text;
using EnbNext.Net7.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class Net7JobsAdminWireTests
{
    [Fact]
    public void CapturedIdOnlyReplyDoesNotInventAnAcceptanceStatus()
    {
        var reply=Net7JobsAdminWire.DecodeJobAcceptReply(Convert.FromHexString("F7ED0000"));
        Assert.Equal(60919u,reply.JobId);
        Assert.Null(reply.AcceptStatus);
    }
    [Fact]
    public void JobFamiliesUseCurrentRawLittleEndianAndCStringGeometry()
    {
        byte[] list = Net7UiPresentationWireTests.Build(w =>
        {
            w.U32(2);
            WriteJob(w, 10, 20, 30, 40, "A", "B", "C");
            WriteJob(w, 11, 21, 31, 41, "D", "E", "F");
        });
        IReadOnlyList<Net7JobListEntry> jobs = Net7JobsAdminWire.DecodeJobList(list);
        Assert.Equal(2, jobs.Count);
        Assert.Equal(10u, jobs[0].Field0);
        Assert.Equal(20u, jobs[0].Field1);
        Assert.Equal(30u, jobs[0].Field2);
        Assert.Equal(40u, jobs[0].Field3);
        Assert.Equal("C", jobs[0].Text2);

        Net7JobDescriptionUpdate description = Net7JobsAdminWire.DecodeJobDescription(
            Net7UiPresentationWireTests.Build(w => { w.U32(10); w.U8(2); w.CString("Title"); w.CString("Description"); }));
        Assert.Equal(10u, description.JobId);
        Assert.Equal((byte)2, description.StillAvailable);
        Assert.Equal("Title", description.Title);

        Assert.Equal(10u, Net7JobsAdminWire.DecodeJobDelete(Convert.FromHexString("0A000000")));
        Net7JobAcceptReplyUpdate reply = Net7JobsAdminWire.DecodeJobAcceptReply(
            Convert.FromHexString("0A000000020000003C000000"));
        Assert.Equal(10u, reply.JobId);
        Assert.Equal(2u, reply.AcceptStatus);
        Assert.Equal(60u, reply.SecondsRemaining);

        Assert.Empty(Net7JobsAdminWire.DecodeJobList(new byte[4]));
    }

    [Fact]
    public void NoteListMirrorsCurrentSeventySixByteAdmissionAndSeventyThreeByteConsumption()
    {
        byte[] first = NoteListRecord(0x01020304, 0x11223344, "Alice");
        Assert.Equal(Net7JobsAdminWire.NoteListMeaningfulRecordSize, first.Length);

        byte[] onePayload = first.Concat(new byte[] { 0xA1, 0xA2, 0xA3 }).ToArray();
        Net7NoteListUpdate one = Net7JobsAdminWire.DecodeNoteList(onePayload);
        Net7NoteListEntry row = Assert.Single(one.Entries);
        Assert.Equal(0x01020304u, row.NoteIndex);
        Assert.Equal(0x11223344u, row.SentTimestamp);
        Assert.Equal("Alice", row.From);
        Assert.Equal(Convert.FromHexString("A1A2A3"), one.IgnoredTrailingBytes);

        byte[] second = NoteListRecord(5, 6, "Bob");
        byte[] twoPayload = first.Concat(second).Concat(new byte[] { 1, 2, 3, 4, 5, 6 }).ToArray();
        Net7NoteListUpdate two = Net7JobsAdminWire.DecodeNoteList(twoPayload);
        Assert.Equal(2, two.Entries.Count);
        Assert.Equal("Bob", two.Entries[1].From);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, two.IgnoredTrailingBytes);

        byte[] belowAdmission = Enumerable.Repeat((byte)0xCC, Net7JobsAdminWire.NoteListAdmissionSize - 1).ToArray();
        Net7NoteListUpdate none = Net7JobsAdminWire.DecodeNoteList(belowAdmission);
        Assert.Empty(none.Entries);
        Assert.Equal(belowAdmission, none.IgnoredTrailingBytes);
    }

    [Fact]
    public void NoteContentReferralAndGmFamiliesUseCurrentNetworkOrderRules()
    {
        byte[] from = FixedFrom("Sender");
        byte[] note = Net7UiPresentationWireTests.Build(w =>
        {
            w.NetworkU32(0x10203040);
            w.Bytes(from);
            w.CString("Line one\nLine two");
        });
        Net7NoteContentUpdate content = Net7JobsAdminWire.DecodeNoteContent(note);
        Assert.Equal(0x10203040u, content.SentTimestamp);
        Assert.Equal("Sender", content.From);
        Assert.Equal("Line one\nLine two", content.Content);

        Net7ReferralPointsUpdate referral = Net7JobsAdminWire.DecodeReferralPoints(
            Net7UiPresentationWireTests.Build(w => { w.NetworkU32(12); w.NetworkU32(3); }));
        Assert.Equal(12u, referral.PointsTotal);
        Assert.Equal(3u, referral.PointsSpent);

        Net7GmInventoryEditorUpdate inventory = Net7JobsAdminWire.DecodeGmEditPlayerInventory(
            Net7UiPresentationWireTests.Build(w => { w.NetworkU32(77); w.NetworkU32(2); w.CString("Cargo A"); w.CString("Cargo B"); }));
        Assert.Equal(77u, inventory.PlayerPackTargetValue);
        Assert.Equal(new[] { "Cargo A", "Cargo B" }, inventory.Entries);

        Net7GmMissionLogUpdate missions = Net7JobsAdminWire.DecodeGmViewMissionLog(
            Net7UiPresentationWireTests.Build(w => { w.NetworkU32(88); w.NetworkU32(2); w.CString("Mission A"); w.CString("Mission B"); }));
        Assert.Equal(88u, missions.Field0);
        Assert.Equal(new[] { "Mission A", "Mission B" }, missions.Entries);
    }

    [Fact]
    public void CountedFamiliesCoverZeroOneManyAndMissingAdminStrings()
    {
        Assert.Empty(Net7JobsAdminWire.DecodeJobList(Net7UiPresentationWireTests.Build(w => w.U32(0))));
        Assert.Empty(Net7JobsAdminWire.DecodeGmEditPlayerInventory(Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.NetworkU32(0); })).Entries);
        Assert.Empty(Net7JobsAdminWire.DecodeGmViewMissionLog(Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.NetworkU32(0); })).Entries);

        Assert.Single(Net7JobsAdminWire.DecodeJobList(Net7UiPresentationWireTests.Build(w => { w.U32(1); WriteJob(w, 1, 2, 3, 4, "", "", ""); })));
        Assert.Equal(3, Net7JobsAdminWire.DecodeGmEditPlayerInventory(Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.NetworkU32(3); w.CString("A"); w.CString("B"); w.CString("C"); })).Entries.Count);
        Assert.Equal(3, Net7JobsAdminWire.DecodeGmViewMissionLog(Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.NetworkU32(3); w.CString("A"); w.CString("B"); w.CString("C"); })).Entries.Count);

        Assert.Empty(Net7JobsAdminWire.DecodeJobList(Net7UiPresentationWireTests.Build(w => w.U32(2))));
        Assert.Throws<InvalidDataException>(() => Net7JobsAdminWire.DecodeJobList(new byte[3]));
        Assert.Equal(new[] { "Only one", "" }, Net7JobsAdminWire.DecodeGmEditPlayerInventory(Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.NetworkU32(2); w.CString("Only one"); })).Entries);
        Assert.Equal(new[] { "Only one", "" }, Net7JobsAdminWire.DecodeGmViewMissionLog(Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.NetworkU32(2); w.CString("Only one"); })).Entries);
    }

    [Fact]
    public void JobListKeepsCompleteRecordsBeforeAnUndefinedRecordRead()
    {
        // 00895F80: u32 fields past the end are uninitialized, CStrings are defined.
        var jobs = Net7JobsAdminWire.DecodeJobList(Net7UiPresentationWireTests.Build(w =>
        {
            w.U32(5); WriteJob(w, 1, 2, 3, 4, "A", "B", "C");
            w.U32(5); w.U32(6); w.U32(7); w.U32(8);
        }));
        Assert.Equal(2, jobs.Count);
        Assert.Equal("A", jobs[0].Text0);
        Assert.Equal((5u, "", "", ""), (jobs[1].Field0, jobs[1].Text0, jobs[1].Text1, jobs[1].Text2));
        Assert.Empty(Net7JobsAdminWire.DecodeJobList(Net7UiPresentationWireTests.Build(w => { w.U32(unchecked((uint)-1)); WriteJob(w, 1, 2, 3, 4, "", "", ""); })));
    }

    [Fact]
    public void FamiliesIgnoreUnusedTail()
    {
        var fixtures = new (ushort Opcode, byte[] Payload)[]
        {
            (0x0093, Net7UiPresentationWireTests.Build(w => { w.U32(1); WriteJob(w, 1, 2, 3, 4, "A", "B", "C"); })),
            (0x0094, Net7UiPresentationWireTests.Build(w => { w.U32(1); w.U8(1); w.CString("T"); w.CString("D"); })),
            (0x0095, new byte[4]),
            (0x0096, new byte[12]),
            (0x00B8, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.Bytes(FixedFrom("A")); w.CString("C"); })),
            (0x00E9, new byte[8]),
            (0x00EA, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.NetworkU32(1); w.CString("A"); })),
            (0x00EB, Net7UiPresentationWireTests.Build(w => { w.NetworkU32(1); w.NetworkU32(1); w.CString("A"); })),
        };

        foreach ((ushort opcode, byte[] valid) in fixtures)
        {
            if (opcode is 0x0096)
                Assert.ThrowsAny<Exception>(() => DecodeStrict(opcode, valid[..^1]));
            DecodeStrict(opcode, valid.Concat(new byte[] { 0xEE }).ToArray());
        }
    }

    [Fact]
    public void JobDescriptionAndDeleteKeepInitializedFieldsOnShortReads()
    {
        Assert.Equal(new Net7JobDescriptionUpdate(0, 0, "", ""), Net7JobsAdminWire.DecodeJobDescription([]));
        Assert.Equal(0x2211u, Net7JobsAdminWire.DecodeJobDelete([0x11, 0x22]));
        Assert.Equal(0u, Net7JobsAdminWire.DecodeJobDelete([]));
        var description = Net7JobsAdminWire.DecodeJobDescription([1, 0, 0, 0, 1, (byte)'T']);
        Assert.Equal(new Net7JobDescriptionUpdate(1, 1, "T", ""), description);
    }

    [Fact]
    public void AdminInitializedFieldsAndSignedCountsMatchRetail()
    {
        Assert.Equal(new Net7ReferralPointsUpdate(0, 0), Net7JobsAdminWire.DecodeReferralPoints([]));
        Assert.Equal(new Net7ReferralPointsUpdate(0x01020000, 0), Net7JobsAdminWire.DecodeReferralPoints([1, 2]));
        Assert.Empty(Net7JobsAdminWire.DecodeGmEditPlayerInventory([]).Entries);
        Assert.Empty(Net7JobsAdminWire.DecodeGmViewMissionLog(Convert.FromHexString("00000001FFFFFFFF")).Entries);
        Assert.Equal(new[] { "", "" }, Net7JobsAdminWire.DecodeGmViewMissionLog(Convert.FromHexString("0000000100000002")).Entries);
    }

    internal static byte[] NoteListRecord(uint index, uint timestamp, string from)
        => Net7UiPresentationWireTests.Build(w =>
        {
            w.NetworkU32(index);
            w.NetworkU32(timestamp);
            w.Bytes(FixedFrom(from));
        });

    internal static byte[] FixedFrom(string value)
    {
        byte[] result = new byte[Net7JobsAdminWire.NoteFromSize];
        byte[] encoded = Encoding.Latin1.GetBytes(value);
        if (encoded.Length >= result.Length)
            throw new ArgumentOutOfRangeException(nameof(value));
        encoded.CopyTo(result, 0);
        return result;
    }

    internal static void WriteJob(Net7UiPresentationWireTests.WireWriter w, uint a, uint b, uint c, uint d, string s0, string s1, string s2)
    {
        w.U32(a); w.U32(b); w.U32(c); w.U32(d);
        w.CString(s0); w.CString(s1); w.CString(s2);
    }

    private static void DecodeStrict(ushort opcode, byte[] payload)
    {
        switch (opcode)
        {
            case 0x0093: _ = Net7JobsAdminWire.DecodeJobList(payload); break;
            case 0x0094: _ = Net7JobsAdminWire.DecodeJobDescription(payload); break;
            case 0x0095: _ = Net7JobsAdminWire.DecodeJobDelete(payload); break;
            case 0x0096: _ = Net7JobsAdminWire.DecodeJobAcceptReply(payload); break;
            case 0x00B8: _ = Net7JobsAdminWire.DecodeNoteContent(payload); break;
            case 0x00E9: _ = Net7JobsAdminWire.DecodeReferralPoints(payload); break;
            case 0x00EA: _ = Net7JobsAdminWire.DecodeGmEditPlayerInventory(payload); break;
            case 0x00EB: _ = Net7JobsAdminWire.DecodeGmViewMissionLog(payload); break;
            default: throw new ArgumentOutOfRangeException(nameof(opcode));
        }
    }
}
