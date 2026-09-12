namespace TyporaAsr;
public static class SessionFiles {
 public static (string Audio,string Transcript) Paths(string root,string session,string document){
  // Non-GUID identifiers are reserved for internal fixtures, never accepted by the public API.
  if(!Guid.TryParse(session,out _))return(Path.Combine(root,"audio",session+".pcm"),Path.Combine(root,"transcripts",session+".md"));
  var full=Path.GetFullPath(document);var directory=Path.GetDirectoryName(full)!;var name=Path.GetFileNameWithoutExtension(full);
  if(name.Length>80)name=name[..80];
  return(Path.Combine(directory,$"{name}.录音-{session}.wav"),Path.Combine(directory,$"{name}.逐字稿-{session}.md"));
 }
 public static string PrepareAudio(string root,string session,string document){
  var target=Paths(root,session,document).Audio;
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
