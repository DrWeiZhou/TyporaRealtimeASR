using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TyporaAsr;

public sealed class PcmConverter
{
    private readonly BufferedWaveProvider input;
    private readonly ISampleProvider resampler;
    private readonly WaveFormat source;
    private long frames,produced;
    private bool ended;
    public PcmConverter(WaveFormat format) {
        source=format;input=new BufferedWaveProvider(format){ReadFully=false,BufferDuration=TimeSpan.FromSeconds(2),DiscardOnBufferOverflow=false};
        ISampleProvider samples=input.ToSampleProvider();
        if(format.Channels==2)samples=new StereoToMonoSampleProvider(samples){LeftVolume=0.5f,RightVolume=0.5f};
        else if(format.Channels!=1)throw new NotSupportedException("请选择单声道或双声道麦克风");
        resampler=format.SampleRate==16000?samples:new WdlResamplingSampleProvider(samples,16000);
    }
    public short[] Push(byte[] bytes,int offset,int count) {
        if(ended)throw new InvalidOperationException("Converter has been flushed");
        if(count%source.BlockAlign!=0)throw new InvalidDataException("Unaligned capture data");
        frames+=count/source.BlockAlign;input.AddSamples(bytes,offset,count);return Read(false);
    }
    public short[] Flush() {
        if(ended)return [];ended=true;
        // Supply interpolation look-ahead only; output is clipped to the real captured duration.
        var padding=new byte[source.BlockAlign*Math.Max(1024,source.SampleRate/20)];input.AddSamples(padding,0,padding.Length);
        return Read(true);
    }
    private short[] Read(bool final) {
        var target=frames*16000/source.SampleRate;
        var wanted=(int)Math.Max(0,target-produced-(final?0:128));
        var floats=new float[wanted];var read=0;
        while(read<wanted){var n=resampler.Read(floats,read,wanted-read);if(n==0)break;read+=n;}
        if(final && read!=wanted)throw new InvalidDataException("Resampler could not flush the audio tail");
        produced+=read;var pcm=new short[read];for(var i=0;i<read;i++)pcm[i]=(short)Math.Clamp((int)Math.Round(floats[i]*32768),short.MinValue,short.MaxValue);return pcm;
    }
}
