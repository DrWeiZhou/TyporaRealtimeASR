namespace TyporaAsr;
public static class SessionFiles {
 private static readonly object Gate=new();
 private static string TranscriptPath(string audio){
  var stem=Path.GetFileNameWithoutExtension(audio);var marker=stem.LastIndexOf(".录音-",StringComparison.Ordinal);
  if(marker<0)throw new IOException("录音文件映射名称无效");
  return Path.Combine(Path.GetDirectoryName(audio)!,stem[..marker]+".逐字稿-"+stem[(marker+4)..]+".md");
 }
 public static (string Audio,string Transcript) Paths(string root,string session,string document){
  // Non-GUID identifiers are reserved for internal fixtures, never accepted by the public API.
  if(!Guid.TryParse(session,out _))return(Path.Combine(root,"audio",session+".pcm"),Path.Combine(root,"transcripts",session+".md"));
  var full=Path.GetFullPath(document);var directory=Path.GetDirectoryName(full)!;var name=Path.GetFileNameWithoutExtension(full);
  if(name.Length>80)name=name[..80];
  lock(Gate){
   var mappings=Path.Combine(root,"session-files");Directory.CreateDirectory(mappings);
   var mapping=Path.Combine(mappings,session+".json");string audio;
   if(File.Exists(mapping))audio=System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(mapping))??throw new IOException("录音文件映射无效");
   else{
    var oldWav=Path.Combine(directory,$"{name}.录音-{session}.wav");var pcm=Path.Combine(root,"audio",session+".pcm");
    var start=DateTimeOffset.Now;
    if(File.Exists(pcm))start=new DateTimeOffset(File.GetCreationTime(pcm));
    else if(File.Exists(oldWav))start=new DateTimeOffset(File.GetCreationTime(oldWav));
    var database=Path.Combine(root,"events.sqlite3");
    if(File.Exists(database)){
     using var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=database,Mode=Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly}.ToString());db.Open();
     using var command=db.CreateCommand();command.CommandText="SELECT wall FROM spans WHERE session=$id ORDER BY start LIMIT 1";command.Parameters.AddWithValue("$id",session);
     if(command.ExecuteScalar() is string wall)start=DateTimeOffset.Parse(wall);
    }
    var stem=Path.Combine(directory,$"{name}.录音-{start:yyyy-MM-dd_HH-mm-ss-fff}");audio=stem+".wav";
    var reserved=Directory.EnumerateFiles(mappings,"*.json").Select(f=>System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(f))).ToHashSet(StringComparer.OrdinalIgnoreCase);
    for(var suffix=2;File.Exists(audio)||File.Exists(TranscriptPath(audio))||reserved.Contains(audio);suffix++)audio=stem+$"-{suffix}.wav";
    var temp=mapping+".tmp";File.WriteAllText(temp,System.Text.Json.JsonSerializer.Serialize(audio));File.Move(temp,mapping,false);
   }
   var transcript=TranscriptPath(audio);var legacyTranscript=Path.Combine(directory,$"{name}.逐字稿-{session}.md");
   if(!File.Exists(transcript)&&File.Exists(legacyTranscript))File.Move(legacyTranscript,transcript,false);
   return(audio,transcript);
  }
 }
 public static string PrepareAudio(string root,string session,string document){
  var target=Paths(root,session,document).Audio;
  if(Guid.TryParse(session,out _)&&!File.Exists(target)){
   var name=Path.GetFileNameWithoutExtension(document);if(name.Length>80)name=name[..80];
   var oldWav=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(document))!,$"{name}.录音-{session}.wav");
   if(File.Exists(oldWav)){File.Move(oldWav,target,false);return target;}
  }
  var legacy=Path.Combine(root,"audio",session+".pcm");
  if(target==legacy||File.Exists(target)||!File.Exists(legacy))return target;
  Directory.CreateDirectory(Path.GetDirectoryName(target)!);
  var temp=target+"."+Guid.NewGuid()+".wav";
  try{
   using(var source=new FileStream(legacy,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=new AudioStore(temp)){
    var bytes=new byte[131072];long remaining=source.Length-source.Length%2;
    while(remaining>0){var count=(int)Math.Min(bytes.Length,remaining);source.ReadExactly(bytes.AsSpan(0,count));var samples=new short[count/2];Buffer.BlockCopy(bytes,0,samples,0,count);output.Append(samples);remaining-=count;}
    if(output.Samples!=source.Length/2)throw new IOException("旧录音转换校验失败");
   }
   File.Move(temp,target,false);return target;
  }finally{if(File.Exists(temp))File.Delete(temp);}
 }
}
