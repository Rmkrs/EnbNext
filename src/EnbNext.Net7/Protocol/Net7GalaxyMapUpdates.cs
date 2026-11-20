using System.Buffers.Binary;

namespace EnbNext.Net7.Protocol;

/// <summary>0097's bounded command view and copy-on-write retail map mutations.</summary>
public static class Net7GalaxyMapUpdates
{
    public static uint Command(ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> blob)
    {
        // 00870FD0 initializes Command to 0xFFFFFFFF and Length to zero before
        // bounded reads, so a short header is a defined (possibly unknown) command
        // with an empty view. 0073AC60's default arm ignores unknown commands.
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 0);
        payload[..Math.Min(8, payload.Length)].CopyTo(header);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        int available = Math.Max(0, payload.Length - 8);
        // Safety rule: the view is allocated from the pool and filled with a
        // bounded copy; bytes beyond the packet are uninitialized.
        if (length > (uint)available) throw new InvalidDataException("GalaxyMap command view is truncated.");
        blob = length == 0 ? ReadOnlySpan<byte>.Empty : payload.Slice(8, (int)length);
        return BinaryPrimitives.ReadUInt32LittleEndian(header);
    }

    public static Net7GalaxyMapData Apply(Net7GalaxyMapData map, ReadOnlySpan<byte> payload)
        => RetainOrder(map,ApplyCore(map,payload));

    private static Net7GalaxyMapData RetainOrder(Net7GalaxyMapData previous,Net7GalaxyMapData current)
        => ReferenceEquals(previous,current) ? previous : current with
        {
            SystemOrder=(previous.SystemOrder ?? Net7GalaxyMapOrder.Empty.Retain(previous.Systems.Select(s=>s.SystemHashId))).Retain(current.Systems.Select(s=>s.SystemHashId)),
            SectorOrder=(previous.SectorOrder ?? Net7GalaxyMapOrder.Empty.Retain(previous.Sectors.Select(s=>s.SectorHashId))).Retain(current.Sectors.Select(s=>s.SectorHashId)),
            GateOrder=(previous.GateOrder ?? Net7GalaxyMapOrder.Empty.Retain(previous.Gates.Select(s=>s.GateHashId))).Retain(current.Gates.Select(s=>s.GateHashId))
        };

    private static Net7GalaxyMapData ApplyCore(Net7GalaxyMapData map, ReadOnlySpan<byte> payload)
    {
        uint command = Command(payload, out var blob);
        switch (command)
        {
            case 2: // 007A5260 / 00836B70: removal by AllData index, not hash.
                return RemoveRecords(map, blob);
            case 3: // 007A52F0: replace; never merge into the previous bitset.
                return map with { AuxiliaryBlob = blob.ToArray() };
            case 5:
                return AddSystem(map, Net7GalaxyMapCodec.ParseSystem(blob));
            case 6: case 7: case 8: case 11:
                return AddSector(map, Net7GalaxyMapCodec.ParseSector(command, blob));
            case 9:
                return AddGate(map, Net7GalaxyMapCodec.ParseGate(blob));
            default:
                return map; // 0073AC60: unknown commands leave the map unchanged.
        }
    }

    // Current proxy 0040F270 requeues GalaxyMap.dat on each login completion.
    // Retail ignores duplicate hashes, so this is a replay, not a catalog reset.
    public static Net7GalaxyMapData ReplayLocalAsset(Net7GalaxyMapData map, Net7GalaxyMapData asset, Action<string>? rejected = null)
    {
        var previous=map;
        if (!map.AuxiliaryBlob.AsSpan().SequenceEqual(asset.AuxiliaryBlob))
            map = map with { AuxiliaryBlob = asset.AuxiliaryBlob.ToArray() };
        void ApplyRecord(Func<Net7GalaxyMapData,Net7GalaxyMapData> add)
        {
            try { map = add(map); }
            catch (InvalidDataException ex) { rejected?.Invoke(ex.Message); }
        }
        foreach (var system in asset.Systems) ApplyRecord(m => AddSystem(m, system));
        foreach (var sector in asset.Sectors) ApplyRecord(m => AddSector(m, sector));
        foreach (var gate in asset.Gates) ApplyRecord(m => AddGate(m, gate));
        return RetainOrder(previous,map);
    }

    private static Net7GalaxyMapData AddSystem(Net7GalaxyMapData map, Net7GalaxySystemRecord system)
    {
        // 00836F30/00837190/008373F0 ignore duplicate hashes.
        if (map.Systems.Any(s => s.SystemHashId == system.SystemHashId)) return map;
        uint id = Admit(map, system.Field0);
        var index = CopyIndex(map); index[id] = (1, system.SystemHashId);
        return map with { AllData = index, Systems = [..map.Systems, system with { Field0 = id }], RecordCount = Math.Max(Count(map), id + 1) };
    }

    private static Net7GalaxyMapData AddSector(Net7GalaxyMapData map, Net7GalaxySectorRecord sector)
    {
        if (map.Sectors.Any(s => s.SectorHashId == sector.SectorHashId)) return map;
        uint id = Admit(map, sector.Field0);
        var index = CopyIndex(map); index[id] = (2, sector.SectorHashId);
        return map with { AllData = index, Sectors = [..map.Sectors, sector with { Field0 = id }], RecordCount = Math.Max(Count(map), id + 1) };
    }

    private static Net7GalaxyMapData AddGate(Net7GalaxyMapData map, Net7GalaxyGateRecord gate)
    {
        if (map.Gates.Any(g => g.GateHashId == gate.GateHashId)) return map;
        uint id = Admit(map, gate.GateHashId);
        var index = CopyIndex(map); index[id] = (3, gate.GateHashId);
        return map with { AllData = index, Gates = [..map.Gates, gate with { GateHashId = id }], RecordCount = Math.Max(Count(map), id + 1) };
    }

    // 00836960 owns a slot independently of the hash collections. Replacing a
    // slot leaves the previous record in its hash collection.
    private static Dictionary<uint, (byte Kind, uint Hash)> CopyIndex(Net7GalaxyMapData map)
    {
        if (map.AllData is { } retained) return new(retained);
        var index = new Dictionary<uint, (byte Kind, uint Hash)>();
        foreach (var record in map.Systems) index[record.Field0] = (1, record.SystemHashId);
        foreach (var record in map.Sectors) index[record.Field0] = (2, record.SectorHashId);
        foreach (var record in map.Gates) index[record.GateHashId] = (3, record.GateHashId);
        return index;
    }

    // 008365C0 (command 4 via 007A4C50) stores the count as MaxDataObjects; raw
    // 00836604/00836606 grow AllData only for a larger count. A smaller count keeps
    // every slot: the presence reply bounds by the count, removal still finds them (MAP-D01).
    public static Net7GalaxyMapData ResizeAllData(Net7GalaxyMapData map, uint count)
        => map with { AllData = CopyIndex(map), RecordCount = count };

    private static Net7GalaxyMapData RemoveRecords(Net7GalaxyMapData map, ReadOnlySpan<byte> blob)
    {
        var index = CopyIndex(map);
        uint count = Count(map);
        for (int i = 0; i + 4 <= blob.Length; i += 4)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(blob[i..]);
            if (!index.Remove(id, out var record)) continue;
            // 00836B70 removes only the hash entry owned by the occupied slot.
            map = record.Kind switch
            {
                1 => map with { Systems = map.Systems.Where(s => s.SystemHashId != record.Hash).ToArray() },
                2 => map with { Sectors = map.Sectors.Where(s => s.SectorHashId != record.Hash).ToArray() },
                3 => map with { Gates = map.Gates.Where(s => s.GateHashId != record.Hash).ToArray() },
                _ => map
            };
        }
        return map with { AllData = index, RecordCount = count };
    }

    private static uint Count(Net7GalaxyMapData map)
    {
        if (map.RecordCount is { } retained) return retained;
        ulong count = map.Systems.Select(s => s.Field0).Concat(map.Sectors.Select(s => s.Field0))
            .Concat(map.Gates.Select(g => g.GateHashId)).Select(id => (ulong)id + 1).DefaultIfEmpty().Max();
        if (count > uint.MaxValue) throw new InvalidDataException("GalaxyMap record index overflow.");
        return (uint)count;
    }

    private static uint Admit(Net7GalaxyMapData map, uint id)
    {
        // 00836960 assigns an unassigned (-1) record the current maximum.
        if (id == uint.MaxValue) id = Count(map);
        if (id == uint.MaxValue)
            throw new InvalidDataException("GalaxyMap record index overflows.");
        return id;
    }
}
