param([string]$Version='0.4.0',[switch]$SkipTests)
# 构建安装程序：测试 → 生成发布 ZIP → 把 ZIP 内置到自包含单文件 Setup.exe → 生成 SHA-256
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid release version'}
$projectRoot=Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try{
    if(!$SkipTests){
        Write-Host '== 1/3 运行测试 =='
        & (Join-Path $PSScriptRoot 'test.ps1')
    }
    Write-Host '== 2/3 生成发布包 =='
    & (Join-Path $PSScriptRoot 'release.ps1') -Version $Version
    $name="TyporaRealtimeASR-v$Version-win-x64"
    $zip=Join-Path $projectRoot "artifacts\$name.zip"
    if(!(Test-Path -LiteralPath $zip)){throw "未找到发布包 $zip"}
    Write-Host '== 3/3 构建安装程序 =='
    $dotnet=Join-Path $projectRoot 'runtime\dotnet\dotnet.exe'
    if(!(Test-Path $dotnet)){$dotnet=(Get-Command dotnet).Source}
    $output=Join-Path $projectRoot ('runtime\installer-'+$Version+'-'+[Guid]::NewGuid().ToString('N'))
    & $dotnet publish (Join-Path $PSScriptRoot 'installer\TyporaAsrSetup.csproj') -c Release -o $output "-p:PayloadZip=$zip" "-p:Version=$Version" -v minimal
    if($LASTEXITCODE -ne 0){throw '安装程序构建失败'}
    $setup=Join-Path $projectRoot "artifacts\TyporaRealtimeASR-v$Version-Setup.exe"
    Copy-Item -LiteralPath (Join-Path $output 'TyporaRealtimeASR-Setup.exe') -Destination $setup -Force
    $hash=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($setup+'.sha256') -Value "$hash  $(Split-Path $setup -Leaf)" -Encoding ASCII
    $size=[math]::Round((Get-Item -LiteralPath $setup).Length/1MB,1)
    Write-Host "Installer: $setup ($size MB)"
    Write-Host "SHA-256: $hash"
}finally{Pop-Location}
