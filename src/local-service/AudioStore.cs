namespace TyporaAsr;

/// <summary>PCM16 with fsync before recognition. A raw tail needs no WAV header repair.</summary>
public sealed class AudioStore : IDisposable
{
    private readonly FileStream stream;
    private readonly object gate=new();
    public long Samples { get { lock(gate) return stream.Length/2; } }
    public AudioStore(string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        stream=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read,65536,FileOptions.None);
        if(stream.Length%2!=0) stream.SetLength(stream.Length-1);
        stream.Position=stream.Length;
    }
    public void Append(short[] samples) {
        var bytes=new byte[samples.Length*2];Buffer.BlockCopy(samples,0,bytes,0,bytes.Length);
        lock(gate) {stream.Position=stream.Length;stream.Write(bytes);stream.Flush(true);}
    }
    public short[] Read(long start,long end) {
        if(start<0 || end<start || end-start>16000*30)throw new ArgumentOutOfRangeException(nameof(end));
        lock(gate) {if(end>Samples)throw new EndOfStreamException();var bytes=new byte[checked((int)(end-start)*2)];stream.Position=start*2;stream.ReadExactly(bytes);var pcm=new short[bytes.Length/2];Buffer.BlockCopy(bytes,0,pcm,0,bytes.Length);return pcm;}
    }
    public void Dispose(){lock(gate)stream.Dispose();}
}
