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
   db.CreateSession("s","d","C:\\test.md");db.EnablePolish("s");
   db.BeginSpan("s",0,DateTimeOffset.Parse("2026-09-12T10:00:00+08:00"));db.EndSpan("s",16000);
   db.BeginSpan("s",16000,DateTimeOffset.Parse("2026-09-12T10:01:00+08:00"));db.EndSpan("s",32000);
   db.AddFinal("s","e1",0,16000,"原始一",false);db.AddFinal("s","e2",16000,32000,"原始二",false);
   if(db.ReadyEvents("s",0).Count!=0)throw new Exception("Raw text released before polish");
   var handler=new FakeLlm();using var http=new HttpClient(handler);
   var pipeline=new PolishPipeline(db,settings,http);
   await pipeline.Step(CancellationToken.None);
   if(db.ReadyEvents("s",0).Count!=0)throw new Exception("Failure released raw text");
   db.RetryPolish("s");await pipeline.Step(CancellationToken.None);
   if(db.ReadyEvents("s",0).Single().Text!="润色一")throw new Exception("Polished output missing");
   await pipeline.Step(CancellationToken.None);
   if(db.ReadyEvents("s",0).Count!=2 || db.Events("s",0)[0].Text!="原始一")throw new Exception("Original overwritten or ordering broken");
   var calls=handler.Calls;await pipeline.Step(CancellationToken.None);if(handler.Calls!=calls)throw new Exception("Completed result requested again");
   var raw=db.Transcript("s");if(!raw.Contains("10:01:00") || !raw.Contains("00:00:01") || !raw.Contains("原始二"))throw new Exception("Pause timestamp mapping lost");
   using var reopened=new Ledger(root);if(reopened.ReadyEvents("s",0).Count!=2)throw new Exception("Results lost on restart");
   Console.WriteLine("PASS encrypted settings, failed polish blocked, retry, ordering, immutable timestamps and restart");
   db.CreateSession("concurrent","d","C:\\test.md");db.EnablePolish("concurrent");db.AddFinal("concurrent","flight",0,16000,"原始一",false);
   var held=new HeldLlm();using var heldHttp=new HttpClient(held);var concurrent=new PolishPipeline(db,settings,heldHttp);
   var step=concurrent.Step(CancellationToken.None);await held.Started.Task;
   var retry=concurrent.Retry("concurrent",CancellationToken.None);
   if(retry.IsCompleted)throw new Exception("Retry mutated an in-flight snapshot");
   held.Release.SetResult();await Task.WhenAll(step,retry);
   if(db.ReadyEvents("concurrent",0).Single().Text!="完成的润色")throw new Exception("In-flight result corrupted by retry");
   Console.WriteLine("PASS retry waits for in-flight task and preserves completed result");
   db.CreateSession("retry-limit","d","C:\\test.md");db.EnablePolish("retry-limit");db.AddFinal("retry-limit","blocked",0,100,"原始文本",false);db.SnapshotPolish("blocked","unused");db.FailPolish("blocked",3,"模拟重试耗尽");
   if(db.NextPolish()!=null||db.ReadyEvents("retry-limit",0).Count!=0)throw new Exception("Retry cap bypassed or raw released");
   db.RetryPolish("retry-limit");if(db.NextPolish()?.Id!="blocked")throw new Exception("Manual retry did not unblock exhausted task");db.Acknowledge("retry-limit","blocked","deleted");
   Console.WriteLine("PASS retry limit retains raw and requires manual retry");
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
 sealed class FakeLlm:HttpMessageHandler {
  public int Calls;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
   Calls++;var body=await request.Content!.ReadAsStringAsync(token);
   if(request.Headers.Authorization?.Scheme!="Bearer" || !body.Contains("test-model"))throw new Exception("Missing model/auth");
   if(Calls==1)return new(HttpStatusCode.TooManyRequests);
   using var doc=JsonDocument.Parse(body);var raw=doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
   return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new {choices=new[]{new {finish_reason="stop",message=new {content=raw=="原始一"?"润色一":"润色二"}}}}),Encoding.UTF8,"application/json")};
  }
 }
 sealed class HeldLlm:HttpMessageHandler {
  public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Started.SetResult();await Release.Task.WaitAsync(token);return new(HttpStatusCode.OK){Content=new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"完成的润色\"}}]}")};}
 }
}
