using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Assets;

/// <summary>
/// Current-retail local presentation data required to reconstruct MainView world fog.
/// This stays in the asset layer: splines.wsd and orbitalNNNN.ini are authored retail
/// inputs, not Net7 protocol defaults.
/// </summary>
public sealed class EnbRetailSceneFogData
{
    private EnbRetailSceneFogData(
        EnbRetailScalarSplineDatabase splines,
        string splineSourcePath,
        string? baseLoadDiagnostic)
    {
        Splines = splines;
        SplineSourcePath = splineSourcePath;
        BaseLoadDiagnostic = baseLoadDiagnostic;
    }

    public EnbRetailScalarSplineDatabase Splines { get; }

    /// <summary>The exact loose retail resource selected for logical splines.wsd.</summary>
    public string SplineSourcePath { get; }

    /// <summary>
    /// Non-null when current retail would reject the selected base file before
    /// inserting any spline records and continue with an empty database.
    /// </summary>
    public string? BaseLoadDiagnostic { get; }

    public static EnbRetailSceneFogData Load(EnbInstalledAssetPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!paths.TryResolveRetailLooseResource("splines.wsd", out string sourcePath))
        {
            throw new FileNotFoundException(
                "Current-retail loose resource search did not resolve splines.wsd.",
                "splines.wsd");
        }

        byte[] bytes = File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(sourcePath));
        if (!EnbRetailScalarSplineDatabase.BeginsWithCurrentRetailRoot(bytes))
        {
            // ClientBaseCtor @ 00726100 constructs a fresh SplineDatabase and
            // calls SplineDatabaseLoadFile, but deliberately ignores its return
            // value. The parser's first gate at 00488500 rejects a non-0x800
            // root before inserting any records. Current retail therefore keeps
            // running with an empty database which later server 0x0038 packets
            // are allowed to populate. The loose winner remains authoritative;
            // there is no post-open fallback to a later loose candidate.
            return new EnbRetailSceneFogData(
                EnbRetailScalarSplineDatabase.CreateEmpty(),
                sourcePath,
                $"base rejected at current-retail 0x800 type gate: {sourcePath}; " +
                $"{EnbRetailScalarSplineDatabase.DescribeRootHeader(bytes)}; runtime 0x0038 starts from empty");
        }

        try
        {
            return new EnbRetailSceneFogData(
                EnbRetailScalarSplineDatabase.ParseWsd(bytes),
                sourcePath,
                baseLoadDiagnostic: null);
        }
        catch (InvalidDataException ex)
        {
            // A later parse failure can occur after current retail has already
            // inserted some records. Until that partial-mutation behavior is
            // required and bounded, do not replace it with an invented empty or
            // fall-through state.
            throw new InvalidDataException($"{sourcePath}: {ex.Message}", ex);
        }
    }
}

public sealed record EnbRetailOrbitalDefinition(
    int Index,
    int BaseAsset,
    float Scale,
    float HsvH,
    float HsvS,
    float HsvV,
    float StartAngle,
    float Rx,
    float Rz,
    float Distance,
    float OrbitsPerHour,
    float PhasePerHour,
    bool EmitsLight,
    string LightIntensitySpline,
    string SkyIntensitySpline,
    float LightRadius,
    bool ForceVisible);

