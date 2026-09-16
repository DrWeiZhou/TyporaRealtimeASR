param([string]$Version='0.4.0',[switch]$SkipTests)
# 一键发布：构建安装程序 → 打标签 v<版本> → 推送 main 和标签 → 有 GitHub CLI 时创建 Release 并上传安装包
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$log=Join-Path $projectRoot 'artifacts\publish-release.log'
New-Item -ItemType Directory -Force -Path (Split-Path $log -Parent) | Out-Null
Start-Transcript -LiteralPath $log -Force | Out-Null
$status='failed'
Push-Location $projectRoot
try{
    $branch=(git rev-parse --abbrev-ref HEAD).Trim()
    if($branch -ne 'main'){throw "请先切换到 main 分支（当前为 $branch）"}
    if(git status --porcelain --untracked-files=no){throw '工作区有未提交的修改，请先提交'}
    $tag="v$Version"
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -Version $Version -SkipTests:$SkipTests
    $setup=Join-Path $projectRoot "artifacts\TyporaRealtimeASR-v$Version-Setup.exe"
    $zip=Join-Path $projectRoot "artifacts\TyporaRealtimeASR-v$Version-win-x64.zip"
    Write-Host "== 打标签 $tag =="
    $existing=git tag --list $tag
    if($existing){
        if((git rev-list -n 1 $tag).Trim() -ne (git rev-parse HEAD).Trim()){throw "标签 $tag 已存在且不指向当前提交"}
    } else {
        git tag -a $tag -m "TyporaRealtimeASR $tag"
        if($LASTEXITCODE -ne 0){throw '创建标签失败'}
    }
    Write-Host '== 推送 main 和标签 =='
    git push origin main
    if($LASTEXITCODE -ne 0){throw '推送 main 失败'}
    git push origin $tag
    if($LASTEXITCODE -ne 0){throw '推送标签失败'}
    $gh=Get-Command gh -ErrorAction SilentlyContinue
    if($gh){
        Write-Host '== 创建 GitHub Release 并上传安装包 =='
        & $gh.Source release view $tag *> $null
        if($LASTEXITCODE -eq 0){
            & $gh.Source release upload $tag $setup "$setup.sha256" $zip "$zip.sha256" --clobber
        } else {
            & $gh.Source release create $tag $setup "$setup.sha256" $zip "$zip.sha256" --title "TyporaRealtimeASR $tag" --notes "Windows x64 安装程序：TyporaRealtimeASR-v$Version-Setup.exe（含自包含服务与 Typora 插件）。详见 README“安装发布包”。"
        }
        if($LASTEXITCODE -ne 0){throw '创建 GitHub Release 失败（代码与标签已推送）'}
    } else {
        Write-Host '未安装 GitHub CLI (gh)：代码与标签已推送。请在 GitHub 网页的 Releases 中基于该标签新建 Release，并上传以下文件：' -ForegroundColor Yellow
        Write-Host "  $setup`n  $setup.sha256`n  $zip`n  $zip.sha256"
    }
    $status='ok'
    Write-Host "发布完成：$tag"
} catch {
    Write-Host ('发布失败：'+$_.Exception.Message) -ForegroundColor Red
} finally {
    Pop-Location
    Write-Host "RELEASE-STATUS: $status"
    Stop-Transcript | Out-Null
}
if($status -ne 'ok'){exit 1}
