using System.Buffers.Binary;

namespace EnbNext.Assets;

/// <summary>
/// Small managed RIFF/WAVE decoder for retail sound resources. OpenAL core takes
/// decoded sample frames rather than compressed WAVE containers, so R6Z26 keeps
/// decoding deterministic and offline-testable instead of delegating authored
/// bytes to a platform media stack.
/// </summary>
public static class EnbRetailWaveAudio
{
    private const ushort WaveFormatPcm = 0x0001;
    private const ushort WaveFormatMsAdpcm = 0x0002;
    private const ushort WaveFormatIeeeFloat = 0x0003;
    private const ushort WaveFormatImaAdpcm = 0x0011;
    private const ushort WaveFormatExtensible = 0xFFFE;

    private static readonly int[] ImaIndexTable =
    [
        -1, -1, -1, -1, 2, 4, 6, 8,
        -1, -1, -1, -1, 2, 4, 6, 8,
    ];

    private static readonly int[] ImaStepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
        34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
        157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544,
        598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707,
        1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871,
        5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    ];

    private static readonly int[] MsAdpcmAdaptationTable =
    [
        230, 230, 230, 230, 307, 409, 512, 614,
        768, 614, 512, 409, 307, 230, 230, 230,
    ];

    private static readonly (short Coeff1, short Coeff2)[] MsAdpcmDefaultCoefficients =
    [
        (256, 0), (512, -256), (0, 0), (192, 64),
        (240, 0), (460, -208), (392, -232),
    ];

    public static bool TryDecodePcm16(
        ReadOnlySpan<byte> bytes,
        out EnbRetailWavePcm16 pcm,
        out string? diagnostic)
    {
        pcm = default!;
        diagnostic = null;

        if (bytes.Length < 12 || !IsFourCc(bytes, 0, "RIFF") || !IsFourCc(bytes, 8, "WAVE"))
        {
            diagnostic = "resource is not a RIFF/WAVE container.";
            return false;
        }

        byte[]? formatChunk = null;
        byte[]? dataChunk = null;
        int cursor = 12;
        while (cursor <= bytes.Length - 8)
        {
            uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor + 4, 4));
            if (rawLength > int.MaxValue)
            {
                diagnostic = "WAVE chunk exceeds managed decode limits.";
                return false;
            }

            int length = (int)rawLength;
            int dataOffset = cursor + 8;
            if (length < 0 || dataOffset > bytes.Length - length)
            {
                diagnostic = "WAVE chunk extends beyond the resource.";
                return false;
            }

            if (IsFourCc(bytes, cursor, "fmt ") && formatChunk is null)
                formatChunk = bytes.Slice(dataOffset, length).ToArray();
            else if (IsFourCc(bytes, cursor, "data") && dataChunk is null)
                dataChunk = bytes.Slice(dataOffset, length).ToArray();

            long next = (long)dataOffset + length + (length & 1);
            if (next > bytes.Length)
                break;
            cursor = (int)next;
        }

        if (formatChunk is null || formatChunk.Length < 16 || dataChunk is null)
        {
            diagnostic = "WAVE resource is missing a complete fmt/data pair.";
            return false;
        }

        ReadOnlySpan<byte> fmt = formatChunk;
        ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(0, 2));
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(2, 2));
        uint sampleRateRaw = BinaryPrimitives.ReadUInt32LittleEndian(fmt.Slice(4, 4));
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(12, 2));
        int bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(14, 2));

        if (formatTag == WaveFormatExtensible)
        {
            if (fmt.Length < 40)
            {
                diagnostic = "WAVE_FORMAT_EXTENSIBLE fmt chunk is truncated.";
                return false;
            }
            formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(24, 2));
        }

        if (channels is < 1 or > 2 || sampleRateRaw is 0 or > int.MaxValue)
        {
            diagnostic = $"WAVE channels/sample-rate are outside R6Z26 output support: channels={channels}, rate={sampleRateRaw}.";
            return false;
        }

        int sampleRate = (int)sampleRateRaw;
        try
        {
            return formatTag switch
            {
                WaveFormatPcm => TryDecodeIntegerPcm(dataChunk, channels, sampleRate, bitsPerSample, out pcm, out diagnostic),
                WaveFormatIeeeFloat => TryDecodeFloatPcm(dataChunk, channels, sampleRate, bitsPerSample, out pcm, out diagnostic),
                WaveFormatImaAdpcm => TryDecodeImaAdpcm(fmt, dataChunk, channels, sampleRate, blockAlign, out pcm, out diagnostic),
                WaveFormatMsAdpcm => TryDecodeMsAdpcm(fmt, dataChunk, channels, sampleRate, blockAlign, out pcm, out diagnostic),
                _ => Fail($"WAVE format 0x{formatTag:X4} is not decoded by R6Z26.", out pcm, out diagnostic),
            };
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException or InvalidDataException)
        {
            diagnostic = $"WAVE decode rejected malformed data: {ex.Message}";
            pcm = default!;
            return false;
        }
    }

    private static bool TryDecodeIntegerPcm(
        ReadOnlySpan<byte> data,
        int channels,
        int sampleRate,
        int bitsPerSample,
        out EnbRetailWavePcm16 pcm,
        out string? diagnostic)
    {
        int bytesPerSample = bitsPerSample switch
        {
            8 => 1,
            16 => 2,
            24 => 3,
            32 => 4,
            _ => 0,
        };
        if (bytesPerSample == 0 || data.Length % (bytesPerSample * channels) != 0)
            return Fail($"PCM{bitsPerSample} data is unsupported or not frame-aligned.", out pcm, out diagnostic);

        byte[] output = new byte[checked((data.Length / bytesPerSample) * 2)];
        int outputOffset = 0;
        for (int inputOffset = 0; inputOffset < data.Length; inputOffset += bytesPerSample)
        {
            short sample = bitsPerSample switch
            {
                8 => (short)((data[inputOffset] - 128) << 8),
                16 => BinaryPrimitives.ReadInt16LittleEndian(data.Slice(inputOffset, 2)),
                24 => (short)(ReadSigned24(data.Slice(inputOffset, 3)) >> 8),
                32 => (short)(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(inputOffset, 4)) >> 16),
                _ => 0,
            };
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(outputOffset, 2), sample);
            outputOffset += 2;
        }

        pcm = new EnbRetailWavePcm16(sampleRate, channels, output, $"PCM{bitsPerSample}");
        diagnostic = null;
        return true;
    }

    private static bool TryDecodeFloatPcm(
        ReadOnlySpan<byte> data,
        int channels,
        int sampleRate,
        int bitsPerSample,
        out EnbRetailWavePcm16 pcm,
        out string? diagnostic)
    {
        int bytesPerSample = bitsPerSample switch { 32 => 4, 64 => 8, _ => 0 };
        if (bytesPerSample == 0 || data.Length % (bytesPerSample * channels) != 0)
            return Fail($"IEEE-float PCM{bitsPerSample} data is unsupported or not frame-aligned.", out pcm, out diagnostic);

        byte[] output = new byte[checked((data.Length / bytesPerSample) * 2)];
        int outputOffset = 0;
        for (int inputOffset = 0; inputOffset < data.Length; inputOffset += bytesPerSample)
        {
            double value = bitsPerSample == 32
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(inputOffset, 4)))
                : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data.Slice(inputOffset, 8)));
            if (!double.IsFinite(value))
                value = 0.0;
            value = Math.Clamp(value, -1.0, 1.0);
            short sample = value <= -1.0 ? short.MinValue : (short)Math.Round(value * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(outputOffset, 2), sample);
            outputOffset += 2;
        }

        pcm = new EnbRetailWavePcm16(sampleRate, channels, output, $"IEEE-float{bitsPerSample}");
        diagnostic = null;
        return true;
    }

    private static bool TryDecodeImaAdpcm(
        ReadOnlySpan<byte> fmt,
        ReadOnlySpan<byte> data,
        int channels,
        int sampleRate,
        int blockAlign,
        out EnbRetailWavePcm16 pcm,
        out string? diagnostic)
    {
        if (fmt.Length < 20 || blockAlign < channels * 4)
            return Fail("IMA ADPCM fmt/block alignment is incomplete.", out pcm, out diagnostic);

        int samplesPerBlock = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(18, 2));
        if (samplesPerBlock <= 0)
            samplesPerBlock = (((blockAlign - (4 * channels)) * 2) / channels) + 1;
        if (samplesPerBlock <= 0)
            return Fail("IMA ADPCM samples-per-block is invalid.", out pcm, out diagnostic);

        var samples = new List<short>(Math.Max(samplesPerBlock * channels, 64));
        int blockOffset = 0;
        while (blockOffset < data.Length)
        {
            int blockLength = Math.Min(blockAlign, data.Length - blockOffset);
            ReadOnlySpan<byte> block = data.Slice(blockOffset, blockLength);
            if (block.Length < channels * 4)
                break;

            var predictors = new int[channels];
            var indexes = new int[channels];
            int header = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                predictors[channel] = BinaryPrimitives.ReadInt16LittleEndian(block.Slice(header, 2));
                indexes[channel] = block[header + 2];
                if (indexes[channel] > 88)
                    return Fail("IMA ADPCM step index exceeds 88.", out pcm, out diagnostic);
                header += 4;
            }

            for (int channel = 0; channel < channels; channel++)
                samples.Add((short)predictors[channel]);

            int framesProduced = 1;
            if (channels == 1)
            {
                for (int i = header; i < block.Length && framesProduced < samplesPerBlock; i++)
                {
                    byte encoded = block[i];
                    samples.Add(DecodeImaNibble(encoded & 0x0F, ref predictors[0], ref indexes[0]));
                    framesProduced++;
                    if (framesProduced >= samplesPerBlock)
                        break;
                    samples.Add(DecodeImaNibble(encoded >> 4, ref predictors[0], ref indexes[0]));
                    framesProduced++;
                }
            }
            else
            {
                int cursor = header;
                while (cursor < block.Length && framesProduced < samplesPerBlock)
                {
                    var channelSamples = new short[channels][];
                    int availableFrames = int.MaxValue;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        int byteCount = Math.Min(4, block.Length - cursor);
                        if (byteCount <= 0)
                        {
                            availableFrames = 0;
                            break;
                        }

                        var decoded = new short[byteCount * 2];
                        int decodedCount = 0;
                        for (int j = 0; j < byteCount; j++)
                        {
                            byte encoded = block[cursor++];
                            decoded[decodedCount++] = DecodeImaNibble(encoded & 0x0F, ref predictors[channel], ref indexes[channel]);
                            decoded[decodedCount++] = DecodeImaNibble(encoded >> 4, ref predictors[channel], ref indexes[channel]);
                        }
                        channelSamples[channel] = decoded;
                        availableFrames = Math.Min(availableFrames, decodedCount);
                    }

                    int frames = Math.Min(availableFrames, samplesPerBlock - framesProduced);
                    for (int frame = 0; frame < frames; frame++)
                        for (int channel = 0; channel < channels; channel++)
                            samples.Add(channelSamples[channel][frame]);
                    framesProduced += frames;
                    if (frames == 0)
                        break;
                }
            }

            blockOffset += blockAlign;
        }

        return FinishCompressed(samples, sampleRate, channels, "IMA-ADPCM", out pcm, out diagnostic);
    }

    private static bool TryDecodeMsAdpcm(
        ReadOnlySpan<byte> fmt,
        ReadOnlySpan<byte> data,
        int channels,
        int sampleRate,
        int blockAlign,
        out EnbRetailWavePcm16 pcm,
        out string? diagnostic)
    {
        int headerSize = 7 * channels;
        if (fmt.Length < 22 || blockAlign < headerSize)
            return Fail("Microsoft ADPCM fmt/block alignment is incomplete.", out pcm, out diagnostic);

        int samplesPerBlock = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(18, 2));
        int coefficientCount = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(20, 2));
        var coefficients = new List<(short Coeff1, short Coeff2)>();
        int coefficientCursor = 22;
        for (int i = 0; i < coefficientCount && coefficientCursor <= fmt.Length - 4; i++, coefficientCursor += 4)
        {
            coefficients.Add((
                BinaryPrimitives.ReadInt16LittleEndian(fmt.Slice(coefficientCursor, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(fmt.Slice(coefficientCursor + 2, 2))));
        }
        if (coefficients.Count == 0)
            coefficients.AddRange(MsAdpcmDefaultCoefficients);

        var samples = new List<short>(Math.Max(samplesPerBlock * channels, 64));
        for (int blockOffset = 0; blockOffset < data.Length; blockOffset += blockAlign)
        {
            int blockLength = Math.Min(blockAlign, data.Length - blockOffset);
            ReadOnlySpan<byte> block = data.Slice(blockOffset, blockLength);
            if (block.Length < headerSize)
                break;

            var states = new MsAdpcmState[channels];
            int cursor = 0;
            for (int channel = 0; channel < channels; channel++)
                states[channel].PredictorIndex = block[cursor++];
            for (int channel = 0; channel < channels; channel++, cursor += 2)
                states[channel].Delta = BinaryPrimitives.ReadInt16LittleEndian(block.Slice(cursor, 2));
            for (int channel = 0; channel < channels; channel++, cursor += 2)
                states[channel].Sample1 = BinaryPrimitives.ReadInt16LittleEndian(block.Slice(cursor, 2));
            for (int channel = 0; channel < channels; channel++, cursor += 2)
                states[channel].Sample2 = BinaryPrimitives.ReadInt16LittleEndian(block.Slice(cursor, 2));

            for (int channel = 0; channel < channels; channel++)
            {
                if (states[channel].PredictorIndex >= coefficients.Count)
                    return Fail("Microsoft ADPCM predictor index is outside the coefficient table.", out pcm, out diagnostic);
                states[channel].Delta = Math.Max(16, states[channel].Delta);
            }

            for (int channel = 0; channel < channels; channel++)
                samples.Add((short)states[channel].Sample2);
            for (int channel = 0; channel < channels; channel++)
                samples.Add((short)states[channel].Sample1);
            int framesProduced = 2;

            while (cursor < block.Length && (samplesPerBlock <= 0 || framesProduced < samplesPerBlock))
            {
                byte encoded = block[cursor++];
                if (channels == 1)
                {
                    samples.Add(DecodeMsAdpcmNibble(encoded >> 4, ref states[0], coefficients));
                    framesProduced++;
                    if (samplesPerBlock > 0 && framesProduced >= samplesPerBlock)
                        break;
                    samples.Add(DecodeMsAdpcmNibble(encoded & 0x0F, ref states[0], coefficients));
                    framesProduced++;
                }
                else
                {
                    samples.Add(DecodeMsAdpcmNibble(encoded >> 4, ref states[0], coefficients));
                    samples.Add(DecodeMsAdpcmNibble(encoded & 0x0F, ref states[1], coefficients));
                    framesProduced++;
                }
            }
        }

        return FinishCompressed(samples, sampleRate, channels, "MS-ADPCM", out pcm, out diagnostic);
    }

    private static short DecodeImaNibble(int nibble, ref int predictor, ref int index)
    {
        int step = ImaStepTable[index];
        int difference = step >> 3;
        if ((nibble & 1) != 0) difference += step >> 2;
        if ((nibble & 2) != 0) difference += step >> 1;
        if ((nibble & 4) != 0) difference += step;
        predictor += (nibble & 8) != 0 ? -difference : difference;
        predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + ImaIndexTable[nibble & 0x0F], 0, 88);
        return (short)predictor;
    }

    private static short DecodeMsAdpcmNibble(
        int nibble,
        ref MsAdpcmState state,
        IReadOnlyList<(short Coeff1, short Coeff2)> coefficients)
    {
        (short coeff1, short coeff2) = coefficients[state.PredictorIndex];
        int signedNibble = nibble >= 8 ? nibble - 16 : nibble;
        int prediction = ((state.Sample1 * coeff1) + (state.Sample2 * coeff2)) / 256;
        int value = Math.Clamp(prediction + (signedNibble * state.Delta), short.MinValue, short.MaxValue);
        state.Sample2 = state.Sample1;
        state.Sample1 = value;
        state.Delta = Math.Max(16, (MsAdpcmAdaptationTable[nibble & 0x0F] * state.Delta) / 256);
        return (short)value;
    }

    private static bool FinishCompressed(
        List<short> samples,
        int sampleRate,
        int channels,
        string sourceFormat,
        out EnbRetailWavePcm16 pcm,
        out string? diagnostic)
    {
        if (samples.Count == 0 || samples.Count % channels != 0)
            return Fail($"{sourceFormat} did not yield complete sample frames.", out pcm, out diagnostic);

        byte[] output = new byte[checked(samples.Count * 2)];
        for (int i = 0; i < samples.Count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2, 2), samples[i]);

        pcm = new EnbRetailWavePcm16(sampleRate, channels, output, sourceFormat);
        diagnostic = null;
        return true;
    }

    private static int ReadSigned24(ReadOnlySpan<byte> bytes)
    {
        int value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        if ((value & 0x0080_0000) != 0)
            value |= unchecked((int)0xFF00_0000);
        return value;
    }

    private static bool IsFourCc(ReadOnlySpan<byte> bytes, int offset, string value)
        => offset >= 0 && offset <= bytes.Length - 4 &&
           bytes[offset] == value[0] && bytes[offset + 1] == value[1] &&
           bytes[offset + 2] == value[2] && bytes[offset + 3] == value[3];

    private static bool Fail(
        string message,
        out EnbRetailWavePcm16 pcm,
        out string? diagnostic)
    {
        pcm = default!;
        diagnostic = message;
        return false;
    }

    private struct MsAdpcmState
    {
        public int PredictorIndex;
        public int Delta;
        public int Sample1;
        public int Sample2;
    }
}

