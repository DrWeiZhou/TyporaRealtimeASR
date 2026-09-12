namespace TyporaAsr;

public sealed record AudioSegment(long Start, long End, short[] Samples, bool NeedsReview);

/// <summary>20 ms energy frames; audio ownership is independent of capture buffers.</summary>
public sealed class Segmenter(int rate = 16000, int silenceMs = 450, int maxSeconds = 15, double threshold = 0.012)
{
    private readonly List<short> active = [];
    private readonly Queue<short> preroll = new();
    private long position, start;
    private int quiet;
    private bool speaking, continuation;
    public long? ActiveStart=>speaking?start:null;
    public AudioSegment? Snapshot() => speaking ? new(start,position,active.ToArray(),continuation) : null;
    public List<AudioSegment> Push(short[] pcm)
    {
        var result = new List<AudioSegment>();
        for(var offset=0;offset<pcm.Length;offset+=rate/50) {
            var frame = pcm.AsSpan(offset,Math.Min(rate/50,pcm.Length-offset));
            double sum=0; foreach(var sample in frame) sum+=(double)sample*sample;
            var voiced = Math.Sqrt(sum/frame.Length)/32768 >= threshold;
            if(!speaking && voiced) {
                start=position-preroll.Count; active.AddRange(preroll); preroll.Clear(); speaking=true;
            }
            position+=frame.Length;
            if(speaking) {
                foreach(var sample in frame) active.Add(sample);
                quiet=voiced ? 0 : quiet+frame.Length;
                if(quiet >= rate*silenceMs/1000 || active.Count >= rate*maxSeconds) {
                    var forced = quiet < rate*silenceMs/1000;
                    result.Add(new(start,position,active.ToArray(),forced || continuation));
                    // Conservative policy: both sides of an artificial boundary require review.
                    active.Clear(); speaking=false; quiet=0; continuation=forced;
                }
            } else {
                foreach(var sample in frame) preroll.Enqueue(sample);
                while(preroll.Count>rate*3/10) preroll.Dequeue();
                if(!voiced && preroll.Count>=rate*3/10) continuation=false;
            }
        }
        return result;
    }
    public AudioSegment? Flush() {
        var last=Snapshot(); active.Clear(); preroll.Clear(); speaking=false; quiet=0; continuation=false; return last;
    }
}
