param([string]$Version='0.4.0',[switch]$SkipTests,[switch]$SkipBuild)
# 一键发布（-SkipBuild 复用已构建的安装包，用于补建 Release）：构建安装程序 → 打标签 v<版本> → 推送 main 和标签 → 有 GitHub CLI 时创建 Release 并上传安装包
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
    $setup=Join-Path $projectRoot "artifacts\TyporaRealtimeASR-v$Version-Setup.exe"
    $zip=Join-Path $projectRoot "artifacts\TyporaRealtimeASR-v$Version-win-x64.zip"
    if($SkipBuild){
        foreach($file in @($setup,"$setup.sha256",$zip,"$zip.sha256")){if(!(Test-Path -LiteralPath $file)){throw "未找到已构建的文件：$file"}}
    } else {
        & (Join-Path $PSScriptRoot 'build-installer.ps1') -Version $Version -SkipTests:$SkipTests
    }
    Write-Host "== 打标签 $tag =="
    $existing=git tag --list $tag
    if($existing){
        $tagged=(git rev-list -n 1 $tag).Trim()
        if($tagged -ne (git rev-parse HEAD).Trim()){
            if(!$SkipBuild){throw "标签 $tag 已存在且不指向当前提交"}
            Write-Host "标签 $tag 已存在（指向 $($tagged.Substring(0,7))），沿用该标签发布已构建的安装包。" -ForegroundColor Yellow
        }
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
        # PowerShell 5.1 turns redirected stderr into errors; this probe is allowed to fail.
        $ErrorActionPreference='Continue'
        & $gh.Source release view $tag *> $null
        $releaseExists=$LASTEXITCODE -eq 0
        $ErrorActionPreference='Stop'
        # Release notes go through a file: Windows PowerShell 5.1 treats curly quotes in arguments as string delimiters.
        $notes=Join-Path $projectRoot "artifacts\release-notes-$Version.md"
        $text=@(
            "Windows x64 安装程序：TyporaRealtimeASR-v$Version-Setup.exe（内含自包含服务与 Typora 插件）。",
            '',
            "- 运行安装程序，按提示选择安装目录并允许管理员授权以安装插件；升级时选择原目录，配置与数据会保留。",
            "- 也可使用 TyporaRealtimeASR-v$Version-win-x64.zip 手动安装，步骤见 README 的安装发布包一节。",
            "- 各文件的 SHA-256 见同名 .sha256 文件。"
        ) -join "`n"
        [IO.File]::WriteAllText($notes,$text,[Text.UTF8Encoding]::new($false))
        if($releaseExists){
            & $gh.Source release upload $tag $setup "$setup.sha256" $zip "$zip.sha256" --clobber
        } else {
            & $gh.Source release create $tag $setup "$setup.sha256" $zip "$zip.sha256" --title "TyporaRealtimeASR $tag" --notes-file $notes --verify-tag
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
