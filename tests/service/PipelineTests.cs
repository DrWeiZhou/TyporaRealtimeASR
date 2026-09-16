using TyporaAsr;
using System.Net;
using System.Text;
using System.Text.Json;

static class PipelineTests {
 public static async Task Run() {
  var root=Path.Combine(Path.GetTempPath(),"asr-pipeline-"+Guid.NewGuid());Directory.CreateDirectory(root);
  try {
   using var db=new Ledger(root);
   var settings=new PolishSettings(root);
   var key="test-secret-"+Guid.NewGuid();
   settings.Save(new PolishConfig("https://example.com/v1","test-model",key,"只修正表达"));
   if(File.ReadAllText(Path.Combine(root,"polish-settings.json")).Contains(key))throw new Exception("Key stored in plaintext");
   if(new PolishSettings(root).Current()?.ApiKey!=key)throw new Exception("Protected key did not round-trip");
   var now=DateTimeOffset.UtcNow;var relaxed=new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};var later=now.AddSeconds(Ledger.WindowMaxWaitSeconds+1);
   db.CreateSession("s","d","C:\\test.md");db.EnablePolish("s");
   db.BeginSpan("s",0,DateTimeOffset.Parse("2026-09-12T10:00:00+08:00"));db.EndSpan("s",16000);
   db.BeginSpan("s",16000,DateTimeOffset.Parse("2026-09-12T10:01:00+08:00"));db.EndSpan("s",32000);
   db.AddFinal("s","e1",0,16000,"原始一",false);db.AddFinal("s","e2",16000,32000,"原始二",false);
   if(db.CutWindows(_=>false,now)!=0)throw new Exception("Short fresh utterances were cut before the wait limit");
   if(db.ReadyEvents("s",0).Count!=0)throw new Exception("Raw text released before polish");
   if(db.CutWindows(_=>false,later)!=1||db.CutWindows(_=>false,later)!=0)throw new Exception("Waiting utterances were not grouped exactly once");
   var llm=new ScriptedLlm();using var http=new HttpClient(llm);
   var pipeline=new PolishPipeline(db,settings,http);
   llm.Next(_=>new(HttpStatusCode.TooManyRequests));
   await pipeline.Step(CancellationToken.None);
   if(db.ReadyEvents("s",0).Count!=0||!JsonSerializer.Serialize(db.PolishStatus("s"),relaxed).Contains("HTTP 429"))throw new Exception("Retrying window released text or hid its error");
   db.RetryPolish("s");
   llm.Next(body=>{
    if(!body.System.Contains("只修正表达")||!body.System.Contains("<待整理>")||!body.System.Contains(PolishFormat.ContinueMark)||body.System.Contains("话题")||!body.User.Contains("<待整理>\n原始一原始二\n</待整理>")||!body.User.Contains("<上文>\n（无）\n</上文>")||body.User.Contains("话题"))throw new Exception("Window request missing contract or joined text");
    return Reply("第一段。\n\n第二段。");
   });
   await pipeline.Step(CancellationToken.None);
   var first=db.ReadyEvents("s",0).Single();
   if(first.Paragraphs?.Length!=2||first.Continues||first.Text!="第一段。\n\n第二段。"||first.Start!=0||first.End!=32000)throw new Exception("Polished window missing");
   if(db.Events("s",0)[0].Text!="原始一")throw new Exception("Original overwritten");
   db.AddFinal("s","e3",32000,48000,new string('长',Ledger.WindowMinChars),false);
   if(db.CutWindows(_=>false,now)!=1)throw new Exception("Full window waited for timer");
   llm.Next(body=>{
    if(!body.User.Contains("<上文>\n第二段。\n</上文>"))throw new Exception("Context is not the last paragraph");
    return Reply("```\n【接续】\n接续内容\n```");
   });
   await pipeline.Step(CancellationToken.None);
   var ready=db.ReadyEvents("s",0);
   if(ready.Count!=2||ready[1].Paragraphs?.Single()!="接续内容"||!ready[1].Continues||ready[1].Seq!=3)throw new Exception("Continuation window missing or out of order");
   var calls=llm.Calls;await pipeline.Step(CancellationToken.None);if(llm.Calls!=calls)throw new Exception("Completed result requested again");
   db.AddFinal("s","e4",48000,52000,"嗯嗯，好的。",false);
   if(db.CutWindows(_=>true,now)!=1)throw new Exception("Stopped session did not flush short window");
   llm.Next(body=>{
    if(!body.User.Contains("<上文>\n第二段。接续内容\n</上文>"))throw new Exception("Continued paragraph not used as context");
    return Reply(PolishFormat.Empty);
   });
   await pipeline.Step(CancellationToken.None);
   ready=db.ReadyEvents("s",0);
   if(ready.Count!=3||ready[2].State!="deleted")throw new Exception("Empty window was not skipped");
   var raw=db.Transcript("s");if(!raw.Contains("10:01:00") || !raw.Contains("00:00:01") || !raw.Contains("原始二"))throw new Exception("Pause timestamp mapping lost");
   db.Acknowledge("s",ready[0].EventId,"applied");
   using(var reopened=new Ledger(root)){var again=reopened.ReadyEvents("s",0);if(again.Count!=3||again[0].State!="applied"||again[0].Paragraphs?.Length!=2||!again[1].Continues)throw new Exception("Results or window state lost on restart");}
   Console.WriteLine("PASS windows wait/flush, continuation contract with last-paragraph context, failed polish blocked, retry, empty skip and restart");
   db.CreateSession("concurrent","d","C:\\test.md");db.EnablePolish("concurrent");db.AddFinal("concurrent","flight",0,16000,"原始一",false);db.CutWindows(_=>true,now);
   var held=new HeldLlm();using var heldHttp=new HttpClient(held);var concurrent=new PolishPipeline(db,settings,heldHttp);
   var step=concurrent.Step(CancellationToken.None);await held.Started.Task;
   var retry=concurrent.Retry("concurrent",CancellationToken.None);
   if(retry.IsCompleted)throw new Exception("Retry mutated an in-flight snapshot");
   held.Release.SetResult();await Task.WhenAll(step,retry);
   if(db.ReadyEvents("concurrent",0).Single().Paragraphs?[0]!="完成的润色")throw new Exception("In-flight result corrupted by retry");
   Console.WriteLine("PASS retry waits for in-flight task and preserves completed result");
   db.CreateSession("parallel","d","C:\\test.md");db.EnablePolish("parallel");
   var parts=new[]{'甲','乙','丙','丁'}.Select(ch=>new string(ch,Ledger.WindowMinChars)).ToArray();
   for(var i=0;i<parts.Length;i++)db.AddFinal("parallel",$"p{i}",i*100,i*100,parts[i],false);
   if(db.CutWindows(_=>false,now)!=Ledger.MaxOpenWindows)throw new Exception("Backlog was not split into per-utterance windows up to the open limit");
   var gateLlm=new GateLlm();using var gateHttp=new HttpClient(gateLlm);var parallel=new PolishPipeline(db,settings,gateHttp,_=>true);
   using(var stopRun=new CancellationTokenSource()){
    var run=parallel.Run(stopRun.Token);
    var until=DateTime.UtcNow.AddSeconds(5);while(gateLlm.Users.Count<PolishPipeline.MaxConcurrent&&DateTime.UtcNow<until)await Task.Delay(20);
    if(gateLlm.Peak!=PolishPipeline.MaxConcurrent)throw new Exception("Windows were not polished concurrently");
    var second=gateLlm.Users.Single(u=>u.Contains("<待整理>\n"+parts[1]+"\n</待整理>"));
    if(!second.Contains("<上文>\n"+parts[0]+"\n</上文>"))throw new Exception("Concurrent window lacks raw context of the previous window");
    if(db.ReadyEvents("parallel",0).Count!=0)throw new Exception("Unfinished windows released text");
    gateLlm.Release.SetResult();
    until=DateTime.UtcNow.AddSeconds(5);while(db.ReadyEvents("parallel",0).Count<parts.Length&&DateTime.UtcNow<until)await Task.Delay(50);
    var done=db.ReadyEvents("parallel",0);
    if(done.Count!=parts.Length||!done.Select(e=>e.Paragraphs![0]).SequenceEqual(parts.Select(x=>x+"。")))throw new Exception("Concurrent results missing or out of order");
    stopRun.Cancel();await run;
   }
   Console.WriteLine("PASS windows are polished concurrently with raw context and inserted in order");
   db.CreateSession("retry-limit","d","C:\\test.md");db.EnablePolish("retry-limit");db.AddFinal("retry-limit","blocked",0,100,"原始文本",false);db.CutWindows(_=>true,now);
   var blocked=db.NextPolish()!;db.SnapshotPolish(blocked.Id,"unused");db.FailPolish(blocked.Id,Ledger.PolishMaxAttempts,"模拟重试耗尽");
   var exhausted=db.ReadyEvents("retry-limit",0).Single();
   if(db.NextPolish()!=null||exhausted.PolishState!="failed"||exhausted.Text!="原始文本")throw new Exception("Retry cap bypassed or raw text not offered for manual review");
   db.AddFinal("retry-limit","after-blocked",100,200,"有效后续内容",false);db.CutWindows(_=>true,now);
   var afterBlocked=db.NextPolish();if(afterBlocked==null||afterBlocked.Id==blocked.Id)throw new Exception("Exhausted polish blocks later windows");
   db.SnapshotPolish(afterBlocked.Id,"unused");db.CompletePolish(afterBlocked.Id,["润色后的有效内容"]);
   if(db.ReadyEvents("retry-limit",0).Count!=2||db.ReadyEvents("retry-limit",0)[1].Paragraphs?[0]!="润色后的有效内容")throw new Exception("Failed window blocks ready content");
   var status=JsonSerializer.Serialize(db.PolishStatus("retry-limit"));if(!status.Contains("\"pending\":1")||!status.Contains("\"failed\":1"))throw new Exception("Polish status wrong: "+status);
   db.RetryPolish("retry-limit",[afterBlocked.Id]);if(db.NextPolish()?.Id!=blocked.Id)throw new Exception("Manual retry did not unblock exhausted window");
   db.Acknowledge("retry-limit",blocked.Id,"applied");if(db.NextPolish()!=null)throw new Exception("Manually inserted raw window was polished again");
   if(db.ReadyEvents("retry-limit",0)[0].Paragraphs?[0]!="原始文本")throw new Exception("Manually inserted raw window lost on replay");
   Console.WriteLine("PASS retry limit offers raw text for manual insertion and requires manual retry");
   db.CreateSession("old-backlog","d","C:\\old.md");db.EnablePolish("old-backlog");db.AddFinal("old-backlog","old-first",0,100,"旧积压",false);
   db.CreateSession("current-note","d","C:\\current.md");db.EnablePolish("current-note");db.AddFinal("current-note","new-first",0,100,"当前第一句",false);db.CutWindows(_=>true,now);db.AddFinal("current-note","new-second",100,200,"当前第二句",false);
   if(db.CutWindows(_=>true,now)!=1)throw new Exception("New window waited for the earlier open window");
   var current=db.NextPolish();if(current?.Session!="current-note"||current.Text!="当前第一句")throw new Exception("Old backlog starved the current document");
   db.TouchSession("old-backlog");if(db.NextPolish()?.Session!="old-backlog")throw new Exception("Active older session was not prioritized");db.TouchSession("current-note");
   db.Acknowledge("current-note",current.Id,"deleted");if(db.NextPolish()?.Text!="当前第二句")throw new Exception("Current document sequence changed");
   db.Acknowledge("current-note",db.NextPolish()!.Id,"deleted");var old=db.NextPolish();if(old?.Session!="old-backlog")throw new Exception("Old backlog never resumed");db.Acknowledge("old-backlog",old.Id,"deleted");
   Console.WriteLine("PASS current document polish bypasses old backlog while preserving session order");
   db.CreateSession("cut","d","C:\\test.md");db.EnablePolish("cut");
   db.AddFinal("cut","c1",0,100,new string('一',30),false);db.AddFinal("cut","c2",110,200,new string('二',40),false);
   db.AddFinal("cut","c3",700,800,new string('三',40),false);db.AddFinal("cut","c4",810,900,new string('四',40),false);
   db.AddFinal("cut","c5",950,1000,new string('五',300),false);
   if(db.CutWindows(_=>false,now)!=3)throw new Exception("Ready utterances not cut into windows");
   var cut=db.NextPolish()!;
   if(cut.Text!=new string('一',30)+new string('二',40))throw new Exception("Window did not prefer the longest pause inside the size band");
   db.Acknowledge("cut",cut.Id,"deleted");cut=db.NextPolish()!;
   if(cut.Text!=new string('三',40)+new string('四',40))throw new Exception("Window exceeded the size limit");
   db.Acknowledge("cut",cut.Id,"deleted");cut=db.NextPolish()!;
   if(cut.Text!=new string('五',300))throw new Exception("Long utterance not sent alone");
   db.Acknowledge("cut",cut.Id,"deleted");
   db.AddFinal("cut","c6",2000,2100,new string('六',20),false);
   if(db.CutWindows(_=>false,now)!=0||db.NextPolish()!=null)throw new Exception("Short fresh tail was cut early");
   db.CutWindows(_=>true,now);var tail=db.NextPolish();if(tail?.Text!=new string('六',20))throw new Exception("Tail not flushed");db.Acknowledge("cut",tail.Id,"deleted");
   Console.WriteLine("PASS window boundaries respect the size band and prefer long pauses");
   db.CreateSession("order","d","C:\\test.md");db.EnablePolish("order");db.AddFinal("order","o1",0,100,new string('甲',Ledger.WindowMinChars),false);
   if(db.CutWindows(_=>false,now)!=1)throw new Exception("Ready window not cut");
   var earlier=db.NextPolish()!;db.SnapshotPolish(earlier.Id,"unused");db.FailPolish(earlier.Id,1,"模拟超时");
   db.AddFinal("order","o2",100,200,new string('乙',Ledger.WindowMinChars),false);
   if(db.CutWindows(_=>false,now)!=1)throw new Exception("Later speech waited for a retrying window");
   var later2=db.NextPolish();if(later2==null||later2.Id==earlier.Id)throw new Exception("Later window not polished while the earlier one backs off");
   db.SnapshotPolish(later2.Id,"unused");db.CompletePolish(later2.Id,["乙"]);
   if(db.ReadyEvents("order",0).Count!=0)throw new Exception("Later result overtook a window that is being retried");
   db.RetryPolish("order");if(db.NextPolish()?.Id!=earlier.Id)throw new Exception("Retried window lost its place");
   db.SnapshotPolish(earlier.Id,"unused");db.CompletePolish(earlier.Id,["甲"]);
   if(!System.Text.RegularExpressions.Regex.IsMatch(JsonSerializer.Serialize(db.PolishStatus("order")),"\"requestSeconds\":\\d"))throw new Exception("Request timing missing");
   if(!db.ReadyEvents("order",0).Select(e=>e.Paragraphs![0]).SequenceEqual(new[]{"甲","乙"}))throw new Exception("Results not released in speech order");
   Console.WriteLine("PASS a retrying window keeps its place while later windows are polished");
   var parsed=PolishFormat.Parse("```text\n<待整理>\n第一段。\r\n\r\n第二段\n继续。\n</待整理>\n```",true);
   if(parsed.Continues||!parsed.Paragraphs.SequenceEqual(new[]{"第一段。","第二段","继续。"}))throw new Exception("Plain-text paragraphs not split: "+string.Join("|",parsed.Paragraphs));
   var continued=PolishFormat.Parse("【接续】\n补充说明。\n\n新的大意。",true);
   if(!continued.Continues||!continued.Paragraphs.SequenceEqual(new[]{"补充说明。","新的大意。"}))throw new Exception("Continuation mark not parsed");
   if(PolishFormat.Parse("[接续] 补充说明。",false) is not {Continues:false,Paragraphs:["补充说明。"]})throw new Exception("Continuation without a previous paragraph accepted");
   if(PolishFormat.Parse("新段落。【接续】",true) is not {Continues:false,Paragraphs:["新段落。"]})throw new Exception("Stray continuation mark kept");
   if(PolishFormat.Parse(PolishFormat.Empty,true).Paragraphs.Length!=0||PolishFormat.Parse("（本段无有效内容）",true).Paragraphs.Length!=0||PolishFormat.Parse("【接续】（无）",true).Continues)throw new Exception("Placeholder prose inserted");
   var stored=PolishFormat.Deserialize(PolishFormat.Serialize(new PolishResult(["甲。","乙。"],true)));
   if(!stored.Continues||!stored.Paragraphs.SequenceEqual(new[]{"甲。","乙。"}))throw new Exception("Stored result did not round-trip");
   var former=PolishFormat.Deserialize("{\"blocks\":[{\"topic\":\"continue\",\"paragraphs\":[\"续写\"]},{\"topic\":\"new\",\"title\":\"旧话题\",\"paragraphs\":[\"旧段落\"]}]}");
   if(!former.Continues||!former.Paragraphs.SequenceEqual(new[]{"续写","旧话题","旧段落"}))throw new Exception("Results stored in the former topic format unreadable");
   if(PolishFormat.Excerpt(new string('头',200)+new string('中',500)+new string('尾',400))!=new string('头',200)+"……"+new string('尾',400))throw new Exception("Long context lost its opening or ending");
   Console.WriteLine("PASS polish output parsing and stored-result compatibility");
   db.CreateSession("legacy","d","C:\\test.md");db.EnablePolish("legacy");db.AddFinal("legacy","legacy-1",0,100,"旧原文",false);
   using(var legacyDb=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(root,"events.sqlite3"))){legacyDb.Open();using var c=legacyDb.CreateCommand();c.CommandText="INSERT INTO polish(id,result) VALUES('legacy-1','旧润色结果')";c.ExecuteNonQuery();}
   if(db.CutWindows(_=>true,now)!=0||db.ReadyEvents("legacy",0).Single().Text!="旧润色结果")throw new Exception("Legacy per-utterance results changed");
   Console.WriteLine("PASS legacy per-utterance polish results stay readable");
   db.CreateSession("empty-asr","d","C:\\test.md");
   Directory.CreateDirectory(Path.Combine(root,"audio"));using(var audio=new AudioStore(Path.Combine(root,"audio","empty-asr.pcm"))){audio.Append(new short[32000]);}
   db.AddJob("empty-asr","empty-1",0,16000,false);db.AddJob("empty-asr","empty-2",16000,32000,false);
   using var emptyHttp=new HttpClient(new EmptyAsr());
   await using(var emptySession=new RecordingSession("empty-asr","d","C:\\test.md",root,db,new AsrClient(emptyHttp,"http://localhost","test"),true)){
    var deadline=DateTime.UtcNow.AddSeconds(3);while(db.PendingCount("empty-asr")>0&&DateTime.UtcNow<deadline)await Task.Delay(50);
    if(db.PendingCount("empty-asr")!=0||db.Events("empty-asr",0).Count!=2)throw new Exception("Empty ASR blocked subsequent jobs");
    if(db.Events("empty-asr",0)[0].State!="no_text"||db.ReadyEvents("empty-asr",0)[0].State!="deleted")throw new Exception("Empty ASR entered polishing or editor");
    if(!db.Transcript("empty-asr").Contains("未返回文字"))throw new Exception("Empty segment missing from raw transcript");
   }
   Console.WriteLine("PASS empty ASR retains timestamp and audio, skips insertion, and does not block next job");
   db.CreateSession("length-asr","d","C:\\test.md");using(var audio=new AudioStore(Path.Combine(root,"audio","length-asr.pcm"))){audio.Append(new short[64000]);}db.AddJob("length-asr","length-asr:0",0,64000,false);
   using var lengthHttp=new HttpClient(new TruncatedAsr());
   await using(var lengthSession=new RecordingSession("length-asr","d","C:\\test.md",root,db,new AsrClient(lengthHttp,"http://localhost","test"),true)){
    var deadline=DateTime.UtcNow.AddSeconds(3);while(db.PendingCount("length-asr")>0&&DateTime.UtcNow<deadline)await Task.Delay(50);
    var events=db.Events("length-asr",0);if(db.PendingCount("length-asr")!=0||events.Count!=4||events.Any(e=>e.End-e.Start!=16000||e.State!="no_text"))throw new Exception("Truncated jobs were not bounded and preserved as one-second review intervals");
   }
   Console.WriteLine("PASS truncated ASR splits into bounded intervals without losing audio coverage");
   using var sentenceHttp=new HttpClient(new SentenceAsr());
   await using(var live=new RecordingSession("live-sentence","d","C:\\test.md",root,db,new AsrClient(sentenceHttp,"http://localhost","test"))){
    live.Accept(Enumerable.Repeat((short)8000,16000).ToArray());live.Accept(new short[2560]);await Task.Delay(350);
    if(db.Events("live-sentence",0).Count!=0)throw new Exception("Short hesitation was mistaken for a complete sentence");
    live.Accept(new short[4800]);
    var deadline=DateTime.UtcNow.AddSeconds(2);while(db.Events("live-sentence",0).Count==0&&DateTime.UtcNow<deadline)await Task.Delay(20);
    live.Accept(new short[40000]);await Task.Delay(300);
    if(db.Events("live-sentence",0).Count!=1)throw new Exception("Silent tail generated phantom utterances");
   }
   Console.WriteLine("PASS short hesitation waits for clear pause; trailing silence creates no phantom events");
   var heldPreview=new HeldPreviewAsr();using var previewHttp=new HttpClient(heldPreview);
   await using(var live=new RecordingSession("live-pause","d","C:\\test.md",root,db,new AsrClient(previewHttp,"http://localhost","test"))){
    live.Accept(Enumerable.Repeat((short)8000,32000).ToArray());await heldPreview.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
    live.Accept(new short[40000]);
    var deadline=DateTime.UtcNow.AddMilliseconds(1500);while(db.Events("live-pause",0).Count==0&&DateTime.UtcNow<deadline)await Task.Delay(20);
    if(db.Events("live-pause",0).Count!=1||!heldPreview.Cancelled)throw new Exception("Preview blocked final utterance or required stopping recording");
   }
   Console.WriteLine("PASS clear pause preempts slow preview and emits final without stop");
   using var asrHttp=new HttpClient();var asr=new AsrClient(asrHttp,"http://127.0.0.1:1","test");
   await using var session=new RecordingSession("pause-test","d","C:\\test.md",root,db,asr);
   session.Accept(Enumerable.Repeat((short)8192,3200).ToArray());
   if(session.Rms<0.24 || session.Peak<0.24)throw new Exception("Audio level missing");
   await session.Pause();
   if(!session.Paused || session.Rms!=0 || db.PendingCount("pause-test")!=1)throw new Exception("Pause lost tail or meter not cleared");
   try{session.Accept([100]);throw new Exception("Pause still accepted audio");}catch(InvalidOperationException){}
   await session.Stop();
   if(session.Paused)throw new Exception("Stop retained paused state");
   Console.WriteLine("PASS pause flushes tail, stops input and clears microphone level");
  }finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
 }
 sealed class SentenceAsr:HttpMessageHandler {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"一句完整的话。\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")});
 }
 sealed class HeldPreviewAsr:HttpMessageHandler {
  public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool Cancelled;private int calls;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
   if(++calls==1){Started.SetResult();try{await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){Cancelled=true;throw;}}
   return new(HttpStatusCode.OK){Content=new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"一句完整的话。\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")};
  }
 }
 static HttpResponseMessage Reply(string content)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new {choices=new[]{new {finish_reason="stop",message=new {content}}}}),Encoding.UTF8,"application/json")};
 sealed record LlmBody(string System,string User);
 sealed class ScriptedLlm:HttpMessageHandler {
  private readonly Queue<Func<LlmBody,HttpResponseMessage>> script=new();
  public int Calls;public int Pending=>script.Count;
  public void Next(Func<LlmBody,HttpResponseMessage> reply)=>script.Enqueue(reply);
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
   Calls++;var body=await request.Content!.ReadAsStringAsync(token);
   if(request.Headers.Authorization?.Scheme!="Bearer" || !body.Contains("test-model"))throw new Exception("Missing model/auth");
   using var doc=JsonDocument.Parse(body);var messages=doc.RootElement.GetProperty("messages");
   return script.Dequeue()(new(messages[0].GetProperty("content").GetString()!,messages[1].GetProperty("content").GetString()!));
  }
 }
 sealed class HeldLlm:HttpMessageHandler {
  public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Started.SetResult();await Release.Task.WaitAsync(token);return Reply("完成的润色");}
 }
 /// <summary>Holds every request until released; records user messages and the peak number of simultaneous requests.</summary>
 sealed class GateLlm:HttpMessageHandler {
  public readonly System.Collections.Concurrent.ConcurrentQueue<string> Users=new();
  public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
  private int active;public int Peak;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
   var body=await request.Content!.ReadAsStringAsync(token);
   using var doc=JsonDocument.Parse(body);var user=doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
   var now=Interlocked.Increment(ref active);lock(Users)Peak=Math.Max(Peak,now);Users.Enqueue(user);
   try{await Release.Task.WaitAsync(token);}finally{Interlocked.Decrement(ref active);}
   const string open="<待整理>\n",close="\n</待整理>";var from=user.IndexOf(open,StringComparison.Ordinal)+open.Length;
   return Reply(user[from..user.IndexOf(close,from,StringComparison.Ordinal)]+"。");
  }
 }
 sealed class EmptyAsr:HttpMessageHandler {
  private int calls;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){var text=++calls==1?"language Chinese<asr_text>":"language Chinese<asr_text>有效识别";var json=JsonSerializer.Serialize(new{choices=new[]{new{delta=new{content=text},finish_reason="stop"}}});return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("data: "+json+"\n\ndata: [DONE]\n\n")});}
 }
 sealed class TruncatedAsr:HttpMessageHandler {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"半句\"},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n")});
 }
}
