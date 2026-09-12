using Microsoft.Data.Sqlite;

namespace TyporaAsr;

public sealed record TranscriptEvent(long Seq,string EventId,string SessionId,long Start,long End,string Text,bool NeedsReview,string State);
public sealed class Ledger : IDisposable
{
    private readonly SqliteConnection db;
    private readonly object gate = new();
    public Ledger(string root) {
        Directory.CreateDirectory(root);
        db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(root,"events.sqlite3") }.ToString()); db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,document TEXT,path TEXT); CREATE TABLE IF NOT EXISTS events(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE,session TEXT,start INTEGER,end INTEGER,text TEXT,review INTEGER,state TEXT); CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,session TEXT,start INTEGER,end INTEGER,review INTEGER,state TEXT,error TEXT);");
        Execute("CREATE TABLE IF NOT EXISTS progress(session TEXT PRIMARY KEY,sample INTEGER)");
    }
    private int Execute(string sql, params object[] values) {
        lock(gate) { using var c=db.CreateCommand(); c.CommandText=sql; for(var i=0;i<values.Length;i++) c.Parameters.AddWithValue("$"+i,values[i]); return c.ExecuteNonQuery(); }
    }
    public void CreateSession(string id,string document,string path) => Execute("INSERT OR IGNORE INTO sessions VALUES($0,$1,$2)",id,document,path);
    public void AddJob(string session,string id,long start,long end,bool review) => Execute("INSERT OR IGNORE INTO jobs VALUES($0,$1,$2,$3,$4,'pending','')",id,session,start,end,review?1:0);
    public void FailJob(string id,string error) => Execute("UPDATE jobs SET error=$1 WHERE id=$0",id,error);
    public void AddFinal(string session,string id,long start,long end,string text,bool review) {
        lock(gate) {
            using var transaction=db.BeginTransaction();
            using var c=db.CreateCommand(); c.Transaction=transaction;
            c.CommandText="INSERT OR IGNORE INTO events(id,session,start,end,text,review,state) VALUES($0,$1,$2,$3,$4,$5,'recognized'); UPDATE jobs SET state='done',error='' WHERE id=$0;";
            object[] values=[id,session,start,end,text,review?1:0]; for(var i=0;i<values.Length;i++) c.Parameters.AddWithValue("$"+i,values[i]);
            c.ExecuteNonQuery(); transaction.Commit();
        }
    }
    public void Acknowledge(string session,string id,string state) {
        if(state is not ("applying" or "applied" or "saved" or "deleted" or "reviewed")) throw new ArgumentException("Invalid acknowledgement");
        Execute("UPDATE events SET state=$2 WHERE session=$0 AND id=$1 AND state!='deleted' AND (state!='saved' OR $2='deleted')",session,id,state);
    }
    public List<TranscriptEvent> Events(string session,long after) {
        lock(gate) {
            using var c=db.CreateCommand(); c.CommandText="SELECT seq,id,session,start,end,text,review,state FROM events WHERE session=$0 AND seq>$1 ORDER BY seq LIMIT 200";
            c.Parameters.AddWithValue("$0",session);c.Parameters.AddWithValue("$1",after);
            using var r=c.ExecuteReader();var result=new List<TranscriptEvent>();
            while(r.Read()) result.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetInt64(4),r.GetString(5),r.GetInt64(6)!=0,r.GetString(7)));return result;
        }
    }
    public List<(string Id,long Start,long End,bool Review)> Pending(string session) {
        lock(gate) { using var c=db.CreateCommand();c.CommandText="SELECT id,start,end,review FROM jobs WHERE session=$0 AND state='pending' ORDER BY start LIMIT 1";c.Parameters.AddWithValue("$0",session);using var r=c.ExecuteReader();var list=new List<(string,long,long,bool)>();while(r.Read()) list.Add((r.GetString(0),r.GetInt64(1),r.GetInt64(2),r.GetInt64(3)!=0));return list; }
    }
    public List<object> Sessions() {
        lock(gate) { using var c=db.CreateCommand();c.CommandText="SELECT id,document,path FROM sessions ORDER BY rowid DESC LIMIT 100";using var r=c.ExecuteReader();var list=new List<object>();while(r.Read())list.Add(new {sessionId=r.GetString(0),documentId=r.GetString(1),path=r.GetString(2)});return list; }
    }
    public long ScheduledEnd(string session) => Scalar("SELECT COALESCE(MAX(end),0) FROM jobs WHERE session=$0",session);
    public void SetProgress(string session,long sample)=>Execute("INSERT INTO progress VALUES($0,$1) ON CONFLICT(session) DO UPDATE SET sample=$1",session,sample);
    public long Progress(string session)=>Scalar("SELECT COALESCE(MAX(sample),0) FROM progress WHERE session=$0",session);
    public long PendingCount(string session) => Scalar("SELECT COUNT(*) FROM jobs WHERE session=$0 AND state='pending'",session);
    private long Scalar(string sql,string session) {lock(gate){using var c=db.CreateCommand();c.CommandText=sql;c.Parameters.AddWithValue("$0",session);return Convert.ToInt64(c.ExecuteScalar());}}
    public (string Document,string Path)? Session(string id) {lock(gate){using var c=db.CreateCommand();c.CommandText="SELECT document,path FROM sessions WHERE id=$0";c.Parameters.AddWithValue("$0",id);using var r=c.ExecuteReader();return r.Read()?(r.GetString(0),r.GetString(1)):null;}}
    public void Dispose() { lock(gate) db.Dispose(); }
}