public sealed record EnbRetailWavePcm16(
    int SampleRate,
    int Channels,
    byte[] Pcm16LittleEndian,
    string SourceFormat)
{
    public int FrameCount => Pcm16LittleEndian.Length / (Channels * 2);

    public double DurationSeconds => SampleRate <= 0 ? 0.0 : (double)FrameCount / SampleRate;
}

/// <summary>
/// Reads bytes from a sound choice already resolved against the selected retail
/// installation. No search policy lives here: loose-vs-MIX precedence was fixed
/// by EnbRetailSoundCatalog.Resolve.
/// </summary>
public static class EnbRetailSoundResourceReader
{
    public static bool TryRead(
        EnbRetailSoundResolvedChoice choice,
        EnbMixResourceCatalog? retailMixResources,
        out byte[] bytes,
        out string source,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(choice);
        bytes = [];
        source = "unresolved";
        diagnostic = null;

        try
        {
            if (choice.Status == EnbRetailSoundResourceStatus.LooseFile &&
                !string.IsNullOrWhiteSpace(choice.LoosePath))
            {
                bytes = File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(choice.LoosePath));
                source = $"loose:{choice.LoosePath}";
                return bytes.Length != 0 || Fail("resolved loose sound resource is empty.", out bytes, out diagnostic);
            }

            if (choice.Status == EnbRetailSoundResourceStatus.MixResource &&
                choice.MixLocation is EnbMixResourceLocation location &&
                retailMixResources is not null &&
                retailMixResources.TryReadResource(location, out bytes))
            {
                source = $"MIX:{location.ArchiveName}/{location.Name}";
                return bytes.Length != 0 || Fail("resolved MIX sound resource is empty.", out bytes, out diagnostic);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"sound resource read failed: {ex.Message}";
            return false;
        }

        diagnostic = $"sound resource status {choice.Status} did not produce readable bytes.";
        return false;
    }

    private static bool Fail(string message, out byte[] bytes, out string? diagnostic)
    {
        bytes = [];
        diagnostic = message;
        return false;
    }
}
