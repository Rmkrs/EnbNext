namespace EnbNext.Engine;

/// <summary>MP3 to interleaved little-endian PCM16 for music, speech and
/// effects, in managed code on every platform (NLayer). Replaces the Windows
/// ACM codec.</summary>
internal static class EnbMp3Decoder
{
    internal readonly record struct Pcm16(int SampleRate,int Channels,byte[] Bytes);

    internal static Pcm16 Decode(byte[] encoded,int pcmLimit=int.MaxValue,CancellationToken cancellation=default)
    {
        using var stream=new MemoryStream(encoded,false);
        using var mpeg=new NLayer.MpegFile(stream);
        int channels=mpeg.Channels,sampleRate=mpeg.SampleRate;
        if(channels is not (1 or 2)||sampleRate<=0)throw new InvalidDataException("MP3 decoder did not produce mono/stereo audio.");
        using var output=new MemoryStream();
        float[] samples=new float[16384];
        byte[] block=new byte[samples.Length*2];
        int read;
        while((read=mpeg.ReadSamples(samples,0,samples.Length))>0)
        {
            cancellation.ThrowIfCancellationRequested();
            for(int i=0;i<read;i++)
            {
                float value=samples[i]*32768f;
                short pcm=value>=32767f?short.MaxValue:value<=-32768f?short.MinValue:(short)MathF.Round(value);
                block[i*2]=(byte)pcm;block[i*2+1]=(byte)(pcm>>8);
            }
            if(output.Length+read*2>pcmLimit)throw new InvalidDataException("Sound exceeds PCM size limit");
            output.Write(block,0,read*2);
        }
        return new(sampleRate,channels,output.ToArray());
    }
}
