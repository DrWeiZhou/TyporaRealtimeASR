using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;

// TyporaRealtimeASR installer: extracts the embedded release package, keeps local configuration and data,
// and installs the Typora plugin (one administrator prompt).
// Options: --dir <path>  --typora <Typora folder>  --quiet (no prompts)  --skip-plugin
Console.OutputEncoding=Encoding.UTF8;
var version=Assembly.GetExecutingAssembly().GetName().Version is {} v?$"{v.Major}.{v.Minor}.{v.Build}":"?";
string? Option(string name){var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
var quiet=args.Contains("--quiet");
var defaultDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","TyporaRealtimeASR");
var defaultTypora=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Typora");
string Ask(string question,string fallback){
 if(quiet)return fallback;
 Console.Write($"{question}\n  [回车使用 {fallback}]: ");
 var answer=Console.ReadLine()?.Trim().Trim('"');
 return string.IsNullOrEmpty(answer)?fallback:answer;
}
void Pause(){if(!quiet){Console.WriteLine("\n按回车键退出…");Console.ReadLine();}}
static void Step(string text){Console.ForegroundColor=ConsoleColor.Cyan;Console.WriteLine("\n== "+text+" ==");Console.ResetColor();}

try{
 Console.WriteLine($"TyporaRealtimeASR v{version} 安装程序");
 Console.WriteLine("将安装本地转写服务和 Typora“语音记录”插件。已有的 config.local.json、.asr 数据和录音不会被覆盖或删除。\n");
 using var payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")??throw new InvalidOperationException("安装包不完整：缺少内置发布文件");

 var target=Path.GetFullPath(Option("--dir")??Ask("安装目录（服务数据也保存在这里，请选择长期保留、当前用户可写的位置）：",defaultDir));
 var existing=File.Exists(Path.Combine(target,"VERSION.txt"))?File.ReadAllText(Path.Combine(target,"VERSION.txt")).Trim():null;
 if(existing!=null)Console.WriteLine($"检测到已安装 v{existing}，将升级到 v{version}。");

 // 1. Stop a running service from this installation so its files can be replaced.
 var running=Process.GetProcessesByName("TyporaAsr.Service").Where(p=>{try{return p.MainModule?.FileName?.StartsWith(target,StringComparison.OrdinalIgnoreCase)==true;}catch{return false;}}).ToList();
 if(running.Count>0){
  Step("停止正在运行的转写服务");
  Console.WriteLine("请确认已结束录音，且待识别、待润色内容已处理完毕。");
  if(!quiet){Console.Write("继续停止服务？[Y/n] ");if(Console.ReadLine()?.Trim().ToLowerInvariant() is "n" or "no")throw new OperationCanceledException("已取消安装");}
  var stop=Path.Combine(target,"tools","stop.ps1");
  if(File.Exists(stop))RunPowerShell(stop,"",elevated:false);
  foreach(var p in running){
   bool exited;try{exited=p.WaitForExit(10000);}catch{exited=true;}
   if(!exited)throw new InvalidOperationException("转写服务未能停止，请在面板点击“终止服务”后重试");
  }
 }

 // 2. Extract. The archive has a single top-level folder; local configuration and data are never overwritten.
 Step("复制程序文件");
 Directory.CreateDirectory(target);
 using(var zip=new ZipArchive(payload,ZipArchiveMode.Read)){
  var files=0;
  foreach(var entry in zip.Entries){
   var slash=entry.FullName.IndexOfAny(['/','\\']);
   var relative=slash<0?"":entry.FullName[(slash+1)..];
   if(relative.Length==0)continue;
   var destination=Path.GetFullPath(Path.Combine(target,relative));
   if(!destination.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装包包含非法路径");
   var top=relative.Split('/','\\')[0];
   if(top.Equals(".asr",StringComparison.OrdinalIgnoreCase)||relative.Equals("config.local.json",StringComparison.OrdinalIgnoreCase))continue;
   if(entry.FullName.EndsWith('/')||entry.FullName.EndsWith('\\')){Directory.CreateDirectory(destination);continue;}
   Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
   var temp=destination+".setup-tmp";
   entry.ExtractToFile(temp,true);File.Move(temp,destination,true);files++;
  }
  Console.WriteLine($"已写入 {files} 个文件到 {target}");
 }
 var localConfig=Path.Combine(target,"config.local.json");
 var needsConfig=!File.Exists(localConfig);
 if(needsConfig){File.Copy(Path.Combine(target,"config.example.json"),localConfig);Console.WriteLine("已根据示例创建 config.local.json（使用本地模型时需填写模型与 llama-server 路径）。");}

 // 3. Plugin: writes into the Typora installation folder, which needs administrator rights.
 if(!args.Contains("--skip-plugin")){
  Step("安装 Typora 插件");
  var typora=Option("--typora")??defaultTypora;
  while(!File.Exists(Path.Combine(typora,"resources","window.html"))){
   if(quiet)throw new DirectoryNotFoundException($"找不到 Typora：{typora}（可用 --typora 指定）");
   typora=Ask($"未在 {typora} 找到 Typora，请输入 Typora 安装目录：",typora);
  }
  Console.WriteLine("请在弹出的 Windows 管理员授权窗口中点击“是”。");
  RunPowerShell(Path.Combine(target,"tools","install-plugin.ps1"),$"-TyporaPath {Quote(typora)}",elevated:true);
 }

 Step("安装完成");
 Console.WriteLine($"安装目录：{target}");
 Console.WriteLine("1. 完全退出并重新打开 Typora，右下角会出现“语音记录”。");
 Console.WriteLine("2. 点击“启动服务”。使用在线 ASR 时可在面板“在线 ASR 模型设置”中配置，不需要本地模型；");
 Console.WriteLine($"   使用本地模型时，请先编辑 {localConfig} 填写模型路径。");
 Console.WriteLine("3. 在“在线润色设置”中填写 API；已有配置可在“配置管理”中导入。");
 if(needsConfig&&!quiet){
  Console.Write("\n现在用记事本打开 config.local.json？[y/N] ");
  if(Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes")Process.Start(new ProcessStartInfo("notepad.exe",$"\"{localConfig}\""){UseShellExecute=true});
 }
 Pause();
 return 0;
}
catch(OperationCanceledException e){Console.WriteLine(e.Message);Pause();return 2;}
catch(Exception e){
 Console.ForegroundColor=ConsoleColor.Red;Console.WriteLine("\n安装失败："+e.Message);Console.ResetColor();
 Pause();return 1;
}

static string Quote(string value)=>"'"+value.Replace("'","''")+"'";
// Runs a PowerShell script. The elevated window stays open on failure so the error can be read.
static void RunPowerShell(string script,string arguments,bool elevated){
 var command=$"& {{ try {{ & {Quote(script)} {arguments}; exit 0 }} catch {{ Write-Host $_ -ForegroundColor Red; if({(elevated?"$true":"$false")}){{ Read-Host '安装失败，按回车关闭' | Out-Null }}; exit 1 }} }}";
 // The command uses only single-quoted PowerShell strings (Windows paths cannot contain '"'), so double-quoting it is safe.
 var info=new ProcessStartInfo("powershell.exe",$"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\""){UseShellExecute=elevated};
 if(elevated)info.Verb="runas";
 Process process;
 try{process=Process.Start(info)??throw new InvalidOperationException("无法启动 PowerShell");}
 catch(System.ComponentModel.Win32Exception e) when(e.NativeErrorCode==1223){throw new OperationCanceledException("已取消管理员授权，插件未安装。可稍后以管理员身份运行安装目录中的 install.cmd。");}
 process.WaitForExit();
 if(process.ExitCode!=0)throw new InvalidOperationException($"{Path.GetFileName(script)} 执行失败（退出码 {process.ExitCode}）");
}
