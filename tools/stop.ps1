$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$dataRoot=Join-Path $projectRoot '.asr'
$logs=Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$mutex=[Threading.Mutex]::new($false,'Local\TyporaRealtimeASR-Startup')
$ownsMutex=$false
try {
    try {$ownsMutex=$mutex.WaitOne(0)} catch [Threading.AbandonedMutexException] {$ownsMutex=$true}
    if(!$ownsMutex){throw '服务正在启动或终止，请稍后重试'}
    $connection=Join-Path $dataRoot 'connection.json'
    if(Test-Path -LiteralPath $connection){
        $conn=Get-Content -LiteralPath $connection -Raw | ConvertFrom-Json
        $headers=@{Authorization=('Bearer '+$conn.token)}
        $health=$null
        try {$health=Invoke-RestMethod ($conn.endpoint+'/health') -Headers $headers -TimeoutSec 3} catch {
            # A refusal means offline; other failures must not terminate its model.
            $probe=[Net.Sockets.TcpClient]::new()
            try {$uri=[Uri]$conn.endpoint;$probe.Connect($uri.Host,$uri.Port);throw '服务仍在监听但无法验证身份，取消终止'}
            catch [Net.Sockets.SocketException] {} finally {$probe.Dispose()}
        }
        if($health){
            if(!$health.canShutdown){throw '运行中的旧版服务不支持安全终止，请先升级服务'}
            $serviceProcess=Get-Process -Id $health.processId -ErrorAction Stop
            Invoke-RestMethod ($conn.endpoint+'/shutdown') -Method Post -Headers $headers -ContentType 'application/json' -Body '{}' -TimeoutSec 30 | Out-Null
            if(!$serviceProcess.WaitForExit(45000)){throw '转写服务仍在保存，请稍后重试；未强制结束进程'}
        }
    }
    $modelRecord=Join-Path $dataRoot 'model-process.json'
    if(Test-Path -LiteralPath $modelRecord){
        $record=Get-Content -LiteralPath $modelRecord -Raw | ConvertFrom-Json
        $modelProcess=Get-Process -Id $record.pid -ErrorAction SilentlyContinue
        if($modelProcess -and $modelProcess.Path -eq $record.path -and $modelProcess.StartTime.ToUniversalTime().Ticks.ToString() -eq $record.started){
            $modelProcess.Kill()
            if(!$modelProcess.WaitForExit(10000)){throw '模型进程尚未退出'}
        }
        Remove-Item -LiteralPath $modelRecord -Force
    }
    Write-Host '转写服务已终止；本项目启动的模型已关闭。'
} catch {
    $_ | Out-String | Set-Content (Join-Path $logs 'stop.stderr.log') -Encoding UTF8
    throw
} finally {if($ownsMutex){$mutex.ReleaseMutex()};$mutex.Dispose()}
