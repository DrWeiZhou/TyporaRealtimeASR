using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using TyporaAsr;

var builder=WebApplication.CreateBuilder(args);
var root=System.IO.Path.GetFullPath(builder.Configuration["DataRoot"] ?? ".asr");
Directory.CreateDirectory(root);
var tokenPath=System.IO.Path.Combine(root,"token");
if(!File.Exists(tokenPath))File.WriteAllText(tokenPath,Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
var token=File.ReadAllText(tokenPath).Trim();
var port=int.Parse(builder.Configuration["Port"] ?? "18082");
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.AddFilter("Microsoft.AspNetCore",LogLevel.Warning);
using var ledger=new Ledger(root);
using var http=new HttpClient {Timeout=TimeSpan.FromSeconds(100)};
var endpoint=builder.Configuration["AsrEndpoint"] ?? "http://127.0.0.1:18081";
var asr=new AsrClient(http,endpoint,builder.Configuration["AsrModel"] ?? "qwen3-asr");
var polishSettings=new PolishSettings(root);
using var onlineHttp=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(65)};
var polishPipeline=new PolishPipeline(ledger,polishSettings,onlineHttp);
using var pipelineCancel=new CancellationTokenSource();
var sessions=new ConcurrentDictionary<string,RecordingSession>();
var leases=new Dictionary<string,(string Owner,DateTime Expires)>(StringComparer.OrdinalIgnoreCase);
var sessionGate=new SemaphoreSlim(1,1);
var app=builder.Build();
app.Use(async(context,next)=>{
    if(!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()),System.Text.Encoding.UTF8.GetBytes("Bearer "+token))) {context.Response.StatusCode=401;return;}
    // No CORS: authenticated native plugin connections only.
    try {await next();}
    catch(Exception error) {context.Response.StatusCode=error is ArgumentException?400:409;await context.Response.WriteAsJsonAsync(new {error=error.Message});}
});
string Client(HttpContext c){var id=c.Request.Headers["X-ASR-Client"].ToString();return Guid.TryParse(id,out _)?id:throw new ArgumentException("Missing client identity");}
void Claim(string path,string owner) {
    lock(leases){if(leases.TryGetValue(path,out var lease)&&lease.Owner!=owner&&lease.Expires>DateTime.UtcNow)throw new InvalidOperationException("该文档已由另一窗口绑定");leases[path]=(owner,DateTime.UtcNow.AddSeconds(12));}
}
RecordingSession Owned(string id,HttpContext context) {
    if(!sessions.TryGetValue(id,out var s))throw new ArgumentException("请先恢复会话");
    Claim(s.Path,Client(context));ledger.TouchSession(id);return s;
}
app.MapGet("/health",()=>new {status="ok",protocolVersion=2});
app.MapGet("/model-health",async()=>{try{using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));using var response=await http.GetAsync(endpoint.TrimEnd('/')+"/health",deadline.Token);return Results.Ok(new {ready=response.IsSuccessStatusCode});}catch{return Results.Ok(new {ready=false});}});
app.MapGet("/polish/config",()=>polishSettings.Public());
app.MapPost("/polish/config",(PolishConfig config)=>{polishSettings.Save(config);return Results.Ok(polishSettings.Public());});
app.MapPost("/polish/test",async(HttpContext context)=>{var config=polishSettings.Current()??throw new ArgumentException("请先保存润色配置");try{await PolishPipeline.Request(onlineHttp,config,"这是一条连接测试文本。",context.RequestAborted);return Results.Ok(new {ok=true});}catch{throw new ArgumentException("在线连接测试失败，请检查地址、模型、密钥与网络");}});
app.MapGet("/devices",()=>{
    using var enumerator=new MMDeviceEnumerator();var devices=enumerator.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active);
    var list=new List<object>{new {id=-1,name="系统默认麦克风"}};
    for(var i=0;i<devices.Count;i++){using var device=devices[i];list.Add(new {id=i,name=device.FriendlyName});}return list;
});
app.MapGet("/sessions",()=>ledger.Sessions());
app.MapPost("/sessions",async(StartRequest request,HttpContext context)=>{
    if(!Guid.TryParse(request.SessionId,out _) || !Guid.TryParse(request.DocumentId,out _))throw new ArgumentException("Invalid session/document id");
    if(!System.IO.Path.IsPathFullyQualified(request.Path)||!request.Path.EndsWith(".md",StringComparison.OrdinalIgnoreCase)||!File.Exists(request.Path))throw new ArgumentException("请先保存目标 Markdown 文件");
    await sessionGate.WaitAsync();
    try {
        var path=System.IO.Path.GetFullPath(request.Path);
        Claim(path,Client(context));
        if(sessions.TryGetValue(request.SessionId,out var prior)){if(prior.Path!=path)throw new ArgumentException("会话路径不匹配");return Results.Ok(prior.Status());}
        if(sessions.Values.Any(s=>s.Recording||s.Paused))throw new InvalidOperationException("已有录音会话，请先结束");
        if(ledger.Session(request.SessionId)!=null)throw new InvalidOperationException("旧会话请使用恢复功能");
        using(var health=await http.GetAsync(endpoint.TrimEnd('/')+"/health"))health.EnsureSuccessStatusCode();
        // Warm the complete audio path; short silence may yield no text, which is harmless here.
        try {await asr.Recognize(new short[16000],context.RequestAborted);} catch(InvalidDataException){}
        var session=new RecordingSession(request.SessionId,request.DocumentId,path,root,ledger,asr);
        ledger.EnablePolish(session.Id);
        sessions[session.Id]=session;
        try {session.Start(request.Device);} catch {sessions.TryRemove(session.Id,out _);await session.DisposeAsync();throw;}
        return Results.Ok(session.Status());
    } finally{sessionGate.Release();}
});
app.MapPost("/sessions/{id}/recover",async(string id,HttpContext context)=>{
    await sessionGate.WaitAsync();try {
        var stored=ledger.Session(id)??throw new ArgumentException("会话不存在");Claim(stored.Path,Client(context));
        ledger.EnablePolish(id);
        var s=sessions.GetOrAdd(id,_=>new RecordingSession(id,stored.Document,stored.Path,root,ledger,asr,true));return Results.Ok(s.Status());
    }finally{sessionGate.Release();}
});
app.MapGet("/sessions/{id}",(string id,HttpContext c)=>Owned(id,c).Status());
app.MapGet("/sessions/{id}/events",(string id,long after,HttpContext c)=>{Owned(id,c);return ledger.ReadyEvents(id,Math.Max(0,after));});
app.MapGet("/sessions/{id}/transcript",(string id,HttpContext c)=>{Owned(id,c);return new {text=ledger.Transcript(id)};});
app.MapPost("/sessions/{id}/polish/retry",async(string id,HttpContext c)=>{Owned(id,c);await polishPipeline.Retry(id,c.RequestAborted);return Results.Ok();});
app.MapPost("/sessions/{id}/pause",async(string id,HttpContext c)=>{var s=Owned(id,c);await s.Pause();return Results.Ok(s.Status());});
app.MapPost("/sessions/{id}/resume",async(string id,ResumeRequest request,HttpContext c)=>{await sessionGate.WaitAsync();try{var s=Owned(id,c);if(sessions.Values.Any(other=>other.Id!=id && other.Recording))throw new ArgumentException("另一个会话正在录音");await s.Resume(request.Device);return Results.Ok(s.Status());}finally{sessionGate.Release();}});
app.MapPost("/sessions/{id}/stop",async(string id,HttpContext c)=>{var s=Owned(id,c);await s.Stop();return Results.Ok(s.Status());});
app.MapPost("/sessions/{id}/ack",(string id,AckRequest r,HttpContext c)=>{Owned(id,c);ledger.Acknowledge(id,r.EventId,r.State);return Results.Ok();});
var connection=new {endpoint=$"http://127.0.0.1:{port}",token,protocolVersion=2};
File.WriteAllText(System.IO.Path.Combine(root,"connection.json"),JsonSerializer.Serialize(connection));
Console.WriteLine($"Typora ASR: http://127.0.0.1:{port}; connection file: {System.IO.Path.Combine(root,"connection.json")}");
var pipelineWorker=polishPipeline.Run(pipelineCancel.Token);
try {await app.RunAsync();}finally{pipelineCancel.Cancel();await pipelineWorker;foreach(var session in sessions.Values)await session.DisposeAsync();}

record StartRequest(string SessionId,string DocumentId,string Path,int Device=-1);
record AckRequest(string EventId,string State);
record ResumeRequest(int Device=-1);
