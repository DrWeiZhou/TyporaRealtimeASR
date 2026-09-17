namespace TyporaAsr;

public sealed class RecordingSession : IAsyncDisposable
{
    private readonly object gate=new();
    private readonly Ledger ledger;
    private readonly AudioStore audio;
    private readonly ISpeechRecognizer asr;
    private readonly IAudioCaptureFactory audioFactory;
    private readonly Segmenter segmenter=Segmenter.ForNotes();
    private readonly CancellationTokenSource cancel=new();
    private readonly Task worker;
    private IAudioCapture? capture;
    private AudioSegment? preview;
    private CancellationTokenSource? previewCancellation;
    private long lastPreview;
    private int revision;
    private volatile bool recording;
    private volatile bool processing;
    private volatile bool inputClosed;
    private volatile bool paused;
    private bool pausing;
    private readonly SemaphoreSlim lifecycle=new(1,1);
    public bool Paused=>paused;
    public double Rms {get;private set;}
    public double Peak {get;private set;}
    private string captureError="";
    private TaskCompletionSource stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Id {get;}
    public string DocumentId {get;}
    public string Path {get;}
    public string Error {get;private set;}="";
    public object? Hypothesis {get;private set;}
    /// <summary>Text of the most recently finalized segment; shown when no live preview exists (e.g. online ASR).</summary>
    public string? LastFinal {get;private set;}
    public bool Recording=>recording;
    public RecordingSession(string id,string document,string path,string root,Ledger ledger,ISpeechRecognizer asr,bool recover=false,IAudioCaptureFactory? audioFactory=null) {
        Id=id;DocumentId=document;Path=path;this.ledger=ledger;this.asr=asr;
        this.audioFactory=audioFactory??PlatformServices.CreateAudioCaptureFactory();
        ledger.CreateSession(id,document,path);
        audio=new AudioStore(SessionFiles.PrepareAudio(root,id,path));
        try{ledger.ExportTranscript(id);}catch{audio.Dispose();throw;}
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
            if(capture!=null || inputClosed || (audio.Samples!=0 && !paused))throw new InvalidOperationException("会话已经结束或正在录音");
            var wasPaused=paused;paused=false;pausing=false;captureError="";
            try {
            stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var next=audioFactory.Create();
            next.PcmAvailable+=pcm=>{
                try {Accept(pcm);}
                catch(Exception error) {captureError="录音保存失败: "+error.Message;next.RequestStop();}
            };
            next.Stopped+=ex=>{
                lock(gate) {
                    recording=false;
                    try{
                        var dataError=next.TakeDataError();
                        if(dataError!=null && captureError.Length==0)captureError="录音保存失败: "+dataError.Message;
                        else if(ex!=null)captureError="录音采集停止异常，请重试或更换设备";
                        Accept(next.Flush());
                        FinalizeTail(!pausing || ex!=null);
                        paused=pausing && !inputClosed;Rms=Peak=0;stopped.TrySetResult();
                    }catch(Exception error){captureError=error.Message;inputClosed=true;paused=false;Rms=Peak=0;stopped.TrySetException(error);}
                }
            };
            capture=next;
            ledger.BeginSpan(Id,audio.Samples,DateTimeOffset.Now);
            recording=true;
            capture.Start(device);
            } catch {recording=false;paused=wasPaused;capture?.Dispose();capture=null;throw;}
        }
    }
    public void Accept(short[] pcm) {
        lock(gate) {
            if(inputClosed || paused)throw new InvalidOperationException("Recording input is closed or paused");
            if(pcm.Length==0)return;
            double sum=0,peak=0;foreach(var value in pcm){double x=value/32768.0;sum+=x*x;peak=Math.Max(peak,Math.Abs(x));}Rms=Math.Sqrt(sum/pcm.Length);Peak=peak;
            audio.Append(pcm);
            ledger.EndSpan(Id,audio.Samples);
            foreach(var segment in segmenter.Push(pcm)) Enqueue(segment);
            ledger.SetProgress(Id,segmenter.ActiveStart ?? audio.Samples);
            // Online recognizers skip speculative previews (each would be a billed request).
            if(audio.Samples-lastPreview>=32000) {preview=asr.Previews?segmenter.Snapshot():null;lastPreview=audio.Samples;}
        }
    }
    private void Enqueue(AudioSegment s) {ledger.AddJob(Id,$"{Id}:{s.Start}",s.Start,s.End,s.NeedsReview);preview=null;Hypothesis=null;previewCancellation?.Cancel();}
    private void FinalizeTail(bool close=true){var tail=segmenter.Flush();if(tail!=null)Enqueue(tail);ledger.SetProgress(Id,audio.Samples);ledger.EndSpan(Id,audio.Samples);preview=null;Hypothesis=null;if(close)inputClosed=true;}
    public Task Pause()=>StopCapture(true);
    public Task Stop()=>StopCapture(false);
    public async Task Resume(int device){await lifecycle.WaitAsync();try{if(!paused)throw new InvalidOperationException("会话未暂停，不能续录");Start(device);}finally{lifecycle.Release();}}
    private async Task StopCapture(bool pause) {
        await lifecycle.WaitAsync();try{
            IAudioCapture? device;lock(gate){if(inputClosed){if(pause)throw new InvalidOperationException("录音已结束");return;}if(pause && paused)return;pausing=pause;device=capture;}
            if(device!=null && recording){device.RequestStop();await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));}
            else lock(gate){FinalizeTail(!pause);paused=pause;}
            lock(gate){capture?.Dispose();capture=null;Rms=Peak=0;if(!pause){inputClosed=true;paused=false;}}
        }finally{lifecycle.Release();}
    }
    private double BufferedSeconds {get{lock(gate){return segmenter.ActiveStart is long start?(audio.Samples-start)/16000.0:0;}}}
    public object Status()=>new {sessionId=Id,documentId=DocumentId,path=Path,recording,paused,processing,ended=inputClosed,rms=Rms,peak=Peak,audioSavedSamples=audio.Samples,samples=audio.Samples,seconds=audio.Samples/16000.0,bufferedSeconds=BufferedSeconds,pending=ledger.PendingCount(Id),polish=ledger.PolishStatus(Id),error=captureError.Length>0?captureError:Error.Length>0?Error:ledger.NoTextCount(Id)>0?$"有 {ledger.NoTextCount(Id)} 段未识别到文字，原始逐字稿已标记，音频已保留":"",hypothesis=Hypothesis,lastFinal=LastFinal};
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
                    if(job.Id==null)lock(gate){previewCancellation=timeout;if(ledger.PendingCount(Id)>0)timeout.Cancel();}
                    var text=await asr.Recognize(job.Id!=null?audio.Read(job.Start,job.End):snap!.Samples,timeout.Token);
                    if(job.Id!=null){ledger.AddFinal(Id,job.Id,job.Start,job.End,text,job.Review);Hypothesis=null;if(!string.IsNullOrWhiteSpace(text))LastFinal=text.Trim();}
                    else lock(gate) {
                        // A result for a finalized segment must never resurrect its preview.
                        var current=segmenter.Snapshot();
                        if(current?.Start==snap!.Start)Hypothesis=new {segmentId=$"{Id}:{snap.Start}",revision=++revision,text,end=snap.End};
                    }
                    Error="";
                } catch(OperationCanceledException) when(job.Id==null && !cancel.IsCancellationRequested && ledger.PendingCount(Id)>0) {
                    // A completed utterance preempts speculative previews without retry delay.
                } catch(InvalidDataException e) when(!cancel.IsCancellationRequested && Equals(e.Data["FinishReason"],"length")) {
                    if(job.Id!=null){if(job.End-job.Start>=32000)ledger.SplitJob(Id,job.Id,job.Start,job.End);else ledger.AddFinal(Id,job.Id,job.Start,job.End,"",true);}
                    Hypothesis=null;Error="";
                } catch(Exception e) when(!cancel.IsCancellationRequested) {
                    Error=e.Message;if(job.Id!=null)ledger.FailJob(job.Id,e.Message);
                    await Task.Delay(2000,cancel.Token);
                } finally {lock(gate){previewCancellation=null;}processing=false;}
            } catch(OperationCanceledException) when(cancel.IsCancellationRequested){break;}
            catch(Exception e){Error=e.Message;await Task.Delay(1000);}
        }
    }
    public async ValueTask DisposeAsync(){await Stop();cancel.Cancel();await worker;capture?.Dispose();audio.Dispose();cancel.Dispose();}
}
