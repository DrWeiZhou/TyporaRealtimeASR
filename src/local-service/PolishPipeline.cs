using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TyporaAsr;
/// <summary>Polishes windows of utterances in order. flush(session) tells whether a short window may be cut now (input paused/stopped).</summary>
public sealed class PolishPipeline(Ledger ledger,PolishSettings settings,HttpClient http,Func<string,bool>? flush=null) {
 private readonly SemaphoreSlim gate=new(1,1);
 private readonly Func<string,bool> flush=flush??(_=>false);
 public async Task Retry(string session,CancellationToken token){await gate.WaitAsync(token);try{ledger.RetryPolish(session);}finally{gate.Release();}}
 public async Task Step(CancellationToken token){await gate.WaitAsync(token);try{await ProcessOne(token);}finally{gate.Release();}}
 private async Task ProcessOne(CancellationToken token){
  ledger.CutWindows(flush,DateTimeOffset.UtcNow);
  var job=ledger.NextPolish();if(job==null)return;
  var config=job.Config==null?settings.Current():JsonSerializer.Deserialize<PolishConfig>(PolishSettings.Unprotect(job.Config));if(config==null)return;
  ledger.SnapshotPolish(job.Id,PolishSettings.Protect(JsonSerializer.Serialize(config)));
  try{var blocks=await Polish(http,config,job,token);ledger.CompletePolish(job.Id,blocks);}
  catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
  catch(Exception e){ledger.FailPolish(job.Id,job.Attempts+1,e is PolishException?e.Message:"在线润色连接失败或超时，请检查配置后重试");}
 }
 public async Task Run(CancellationToken token){while(!token.IsCancellationRequested){try{await Step(token);}catch(OperationCanceledException)when(token.IsCancellationRequested){break;}catch{ /* Never log remote bodies or credentials. Durable state remains pending. */ }try{await Task.Delay(100,token);}catch(OperationCanceledException){break;}}}
 /// <summary>Requests one window; an unparseable answer is requested once more before the window counts as failed.</summary>
 public static async Task<List<PolishBlock>> Polish(HttpClient http,PolishConfig c,PolishJob job,CancellationToken token){
  var topics=job.Topics??[];
  var system=PolishFormat.SystemPrompt(c.Prompt);var input=PolishFormat.Input(job.Context,topics,job.Text);
  for(var attempt=0;;attempt++){
   var blocks=PolishFormat.Parse(await Request(http,c,system,input,token),topics.Length>0);
   if(blocks!=null)return blocks;
   if(attempt>=1)throw new PolishException("润色结果格式无法解析，请重试");
  }
 }
 public static Task<string> Request(HttpClient http,PolishConfig c,string text,CancellationToken token)=>Request(http,c,c.Prompt,text,token);
 public static async Task<string> Request(HttpClient http,PolishConfig c,string system,string text,CancellationToken token){PolishSettings.Validate(c);using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(60));using var request=new HttpRequestMessage(HttpMethod.Post,c.BaseUrl.TrimEnd('/')+"/chat/completions");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",c.ApiKey);request.Content=JsonContent.Create(new {model=c.Model,messages=new[]{new {role="system",content=system},new {role="user",content=text}},stream=false});using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);if(!response.IsSuccessStatusCode)throw new PolishException($"在线润色返回 HTTP {(int)response.StatusCode}，请检查配置或稍后重试");await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);using var bytes=new MemoryStream();var buffer=new byte[8192];int count;while((count=await stream.ReadAsync(buffer,timeout.Token))>0){if(bytes.Length+count>1024*1024)throw new PolishException("润色响应过大");bytes.Write(buffer,0,count);}using var json=JsonDocument.Parse(bytes.ToArray());var choice=json.RootElement.GetProperty("choices")[0];if(choice.TryGetProperty("finish_reason",out var reason)&&reason.GetString()!="stop")throw new PolishException("润色结果未完整生成，请重试");var result=choice.GetProperty("message").GetProperty("content").GetString()?.Trim();if(string.IsNullOrWhiteSpace(result)||result.Length>32000)throw new PolishException("润色结果为空或过长");return result;}
 private sealed class PolishException(string message):Exception(message);
}
