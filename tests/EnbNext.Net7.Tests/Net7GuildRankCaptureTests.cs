using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class Net7GuildRankCaptureTests
{
    // Passive session capture 20260916T072103505Z, frame 95 at 09:21:15.992:
    // D3 payload extracted after the UDP (12-byte) and inner (4-byte) headers.
    internal static readonly byte[] CapturedRanks = Convert.FromHexString(
        "0000000A070052656372756974000000010A0041707072656E74696365000000020D005065747479204F666669636572000000030C004D6173746572204368696566000000040600456E7369676E000000050A004C69657574656E616E74000000060900436F6D6D616E6465720000000707004361707461696E000000080900436F6D6D6F646F726500000009070041646D6972616C0000000A");

    [Fact]
    public void CapturedServerReplyDecodesAllTenRankNames()
    {
        var ranks = Net7GameplayWire.DecodeGuildRankNames(CapturedRanks);
        Assert.Equal(new[] { "Recruit", "Apprentice", "Petty Officer", "Master Chief", "Ensign", "Lieutenant", "Commander", "Captain", "Commodore", "Admiral" }, ranks.Select(r => r.Name));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (uint)i), ranks.Select(r => r.Rank));
    }

    [Fact]
    public void TruncatedRepliesKeepTheCompleteRecordsBeforeTheEnd()
    {
        // 0088DE10 starts the count at zero: an absent count is an empty list.
        for (int length = 0; length < 4; length++)
            Assert.Empty(Net7GameplayWire.DecodeGuildRankNames(CapturedRanks.AsSpan(0, length)));
        // Safety rule: a record past the packet end is an undefined native
        // read; the complete records before it are kept.
        var all = Net7GameplayWire.DecodeGuildRankNames(CapturedRanks);
        var ends = new List<int>();
        int end = 4;
        foreach (var rank in all) ends.Add(end += 2 + System.Text.Encoding.ASCII.GetByteCount(rank.Name) + 4);
        for (int length = 4; length < CapturedRanks.Length; length++)
            Assert.Equal(all.Take(ends.Count(e => e <= length)), Net7GameplayWire.DecodeGuildRankNames(CapturedRanks.AsSpan(0, length)));
    }

    [Fact]
    public void RankReplyIgnoresUnusedTailAndFollowingReplyStillReplacesRanks()
    {
        var state = new Net7GameplayState();
        state.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x00D3, (byte[])[..CapturedRanks, 0xFF, 0x80]));
        Assert.Equal(Net7GameplayWire.DecodeGuildRankNames(CapturedRanks), state.GuildRankNames);
        state.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x00D3, new byte[4]));
        Assert.Empty(state.GuildRankNames);
    }

    [Theory]
    [InlineData("80000000")]
    [InlineData("FFFFFFFF01020304")]
    [InlineData("0000000001020304")]
    public void NonpositiveRankCountsProduceEmptyLists(string payload)
        => Assert.Empty(Net7GameplayWire.DecodeGuildRankNames(Convert.FromHexString(payload)));
}
