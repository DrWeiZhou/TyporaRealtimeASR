using System.Text;
using System.Text.Json;

namespace TyporaAsr;
public sealed record PolishJob(string Id,string Session,string Text,string? Config,int Attempts);
public sealed partial class Ledger {
 private string transcriptRoot="";
 private void InitializePipeline(string root){
  transcriptRoot=Path.Combine(root,"transcripts");Directory.CreateDirectory(transcriptRoot);
  Execute("CREATE TABLE IF NOT EXISTS polish_sessions(session TEXT PRIMARY KEY); CREATE TABLE IF NOT EXISTS polish(id TEXT PRIMARY KEY,config TEXT,result TEXT,attempts INTEGER NOT NULL DEFAULT 0,next INTEGER NOT NULL DEFAULT 0,error TEXT NOT NULL DEFAULT ''); CREATE TABLE IF NOT EXISTS spans(session TEXT,start INTEGER,end INTEGER,wall TEXT,PRIMARY KEY(session,start));");
 }
 public void EnablePolish(string session)=>Execute("INSERT OR IGNORE INTO polish_sessions VALUES($0)",session);
 public void BeginSpan(string session,long sample,DateTimeOffset wall)=>Execute("INSERT OR REPLACE INTO spans VALUES($0,$1,$1,$2)",session,sample,wall.ToString("O"));
 public void EndSpan(string session,long sample)=>Execute("UPDATE spans SET end=$1 WHERE session=$0 AND start=(SELECT MAX(start) FROM spans WHERE session=$0)",session,sample);
 public PolishJob? NextPolish(){lock(gate){
  // Only the first unfinished event in each session may be dispatched. Failed sessions do not block other sessions.
  using var c=db.CreateCommand();c.CommandText="SELECT e.id,e.session,e.text,p.config,COALESCE(p.attempts,0) FROM events e JOIN polish_sessions s ON s.session=e.session LEFT JOIN polish p ON p.id=e.id WHERE e.state IN ('recognized','reviewed') AND p.result IS NULL AND COALESCE(p.attempts,0)<3 AND COALESCE(p.next,0)<=$0 AND NOT EXISTS(SELECT 1 FROM events before LEFT JOIN polish bp ON bp.id=before.id WHERE before.session=e.session AND before.seq<e.seq AND before.state IN ('recognized','reviewed') AND bp.result IS NULL) ORDER BY e.seq LIMIT 1";c.Parameters.AddWithValue("$0",DateTimeOffset.UtcNow.ToUnixTimeSeconds());using var r=c.ExecuteReader();return r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.GetInt32(4)):null;
 }}
 public void SnapshotPolish(string id,string config)=>Execute("INSERT INTO polish(id,config) VALUES($0,$1) ON CONFLICT(id) DO UPDATE SET config=COALESCE(config,$1)",id,config);
 public void CompletePolish(string id,string result)=>Execute("UPDATE polish SET result=$1,error='' WHERE id=$0 AND result IS NULL",id,result);
 public void FailPolish(string id,int attempts,string error)=>Execute("UPDATE polish SET attempts=$1,next=$2,error=$3 WHERE id=$0",id,attempts,DateTimeOffset.UtcNow.AddSeconds(Math.Min(60,attempts*10)).ToUnixTimeSeconds(),error);
 public void RetryPolish(string session)=>Execute("UPDATE polish SET attempts=0,next=0,config=NULL,error='' WHERE result IS NULL AND id IN (SELECT id FROM events WHERE session=$0)",session);
 public object PolishStatus(string session){lock(gate){using var c=db.CreateCommand();c.CommandText="SELECT COUNT(*),COALESCE(SUM(CASE WHEN COALESCE(p.attempts,0)>=3 THEN 1 ELSE 0 END),0),COALESCE(MAX(p.error),'') FROM events e LEFT JOIN polish p ON p.id=e.id WHERE e.session=$0 AND e.state IN ('recognized','reviewed') AND p.result IS NULL";c.Parameters.AddWithValue("$0",session);using var r=c.ExecuteReader();r.Read();return new {pending=r.GetInt64(0),failed=r.GetInt64(1),error=r.GetString(2)};}}
 public List<TranscriptEvent> ReadyEvents(string session,long after){lock(gate){var result=new List<TranscriptEvent>();foreach(var e in Events(session,after)){using var c=db.CreateCommand();c.CommandText="SELECT result FROM polish WHERE id=$0";c.Parameters.AddWithValue("$0",e.EventId);var polished=c.ExecuteScalar() as string;if(polished==null && e.State is "recognized" or "reviewed")break;result.Add(e with{Text=polished??e.Text});}return result;}}
 private string? Wall(string session,long sample){using var c=db.CreateCommand();c.CommandText="SELECT start,wall FROM spans WHERE session=$0 AND start<=$1 AND end>=$1 ORDER BY start DESC LIMIT 1";c.Parameters.AddWithValue("$0",session);c.Parameters.AddWithValue("$1",sample);using var r=c.ExecuteReader();return r.Read()?DateTimeOffset.Parse(r.GetString(1)).AddSeconds((sample-r.GetInt64(0))/16000.0).ToString("yyyy-MM-dd HH:mm:ss zzz"):null;}
 public static string Offset(long samples){var seconds=samples/16000;return $"{seconds/3600:00}:{seconds/60%60:00}:{seconds%60:00}";}
 public string Transcript(string session){lock(gate){var b=new StringBuilder("# 原始逐字稿\n\n以下为本地 ASR 原始结果，未经在线润色。时间为有效录音偏移；实际时间包含时区，暂停间隔不计入录音偏移。\n\n");long after=0;while(true){var list=Events(session,after);if(list.Count==0)break;foreach(var e in list){b.AppendLine($"## [{Offset(e.Start)}–{Offset(e.End)}] {Wall(session,e.Start)??"历史实际时间未知"}");b.AppendLine();b.AppendLine(e.Text.Replace("\r"," ").Replace("\n"," "));b.AppendLine();after=e.Seq;}}return b.ToString();}}
 private void ExportTranscript(string session){if(!Guid.TryParse(session,out _))return;var file=Path.Combine(transcriptRoot,session+".md");File.WriteAllText(file+".tmp",Transcript(session));File.Move(file+".tmp",file,true);}
}