public sealed record EnbRetailOrbitalSceneDefinition(
    string LightColorSpline,
    string SkyColorSpline,
    string SkyEmissiveSpline,
    IReadOnlyList<EnbRetailOrbitalDefinition> Orbitals)
{
    public static EnbRetailOrbitalSceneDefinition Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(EnbRetailIniDocument.Load(path));
    }

    public static EnbRetailOrbitalSceneDefinition Parse(EnbRetailIniDocument ini)
    {
        ArgumentNullException.ThrowIfNull(ini);
        // 004F34C0 reads each global with its default, including count=0.
        if (!ini.TryGetSection("ORBITALS", out IReadOnlyDictionary<string, string> globals))
            globals = new Dictionary<string, string>();

        int count = ReadInt(globals, "NUM_ORBITALS", 0);
        if (count < 0 || count > 4096)
            throw new InvalidDataException($"Retail orbital count {count} is outside the supported current-client geometry.");

        string lightColor = ReadString(globals, "LIGHTCOLOR", "LightColor.Earth");
        string skyColor = ReadString(globals, "SKYCOLOR", "SkyColor.Earth");
        string skyEmissive = ReadString(globals, "SKYEMISSIVE", "SkyEmissive.Default");

        var orbitals = new List<EnbRetailOrbitalDefinition>(count);
        for (int i = 0; i < count; i++)
        {
            string sectionName = $"ORBITAL{i:000}";
            // 004F2960 returns null for an absent section; 004F34C0 skips
            // that slot and continues, preserving subsequent authored indexes.
            if (!ini.TryGetSection(sectionName, out IReadOnlyDictionary<string, string> values))
                continue;

            orbitals.Add(new EnbRetailOrbitalDefinition(
                i,
                ReadInt(values, "OBJECT", 0),
                ReadFloat(values, "SCALE", 1f),
                ReadFloat(values, "HUE", 0f),
                ReadFloat(values, "SAT", 0f),
                ReadFloat(values, "VAL", 0f),
                ReadFloat(values, "STARTANGLE", 0f),
                ReadFloat(values, "RX", 0f),
                ReadFloat(values, "RZ", 0f),
                ReadFloat(values, "DISTANCE", 100f),
                ReadFloat(values, "ORBITSPERHOUR", 0f),
                ReadFloat(values, "PHASEPERHOUR", 0f),
                EnbRetailIniDocument.ReadBool(values, "EMITS_LIGHT"),
                ReadString(values, "LIGHTINTENSITY", "LightIntensity.Sun"),
                ReadString(values, "SKYINTENSITY", "SkyIntensity.Sun"),
                ReadFloat(values, "LIGHTRADIUS", 0f),
                EnbRetailIniDocument.ReadBool(values, "FORCE_VISIBLE")));
        }

        return new EnbRetailOrbitalSceneDefinition(lightColor, skyColor, skyEmissive, orbitals);
    }

    private static string ReadString(IReadOnlyDictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
        => EnbRetailIniDocument.TryReadInt(values, key, out int value) ? value : fallback;

    private static float ReadFloat(IReadOnlyDictionary<string, string> values, string key, float fallback)
        => EnbRetailIniDocument.TryReadFloat(values, key, out float value) ? value : fallback;
}

public enum EnbRetailScalarSplineKind : byte
{
    Linear = 3,
    Hermite = 4,
}

public readonly record struct EnbRetailScalarSplineKey(float Value, float Time);

/// <summary>
/// Current-client scalar spline semantics used by orbital color/intensity curves.
/// Type-4 WSD records carry two stored tangent floats per key, but the current
/// Type4 constructor/AddKey path leaves the dirty latch set and SetTangents does
/// not clear it. The first sample therefore runs 008D5B20 and rebuilds those
/// tangents. This evaluator intentionally reproduces that observable behavior.
/// </summary>
public sealed class EnbRetailScalarSpline
{
    private readonly EnbRetailScalarSplineKey[] _keys;
    private readonly (float Incoming, float Outgoing)[]? _tangents;

    public EnbRetailScalarSpline(EnbRetailScalarSplineKind kind, bool loop, IEnumerable<EnbRetailScalarSplineKey> keys)
    {
        Kind = kind;
        Loop = loop;
        _keys = keys.OrderBy(static key => key.Time).ToArray();
        if (_keys.Any(static key => !float.IsFinite(key.Value) || !float.IsFinite(key.Time)))
            throw new InvalidDataException("Retail scalar spline contains a non-finite key.");
        if (kind == EnbRetailScalarSplineKind.Hermite)
            _tangents = BuildTangents(_keys, loop);
    }

    public EnbRetailScalarSplineKind Kind { get; }
    public bool Loop { get; }
    public IReadOnlyList<EnbRetailScalarSplineKey> Keys => _keys;

    public float Sample(float time)
    {
        if (!float.IsFinite(time))
            throw new ArgumentOutOfRangeException(nameof(time));
        if (_keys.Length == 0)
            return 0f;
        if (_keys.Length == 1)
            return _keys[0].Value;

        if (!TryFindSegment(time, out int leftIndex, out int rightIndex, out float s))
            return time < _keys[0].Time ? _keys[0].Value : _keys[^1].Value;

        EnbRetailScalarSplineKey left = _keys[leftIndex];
        EnbRetailScalarSplineKey right = _keys[rightIndex];
        if (Kind == EnbRetailScalarSplineKind.Linear)
            return left.Value + (right.Value - left.Value) * s;

        (float Incoming, float Outgoing) leftTangent = _tangents![leftIndex];
        (float Incoming, float Outgoing) rightTangent = _tangents[rightIndex];
        float s2 = s * s;
        float s3 = s2 * s;
        float h00 = 2f * s3 - 3f * s2 + 1f;
        float h10 = s3 - 2f * s2 + s;
        float h01 = -2f * s3 + 3f * s2;
        float h11 = s3 - s2;
        return h00 * left.Value + h10 * leftTangent.Outgoing + h01 * right.Value + h11 * rightTangent.Incoming;
    }

