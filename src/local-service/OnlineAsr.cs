using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TyporaAsr;

/// <summary>Speech recognizer used by recording sessions. Previews=false disables speculative preview requests.</summary>
public interface ISpeechRecognizer {
 bool Previews {get;}
 Task<string> Recognize(short[] pcm,CancellationToken ct);
}

/// <summary>Protocol: "chat" = OpenAI-compatible /chat/completions with input_audio (e.g. Qwen3-ASR);
/// "transcriptions" = /audio/transcriptions multipart (e.g. Whisper, SenseVoice).</summary>
public sealed record OnlineAsrConfig(bool Enabled,string Protocol,string BaseUrl,string Model,string ApiKey);

public sealed class OnlineAsrSettings {
 public static readonly string[] Protocols=["chat","transcriptions"];
 private static readonly ISecretProtector DefaultProtector=new DpapiSecretProtector();
 private readonly string file,modeFile;private readonly object gate=new();private readonly ISecretProtector protector;
 private OnlineAsrConfig? cached;private bool loaded;
 public OnlineAsrSettings(string root,ISecretProtector? protector=null){file=Path.Combine(root,"asr-settings.json");modeFile=Path.Combine(root,"asr-mode.json");this.protector=protector??DefaultProtector;}
 public OnlineAsrConfig? Current(){lock(gate){
  if(!loaded){cached=File.Exists(file)?JsonSerializer.Deserialize<OnlineAsrConfig>(protector.Unprotect(JsonSerializer.Deserialize<string>(File.ReadAllText(file))!)):null;loaded=true;}
  return cached;
 }}
 /// <summary>True when recognition should go to the online service.</summary>
 public bool Online=>Current() is {Enabled:true};
 public object Public(){var c=Current();return new {enabled=c?.Enabled??false,protocol=c?.Protocol??"chat",baseUrl=c?.BaseUrl??"",model=c?.Model??"",hasKey=!string.IsNullOrEmpty(c?.ApiKey)};}
 /// <summary>Saves the configuration. A blank key keeps the stored key.</summary>
 public OnlineAsrConfig Save(OnlineAsrConfig c){lock(gate){
  if(string.IsNullOrWhiteSpace(c.ApiKey))c=c with{ApiKey=Current()?.ApiKey??""};
  c=c with{Protocol=c.Protocol.Trim(),BaseUrl=c.BaseUrl.Trim(),Model=c.Model.Trim(),ApiKey=c.ApiKey.Trim()};
  Validate(c);
  var tmp=file+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(protector.Protect(JsonSerializer.Serialize(c))));File.Move(tmp,file,true);
  // Plain mode flag lets tools/start.ps1 skip the local model without reading secrets.
  var modeTmp=modeFile+".tmp";File.WriteAllText(modeTmp,JsonSerializer.Serialize(new {online=c.Enabled}));File.Move(modeTmp,modeFile,true);
  cached=c;loaded=true;return c;
 }}
 /// <summary>Turns online recognition off without requiring a valid form.</summary>
 public void Disable(){lock(gate){var c=Current();if(c==null){var modeTmp=modeFile+".tmp";File.WriteAllText(modeTmp,JsonSerializer.Serialize(new {online=false}));File.Move(modeTmp,modeFile,true);return;}Save(c with{Enabled=false});}}
 public static void Validate(OnlineAsrConfig c){
  if(!Protocols.Contains(c.Protocol))throw new ArgumentException("在线 ASR 接口类型无效");
  if(!Uri.TryCreate(c.BaseUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!string.IsNullOrEmpty(uri.UserInfo)||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment))throw new ArgumentException("在线 ASR 地址必须为 HTTPS，且不含用户名、查询参数或片段");
  if(string.IsNullOrWhiteSpace(c.Model)||string.IsNullOrWhiteSpace(c.ApiKey))throw new ArgumentException("请填写在线 ASR 模型名称和 API Key");
  if(c.ApiKey.Length>4096||c.Model.Length>256||c.BaseUrl.Length>2048)throw new ArgumentException("在线 ASR 配置内容过长");
 }
}

