param([switch]$SkipModel)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$startMutex=[Threading.Mutex]::new($false,'Local\TyporaRealtimeASR-Startup')
$ownsMutex=$false
try {
try {$ownsMutex=$startMutex.WaitOne(0)} catch [Threading.AbandonedMutexException] {$ownsMutex=$true}
if(!$ownsMutex){throw '另一窗口正在启动服务，请稍后检查服务状态'}
$configPath=Join-Path $projectRoot 'config.local.json'
if(!(Test-Path $configPath)){throw '请将 config.example.json 复制为 config.local.json 并填写现有模型路径'}
$cfg=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$dataRoot=Join-Path $projectRoot '.asr'
$logs=Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $dataRoot,$logs | Out-Null
$ready=$false
try{$ready=(Invoke-RestMethod ($cfg.asrEndpoint+'/health') -TimeoutSec 2).status -eq 'ok'}catch{}
if(!$ready -and !$SkipModel){
    if($cfg.asrEndpoint -ne 'http://127.0.0.1:18081'){throw '自定义模型端点请先自行启动，然后使用 -SkipModel'}
    foreach($asset in @($cfg.model,$cfg.mmproj,(Join-Path $cfg.llamaDirectory 'llama-server.exe'))){if(!(Test-Path -LiteralPath $asset)){throw "找不到 $asset"}}
    $arguments=@('-m',('"'+$cfg.model+'"'),'--mmproj',('"'+$cfg.mmproj+'"'),'--alias','qwen3-asr','--host','127.0.0.1','--port','18081','-c','4096','-np','1','-ngl','99','-b','256','-ub','256','-t','8','--device',$cfg.device,'--mmproj-device',$cfg.device,'--no-webui')
    $modelProcess=Start-Process -FilePath (Join-Path $cfg.llamaDirectory 'llama-server.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'model.stdout.log') -RedirectStandardError (Join-Path $logs 'model.stderr.log')
    Set-Content (Join-Path $dataRoot 'model.pid') $modelProcess.Id
    @{pid=$modelProcess.Id;started=$modelProcess.StartTime.ToUniversalTime().Ticks.ToString();path=$modelProcess.Path} | ConvertTo-Json | Set-Content (Join-Path $dataRoot 'model-process.json') -Encoding UTF8
    for($attempt=0;$attempt -lt 90;$attempt++){
        try{if((Invoke-RestMethod ($cfg.asrEndpoint+'/health') -TimeoutSec 2).status -eq 'ok'){$ready=$true;break}}catch{}
        Start-Sleep -Seconds 1
    }
}
if(!$ready){throw '模型未就绪，请检查 artifacts 中的日志'}
$connectionPath=Join-Path $dataRoot 'connection.json'
if(Test-Path $connectionPath){
    $conn=Get-Content $connectionPath -Raw | ConvertFrom-Json
    try{if((Invoke-RestMethod ($conn.endpoint+'/health') -Headers @{Authorization=('Bearer '+$conn.token)} -TimeoutSec 2).status -eq 'ok'){Write-Host '服务已经运行。';exit}}catch{}
}
$published=Join-Path $projectRoot 'runtime\publish\TyporaAsr.Service.exe'
if(Test-Path $published){$file=$published;$arguments=@('--DataRoot',('"'+$dataRoot+'"'),'--AsrEndpoint',$cfg.asrEndpoint)}
else{
    $file=Join-Path $projectRoot 'runtime\dotnet\dotnet.exe'
    if(!(Test-Path $file)){$file=(Get-Command dotnet -ErrorAction Stop).Source}
    & $file build (Join-Path $projectRoot 'src\local-service') -v quiet
    if($LASTEXITCODE -ne 0){throw '服务构建失败'}
    $dll=Join-Path $projectRoot 'src\local-service\bin\Debug\net10.0-windows\TyporaAsr.Service.dll'
    $arguments=@(('"'+$dll+'"'),'--DataRoot',('"'+$dataRoot+'"'),'--AsrEndpoint',$cfg.asrEndpoint)
}
$service=Start-Process -FilePath $file -ArgumentList $arguments -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'service.stdout.log') -RedirectStandardError (Join-Path $logs 'service.stderr.log')
Set-Content (Join-Path $dataRoot 'service.pid') $service.Id
for($attempt=0;$attempt -lt 30;$attempt++){
    Start-Sleep -Milliseconds 500
    try{$conn=Get-Content $connectionPath -Raw | ConvertFrom-Json;if((Invoke-RestMethod ($conn.endpoint+'/health') -Headers @{Authorization=('Bearer '+$conn.token)} -TimeoutSec 1).status -eq 'ok'){Write-Host '本地模型与转写服务就绪。请在 Typora 面板点击开始录音。';exit}}catch{}
}
throw '服务启动失败，请查看 artifacts/service.stderr.log'

} finally {if($ownsMutex){$startMutex.ReleaseMutex()};$startMutex.Dispose()}
