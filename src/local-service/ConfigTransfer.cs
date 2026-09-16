namespace TyporaAsr;

public sealed record PolishExport(string? BaseUrl,string? Model,string? ApiKey,string? Prompt);
public sealed record OnlineAsrExport(bool Enabled,string? Protocol,string? BaseUrl,string? Model,string? ApiKey);
public sealed record StorageExport(string? RecordDirectory);
/// <summary>Exported settings file. API keys are included in plain text by design (the user keeps the file safe).</summary>
public sealed record ConfigExport(string? Format,int Version,string? ExportedAt,PolishExport? Polish,OnlineAsrExport? OnlineAsr,StorageExport? Storage);

/// <summary>Export and import of the online polish, online ASR and record-data directory settings.</summary>
public static class ConfigTransfer {
 public const string FormatName="TyporaASR-config";
 public const int CurrentVersion=1;

 public static ConfigExport Export(PolishSettings polish,OnlineAsrSettings asr,StorageSettings storage){
  var p=polish.Current();var a=asr.Current();
  return new(FormatName,CurrentVersion,DateTimeOffset.Now.ToString("O"),
   p==null?null:new PolishExport(p.BaseUrl,p.Model,p.ApiKey,p.Prompt),
   a==null?null:new OnlineAsrExport(a.Enabled,a.Protocol,a.BaseUrl,a.Model,a.ApiKey),
   new StorageExport(storage.RecordDirectory));
 }

 /// <summary>Validates every section before changing anything, then applies them. A blank key keeps the stored key.
 /// Online ASR is enabled only when test(config) succeeds; otherwise it is saved disabled. Returns one message per section.</summary>
 public static async Task<List<string>> Import(ConfigExport file,PolishSettings polish,OnlineAsrSettings asr,StorageSettings storage,Func<OnlineAsrConfig,Task> test){
  if(file.Format!=FormatName)throw new ArgumentException("不是 TyporaASR 配置文件");
  if(file.Version<1||file.Version>CurrentVersion)throw new ArgumentException($"不支持的配置文件版本 {file.Version}，请升级程序后再导入");
  if(file.Polish==null&&file.OnlineAsr==null&&file.Storage==null)throw new ArgumentException("配置文件不包含任何设置");

  PolishConfig? p=null;
  if(file.Polish is {} fp){
   var candidate=new PolishConfig(Trim(fp.BaseUrl),Trim(fp.Model),Key(fp.ApiKey,polish.Current()?.ApiKey),fp.Prompt??"");
   Check("在线润色设置",()=>PolishSettings.Validate(candidate));p=candidate;
  }
  OnlineAsrConfig? a=null;
  if(file.OnlineAsr is {} fa){
   var candidate=new OnlineAsrConfig(fa.Enabled,string.IsNullOrWhiteSpace(fa.Protocol)?"chat":fa.Protocol.Trim(),Trim(fa.BaseUrl),Trim(fa.Model),Key(fa.ApiKey,asr.Current()?.ApiKey));
   if(candidate.Enabled)Check("在线 ASR 设置",()=>OnlineAsrSettings.Validate(candidate));a=candidate;
  }
  string? directory=null;
  if(file.Storage is {} fs)Check("资料记录目录",()=>directory=StorageSettings.Normalize(fs.RecordDirectory));

  var messages=new List<string>();
  if(p!=null){polish.Save(p);messages.Add("在线润色设置已导入。");}
  if(directory!=null){
   try{messages.Add("资料记录目录已导入："+storage.Save(directory));}
   catch(ArgumentException e){messages.Add($"资料记录目录未导入（{e.Message}），仍使用 {storage.RecordDirectory}。");}
  }
  if(a!=null){
   if(!a.Enabled){
    try{asr.Save(a);}catch(ArgumentException){asr.Disable();}
    messages.Add("在线 ASR 设置已导入（未启用，使用本地模型）。");
   } else {
    string? failure=null;
    try{await test(a);}
    catch(Exception e) when(e is not OperationCanceledException){failure=e is HttpRequestException or InvalidDataException?e.Message:"连接在线 ASR 失败";}
    catch(OperationCanceledException){failure="连接在线 ASR 超时";}
    if(failure==null){asr.Save(a);messages.Add("在线 ASR 设置已导入并启用。");}
    else{asr.Save(a with{Enabled=false});messages.Add($"在线 ASR 设置已导入，但连接测试未通过（{failure}），暂未启用，仍使用本地模型。");}
   }
  }
  return messages;
 }

 private static string Trim(string? value)=>(value??"").Trim();
 private static string Key(string? imported,string? stored)=>string.IsNullOrWhiteSpace(imported)?stored??"":imported.Trim();
 private static void Check(string section,Action validate){try{validate();}catch(ArgumentException e){throw new ArgumentException($"{section}无效：{e.Message}");}}
}
