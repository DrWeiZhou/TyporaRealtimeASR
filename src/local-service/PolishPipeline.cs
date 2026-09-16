using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TyporaAsr;
/// <summary>Polishes windows of utterances, up to MaxConcurrent at a time; the editor receives results in session order.
/// flush(session) tells whether a short window may be cut now (input paused/stopped).</summary>
public sealed class PolishPipeline(Ledger ledger,PolishSettings settings,HttpClient http,Func<string,bool>? flush=null) {
 public const int MaxConcurrent=3;
 private readonly SemaphoreSlim gate=new(1,1);
 private readonly Func<string,bool> flush=flush??(_=>false);
 private readonly ConcurrentDictionary<string,byte> busy=new();
 private sealed record Work(PolishJob Job,PolishConfig Config);
 /// <summary>Manual retry; windows being requested right now keep their snapshot and finish normally.</summary>
 public async Task Retry(string session,CancellationToken token){await gate.WaitAsync(token);try{ledger.RetryPolish(session,busy.Keys.ToArray());}finally{gate.Release();}}
 /// <summary>Requests one window and waits for it (sequential; used by tests).</summary>
 public async Task Step(CancellationToken token){await gate.WaitAsync(token);try{if(Take() is {} work)await Process(work,token);}finally{gate.Release();}}
 private Work? Take(){
  ledger.CutWindows(flush,DateTimeOffset.UtcNow);
  var job=ledger.NextPolish(busy.Keys.ToArray());if(job==null)return null;
  var config=job.Config==null?settings.Current():JsonSerializer.Deserialize<PolishConfig>(PolishSettings.Unprotect(job.Config));if(config==null)return null;
  ledger.SnapshotPolish(job.Id,PolishSettings.Protect(JsonSerializer.Serialize(config)));
  busy[job.Id]=0;return new(job,config);
 }
 private async Task Process(Work work,CancellationToken token){
  try{ledger.CompletePolish(work.Job.Id,await Polish(http,work.Config,work.Job,token));}
  catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
  catch(Exception e){ledger.FailPolish(work.Job.Id,work.Job.Attempts+1,e is PolishException?e.Message:"在线润色连接失败或超时，请检查配置后重试");}
  finally{busy.TryRemove(work.Job.Id,out _);}
 }
 public async Task Run(CancellationToken token){
  var running=new List<Task>();
  while(!token.IsCancellationRequested){
   running.RemoveAll(t=>t.IsCompleted);
   try{
    await gate.WaitAsync(token);
    try{while(running.Count<MaxConcurrent&&Take() is {} work)running.Add(Process(work,token));}
    finally{gate.Release();}
   }
   catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
   catch{ /* Never log remote bodies or credentials. Durable state remains pending. */ }
   try{await Task.Delay(100,token);}catch(OperationCanceledException){break;}
  }
  try{await Task.WhenAll(running);}catch{ /* Cancelled requests stay pending and resume on next start. */ }
 }
 /// <summary>Requests one window; the answer is plain text split into paragraphs, the first possibly continuing the context.</summary>
 public static async Task<PolishResult> Polish(HttpClient http,PolishConfig c,PolishJob job,CancellationToken token)=>
  PolishFormat.Parse(await Request(http,c,PolishFormat.SystemPrompt(c.Prompt),PolishFormat.Input(job.Context,job.Text),token),job.Context.Length>0);
 public static Task<string> Request(HttpClient http,PolishConfig c,string text,CancellationToken token)=>Request(http,c,c.Prompt,text,token);
 public static async Task<string> Request(HttpClient http,PolishConfig c,string system,string text,CancellationToken token){PolishSettings.Validate(c);using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(60));using var request=new HttpRequestMessage(HttpMethod.Post,c.BaseUrl.TrimEnd('/')+"/chat/completions");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",c.ApiKey);request.Content=JsonContent.Create(new {model=c.Model,messages=new[]{new {role="system",content=system},new {role="user",content=text}},stream=false});using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);if(!response.IsSuccessStatusCode)throw new PolishException($"在线润色返回 HTTP {(int)response.StatusCode}，请检查配置或稍后重试");await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);using var bytes=new MemoryStream();var buffer=new byte[8192];int count;while((count=await stream.ReadAsync(buffer,timeout.Token))>0){if(bytes.Length+count>1024*1024)throw new PolishException("润色响应过大");bytes.Write(buffer,0,count);}using var json=JsonDocument.Parse(bytes.ToArray());var choice=json.RootElement.GetProperty("choices")[0];if(choice.TryGetProperty("finish_reason",out var reason)&&reason.GetString()!="stop")throw new PolishException("润色结果未完整生成，请重试");var result=choice.GetProperty("message").GetProperty("content").GetString()?.Trim();if(string.IsNullOrWhiteSpace(result)||result.Length>32000)throw new PolishException("润色结果为空或过长");return result;}
 private sealed class PolishException(string message):Exception(message);
}
