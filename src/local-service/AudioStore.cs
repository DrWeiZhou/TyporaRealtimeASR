using System.Text;
namespace TyporaAsr;

/// <summary>Durable PCM16 storage; .wav files have a repairable 44-byte RIFF header.</summary>
public sealed class AudioStore : IDisposable
{
    private readonly FileStream stream;
    private readonly object gate=new();
    private readonly int header;
    public long Samples { get { lock(gate) return (stream.Length-header)/2; } }
    public AudioStore(string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        header=path.EndsWith(".wav",StringComparison.OrdinalIgnoreCase)?44:0;
        stream=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read,65536,FileOptions.None);
        try {
            if(header!=0) {
                if(stream.Length==0){stream.SetLength(44);WriteHeader();}
                else {
                    if(stream.Length<44)throw new InvalidDataException("WAV header is incomplete");
                    using var reader=new BinaryReader(stream,Encoding.ASCII,true);stream.Position=0;
                    if(Encoding.ASCII.GetString(reader.ReadBytes(4))!="RIFF")throw new InvalidDataException("Not a RIFF WAV file");
                    stream.Position=8;if(Encoding.ASCII.GetString(reader.ReadBytes(8))!="WAVEfmt "||reader.ReadUInt32()!=16||reader.ReadUInt16()!=1||reader.ReadUInt16()!=1||reader.ReadUInt32()!=16000||reader.ReadUInt32()!=32000||reader.ReadUInt16()!=2||reader.ReadUInt16()!=16||Encoding.ASCII.GetString(reader.ReadBytes(4))!="data")throw new InvalidDataException("Expected 16kHz mono PCM16 WAV");
                }
            }
            if((stream.Length-header)%2!=0)stream.SetLength(stream.Length-1);
            if(header!=0)WriteHeader();stream.Position=stream.Length;
        }catch{stream.Dispose();throw;}
    }
    private void WriteHeader(){
        var bytes=stream.Length-44;if(bytes>uint.MaxValue-36)throw new IOException("WAV exceeds the RIFF size limit; start a new recording");
        stream.Position=0;using var writer=new BinaryWriter(stream,Encoding.ASCII,true);
        writer.Write("RIFF"u8);writer.Write((uint)(36+bytes));writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(16000);writer.Write(32000);writer.Write((short)2);writer.Write((short)16);writer.Write("data"u8);writer.Write((uint)bytes);writer.Flush();stream.Flush(true);
    }
    public void Append(short[] samples) {
        var bytes=new byte[samples.Length*2];Buffer.BlockCopy(samples,0,bytes,0,bytes.Length);
        lock(gate) {if(header!=0 && stream.Length-header+bytes.Length>uint.MaxValue-36)throw new IOException("WAV exceeds the RIFF size limit; start a new recording");stream.Position=stream.Length;stream.Write(bytes);stream.Flush(true);if(header!=0)WriteHeader();}
    }
    public short[] Read(long start,long end) {
        if(start<0 || end<start || end-start>16000*30)throw new ArgumentOutOfRangeException(nameof(end));
        lock(gate) {if(end>Samples)throw new EndOfStreamException();var bytes=new byte[checked((int)(end-start)*2)];stream.Position=header+start*2;stream.ReadExactly(bytes);var pcm=new short[bytes.Length/2];Buffer.BlockCopy(bytes,0,pcm,0,bytes.Length);return pcm;}
    }
    public void Dispose(){lock(gate)stream.Dispose();}
}
