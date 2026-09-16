using TyporaAsr;
using NAudio.Wave;
using NAudio.CoreAudioApi;

if(args.Length==3 && args[0]=="--segment-wav"){
 using var input=new FileStream(args[1],FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var wav=new WaveFileReader(input);
 if(wav.WaveFormat.SampleRate!=16000||wav.WaveFormat.Channels!=1||wav.WaveFormat.BitsPerSample!=16)throw new Exception("Expected mono PCM16 16kHz WAV");
 var segmenter=Segmenter.ForNotes();var segments=new List<AudioSegment>();var bytes=new byte[640];int count;
 while((count=wav.Read(bytes,0,bytes.Length))>0){var samples=new short[count/2];Buffer.BlockCopy(bytes,0,samples,0,count);segments.AddRange(segmenter.Push(samples));}
 var tail=segmenter.Flush();if(tail!=null)segments.Add(tail);
 File.WriteAllText(args[2],System.Text.Json.JsonSerializer.Serialize(segments.Select(s=>new {start=s.Start,end=s.End,review=s.NeedsReview})));
 Console.WriteLine($"PASS real WAV replay: {segments.Count} accumulated segments, {segments.Count(s=>s.NeedsReview)} waiting for review");return;
}

var failures = 0;
void Check(string name, Action test) { try { test(); Console.WriteLine($"PASS {name}"); } catch(Exception e) { failures++; Console.WriteLine($"FAIL {name}: {e.Message}"); } }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected,actual)) throw new Exception($"Expected {expected}, got {actual}"); }
Check("SSE strips a split ASR marker and rejects truncated final", () => {
    var p = new AsrOutput();
    p.Add("language Chinese<asr_"); p.Add("text>你好。", "stop");
    Equal("你好。",p.FinalText());
    var bad = new AsrOutput(); bad.Add("半句话", "length");
    try { bad.FinalText(); throw new Exception("accepted truncation"); } catch(InvalidDataException) {}
});
Check("VAD retains sub-block tail and emits final at stop", () => {
    var vad = new Segmenter(16000, 700, 15, 0.01);
    var pcm = Enumerable.Repeat((short)3000, 1234).ToArray();
    vad.Push(pcm); var last = vad.Flush();
    Equal(1234L, last!.End); Equal(0L,last.Start);
});
Check("Normally finished empty ASR result is distinct from an interrupted stream",()=>{
 var empty=new AsrOutput();empty.Add("language Chinese<asr_text>","stop");Equal("",empty.FinalText());
 var interrupted=new AsrOutput();interrupted.Add("");try{interrupted.FinalText();throw new Exception("Accepted unfinished stream");}catch(InvalidDataException){}
});
Check("Default VAD emits during a clear pause before recording stops",()=>{
 var vad=new Segmenter();vad.Push(Enumerable.Repeat((short)5000,16000).ToArray());
 Equal(0,vad.Push(new short[3200]).Count);
 Equal(1,vad.Push(new short[4160]).Count);
});
Check("VAD silence produces no speech and endpoint includes tail", () => {
    var vad = new Segmenter(16000,700,15,0.01);
    Equal(0,vad.Push(new short[16000]).Count);
    vad.Push(Enumerable.Repeat((short)5000,3200).ToArray());
    var done = vad.Push(new short[11200]);
    Equal(1,done.Count); Equal(false,done[0].NeedsReview);
    Equal(30400L,done[0].End);
});
Check("Continuous speech is bounded and marked for review", () => {
    var vad = new Segmenter(16000,700,1,0.01);
    var done = vad.Push(Enumerable.Repeat((short)5000,16000).ToArray());
    Equal(1,done.Count); Equal(true,done[0].NeedsReview);
});
Check("Note segments accumulate short phrases and flush at a natural pause",()=>{
 var vad=Segmenter.ForNotes();var list=new List<AudioSegment>();
 for(var i=0;i<3;i++){list.AddRange(vad.Push(Enumerable.Repeat((short)4000,24000).ToArray()));list.AddRange(vad.Push(new short[9600]));}
 Equal(0,list.Count);list.AddRange(vad.Push(new short[9600]));Equal(1,list.Count);Equal(false,list[0].NeedsReview);
});
Check("Note segments have bounded latency and continuous speech never waits for review",()=>{
 var vad=Segmenter.ForNotes();var first=vad.Push(Enumerable.Repeat((short)4000,192000).ToArray());Equal(1,first.Count);Equal(false,first[0].NeedsReview);
 var second=vad.Push(Enumerable.Repeat((short)4000,192000).ToArray());Equal(1,second.Count);Equal(false,second[0].NeedsReview);Equal(first[0].End,second[0].Start);vad.Push(Enumerable.Repeat((short)4000,8000).ToArray());Equal(false,vad.Flush()!.NeedsReview);
 var shortNote=Segmenter.ForNotes();shortNote.Push(Enumerable.Repeat((short)4000,8000).ToArray());Equal(1,shortNote.Push(new short[40000]).Count);
});
Check("Ledger persists events, deduplicates finals and preserves deletion state", () => {
    var root = Path.Combine(Path.GetTempPath(),"typora-asr-test-"+Guid.NewGuid());
    Directory.CreateDirectory(root);
    try {
      using(var db = new Ledger(root)) {
        db.CreateSession("s","d", "C:\\notes\\record.md");
        db.AddFinal("s","e1",0,3200,"第一句",false);
        db.AddFinal("s","e1",0,3200,"第一句",false);
        Equal(1,db.Events("s",0).Count);
        db.Acknowledge("s","e1","deleted");
        db.Acknowledge("s","e1","applied");
      }
      using(var db = new Ledger(root)) Equal("deleted",db.Events("s",0)[0].State);
    } finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
});
Check("Audio persistence keeps exact samples including a final partial block",()=>{
 var root=Path.Combine(Path.GetTempPath(),"asr-audio-"+Guid.NewGuid());Directory.CreateDirectory(root);
 try {using(var audio=new AudioStore(Path.Combine(root,"audio.pcm"))){audio.Append([123,-456,789]);Equal(3L,audio.Samples);Equal((short)-456,audio.Read(1,2)[0]);}
 using(var audio=new AudioStore(Path.Combine(root,"audio.pcm"))) {Equal(3L,audio.Samples);} }
 finally{Directory.Delete(root,true);}
});
Check("48k stereo capture resamples to exact mono duration across irregular chunks",()=>{
 var converter=new PcmConverter(WaveFormat.CreateIeeeFloatWaveFormat(48000,2));
 var source=new float[48000*2];for(var i=0;i<source.Length;i+=2){source[i]=0.5f;source[i+1]=0;}
 var data=new byte[source.Length*4];Buffer.BlockCopy(source,0,data,0,data.Length);
 var output=new List<short>();
 for(var offset=0;offset<data.Length;offset+=3848){var count=Math.Min(3848,data.Length-offset);output.AddRange(converter.Push(data,offset,count));}
 output.AddRange(converter.Flush());Equal(16000,output.Count);
 if(Math.Abs(output[500]-8192)>40)throw new Exception("Stereo mix or amplitude is incorrect");
});
if(args.Length==2 && args[0]=="--live") {
 var bytes=File.ReadAllBytes(args[1]);var pcm=new short[bytes.Length/2];Buffer.BlockCopy(bytes,0,pcm,0,bytes.Length);
 var root=Path.Combine(Path.GetTempPath(),"asr-live-"+Guid.NewGuid());Directory.CreateDirectory(root);
 try {
  using var ledger=new Ledger(root);using var http=new HttpClient();var asr=new AsrClient(http,"http://127.0.0.1:18081","qwen3-asr");
  await using var session=new RecordingSession("live","doc","C:\\test.md",root,ledger,asr);
  for(var i=0;i<pcm.Length;i+=3200) session.Accept(pcm.Skip(i).Take(3200).ToArray());
  await session.Stop();var deadline=DateTime.UtcNow.AddSeconds(60);
  while(ledger.PendingCount("live")>0 && DateTime.UtcNow<deadline)await Task.Delay(100);
  var events=ledger.Events("live",0);
  if(events.Count==0 || ledger.PendingCount("live")!=0)throw new Exception("Live transcription incomplete: "+session.Error);
  Console.WriteLine("PASS real model → persisted audio → segmentation → final events: "+string.Join(" / ",events.Select(e=>e.Text)));
 }catch(Exception e){failures++;Console.WriteLine("FAIL live: "+e.Message);}
 finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
{
 var root=Path.Combine(Path.GetTempPath(),"asr-silence-"+Guid.NewGuid());Directory.CreateDirectory(root);
 try {
  using var db=new Ledger(root);using var http=new HttpClient();var asr=new AsrClient(http,"http://127.0.0.1:1","test");
  await using(var original=new RecordingSession("silence","doc","C:\\test.md",root,db,asr)){original.Accept(new short[16000]);await original.Stop();}
  await using(var restored=new RecordingSession("silence","doc","C:\\test.md",root,db,asr,true)){
   Check("Gracefully stopped silence is not retranscribed on recovery",()=>Equal(0L,db.PendingCount("silence")));
  }
 }finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
if(args.Contains("--capture")){
 try{
  using var capture=new WasapiCapture();long received=0;
  capture.DataAvailable+=(_,e)=>Interlocked.Add(ref received,e.BytesRecorded);
  capture.StartRecording();await Task.Delay(2200);capture.StopRecording();await Task.Delay(300);
  if(received==0)throw new Exception("No audio frames received");
  Console.WriteLine($"PASS WASAPI microphone capture: {received} bytes, {capture.WaveFormat}");
 }catch(Exception e){failures++;Console.WriteLine("FAIL WASAPI microphone capture: "+e);}
}
if(args.Contains("--pause-capture")){
 var root=Path.Combine(Path.GetTempPath(),"asr-pause-capture-"+Guid.NewGuid());Directory.CreateDirectory(root);
 try{using var db=new Ledger(root);using var http=new HttpClient();var asr=new AsrClient(http,"http://127.0.0.1:1","test");
 new StorageSettings(root).Save(Path.Combine(root,"records"));
 var captureId=Guid.NewGuid().ToString();var captureDoc=Path.Combine(root,"note.md");File.WriteAllText(captureDoc,"# Capture test");
 await using var s=new RecordingSession(captureId,"d",captureDoc,root,db,asr);
 s.Start(-1);await Task.Delay(1200);await s.Pause();var first=System.Text.Json.JsonSerializer.SerializeToElement(s.Status());var count=first.GetProperty("samples").GetInt64();
 if(count<8000||!s.Paused||s.Rms!=0)throw new Exception("Initial capture/pause failed");
 await Task.Delay(500);if(System.Text.Json.JsonSerializer.SerializeToElement(s.Status()).GetProperty("samples").GetInt64()!=count)throw new Exception("Pause kept recording");
 try{await s.Resume(int.MaxValue);throw new Exception("Invalid device unexpectedly accepted");}catch(ArgumentException){}catch(IndexOutOfRangeException){}
 if(!s.Paused)throw new Exception("Failed resume lost paused state");
 for(var i=0;i<2;i++){await s.Resume(-1);await Task.Delay(900);await s.Pause();}
 await s.Stop();var last=System.Text.Json.JsonSerializer.SerializeToElement(s.Status());
 if(last.GetProperty("samples").GetInt64()<=count+16000||s.Paused||s.Recording)throw new Exception("Resume did not append audio");
 using var wavStream=new FileStream(SessionFiles.Paths(root,captureId,captureDoc).Audio,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var wavReader=new WaveFileReader(wavStream);if(wavReader.Length!=last.GetProperty("samples").GetInt64()*2)throw new Exception("WAV header length does not match captured samples");
 Console.WriteLine("PASS actual WASAPI WAV: two resumes, fixed samples during pause, invalid-device retry and final header");
 }catch(Exception e){failures++;Console.WriteLine("FAIL pause capture: "+e);}
 finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
if(args.Length==5 && args[0]=="--editor-integration") {
 using var ledger=new Ledger(args[2]);using var http=new HttpClient();var client=new AsrClient(http,"http://127.0.0.1:18081","qwen3-asr");
 var id=Guid.NewGuid().ToString();new StorageSettings(args[2]).Save(Path.Combine(args[2],"records"));
 await using var s=new RecordingSession(id,args[4],args[3],args[2],ledger,client);
 var bytes=File.ReadAllBytes(args[1]);var pcm=new short[bytes.Length/2];Buffer.BlockCopy(bytes,0,pcm,0,bytes.Length);
 for(var offset=0;offset<pcm.Length;offset+=3200)s.Accept(pcm.Skip(offset).Take(3200).ToArray());await s.Stop();
 var deadline=DateTime.UtcNow.AddSeconds(60);while(ledger.PendingCount(id)>0 && DateTime.UtcNow<deadline)await Task.Delay(100);
 if(ledger.PendingCount(id)>0)throw new Exception(s.Error);
 File.WriteAllText(System.IO.Path.Combine(args[2],"integration-session.json"),System.Text.Json.JsonSerializer.Serialize(new {sessionId=id,documentId=args[4],events=ledger.Events(id,0).Count}));
 Console.WriteLine("PASS persisted real-model fixture for editor integration");
}
if(args.Length==3 && args[0]=="--seed-polish-fixture"){
 Directory.CreateDirectory(args[1]);new StorageSettings(args[1]).Save(Path.Combine(args[1],"records"));using var db=new Ledger(args[1]);var session=Guid.NewGuid().ToString();var doc=Guid.NewGuid().ToString();
 File.WriteAllText(args[2],$"# 润色集成测试\n\n人工笔记起始内容\n\n<!-- asr-insert:{doc} -->\n");db.CreateSession(session,doc,Path.GetFullPath(args[2]));db.EnablePolish(session);
 db.BeginSpan(session,0,DateTimeOffset.Parse("2026-09-12T10:00:00+08:00"));db.EndSpan(session,32000);
 db.AddFinal(session,session+":0",0,16000,"原始口语一",false);db.CutWindows(_=>true,DateTimeOffset.UtcNow);
 db.AddFinal(session,session+":16000",16000,32000,"原始口语二",false);db.CutWindows(_=>true,DateTimeOffset.UtcNow);
 foreach(var (paragraphs,continues) in new[]{(new[]{"已润色的第一句话。","已润色的第二句话。"},false),(new[]{"已润色的接续内容。"},true)}){
  var fixtureJob=db.NextPolish()??throw new Exception("Fixture window missing");
  db.SnapshotPolish(fixtureJob.Id,"fixture-only");db.CompletePolish(fixtureJob.Id,paragraphs,continues);
 }
 db.AddFinal(session,session+":32000",32000,48000,"不能入文的未润色原始内容",false);
 File.WriteAllText(Path.Combine(args[1],"integration-session.json"),System.Text.Json.JsonSerializer.Serialize(new {sessionId=session,documentId=doc}));Console.WriteLine("PASS prepared isolated editor fixture");
}
try {await PipelineTests.Run();}catch(Exception e){failures++;Console.WriteLine("FAIL pipeline: "+e);}
try {await OnlineAsrTests.Run();}catch(Exception e){failures++;Console.WriteLine("FAIL online ASR: "+e);}
try {await ConfigTransferTests.Run();}catch(Exception e){failures++;Console.WriteLine("FAIL config transfer: "+e);}
try {await WavTests.Run();}catch(Exception e){failures++;Console.WriteLine("FAIL WAV: "+e.Message);}
Environment.ExitCode = failures == 0 ? 0 : 1;
