$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$dotnet=Join-Path $projectRoot 'runtime\dotnet\dotnet.exe'
if(!(Test-Path $dotnet)){$dotnet=(Get-Command dotnet).Source}
& $dotnet publish (Join-Path $projectRoot 'src\local-service') -c Release -r win-x64 --self-contained true -o (Join-Path $projectRoot 'runtime\publish') -v minimal
if($LASTEXITCODE -ne 0){throw '打包失败；运行中的发布版服务需要先停止'}
