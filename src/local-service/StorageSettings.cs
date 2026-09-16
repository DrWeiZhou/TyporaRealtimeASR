using System.Text.Json;

namespace TyporaAsr;
/// <summary>Record-data directory: where WAV recordings and raw transcripts of new sessions are saved.
/// Defaults to ~/.TyporaASR/RecordData, separate from the Markdown documents. Sessions that already have a file mapping keep their paths.</summary>
public sealed class StorageSettings(string root) {
 private readonly string file=Path.Combine(root,"storage-settings.json");
 private static readonly object Gate=new();
 public static string DefaultRecordDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".TyporaASR","RecordData");
 private sealed record Stored(string RecordDirectory);
 public string RecordDirectory{get{lock(Gate){
  try{if(File.Exists(file)&&JsonSerializer.Deserialize<Stored>(File.ReadAllText(file),PolishFormat.Json) is {RecordDirectory.Length:>0} stored)return stored.RecordDirectory;}
  catch(JsonException){}
  return DefaultRecordDirectory;
 }}}
 public object Public(){var current=RecordDirectory;return new {recordDirectory=current,defaultDirectory=DefaultRecordDirectory,isDefault=Same(current,DefaultRecordDirectory)};}
 /// <summary>Validates, creates and saves the directory; blank restores the default. Applies to sessions started afterwards.</summary>
 public string Save(string? directory){
  var value=Normalize(directory);
  try{
   Directory.CreateDirectory(value);
   var probe=Path.Combine(value,".write-test-"+Guid.NewGuid().ToString("N")+".tmp");
   File.WriteAllText(probe,"");File.Delete(probe);
  }catch(Exception e) when(e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException){
   throw new ArgumentException("无法在该目录创建或写入文件："+e.Message);
  }
  lock(Gate){
   if(Same(value,DefaultRecordDirectory)){if(File.Exists(file))File.Delete(file);}
   else{var temp=file+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(new Stored(value),PolishFormat.Json));File.Move(temp,file,true);}
  }
  return value;
 }
 public static string Normalize(string? directory){
  var value=(directory??"").Trim().Trim('"');
  if(value.Length==0)return DefaultRecordDirectory;
  if(value=="~"||value.StartsWith("~/",StringComparison.Ordinal)||value.StartsWith("~\\",StringComparison.Ordinal))
   value=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)+value[1..];
  value=Environment.ExpandEnvironmentVariables(value);
  if(value.Length>1024||!Path.IsPathFullyQualified(value))throw new ArgumentException("资料记录目录必须是完整的绝对路径，例如 D:\\TyporaASR\\RecordData");
  return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
 }
 private static bool Same(string a,string b)=>string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
}