    private bool TryFindSegment(float time, out int leftIndex, out int rightIndex, out float s)
    {
        leftIndex = 0;
        rightIndex = 1;
        s = 0f;

        float first = _keys[0].Time;
        float last = _keys[^1].Time;
        if (!Loop && (time < first || time > last))
            return false;

        if (Loop && (time < first || time > last))
        {
            leftIndex = _keys.Length - 1;
            rightIndex = 0;
            float sample = time < first ? time + 1f : time;
            float rightTime = first + 1f;
            float denominator = rightTime - last;
            s = denominator == 0f ? 0f : (sample - last) / denominator;
            return true;
        }

        for (int i = 0; i < _keys.Length - 1; i++)
        {
            float t0 = _keys[i].Time;
            float t1 = _keys[i + 1].Time;
            if (time < t0 || time > t1)
                continue;
            leftIndex = i;
            rightIndex = i + 1;
            float denominator = t1 - t0;
            s = denominator == 0f ? 0f : (time - t0) / denominator;
            return true;
        }

        return false;
    }

    private static (float Incoming, float Outgoing)[] BuildTangents(EnbRetailScalarSplineKey[] keys, bool loop)
    {
        var tangents = new (float Incoming, float Outgoing)[keys.Length];
        if (keys.Length < 2)
            return tangents;

        int last = keys.Length - 1;
        if (loop)
        {
            // Current 008D5B20 treats the final key as the loop-closing copy
            // and derives the shared endpoint tangent from key 1 versus the
            // penultimate key. It stores that value as first.Outgoing and
            // last.Incoming before applying the endpoint time weighting.
            float baseTangent = 0.5f * (keys[1].Value - keys[last - 1].Value);
            tangents[0].Outgoing = baseTangent;
            tangents[last].Incoming = baseTangent;

            float dtFirst = keys[1].Time - keys[0].Time;
            float dtLast = keys[last].Time - keys[last - 1].Time;
            float total = dtFirst + dtLast;
            if (total != 0f)
            {
                tangents[0].Outgoing *= 2f * dtFirst / total;
                tangents[last].Incoming *= 2f * dtLast / total;
            }
        }
        else
        {
            tangents[0].Outgoing = 0.25f * (keys[1].Value - keys[0].Value);
            tangents[last].Incoming = 0.25f * (keys[last].Value - keys[last - 1].Value);
        }

        for (int i = 1; i < last; i++)
        {
            float baseTangent = 0.5f * (keys[i + 1].Value - keys[i - 1].Value);
            float total = keys[i + 1].Time - keys[i - 1].Time;
            float incoming = baseTangent;
            float outgoing = baseTangent;
            if (total != 0f)
            {
                incoming *= 2f * (keys[i].Time - keys[i - 1].Time) / total;
                outgoing *= 2f * (keys[i + 1].Time - keys[i].Time) / total;
            }
            tangents[i] = (incoming, outgoing);
        }

        return tangents;
    }
}

public sealed class EnbRetailScalarSplineDatabase
{
    private const uint SizeMask = 0x7fff_ffffu;
    private const uint WsdRootChunk = 0x800u;
    private const uint WsdStringTableChunk = 0x808u;
    private const uint WsdRecordChunk = 0x810u;
    private const uint WsdRecordWithExtraStringChunk = 0x811u;
    private const uint WsdVersion = 0x100u;
    private readonly Dictionary<uint, EnbRetailScalarSpline> _splines;
    private readonly Dictionary<uint, byte> _entryTypes;

    private EnbRetailScalarSplineDatabase(
        Dictionary<uint, EnbRetailScalarSpline> splines,
        Dictionary<uint, byte> entryTypes)
    {
        _splines = splines;
        _entryTypes = entryTypes;
    }

    public int Count => _splines.Count;

    public static EnbRetailScalarSplineDatabase CreateEmpty()
        => new(new Dictionary<uint, EnbRetailScalarSpline>(), new Dictionary<uint, byte>());

