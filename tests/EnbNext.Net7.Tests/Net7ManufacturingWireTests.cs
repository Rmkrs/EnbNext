using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7ManufacturingWireTests
{
    [Fact]
    public void ManufacturingRequestFamilyMatchesClosedMixedEndianWireContracts()
    {
        const uint player = 0x11223344;
        Assert.Equal(Convert.FromHexString("1122334400000001"), Net7GameplayWire.BuildManufacturingMode(player, Net7ManufacturingMode.Manufacture));
        Assert.Equal(Convert.FromHexString("1122334400000002"), Net7GameplayWire.BuildManufacturingMode(player, Net7ManufacturingMode.Analyze));
        Assert.Equal(Convert.FromHexString("1122334400000004"), Net7GameplayWire.BuildManufacturingMode(player, Net7ManufacturingMode.Refining));
        Assert.Equal(Convert.FromHexString("1122334400000000"), Net7GameplayWire.BuildManufacturingMode(player, Net7ManufacturingMode.None));
        Assert.Equal(Convert.FromHexString("1122334455667788"), Net7GameplayWire.BuildManufacturingCategory(player, 0x55667788));
        Assert.Equal(Convert.FromHexString("1122334455667788"), Net7GameplayWire.BuildManufactureItem(player, 0x55667788));
        Assert.Equal(Convert.FromHexString("4433221188776655"), Net7GameplayWire.BuildRefineryItem(player, 0x55667788));
        Assert.Equal(Convert.FromHexString("1122334400000003"), Net7GameplayWire.BuildManufacturingAction(player, 3));
    }

    [Fact]
    public void ManufacturingFilterHasExactNineByteCurrentPayloadAndTlMasks()
    {
        const uint player = 0x11223344;
        Assert.Equal(Convert.FromHexString("112233440100000001"), Net7GameplayWire.BuildManufacturingFilter(player, true, 0x001));
        Assert.Equal(Convert.FromHexString("112233440000000100"), Net7GameplayWire.BuildManufacturingFilter(player, false, 0x100));
        Assert.Equal(Convert.FromHexString("1122334401000001FF"), Net7GameplayWire.BuildManufacturingFilter(player, true, 0x1FF));
    }

    [Fact]
    public void ActiveManufacturingObjectUsesInitializedBoundedBigEndianRead()
    {
        Assert.Equal(0x11223344u, Net7GameplayWire.DecodeActiveManufacturingObject(Convert.FromHexString("11223344")));
        for (int length = 0; length < 4; length++)
            Assert.Equal(0u, Net7GameplayWire.DecodeActiveManufacturingObject(new byte[length]));
        Assert.Equal(0u, Net7GameplayWire.DecodeActiveManufacturingObject(new byte[5]));
    }

    [Fact]
    public void SameOpcodeManufacturingRequestsAreNotInventedAsInboundAcknowledgements()
    {
        var state = new Net7GameplayState();
        foreach (ushort opcode in new ushort[] { 0x0079, 0x007A, 0x007B, 0x007C, 0x007E, 0x0080 })
            Assert.False(state.Apply(Packet.ForOpcode(opcode, Array.Empty<byte>())));
    }
}
