using System.Text;
using System.Text.Json;

namespace TyporaAsr;
/// <summary>A polish request for one window of consecutive recognized utterances.</summary>
public sealed record PolishJob(string Id,string Session,string Text,string? Config,int Attempts,string Context="");
public sealed partial class Ledger {
 // Window cut policy (target: a new write at most ~15 s after the previous one): each recognized utterance is sent almost
 // immediately. Cut when 60 characters are ready, the oldest utterance has waited 3 s, or input is paused/stopped.
 // Up to MaxOpenWindows windows per session are polished concurrently; only when that many are still open do new
 // utterances accumulate (up to WindowMaxChars) so a slow model gets larger windows instead of a growing queue.
 public const int WindowMinChars=60,WindowMaxChars=400,WindowMaxWaitSeconds=3,MaxOpenWindows=3,PolishMaxAttempts=3;
 private const string Eligible="e.win IS NULL AND e.state IN ('recognized','reviewed') AND NOT EXISTS(SELECT 1 FROM polish lp WHERE lp.id=e.id AND lp.result IS NOT NULL)";
 private const string OpenWindow="w.result IS NULL AND w.state IN ('recognized','reviewed')";
 private string storageRoot="";
 private readonly Dictionary<string,DateTime> recentActivity=new();
 public void TouchSession(string session){lock(gate){recentActivity[session]=DateTime.UtcNow;}}
 private void InitializePipeline(string root){
  storageRoot=root;
  Execute("CREATE TABLE IF NOT EXISTS polish_sessions(session TEXT PRIMARY KEY); CREATE TABLE IF NOT EXISTS polish(id TEXT PRIMARY KEY,config TEXT,result TEXT,attempts INTEGER NOT NULL DEFAULT 0,next INTEGER NOT NULL DEFAULT 0,error TEXT NOT NULL DEFAULT ''); CREATE TABLE IF NOT EXISTS spans(session TEXT,start INTEGER,end INTEGER,wall TEXT,PRIMARY KEY(session,start));");
  Execute("CREATE TABLE IF NOT EXISTS polish_windows(id TEXT PRIMARY KEY,session TEXT NOT NULL,first_seq INTEGER NOT NULL,last_seq INTEGER NOT NULL,review INTEGER NOT NULL DEFAULT 0,config TEXT,result TEXT,attempts INTEGER NOT NULL DEFAULT 0,next INTEGER NOT NULL DEFAULT 0,error TEXT NOT NULL DEFAULT '',state TEXT NOT NULL DEFAULT 'recognized'); CREATE INDEX IF NOT EXISTS polish_windows_session ON polish_windows(session,first_seq); DROP TABLE IF EXISTS polish_topics;");
  // events.win: owning window (non-null = consumed by window polishing); events.at: unix seconds when recognized.
  var columns=Columns("events");
  if(!columns.Contains("win"))Execute("ALTER TABLE events ADD COLUMN win TEXT");
  if(!columns.Contains("at"))Execute("ALTER TABLE events ADD COLUMN at INTEGER");
  Execute("CREATE INDEX IF NOT EXISTS events_win ON events(win)");
  // Timing (unix ms) for latency diagnostics.
  var windowColumns=Columns("polish_windows");
  foreach(var column in new[]{"created","started","finished"})if(!windowColumns.Contains(column))Execute($"ALTER TABLE polish_windows ADD COLUMN {column} INTEGER");
 }
 private HashSet<string> Columns(string table){lock(gate){using var c=db.CreateCommand();c.CommandText=$"PRAGMA table_info({table})";using var r=c.ExecuteReader();var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);while(r.Read())set.Add(r.GetString(1));return set;}}
 public void EnablePolish(string session)=>Execute("INSERT OR IGNORE INTO polish_sessions VALUES($0)",session);
 public void BeginSpan(string session,long sample,DateTimeOffset wall)=>Execute("INSERT OR REPLACE INTO spans VALUES($0,$1,$1,$2)",session,sample,wall.ToString("O"));
 public void EndSpan(string session,long sample)=>Execute("UPDATE spans SET end=$1 WHERE session=$0 AND start=(SELECT MAX(start) FROM spans WHERE session=$0)",session,sample);

 private sealed record Utterance(long Seq,string Id,long Start,long End,string Text,bool Review,long? At);
 /// <summary>Groups recognized utterances into polish windows. flush(session) is true when no more audio is expected soon.</summary>
 public int CutWindows(Func<string,bool> flush,DateTimeOffset now){lock(gate){
  var sessions=new List<(string Session,int Open)>();
  using(var c=db.CreateCommand()){c.CommandText=$"SELECT DISTINCT e.session,(SELECT COUNT(*) FROM polish_windows w WHERE w.session=e.session AND {OpenWindow} AND w.attempts<{PolishMaxAttempts}) FROM events e JOIN polish_sessions s ON s.session=e.session WHERE {Eligible}";using var r=c.ExecuteReader();while(r.Read())sessions.Add((r.GetString(0),r.GetInt32(1)));}
  var created=0;
  foreach(var (session,open) in sessions){
   if(open>=MaxOpenWindows)continue;
   var rows=new List<Utterance>();
   using(var c=db.CreateCommand()){c.CommandText=$"SELECT e.seq,e.id,e.start,e.end,e.text,e.review,e.at FROM events e WHERE e.session=$0 AND {Eligible} ORDER BY e.seq";c.Parameters.AddWithValue("$0",session);using var r=c.ExecuteReader();while(r.Read())rows.Add(new(r.GetInt64(0),r.GetString(1),r.GetInt64(2),r.GetInt64(3),r.GetString(4),r.GetInt64(5)!=0,r.IsDBNull(6)?(long?)null:r.GetInt64(6)));}
   var shouldFlush=flush(session);
   for(int from=0,count=open;from<rows.Count&&count<MaxOpenWindows;count++){
    var last=PickCut(rows,from,now,shouldFlush);if(last<0)break;
    CreateWindow(session,rows.GetRange(from,last-from+1));created++;from=last+1;
   }
  }
  return created;
 }}
 private static int PickCut(List<Utterance> rows,int from,DateTimeOffset now,bool flush){
  int total=0,best=-1,last=from;long bestGap=-1;var full=false;
  for(var i=from;i<rows.Count;i++){
   var length=rows[i].Text.Length;
   if(i>from&&total+length>WindowMaxChars){full=true;break;}
   total+=length;last=i;
   if(total>=WindowMinChars){
    // Prefer the longest pause between utterances inside the WindowMinChars–WindowMaxChars band.
    var gap=i+1<rows.Count?rows[i+1].Start-rows[i].End:0;
    if(best<0||gap>bestGap){best=i;bestGap=gap;}
   }
  }
  if(best>=0)return best;
  if(full||flush)return last;
  var oldest=rows[from].At;
  return oldest==null||now.ToUnixTimeSeconds()-oldest.Value>=WindowMaxWaitSeconds?last:-1;
 }
 private void CreateWindow(string session,List<Utterance> rows){
  var id=$"win:{session}:{rows[0].Seq}";
  using var transaction=db.BeginTransaction();
  using(var c=db.CreateCommand()){c.Transaction=transaction;c.CommandText="INSERT OR IGNORE INTO polish_windows(id,session,first_seq,last_seq,review,created) VALUES($0,$1,$2,$3,$4,$5)";c.Parameters.AddWithValue("$5",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());c.Parameters.AddWithValue("$0",id);c.Parameters.AddWithValue("$1",session);c.Parameters.AddWithValue("$2",rows[0].Seq);c.Parameters.AddWithValue("$3",rows[^1].Seq);c.Parameters.AddWithValue("$4",rows.Any(x=>x.Review)?1:0);c.ExecuteNonQuery();}
  foreach(var row in rows){using var c=db.CreateCommand();c.Transaction=transaction;c.CommandText="UPDATE events SET win=$0 WHERE id=$1 AND win IS NULL";c.Parameters.AddWithValue("$0",id);c.Parameters.AddWithValue("$1",row.Id);c.ExecuteNonQuery();}
  transaction.Commit();
 }

 private sealed record WindowRow(string Id,string Session,long FirstSeq,long LastSeq,bool Review,string? Config,string? Result,int Attempts,string State,string Text,long Start,long End);
 private WindowRow? Window(string id){
  string session,state;long first,last;bool review;string? config,result;int attempts;
  using(var c=db.CreateCommand()){c.CommandText="SELECT session,first_seq,last_seq,review,config,result,attempts,state FROM polish_windows WHERE id=$0";c.Parameters.AddWithValue("$0",id);using var r=c.ExecuteReader();if(!r.Read())return null;
   session=r.GetString(0);first=r.GetInt64(1);last=r.GetInt64(2);review=r.GetInt64(3)!=0;config=r.IsDBNull(4)?null:r.GetString(4);result=r.IsDBNull(5)?null:r.GetString(5);attempts=r.GetInt32(6);state=r.GetString(7);}
  var text=new StringBuilder();long start=long.MaxValue,end=0;
  using(var c=db.CreateCommand()){c.CommandText="SELECT text,start,end FROM events WHERE win=$0 ORDER BY seq";c.Parameters.AddWithValue("$0",id);using var r=c.ExecuteReader();while(r.Read()){text.Append(r.GetString(0).Trim());start=Math.Min(start,r.GetInt64(1));end=Math.Max(end,r.GetInt64(2));}}
  return new(id,session,first,last,review,config,result,attempts,state,text.ToString(),start==long.MaxValue?0:start,end);
 }

 /// <summary>Next window to request. busy lists windows already being requested; windows of one session may run concurrently,
 /// the editor still receives them in order.</summary>
 public PolishJob? NextPolish(IReadOnlyCollection<string>? busy=null){lock(gate){
  // Prioritize the session being used, then newer sessions; windows stay in order inside each session.
  var preferred=recentActivity.Where(x=>x.Value>DateTime.UtcNow.AddSeconds(-12)).OrderByDescending(x=>x.Value).Select(x=>x.Key).FirstOrDefault()??"";
  string? id;
  using(var c=db.CreateCommand()){
   var excluded=busy==null||busy.Count==0?"":" AND w.id NOT IN ("+string.Join(",",busy.Select((_,i)=>"$b"+i))+")";
   c.CommandText=$"SELECT w.id FROM polish_windows w JOIN polish_sessions s ON s.session=w.session JOIN sessions metadata ON metadata.id=w.session WHERE {OpenWindow} AND w.attempts<{PolishMaxAttempts} AND w.next<=$0{excluded} ORDER BY CASE WHEN w.session=$1 THEN 0 ELSE 1 END,metadata.rowid DESC,w.first_seq LIMIT 1";
   c.Parameters.AddWithValue("$1",preferred);c.Parameters.AddWithValue("$0",DateTimeOffset.UtcNow.ToUnixTimeSeconds());
   if(busy!=null)foreach(var (value,i) in busy.Select((x,i)=>(x,i)))c.Parameters.AddWithValue("$b"+i,value);
   id=c.ExecuteScalar() as string;
  }
  var w=id==null?null:Window(id);if(w==null)return null;
  return new(w.Id,w.Session,w.Text,w.Config,w.Attempts,Context(w.Session,w.FirstSeq));
 }}
 /// <summary>The paragraph the document ends with before this window: the last paragraph of the previous window, extended
 /// backwards through windows that continued it. A previous window still being polished contributes its raw text.</summary>
 private string Context(string session,long before){
  var parts=new List<string>();
  using(var c=db.CreateCommand()){c.CommandText="SELECT id,result FROM polish_windows WHERE session=$0 AND last_seq<$1 AND state<>'deleted' ORDER BY last_seq DESC LIMIT 20";c.Parameters.AddWithValue("$0",session);c.Parameters.AddWithValue("$1",before);
   var rows=new List<(string Id,string? Result)>();
   using(var r=c.ExecuteReader())while(r.Read())rows.Add((r.GetString(0),r.IsDBNull(1)?null:r.GetString(1)));
   foreach(var (id,json) in rows){
    if(json==null){if(Window(id) is {Text.Length:>0} raw)parts.Insert(0,raw.Text);break;}
    var result=PolishFormat.Deserialize(json);if(result.Paragraphs.Length==0)continue;
    parts.Insert(0,result.Paragraphs[^1]);
    if(!(result.Continues&&result.Paragraphs.Length==1))break;
    if(parts.Sum(x=>x.Length)>PolishFormat.MaxContextChars*4)break;
   }
  }
  if(parts.Count>0)return PolishFormat.Excerpt(string.Concat(parts));
  // Sessions polished before windowing existed: use their last per-utterance result.
  using(var c=db.CreateCommand()){c.CommandText="SELECT p.result FROM events e JOIN polish p ON p.id=e.id WHERE e.session=$0 AND e.seq<$1 AND p.result IS NOT NULL ORDER BY e.seq DESC LIMIT 1";c.Parameters.AddWithValue("$0",session);c.Parameters.AddWithValue("$1",before);
   return c.ExecuteScalar() is string legacy?PolishFormat.Excerpt(legacy.Trim()):"";}
 }
 public void SnapshotPolish(string id,string config)=>Execute("UPDATE polish_windows SET config=COALESCE(config,$1),started=$2 WHERE id=$0",id,config,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
 public void CompletePolish(string id,IReadOnlyList<string> paragraphs,bool continues=false)=>CompletePolish(id,new PolishResult(paragraphs.ToArray(),continues));
 public void CompletePolish(string id,PolishResult result){lock(gate){
  using var transaction=db.BeginTransaction();
  int updated;
  using(var c=db.CreateCommand()){c.Transaction=transaction;c.CommandText="UPDATE polish_windows SET result=$1,error='',finished=$2 WHERE id=$0 AND result IS NULL";c.Parameters.AddWithValue("$0",id);c.Parameters.AddWithValue("$2",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());c.Parameters.AddWithValue("$1",PolishFormat.Serialize(result));updated=c.ExecuteNonQuery();}
  // Nothing worth recording: the window is consumed silently.
  if(updated>0&&result.Paragraphs.Length==0)using(var c=db.CreateCommand()){c.Transaction=transaction;c.CommandText="UPDATE polish_windows SET state='deleted' WHERE id=$0 AND state IN ('recognized','reviewed')";c.Parameters.AddWithValue("$0",id);c.ExecuteNonQuery();}
  transaction.Commit();
 }}
 public void FailPolish(string id,int attempts,string error)=>Execute("UPDATE polish_windows SET attempts=$1,next=$2,error=$3 WHERE id=$0",id,attempts,DateTimeOffset.UtcNow.AddSeconds(Math.Min(30,attempts*3)).ToUnixTimeSeconds(),error);
 /// <summary>Manual retry. Windows listed in busy are being requested right now and keep their snapshot.</summary>
 public void RetryPolish(string session,IReadOnlyCollection<string>? busy=null){lock(gate){
  using var c=db.CreateCommand();
  var excluded=busy==null||busy.Count==0?"":" AND id NOT IN ("+string.Join(",",busy.Select((_,i)=>"$b"+i))+")";
  c.CommandText=$"UPDATE polish_windows SET attempts=0,next=0,config=NULL,error='' WHERE session=$0 AND result IS NULL AND state IN ('recognized','reviewed'){excluded}";
  c.Parameters.AddWithValue("$0",session);
  if(busy!=null)foreach(var (value,i) in busy.Select((x,i)=>(x,i)))c.Parameters.AddWithValue("$b"+i,value);
  c.ExecuteNonQuery();
 }}
 public object PolishStatus(string session){lock(gate){
  long Count(string sql){using var c=db.CreateCommand();c.CommandText=sql;c.Parameters.AddWithValue("$0",session);return Convert.ToInt64(c.ExecuteScalar());}
  var waiting=Count($"SELECT COUNT(*) FROM events e WHERE e.session=$0 AND {Eligible}");
  var open=Count($"SELECT COUNT(*) FROM polish_windows w WHERE w.session=$0 AND {OpenWindow}");
  var failed=Count($"SELECT COUNT(*) FROM polish_windows w WHERE w.session=$0 AND {OpenWindow} AND w.attempts>={PolishMaxAttempts}");
  var completed=Count("SELECT COUNT(*) FROM polish_windows WHERE session=$0 AND result IS NOT NULL")+Count("SELECT COUNT(*) FROM events e JOIN polish p ON p.id=e.id WHERE e.session=$0 AND p.result IS NOT NULL");
  string error;using(var c=db.CreateCommand()){c.CommandText=$"SELECT COALESCE(MAX(w.error),'') FROM polish_windows w WHERE w.session=$0 AND {OpenWindow}";c.Parameters.AddWithValue("$0",session);error=(string)c.ExecuteScalar()!;}
  // Latest finished window: model request time and speech-to-result delay.
  double? requestSeconds=null,delaySeconds=null;
  using(var c=db.CreateCommand()){c.CommandText="SELECT w.started,w.finished,(SELECT MIN(start) FROM events WHERE win=w.id) FROM polish_windows w WHERE w.session=$0 AND w.finished IS NOT NULL ORDER BY w.finished DESC LIMIT 1";c.Parameters.AddWithValue("$0",session);long? started=null,finished=null,firstSample=null;
   using(var r=c.ExecuteReader())if(r.Read()){started=r.IsDBNull(0)?null:r.GetInt64(0);finished=r.GetInt64(1);firstSample=r.IsDBNull(2)?null:r.GetInt64(2);}
   if(finished is long done){
    if(started is long begun)requestSeconds=Math.Round((done-begun)/1000.0,1);
    if(firstSample is long sample&&WallTime(session,sample) is DateTimeOffset spoken)delaySeconds=Math.Round((done-spoken.ToUnixTimeMilliseconds())/1000.0,1);
   }}
  return new {pending=waiting+open,failed,error,completed,requestSeconds,delaySeconds};
 }}
 public List<TranscriptEvent> ReadyEvents(string session,long after){lock(gate){
  var result=new List<TranscriptEvent>();var windows=new Dictionary<string,WindowRow?>();
  var rows=new List<(TranscriptEvent Event,string? Win)>();
  using(var c=db.CreateCommand()){c.CommandText="SELECT seq,id,session,start,end,text,review,state,win FROM events WHERE session=$0 AND seq>$1 ORDER BY seq LIMIT 400";c.Parameters.AddWithValue("$0",session);c.Parameters.AddWithValue("$1",after);using var r=c.ExecuteReader();
   while(r.Read())rows.Add((new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetInt64(4),r.GetString(5),r.GetInt64(6)!=0,r.GetString(7)),r.IsDBNull(8)?null:r.GetString(8)));}
  foreach(var (e,win) in rows){
   if(e.State=="no_text"){result.Add(e with{State="deleted"});continue;}
   if(win==null){
    // Legacy per-utterance results remain readable; anything else unwindowed is still waiting for its window.
    string? polished=null;
    using(var c=db.CreateCommand()){c.CommandText="SELECT result FROM polish WHERE id=$0";c.Parameters.AddWithValue("$0",e.EventId);polished=c.ExecuteScalar() as string;}
    if(polished==null&&e.State is "recognized" or "reviewed")break;
    result.Add(e with{Text=polished??e.Text});continue;
   }
   if(!windows.TryGetValue(win,out var w))windows[win]=w=Window(win);
   if(w==null)continue;
   var open=w.State is "recognized" or "reviewed";
   if(w.Result==null&&open&&w.Attempts<PolishMaxAttempts)break;
   if(e.Seq!=w.LastSeq)continue;
   var windowEvent=new TranscriptEvent(e.Seq,w.Id,session,w.Start,w.End,w.Text,w.Review,w.State);
   if(w.Result!=null){
    var polished=PolishFormat.Deserialize(w.Result);
    result.Add(windowEvent with{Text=PolishFormat.PlainText(polished.Paragraphs),Paragraphs=polished.Paragraphs,Continues=polished.Continues,State=polished.Paragraphs.Length==0&&open?"deleted":w.State});
   }
   else if(open)result.Add(windowEvent with{PolishState="failed"});
   else result.Add(windowEvent with{Paragraphs=[w.Text]});
  }
  return result;
 }}
 private DateTimeOffset? WallTime(string session,long sample){using var c=db.CreateCommand();c.CommandText="SELECT start,wall FROM spans WHERE session=$0 AND start<=$1 AND end>=$1 ORDER BY start DESC LIMIT 1";c.Parameters.AddWithValue("$0",session);c.Parameters.AddWithValue("$1",sample);using var r=c.ExecuteReader();return r.Read()?DateTimeOffset.Parse(r.GetString(1)).AddSeconds((sample-r.GetInt64(0))/16000.0):null;}
 private string? Wall(string session,long sample)=>WallTime(session,sample)?.ToString("yyyy-MM-dd HH:mm:ss zzz");
 public static string Offset(long samples){var seconds=samples/16000;return $"{seconds/3600:00}:{seconds/60%60:00}:{seconds%60:00}";}
 public string Transcript(string session){lock(gate){var b=new StringBuilder("# 原始逐字稿\n\n以下为本地 ASR 原始结果，未经在线润色。时间为有效录音偏移；实际时间包含时区，暂停间隔不计入录音偏移。\n\n");long after=0;while(true){var list=Events(session,after);if(list.Count==0)break;foreach(var e in list){b.AppendLine($"## [{Offset(e.Start)}–{Offset(e.End)}] {Wall(session,e.Start)??"历史实际时间未知"}");b.AppendLine();b.AppendLine((e.State=="no_text"?"[识别状态：模型未返回文字或输出持续截断；原始音频已保留，请按时间核对。]":e.Text).Replace("\r"," ").Replace("\n"," "));b.AppendLine();after=e.Seq;}}return b.ToString();}}
 public void ExportTranscript(string session){if(!Guid.TryParse(session,out _))return;var stored=Session(session);if(stored==null)return;var file=SessionFiles.Paths(storageRoot,session,stored.Value.Path).Transcript;Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file+".tmp",Transcript(session));File.Move(file+".tmp",file,true);}
}