    public static bool BeginsWithCurrentRetailRoot(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
            return false;
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]);
        return type == WsdRootChunk;
    }

    public static string DescribeRootHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
            return $"root header truncated ({bytes.Length}/8 bytes)";

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]);
        uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        uint payloadSize = rawSize & SizeMask;
        return $"root type 0x{type:X}, raw size 0x{rawSize:X8}, payload {payloadSize}";
    }

    public static EnbRetailScalarSplineDatabase ParseWsd(ReadOnlySpan<byte> bytes)
    {
        Chunk root = ReadChunk(bytes, 0, bytes.Length, "splines.wsd root");
        if (root.Type != WsdRootChunk)
            throw new InvalidDataException(
                $"splines.wsd does not begin with current-retail root type 0x800 ({DescribeRootHeader(bytes)}).");

        ReadOnlySpan<byte> rootPayload = bytes.Slice(root.PayloadOffset, root.PayloadLength);
        if (rootPayload.Length < 16)
            throw new InvalidDataException("splines.wsd 0x800 header is shorter than 16 bytes.");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(rootPayload[..4]);
        uint recordCountRaw = BinaryPrimitives.ReadUInt32LittleEndian(rootPayload.Slice(4, 4));
        if (version != WsdVersion)
            throw new InvalidDataException($"splines.wsd version 0x{version:X} is not current-retail 0x{WsdVersion:X}.");
        if (recordCountRaw > 1_000_000u)
            throw new InvalidDataException($"splines.wsd record count {recordCountRaw} is unreasonable.");
        int recordCount = checked((int)recordCountRaw);

        // SplineDatabaseParse @ 00488500 closes the 0x800 chunk after
        // reading its 16-byte header, then opens 0x808 from the file
        // stream at the next top-level position. The chunk reader
        // (009D4310/009D4370) advances a parent only when one remains
        // open. Therefore 0x808 and the following 0x810/0x811 records
        // are siblings of 0x800, not children inside its payload.
        int offset = root.EndOffset;
        Chunk strings = ReadChunk(bytes, offset, bytes.Length, "splines.wsd string table");
        if (strings.Type != WsdStringTableChunk)
            throw new InvalidDataException($"splines.wsd expected top-level 0x808 string table, got 0x{strings.Type:X}.");
        ValidateStringTable(bytes.Slice(strings.PayloadOffset, strings.PayloadLength));
        offset = strings.EndOffset;

        var splines = new Dictionary<uint, EnbRetailScalarSpline>();
        var entryTypes = new Dictionary<uint, byte>();
        for (int i = 0; i < recordCount; i++)
        {
            Chunk record = ReadChunk(bytes, offset, bytes.Length, $"splines.wsd record {i}");
            if (record.Type is not (WsdRecordChunk or WsdRecordWithExtraStringChunk))
                throw new InvalidDataException($"splines.wsd record {i} has unexpected top-level chunk 0x{record.Type:X}.");
            ParseBaseRecord(
                bytes.Slice(record.PayloadOffset, record.PayloadLength),
                record.Type,
                splines,
                entryTypes);
            offset = record.EndOffset;
        }

        return new EnbRetailScalarSplineDatabase(splines, entryTypes);
    }

    public EnbRetailScalarSplineDatabase Clone()
        => new(
            new Dictionary<uint, EnbRetailScalarSpline>(_splines),
            new Dictionary<uint, byte>(_entryTypes));

    public void ApplyRuntimeUpdate(ReadOnlySpan<byte> payload)
    {
        // 00486F60 walks the 0x0038 blob through a raw cursor: a u16 count, then
        // per record a CRC and, for a nonzero CRC, 00489830 at the type byte.
        // Nothing bounds the cursor, so a record that runs past the payload is
        // an undefined native read. Safety rule: stop there and keep the
        // records already applied. Trailing bytes after the count are ignored.
        if (payload.Length < 2) return;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        int offset = 2;
        for (int i = 0; i < count; i++)
        {
            if (payload.Length - offset < 4) return;
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
            offset += 4;
            // 00486F60 consumes only the CRC of a zero-CRC record.
            if (crc == 0) continue;
            if (payload.Length - offset < 1) return;
            byte type = payload[offset];
            // 00489830 accepts a record only for an unset wrapper type (-1) or the
            // same type; otherwise it asserts and leaves the cursor at this type
            // byte, where the next record's CRC is then read.
            if (_entryTypes.TryGetValue(crc, out byte existingType) && existingType != type)
                continue;
            offset++;
            // The wrapper type is stored and the current curve destroyed before
            // the record body is read.
            _entryTypes[crc] = type;
            _splines.Remove(crc);
            switch (type)
            {
                case 1:
                    if (payload.Length - offset < 12) return;
                    offset += 12;
                    break;
                case 2:
                {
                    if (payload.Length - offset < 12) return;
                    float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]));
                    offset += 12; // value, then two reference CRCs not needed by scalar sampling
                    _splines[crc] = new EnbRetailScalarSpline(
                        EnbRetailScalarSplineKind.Hermite,
                        loop: false,
                        new[] { new EnbRetailScalarSplineKey(value, 0.5f) });
                    break;
                }
                case 3:
                case 4:
                {
                    if (payload.Length - offset < 5) return;
                    bool loop = payload[offset] != 0;
                    uint keyCount = BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 1)..]);
                    offset += 5;
                    if (keyCount > (uint)((payload.Length - offset) / 8)) return;
                    var keys = new EnbRetailScalarSplineKey[(int)keyCount];
                    for (int key = 0; key < keys.Length; key++, offset += 8)
                        keys[key] = new EnbRetailScalarSplineKey(
                            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[offset..])),
                            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 4)..])));
                    _splines[crc] = new EnbRetailScalarSpline(
                        type == 3 ? EnbRetailScalarSplineKind.Linear : EnbRetailScalarSplineKind.Hermite,
                        loop,
                        keys);
                    break;
                }
                default:
                    // 00489830 asserts and advances past the type byte only.
                    break;
            }
        }
    }

    public bool TrySample(uint crc, float time, out float value)
    {
        if (_splines.TryGetValue(crc, out EnbRetailScalarSpline? spline))
        {
            value = spline.Sample(time);
            return true;
        }
        value = 0f;
        return false;
    }

    public bool TrySample(string name, char component, float time, out float value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (component is not ('R' or 'G' or 'B'))
            throw new ArgumentOutOfRangeException(nameof(component));
        return TrySample(ComputeCurrentRetailCrc32($"{name}_{component}"), time, out value);
    }

    public static uint ComputeCurrentRetailCrc32(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        uint crc = 0xffff_ffffu;
        foreach (byte b in Encoding.Latin1.GetBytes(value))
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb8_8320u : 0u);
        }
        return ~crc;
    }

    private static void ValidateStringTable(ReadOnlySpan<byte> payload)
    {
        Require(payload, 0, 4, "splines.wsd 0x808 string count");
        uint countRaw = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        if (countRaw > 1_000_000u)
            throw new InvalidDataException($"splines.wsd string count {countRaw} is unreasonable.");
        int count = checked((int)countRaw);
        int offset = 4;
        for (int i = 0; i < count; i++)
        {
            Require(payload, offset, 4, $"splines.wsd string {i} length");
            uint lengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            if (lengthRaw > int.MaxValue)
                throw new InvalidDataException($"splines.wsd string {i} is too large.");
            int length = (int)lengthRaw;
            Require(payload, offset, length, $"splines.wsd string {i}");
            offset += length;
        }
        // FUN_009D4370 closes the chunk and skips any unread tail, so
        // current retail does not require exact payload exhaustion.
    }

    private static void ParseBaseRecord(
        ReadOnlySpan<byte> payload,
        uint chunkType,
        Dictionary<uint, EnbRetailScalarSpline> destination,
        Dictionary<uint, byte> entryTypes)
    {
        if (payload.Length < 0x24)
            throw new InvalidDataException("splines.wsd record is shorter than its 0x24-byte fixed header.");

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        uint nameLengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        if (nameLengthRaw > 0x80u)
            throw new InvalidDataException($"splines.wsd record name length {nameLengthRaw} exceeds current-retail 0x80 limit.");
        int nameLength = checked((int)nameLengthRaw);
        int offset = 0x24;
        Require(payload, offset, nameLength, "splines.wsd record name");
        string name = ReadCStringBytes(payload.Slice(offset, nameLength));
        offset += nameLength;

        if (chunkType == WsdRecordWithExtraStringChunk)
        {
            Require(payload, offset, 4, "splines.wsd 0x811 extra-string length");
            int extraLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4)));
            offset += 4;
            Require(payload, offset, extraLength, "splines.wsd 0x811 extra string");
            offset += extraLength;
        }

        if (string.IsNullOrEmpty(name))
            return;
        uint crc = ComputeCurrentRetailCrc32(name);
        if (type is < 1u or > 4u)
            throw new InvalidDataException($"splines.wsd record '{name}' has unsupported current-retail type {type}.");
        entryTypes[crc] = checked((byte)type);

        switch (type)
        {
            case 1:
                // The current type-1 wrapper has no scalar +0x20 curve. Its
                // RGB component CRCs are derived from the record name, so
                // there is no scalar body for the orbital sampler to retain.
                return;
            case 2:
                // SplineRecordCtor case 2 installs the same Hermite curve
                // implementation used by type 4 at wrapper +0x20, and
                // FUN_00488DC0 reads its full loop/key/tangent body. The two
                // trailing reference fields do not participate in the scalar
                // +0x20 pointer returned to OrbitalManager, but their exact
                // on-disk framing is consumed here to keep the record grammar
                // bounded to current retail.
                destination[crc] = ReadBaseCurve(payload, ref offset, EnbRetailScalarSplineKind.Hermite);
                SkipBaseType2References(payload, ref offset, chunkType);
                return;
            case 4:
                destination[crc] = ReadBaseCurve(payload, ref offset, EnbRetailScalarSplineKind.Hermite);
                return;
            case 3:
                destination[crc] = ReadBaseCurve(payload, ref offset, EnbRetailScalarSplineKind.Linear);
                return;
        }
    }


    private static void SkipBaseType2References(ReadOnlySpan<byte> payload, ref int offset, uint chunkType)
    {
        if (chunkType == WsdRecordWithExtraStringChunk)
        {
            // Current 0x811 stores the two already-resolved CRCs directly.
            Require(payload, offset, 8, "splines.wsd type-2 0x811 reference CRCs");
            offset += 8;
            return;
        }

        // Current 0x810 stores both references as u32 byte length followed by
        // that many bytes; the retail loader CRCs each string after reading it.
        for (int i = 0; i < 2; i++)
        {
            Require(payload, offset, 4, "splines.wsd type-2 reference-string length");
            uint lengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            if (lengthRaw > int.MaxValue)
                throw new InvalidDataException("splines.wsd type-2 reference string is too large.");
            int length = (int)lengthRaw;
            Require(payload, offset, length, "splines.wsd type-2 reference string");
            offset += length;
        }
    }

    private static EnbRetailScalarSpline ReadBaseCurve(
        ReadOnlySpan<byte> payload,
        ref int offset,
        EnbRetailScalarSplineKind kind)
    {
        Require(payload, offset, 5, "splines.wsd curve header");
        bool loop = payload[offset++] != 0;
        uint keyCountRaw = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
        offset += 4;
        if (keyCountRaw > 1_000_000u)
            throw new InvalidDataException($"splines.wsd key count {keyCountRaw} is unreasonable.");
        int keyCount = checked((int)keyCountRaw);
        int stride = kind == EnbRetailScalarSplineKind.Linear ? 12 : 20;
        Require(payload, offset, checked(keyCount * stride), "splines.wsd curve keys");

        var keys = new EnbRetailScalarSplineKey[keyCount];
        for (int i = 0; i < keyCount; i++)
        {
            float value = ReadF32(payload, offset);
            float time = ReadF32(payload, offset + 4);
            keys[i] = new EnbRetailScalarSplineKey(value, time);
            offset += stride;
        }

        return new EnbRetailScalarSpline(kind, loop, keys);
    }

    private static string ReadCStringBytes(ReadOnlySpan<byte> bytes)
    {
        int zero = bytes.IndexOf((byte)0);
        if (zero >= 0)
            bytes = bytes[..zero];
        return Encoding.Latin1.GetString(bytes);
    }

    private static float ReadF32(ReadOnlySpan<byte> bytes, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4)));

    private static void Require(ReadOnlySpan<byte> bytes, int offset, int length, string context)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
            throw new InvalidDataException($"{context} exceeds its bounded record.");
    }

    private static Chunk ReadChunk(ReadOnlySpan<byte> bytes, int offset, int end, string context)
    {
        if (offset < 0 || end < offset || end > bytes.Length || end - offset < 8)
            throw new InvalidDataException($"{context} has no complete 8-byte chunk header.");
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
        uint sizeRaw = rawSize & SizeMask;
        if (sizeRaw > int.MaxValue)
            throw new InvalidDataException($"{context} chunk is too large.");
        int size = (int)sizeRaw;
        int payloadOffset = checked(offset + 8);
        int chunkEnd = checked(payloadOffset + size);
        if (chunkEnd > end)
            throw new InvalidDataException($"{context} chunk overruns its parent.");
        return new Chunk(type, payloadOffset, size, chunkEnd);
    }

    private readonly record struct Chunk(uint Type, int PayloadOffset, int PayloadLength, int EndOffset);
}
