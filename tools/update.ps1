param([string]$TyporaPath='C:\Program Files\Typora',[switch]$SkipTests)
# 一键更新：测试 → 停止服务 → 发布 → 安装插件（一次管理员授权）→ 启动服务 → 重启 Typora
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$log=Join-Path $projectRoot 'artifacts\update.log'
New-Item -ItemType Directory -Force -Path (Split-Path $log -Parent) | Out-Null
Start-Transcript -LiteralPath $log -Force | Out-Null
$status='failed'
try {
    Push-Location $projectRoot
    if(!$SkipTests){
        Write-Host '== 1/5 运行测试 =='
        & (Join-Path $PSScriptRoot 'test.ps1')
    }
    Write-Host '== 2/5 停止转写服务与模型 =='
    & (Join-Path $PSScriptRoot 'stop.ps1')
    Write-Host '== 3/5 发布服务 =='
    & (Join-Path $PSScriptRoot 'publish.ps1')
    Write-Host '== 4/5 安装 Typora 插件（请在弹出的管理员授权中点“是”） =='
    $installer=Join-Path $PSScriptRoot 'install-plugin.ps1'
    $installLog=Join-Path $projectRoot 'artifacts\update-install.log'
    Remove-Item -LiteralPath $installLog -ErrorAction SilentlyContinue
    $command="& { try { & '$installer' -TyporaPath '$TyporaPath' *>&1 | Out-File -LiteralPath '$installLog' -Encoding utf8; exit 0 } catch { `$_ | Out-String | Out-File -LiteralPath '$installLog' -Encoding utf8 -Append; exit 1 } }"
    $elevated=Start-Process powershell.exe -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-Command',$command)
    if(Test-Path -LiteralPath $installLog){Get-Content -LiteralPath $installLog | Write-Host}
    if($elevated.ExitCode -ne 0){throw '插件安装失败，详见 artifacts\update-install.log'}
    Write-Host '== 5/5 启动服务并重启 Typora =='
    & (Join-Path $PSScriptRoot 'start.ps1')
    $typoraExe=Join-Path $TyporaPath 'Typora.exe'
    $running=@(Get-Process -Name Typora -ErrorAction SilentlyContinue)
    if($running.Count){
        # 正常关闭窗口：有未保存内容时 Typora 会弹窗询问，不会强制结束。
        foreach($p in $running){if($p.MainWindowHandle -ne 0){[void]$p.CloseMainWindow()}}
        Write-Host '正在关闭 Typora（如有未保存内容，请在 Typora 中选择是否保存）…'
        $deadline=(Get-Date).AddMinutes(2)
        while((Get-Process -Name Typora -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline){Start-Sleep -Milliseconds 500}
    }
    if(Get-Process -Name Typora -ErrorAction SilentlyContinue){
        Write-Host 'Typora 仍未退出，请手动完全退出后重新打开，插件更新才会生效。'
    } elseif(Test-Path -LiteralPath $typoraExe){
        Start-Process -FilePath $typoraExe
    }
    $status='ok'
    Write-Host '更新完成。'
} catch {
    Write-Host ('更新失败：'+$_.Exception.Message) -ForegroundColor Red
    $_ | Out-String | Write-Host
} finally {
    Pop-Location
    Write-Host "UPDATE-STATUS: $status"
    Stop-Transcript | Out-Null
}
if($status -ne 'ok'){exit 1}
