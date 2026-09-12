using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace TyporaAsr;

public sealed class RecordingSession : IAsyncDisposable
{
    private readonly object gate=new();
    private readonly Ledger ledger;
    private readonly AudioStore audio;
    private readonly AsrClient asr;
    private readonly Segmenter segmenter=new();
    private readonly CancellationTokenSource cancel=new();
    private readonly Task worker;
    private WasapiCapture? capture;
    private MMDevice? endpoint;
    private PcmConverter? converter;
    private AudioSegment? preview;
    private long lastPreview;
    private int revision;
    private volatile bool recording;
    private volatile bool processing;
    private volatile bool inputClosed;
    private string captureError="";
    private TaskCompletionSource stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Id {get;}
    public string DocumentId {get;}
    public string Path {get;}
    public string Error {get;private set;}="";
    public object? Hypothesis {get;private set;}
    public bool Recording=>recording;
    public RecordingSession(string id,string document,string path,string root,Ledger ledger,AsrClient asr,bool recover=false) {
        Id=id;DocumentId=document;Path=path;this.ledger=ledger;this.asr=asr;
        ledger.CreateSession(id,document,path);
        audio=new AudioStore(System.IO.Path.Combine(root,"audio",id+".pcm"));
        if(recover) {
            var end=Math.Max(ledger.ScheduledEnd(id),ledger.Progress(id));
            // Audio written before a crash but not scheduled: bounded, explicitly reviewable recovery chunks.
            while(end<audio.Samples) {var next=Math.Min(end+16000*15,audio.Samples);ledger.AddJob(id,$"{id}:{end}",end,next,true);end=next;}
            inputClosed=true;
        }
        worker=Task.Run(Work);
    }
    public void Start(int device) {
        lock(gate) {
            if(capture!=null || audio.Samples!=0)throw new InvalidOperationException("A recording cannot be started twice; create a new session.");
            using var enumerator=new MMDeviceEnumerator();
            endpoint=device<0?enumerator.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Console):enumerator.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active)[device];
            capture=new WasapiCapture(endpoint);
            converter=new PcmConverter(capture.WaveFormat);
            capture.DataAvailable+=(_,e)=>{
                try {Accept(converter.Push(e.Buffer,0,e.BytesRecorded));}
                catch(Exception error) {captureError="录音保存失败: "+error.Message;capture?.StopRecording();}
            };
            capture.RecordingStopped+=(_,e)=>{
                lock(gate) {recording=false;try{if(e.Exception!=null)captureError=e.Exception.Message;Accept(converter.Flush());FinalizeTail();stopped.TrySetResult();}catch(Exception error){captureError=error.Message;stopped.TrySetException(error);}}
            };
            recording=true;
            try {capture.StartRecording();} catch {recording=false;capture.Dispose();capture=null;endpoint.Dispose();endpoint=null;throw;}
        }
    }
    public void Accept(short[] pcm) {
        lock(gate) {
            if(inputClosed)throw new InvalidOperationException("Recording input is closed");
            audio.Append(pcm);
            foreach(var segment in segmenter.Push(pcm)) Enqueue(segment);
            ledger.SetProgress(Id,segmenter.ActiveStart ?? audio.Samples);
            if(audio.Samples-lastPreview>=32000) {preview=segmenter.Snapshot();lastPreview=audio.Samples;}
        }
    }
    private void Enqueue(AudioSegment s) {ledger.AddJob(Id,$"{Id}:{s.Start}",s.Start,s.End,s.NeedsReview);preview=null;Hypothesis=null;}
    private void FinalizeTail(){var tail=segmenter.Flush();if(tail!=null)Enqueue(tail);ledger.SetProgress(Id,audio.Samples);preview=null;inputClosed=true;}
    public async Task Stop() {
        WasapiCapture? device;lock(gate)device=capture;
        if(device!=null && recording){device.StopRecording();await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));device.Dispose();lock(gate){capture=null;endpoint?.Dispose();endpoint=null;}}
        else lock(gate)FinalizeTail();
    }
    public object Status()=>new {sessionId=Id,documentId=DocumentId,path=Path,recording,processing,samples=audio.Samples,seconds=audio.Samples/16000.0,pending=ledger.PendingCount(Id),error=captureError.Length>0?captureError:Error,hypothesis=Hypothesis};
    private async Task Work() {
        while(!cancel.IsCancellationRequested) {
            try {
                var job=ledger.Pending(Id).FirstOrDefault();
                AudioSegment? snap=null;
                if(job.Id==null)lock(gate){snap=preview;preview=null;}
                if(job.Id==null && snap==null){if(inputClosed && ledger.PendingCount(Id)==0)break;await Task.Delay(150,cancel.Token);continue;}
                processing=true;
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);timeout.CancelAfter(TimeSpan.FromSeconds(90));
                try {
                    var text=await asr.Recognize(job.Id!=null?audio.Read(job.Start,job.End):snap!.Samples,timeout.Token);
                    if(job.Id!=null){ledger.AddFinal(Id,job.Id,job.Start,job.End,text,job.Review);Hypothesis=null;}
                    else lock(gate) {
                        // A result for a finalized segment must never resurrect its preview.
                        var current=segmenter.Snapshot();
                        if(current?.Start==snap!.Start)Hypothesis=new {segmentId=$"{Id}:{snap.Start}",revision=++revision,text,end=snap.End};
                    }
                    Error="";
                } catch(Exception e) when(!cancel.IsCancellationRequested) {
                    Error=e.Message;if(job.Id!=null)ledger.FailJob(job.Id,e.Message);
                    await Task.Delay(2000,cancel.Token);
                } finally {processing=false;}
            } catch(OperationCanceledException) when(cancel.IsCancellationRequested){break;}
            catch(Exception e){Error=e.Message;await Task.Delay(1000);}
        }
    }
    public async ValueTask DisposeAsync(){await Stop();cancel.Cancel();await worker;capture?.Dispose();endpoint?.Dispose();audio.Dispose();cancel.Dispose();}
}
