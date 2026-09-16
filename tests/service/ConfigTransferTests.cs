using TyporaAsr;
using System.Text.Json;

static class ConfigTransferTests {
 public static async Task Run() {
  var from=Path.Combine(Path.GetTempPath(),"asr-config-from-"+Guid.NewGuid());var to=Path.Combine(Path.GetTempPath(),"asr-config-to-"+Guid.NewGuid());
  Directory.CreateDirectory(from);Directory.CreateDirectory(to);
  try {
   var web=new JsonSerializerOptions(JsonSerializerDefaults.Web);
   var polish=new PolishSettings(from);var asr=new OnlineAsrSettings(from);var storage=new StorageSettings(from);
   polish.Save(new PolishConfig("https://llm.example.com/v1","fast-model","polish-key","整理提示词"));
   asr.Save(new OnlineAsrConfig(true,"transcriptions","https://asr.example.com/v1","asr-model","asr-key"));
   storage.Save(Path.Combine(from,"records"));
   var json=JsonSerializer.Serialize(ConfigTransfer.Export(polish,asr,storage),web);
   using(var doc=JsonDocument.Parse(json)){
    var r=doc.RootElement;var p=r.GetProperty("polish");var a=r.GetProperty("onlineAsr");
    if(r.GetProperty("format").GetString()!="TyporaASR-config"||r.GetProperty("version").GetInt32()!=1
     ||p.GetProperty("apiKey").GetString()!="polish-key"||p.GetProperty("prompt").GetString()!="整理提示词"
     ||a.GetProperty("apiKey").GetString()!="asr-key"||a.GetProperty("protocol").GetString()!="transcriptions"||!a.GetProperty("enabled").GetBoolean()
     ||r.GetProperty("storage").GetProperty("recordDirectory").GetString()!=Path.Combine(from,"records"))throw new Exception("Export incomplete: "+json);
   }
   var file=JsonSerializer.Deserialize<ConfigExport>(json,web)!;

   var polish2=new PolishSettings(to);var asr2=new OnlineAsrSettings(to);var storage2=new StorageSettings(to);
   var tested=0;
   var messages=await ConfigTransfer.Import(file with{Storage=new(Path.Combine(to,"records"))},polish2,asr2,storage2,c=>{tested++;if(c.ApiKey!="asr-key"||c.Protocol!="transcriptions")throw new Exception("Wrong candidate tested");return Task.CompletedTask;});
   if(tested!=1||messages.Count!=3)throw new Exception("Import messages: "+string.Join("|",messages));
   if(new PolishSettings(to).Current() is not {BaseUrl:"https://llm.example.com/v1",Model:"fast-model",ApiKey:"polish-key",Prompt:"整理提示词"})throw new Exception("Polish settings not imported");
   if(new OnlineAsrSettings(to).Current() is not {Enabled:true,Model:"asr-model",ApiKey:"asr-key"})throw new Exception("Online ASR not imported and enabled");
   if(new StorageSettings(to).RecordDirectory!=Path.Combine(to,"records")||!Directory.Exists(Path.Combine(to,"records")))throw new Exception("Record directory not imported");
   if(File.ReadAllText(Path.Combine(to,"polish-settings.json")).Contains("polish-key"))throw new Exception("Imported key stored in plaintext");
   Console.WriteLine("PASS configuration export includes keys and import restores all three sections");

   messages=await ConfigTransfer.Import(file with{Polish=file.Polish! with{ApiKey=""},OnlineAsr=file.OnlineAsr! with{ApiKey=" ",Model="asr-model-2"},Storage=null},polish2,asr2,storage2,_=>throw new HttpRequestException("在线 ASR 返回 HTTP 401"));
   if(new PolishSettings(to).Current()?.ApiKey!="polish-key")throw new Exception("Blank imported key did not keep the stored key");
   if(new OnlineAsrSettings(to).Current() is not {Enabled:false,Model:"asr-model-2",ApiKey:"asr-key"}||!messages.Any(m=>m.Contains("HTTP 401")&&m.Contains("暂未启用")))throw new Exception("Failed connection test enabled online ASR: "+string.Join("|",messages));
   if(new StorageSettings(to).RecordDirectory!=Path.Combine(to,"records"))throw new Exception("Missing storage section changed the directory");
   Console.WriteLine("PASS import keeps stored keys for blank values and leaves online ASR disabled when its test fails");

   foreach(var (bad,reason) in new (ConfigExport,string)[]{
    (file with{Format="other"},"不是 TyporaASR 配置文件"),
    (file with{Version=99},"版本"),
    (new ConfigExport("TyporaASR-config",1,null,null,null,null),"不包含任何设置"),
    (file with{Polish=file.Polish! with{BaseUrl="http://insecure.example/v1"},Storage=new(Path.Combine(to,"never"))},"在线润色设置无效"),
    (file with{Storage=new("relative\\dir")},"资料记录目录无效")}){
    var before=File.ReadAllText(Path.Combine(to,"polish-settings.json"));
    try{await ConfigTransfer.Import(bad,polish2,asr2,storage2,_=>Task.CompletedTask);throw new Exception("Invalid import accepted: "+reason);}
    catch(ArgumentException e) when(e.Message.Contains(reason)){}
    if(File.ReadAllText(Path.Combine(to,"polish-settings.json"))!=before||Directory.Exists(Path.Combine(to,"never")))throw new Exception("Invalid import changed settings: "+reason);
   }
   Console.WriteLine("PASS invalid configuration files are rejected before anything is changed");
  } finally {Directory.Delete(from,true);Directory.Delete(to,true);}
 }
}
