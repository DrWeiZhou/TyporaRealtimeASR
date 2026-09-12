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
   var id=Guid.NewGuid().ToString();var dir=Path.Combine(root,"notes");Directory.CreateDirectory(dir);var doc=Path.Combine(dir,"会议.md");File.WriteAllText(doc,"# 会议");
   Directory.CreateDirectory(Path.Combine(root,"audio"));var legacy=Path.Combine(root,"audio",id+".pcm");File.WriteAllBytes(legacy,[123,0,200,0]);
   using(var db=new Ledger(root)){using var http=new HttpClient();await using(var session=new RecordingSession(id,"document",doc,root,db,new AsrClient(http,"http://localhost:1","test"),true)){
    var target=Path.Combine(dir,$"会议.录音-{id}.wav");if(!File.Exists(target))throw new Exception("Recording not beside Markdown");
    using var readStream=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var reader=new WaveFileReader(readStream);if(reader.Length!=4)throw new Exception("Legacy PCM migration changed samples");
    if(!File.Exists(legacy))throw new Exception("Legacy backup deleted");
    db.AddFinal(id,id+":0",0,2,"原始文字",false);
    var transcript=Path.Combine(dir,$"会议.逐字稿-{id}.md");if(!File.Exists(transcript)||!File.ReadAllText(transcript).Contains("原始文字"))throw new Exception("Transcript not beside Markdown");
    if(File.ReadAllText(doc)!="# 会议")throw new Exception("Target Markdown overwritten");
   }}
   Console.WriteLine("PASS WAV format, append, header recovery, legacy conversion and same-directory transcript");
  }finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
 }
}
