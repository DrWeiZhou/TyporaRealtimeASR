'use strict';
// Native dialogs for the Typora window. Each tries Typora's JSBridge dialog, then Electron remote dialogs, and finally
// the Windows dialogs through PowerShell. Resolves to the chosen absolute path, or null when cancelled.
const JSON_FILTERS=[{name:'JSON 配置文件',extensions:['json']}];
const WINDOWS_FILTER='JSON 配置文件 (*.json)|*.json|所有文件 (*.*)|*.*';
const PREAMBLE=['$ErrorActionPreference="Stop"','[Console]::OutputEncoding=[Text.Encoding]::UTF8','Add-Type -AssemblyName System.Windows.Forms','$owner=New-Object System.Windows.Forms.Form -Property @{TopMost=$true}'];
const SCRIPTS={
 folder:[...PREAMBLE,
  '$d=New-Object System.Windows.Forms.FolderBrowserDialog','$d.Description=$env:ASR_PICK_TITLE','$d.ShowNewFolderButton=$true',
  'if($env:ASR_PICK_START -and (Test-Path -LiteralPath $env:ASR_PICK_START)){$d.SelectedPath=$env:ASR_PICK_START}',
  'if($d.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK){[Console]::Out.Write($d.SelectedPath)}','$owner.Dispose()'].join('; '),
 open:[...PREAMBLE,
  '$d=New-Object System.Windows.Forms.OpenFileDialog','$d.Title=$env:ASR_PICK_TITLE','$d.Filter=$env:ASR_PICK_FILTER','$d.CheckFileExists=$true',
  'if($env:ASR_PICK_START){$dir=[IO.Path]::GetDirectoryName($env:ASR_PICK_START);if($dir -and (Test-Path -LiteralPath $dir)){$d.InitialDirectory=$dir}}',
  'if($d.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK){[Console]::Out.Write($d.FileName)}','$owner.Dispose()'].join('; '),
 save:[...PREAMBLE,
  '$d=New-Object System.Windows.Forms.SaveFileDialog','$d.Title=$env:ASR_PICK_TITLE','$d.Filter=$env:ASR_PICK_FILTER','$d.OverwritePrompt=$true','$d.AddExtension=$true','$d.DefaultExt="json"',
  'if($env:ASR_PICK_START){$dir=[IO.Path]::GetDirectoryName($env:ASR_PICK_START);if($dir -and (Test-Path -LiteralPath $dir)){$d.InitialDirectory=$dir};$d.FileName=[IO.Path]::GetFileName($env:ASR_PICK_START)}',
  'if($d.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK){[Console]::Out.Write($d.FileName)}','$owner.Dispose()'].join('; ')
};
const fromResult=result=>{
 if(!result)return null;
 if(typeof result==='string')return result||null;
 if(Array.isArray(result))return result[0]||null;
 if(result.canceled)return null;
 return result.filePath||(result.filePaths||[])[0]||null;
};
async function show(w,kind,{title,defaultPath='',filters}){
 const save=kind==='save',method=save?'showSaveDialog':'showOpenDialog';
 const options={title,defaultPath:defaultPath||undefined,filters,properties:kind==='folder'?['openDirectory','createDirectory']:save?['showOverwriteConfirmation']:['openFile']};
 if(w.JSBridge?.invoke){
  // An unsupported handler resolves to nothing; only a real dialog result (chosen or cancelled) is final.
  try{const result=await w.JSBridge.invoke('dialog.'+method,options);if(result!=null)return fromResult(result);}catch(_){/* try the next dialog */}
 }
 for(const load of [()=>w.reqnode('@electron/remote'),()=>w.reqnode('electron').remote]){
  let dialog=null,win=null;
  try{const remote=load();dialog=remote?.dialog;win=remote?.getCurrentWindow?.();}catch(_){continue;}
  if(!dialog?.[method])continue;
  return fromResult(await (win?dialog[method](win,options):dialog[method](options)));
 }
 const {execFile}=w.reqnode('child_process');
 return await new Promise((resolve,reject)=>{
  execFile('powershell.exe',['-NoProfile','-STA','-ExecutionPolicy','Bypass','-Command',SCRIPTS[kind]],
   {windowsHide:true,encoding:'utf8',env:{...process.env,ASR_PICK_TITLE:title,ASR_PICK_START:defaultPath||'',ASR_PICK_FILTER:WINDOWS_FILTER}},
   (error,stdout)=>{if(error)reject(new Error('无法打开选择窗口：'+error.message));else resolve(String(stdout).trim()||null);});
 });
}
const pickFolder=(w,{title='选择资料记录目录',defaultPath=''}={})=>show(w,'folder',{title,defaultPath});
const pickOpenJson=(w,{title='选择配置文件',defaultPath=''}={})=>show(w,'open',{title,defaultPath,filters:JSON_FILTERS});
const pickSaveJson=(w,{title='导出配置',defaultPath=''}={})=>show(w,'save',{title,defaultPath,filters:JSON_FILTERS});
module.exports={pickFolder,pickOpenJson,pickSaveJson,fromResult};