public static class OnlineAsrClient {
 public static async Task<string> Recognize(HttpClient http,OnlineAsrConfig c,short[] pcm,CancellationToken ct){
  OnlineAsrSettings.Validate(c);
  var wav=AsrClient.ToWav(pcm);var root=c.BaseUrl.TrimEnd('/');
  using var request=new HttpRequestMessage(HttpMethod.Post,root+(c.Protocol=="transcriptions"?"/audio/transcriptions":"/chat/completions"));
  request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",c.ApiKey);
  if(c.Protocol=="transcriptions"){
   var form=new MultipartFormDataContent();
   var audio=new ByteArrayContent(wav);audio.Headers.ContentType=new MediaTypeHeaderValue("audio/wav");
   form.Add(audio,"file","audio.wav");form.Add(new StringContent(c.Model),"model");form.Add(new StringContent("json"),"response_format");
   request.Content=form;
  } else {
   var base64=Convert.ToBase64String(wav);
   // DashScope (Qwen3-ASR) expects a data URI; OpenAI-style services expect raw base64 plus format.
   var host=new Uri(root).Host;
   var data=host.Contains("dashscope",StringComparison.OrdinalIgnoreCase)||host.EndsWith("aliyuncs.com",StringComparison.OrdinalIgnoreCase)?"data:audio/wav;base64,"+base64:base64;
   request.Content=JsonContent.Create(new {model=c.Model,stream=false,temperature=0,messages=new[]{new {role="user",content=new[]{new {type="input_audio",input_audio=new {data,format="wav"}}}}}});
  }
  using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
  var body=await ReadLimited(response,ct);
  if(!response.IsSuccessStatusCode)throw new HttpRequestException($"在线 ASR 返回 HTTP {(int)response.StatusCode}{ErrorMessage(body)}，请检查配置");
  using var json=JsonDocument.Parse(body);
  if(c.Protocol=="transcriptions")return json.RootElement.TryGetProperty("text",out var t)&&t.ValueKind==JsonValueKind.String?(t.GetString()??"").Trim():throw new InvalidDataException("在线 ASR 响应缺少 text");
  var choice=json.RootElement.GetProperty("choices")[0];
  var finish=choice.TryGetProperty("finish_reason",out var r)&&r.ValueKind==JsonValueKind.String?r.GetString():"stop";
  var message=choice.GetProperty("message");var text=new StringBuilder();
  if(message.TryGetProperty("content",out var content)){
   if(content.ValueKind==JsonValueKind.String)text.Append(content.GetString());
   else if(content.ValueKind==JsonValueKind.Array)foreach(var part in content.EnumerateArray())if(part.ValueKind==JsonValueKind.Object&&part.TryGetProperty("text",out var p)&&p.ValueKind==JsonValueKind.String)text.Append(p.GetString());
  }
  var output=new AsrOutput();output.Add(text.ToString(),finish);
  return output.FinalText();
 }
 private static async Task<byte[]> ReadLimited(HttpResponseMessage response,CancellationToken ct){
  await using var stream=await response.Content.ReadAsStreamAsync(ct);using var bytes=new MemoryStream();var buffer=new byte[8192];int count;
  while((count=await stream.ReadAsync(buffer,ct))>0){if(bytes.Length+count>1024*1024)throw new InvalidDataException("在线 ASR 响应过大");bytes.Write(buffer,0,count);}
  return bytes.ToArray();
 }
 private static string ErrorMessage(byte[] body){
  try{using var json=JsonDocument.Parse(body);var root=json.RootElement;
   var error=root.TryGetProperty("error",out var e)?e:root;
   var message=error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("message",out var m)&&m.ValueKind==JsonValueKind.String?m.GetString():error.ValueKind==JsonValueKind.String?error.GetString():null;
   if(string.IsNullOrWhiteSpace(message))return "";
   message=message.ReplaceLineEndings(" ");return "（"+(message.Length>120?message[..120]:message)+"）";
  }catch{return "";}
 }
}

/// <summary>Routes each recognition to the online service when enabled, otherwise to the local model.</summary>
public sealed class RecognizerRouter(AsrClient local,OnlineAsrSettings settings,HttpClient http):ISpeechRecognizer {
 public bool Previews=>!settings.Online;
 public Task<string> Recognize(short[] pcm,CancellationToken ct){
  var config=settings.Current();
  return config is {Enabled:true}?OnlineAsrClient.Recognize(http,config,pcm,ct):local.Recognize(pcm,ct);
 }
}
