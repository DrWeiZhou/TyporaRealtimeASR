using TyporaAsr;
using NAudio.Wave;
static class WavTests {
 public static async Task Run(){
  var root=Path.Combine(Path.GetTempPath(),"asr-wav-"+Guid.NewGuid());Directory.CreateDirectory(root);
  try{
   var wav=Path.Combine(root,"test.wav");
   using(var audio=new AudioStore(wav)){audio.Append([123,-456,789]);if(audio.Samples!=3||audio.Read(1,2)[0]!=-456)throw new Exception("WAV sample offsets incorrect");}
   using(var reader=new WaveFileReader(wav)){if(reader.WaveFormat.SampleRate!=16000||reader.WaveFormat.Channels!=1||reader.WaveFormat.BitsPerSample!=16||reader.Length!=6)throw new Exception("WAV format or data length incorrect");}
   // Emulate a crash after audio payload was persisted but before RIFF lengths were updated.
   using(var raw=new FileStream(wav,FileMode.Append)){raw.Write([1,0,99]);}
   using(var audio=new AudioStore(wav)){if(audio.Samples!=4||audio.Read(3,4)[0]!=1)throw new Exception("WAV crash tail not repaired");audio.Append([2]);}
   using(var reader=new WaveFileReader(wav)){if(reader.Length!=10)throw new Exception("Reopened WAV header not updated");}
   var storage=new StorageSettings(root);
   if(storage.RecordDirectory!=StorageSettings.DefaultRecordDirectory||!StorageSettings.DefaultRecordDirectory.EndsWith(Path.Combine(".TyporaASR","RecordData")))throw new Exception("Default record directory wrong");
   try{storage.Save("relative\\records");throw new Exception("Relative record directory accepted");}catch(ArgumentException){}
   var records=storage.Save(Path.Combine(root,"records")+Path.DirectorySeparatorChar);
   if(records!=Path.Combine(root,"records")||new StorageSettings(root).RecordDirectory!=records||!Directory.Exists(records))throw new Exception("Record directory not saved");
   var id=Guid.NewGuid().ToString();var dir=Path.Combine(root,"notes");Directory.CreateDirectory(dir);var doc=Path.Combine(dir,"会议.md");File.WriteAllText(doc,"# 会议");
   Directory.CreateDirectory(Path.Combine(root,"audio"));var legacy=Path.Combine(root,"audio",id+".pcm");File.WriteAllBytes(legacy,[123,0,200,0]);
   using(var db=new Ledger(root)){using var http=new HttpClient();await using(var session=new RecordingSession(id,"document",doc,root,db,new AsrClient(http,"http://localhost:1","test"),true)){
    var target=SessionFiles.Paths(root,id,doc).Audio;if(!File.Exists(target)||Path.GetDirectoryName(target)!=records)throw new Exception("Recording not in the record directory");
    if(Path.GetFileName(target).Contains(id)||!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(target),@"^会议\.录音-\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}-\d{3}\.wav$"))throw new Exception("WAV filename must use start timestamp, not session id");
    if(SessionFiles.PrepareAudio(root,id,doc)!=target)throw new Exception("Resume changed WAV filename");
    using var readStream=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var reader=new WaveFileReader(readStream);if(reader.Length!=4)throw new Exception("Legacy PCM migration changed samples");
    if(!File.Exists(legacy))throw new Exception("Legacy backup deleted");
    db.AddFinal(id,id+":0",0,2,"原始文字",false);
    var transcript=SessionFiles.Paths(root,id,doc).Transcript;
    if(Path.GetFileName(transcript)!=Path.GetFileName(target).Replace(".录音-",".逐字稿-").Replace(".wav",".md"))throw new Exception("Transcript timestamp must match WAV");if(!File.Exists(transcript)||!File.ReadAllText(transcript).Contains("原始文字"))throw new Exception("Transcript not in the record directory");
    if(File.ReadAllText(doc)!="# 会议")throw new Exception("Target Markdown overwritten");
   }}
   using(var db=new Ledger(root)){
    var oldId=Guid.NewGuid().ToString();db.CreateSession(oldId,"old",doc);db.BeginSpan(oldId,0,DateTimeOffset.Parse("2026-09-12T08:09:10.123+08:00"));
    var oldWav=Path.Combine(dir,$"会议.录音-{oldId}.wav");using(var audio=new AudioStore(oldWav))audio.Append([42,-17]);var before=File.ReadAllBytes(oldWav);
    var oldTranscript=Path.Combine(dir,$"会议.逐字稿-{oldId}.md");File.WriteAllText(oldTranscript,"保留旧逐字稿");
    var migrated=SessionFiles.PrepareAudio(root,oldId,doc);
    var migratedTranscript=SessionFiles.Paths(root,oldId,doc).Transcript;
    if(File.Exists(oldTranscript)||!File.Exists(migratedTranscript)||File.ReadAllText(migratedTranscript)!="保留旧逐字稿")throw new Exception("Legacy transcript migration lost content");
    if(Path.GetDirectoryName(migrated)!=records||Path.GetDirectoryName(migratedTranscript)!=records)throw new Exception("Legacy files beside Markdown not moved to the record directory");
    if(Path.GetFileName(migrated)!="会议.录音-2026-09-12_08-09-10-123.wav"||File.Exists(oldWav)||!File.ReadAllBytes(migrated).SequenceEqual(before))throw new Exception("Timestamp WAV migration lost data or original start time");
    if(SessionFiles.Paths(root,oldId,doc).Audio!=migrated)throw new Exception("Recovered filename changed");
    var other=Guid.NewGuid().ToString();db.CreateSession(other,"other",doc);db.BeginSpan(other,0,DateTimeOffset.Parse("2026-09-12T08:09:10.123+08:00"));
    if(SessionFiles.Paths(root,other,doc).Audio==migrated)throw new Exception("Same-millisecond sessions collide");
    var moved=storage.Save(Path.Combine(root,"records-2"));
    if(SessionFiles.Paths(root,oldId,doc).Audio!=migrated)throw new Exception("Changing the record directory moved an existing recording");
    var later=Guid.NewGuid().ToString();db.CreateSession(later,"later",doc);
    if(Path.GetDirectoryName(SessionFiles.Paths(root,later,doc).Audio)!=moved)throw new Exception("New session ignored the changed record directory");
    if(storage.Save(" ")!=StorageSettings.DefaultRecordDirectory||File.Exists(Path.Combine(root,"storage-settings.json")))throw new Exception("Blank directory did not restore the default");
   }
   Console.WriteLine("PASS WAV format, append, header recovery, legacy conversion, record directory setting, timestamp migration, stable names and collision handling");
  }finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
 }
}
