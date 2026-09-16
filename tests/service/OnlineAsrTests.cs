using TyporaAsr;
using System.Net;
using System.Text;
using System.Text.Json;

static class OnlineAsrTests {
 public static async Task Run() {
  var root=Path.Combine(Path.GetTempPath(),"asr-online-"+Guid.NewGuid());Directory.CreateDirectory(root);
  try {
   var settings=new OnlineAsrSettings(root);
   if(settings.Online||JsonSerializer.Serialize(settings.Public()).Contains("true"))throw new Exception("Online ASR enabled by default");
   var key="asr-secret-"+Guid.NewGuid();
   settings.Save(new OnlineAsrConfig(true,"chat","https://dashscope.aliyuncs.com/compatible-mode/v1","qwen3-asr-flash",key));
   if(File.ReadAllText(Path.Combine(root,"asr-settings.json")).Contains(key))throw new Exception("ASR key stored in plaintext");
   if(!File.ReadAllText(Path.Combine(root,"asr-mode.json")).Contains("\"online\":true"))throw new Exception("Mode flag for start.ps1 missing");
   var reopened=new OnlineAsrSettings(root);
   if(!reopened.Online||reopened.Current()?.ApiKey!=key)throw new Exception("ASR settings did not round-trip");
   reopened.Save(new OnlineAsrConfig(true,"chat","https://dashscope.aliyuncs.com/compatible-mode/v1","qwen3-asr-flash",""));
   if(reopened.Current()?.ApiKey!=key)throw new Exception("Blank key did not keep stored key");
   try{reopened.Save(new OnlineAsrConfig(true,"chat","http://insecure.example/v1","m","k"));throw new Exception("HTTP URL accepted");}catch(ArgumentException){}
   try{reopened.Save(new OnlineAsrConfig(true,"other","https://example.com/v1","m","k"));throw new Exception("Unknown protocol accepted");}catch(ArgumentException){}
   reopened.Disable();
   if(reopened.Online||!File.ReadAllText(Path.Combine(root,"asr-mode.json")).Contains("\"online\":false"))throw new Exception("Disable did not switch back to local");
   Console.WriteLine("PASS online ASR settings encrypted, key kept, validated, mode flag written and disabled");

   var chat=new FakeAsr(body=>{
    using var doc=JsonDocument.Parse(body);var part=doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
    var data=part.GetProperty("input_audio").GetProperty("data").GetString()!;
    if(part.GetProperty("type").GetString()!="input_audio"||!data.StartsWith("data:audio/wav;base64,UklGR"))throw new Exception("DashScope audio must be a WAV data URI");
    if(doc.RootElement.GetProperty("model").GetString()!="qwen3-asr-flash")throw new Exception("Model missing");
    return Json("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"在线识别结果。\"}}]}");
   });
   using(var http=new HttpClient(chat)){
    var config=new OnlineAsrConfig(true,"chat","https://dashscope.aliyuncs.com/compatible-mode/v1/","qwen3-asr-flash","k");
    if(await OnlineAsrClient.Recognize(http,config,new short[16000],CancellationToken.None)!="在线识别结果。")throw new Exception("Chat transcript missing");
    if(chat.Path!="/compatible-mode/v1/chat/completions"||chat.Auth!="Bearer k")throw new Exception("Chat endpoint or auth wrong");
   }
   var openai=new FakeAsr(body=>{
    using var doc=JsonDocument.Parse(body);var data=doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("input_audio").GetProperty("data").GetString()!;
    if(!data.StartsWith("UklGR"))throw new Exception("OpenAI-style audio must be raw base64");
    return Json("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"半句\"}}]}");
   });
   using(var http=new HttpClient(openai)){
    try{await OnlineAsrClient.Recognize(http,new OnlineAsrConfig(true,"chat","https://api.example.com/v1","m","k"),new short[16000],CancellationToken.None);throw new Exception("Truncated online result accepted");}
    catch(InvalidDataException e) when(Equals(e.Data["FinishReason"],"length")){}
   }
   var whisper=new FakeAsr(body=>{
    if(!body.Contains("audio.wav")||!body.Contains("whisper-1")||!body.Contains("RIFF"))throw new Exception("Multipart form incomplete");
    return Json("{\"text\":\" 转写文本 \"}");
   });
   using(var http=new HttpClient(whisper)){
    if(await OnlineAsrClient.Recognize(http,new OnlineAsrConfig(true,"transcriptions","https://api.example.com/v1","whisper-1","k"),new short[1600],CancellationToken.None)!="转写文本")throw new Exception("Transcription text missing");
    if(whisper.Path!="/v1/audio/transcriptions")throw new Exception("Transcriptions endpoint wrong");
   }
   var denied=new FakeAsr(_=>new(HttpStatusCode.Unauthorized){Content=new StringContent("{\"error\":{\"message\":\"Invalid API key\"}}")});
   using(var http=new HttpClient(denied)){
    try{await OnlineAsrClient.Recognize(http,new OnlineAsrConfig(true,"chat","https://api.example.com/v1","m","k"),new short[1600],CancellationToken.None);throw new Exception("HTTP error ignored");}
    catch(HttpRequestException e) when(e.Message.Contains("401")&&e.Message.Contains("Invalid API key")){}
   }
   Console.WriteLine("PASS online ASR chat (data URI / base64), truncation, transcriptions and error messages");

   var routed=new FakeAsr(_=>Json("{\"text\":\"在线\"}"));using var routedHttp=new HttpClient(routed);
   var routing=new OnlineAsrSettings(root);
   var router=new RecognizerRouter(new AsrClient(new HttpClient(),"http://127.0.0.1:1","local"),routing,routedHttp);
   if(!router.Previews)throw new Exception("Local mode lost previews");
   routing.Save(new OnlineAsrConfig(true,"transcriptions","https://api.example.com/v1","whisper-1","k"));
   if(router.Previews||await router.Recognize(new short[1600],CancellationToken.None)!="在线")throw new Exception("Router did not use online ASR");
   routing.Disable();
   try{await router.Recognize(new short[1600],CancellationToken.None);throw new Exception("Router still online after disable");}catch(HttpRequestException){}
   Console.WriteLine("PASS recognizer switches between local and online without restart and disables online previews");
  } finally {Directory.Delete(root,true);}
 }
 static HttpResponseMessage Json(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
 sealed class FakeAsr(Func<string,HttpResponseMessage> reply):HttpMessageHandler {
  public string? Path,Auth;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
   Path=request.RequestUri!.AbsolutePath;Auth=request.Headers.Authorization?.ToString();
   var bytes=await request.Content!.ReadAsByteArrayAsync(token);
   var body=request.Content is MultipartFormDataContent?Encoding.Latin1.GetString(bytes):Encoding.UTF8.GetString(bytes);
   return reply(body);
  }
 }
}
