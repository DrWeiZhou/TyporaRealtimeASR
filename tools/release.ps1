param([string]$Version='0.4.0')
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid release version'}
$projectRoot=Split-Path $PSScriptRoot -Parent
$name="TyporaRealtimeASR-v$Version-win-x64"
$staging=Join-Path $projectRoot ('runtime\release-'+$Version+'-'+[Guid]::NewGuid().ToString('N'))
$package=Join-Path $staging $name
New-Item -ItemType Directory -Path $package -Force | Out-Null
& (Join-Path $PSScriptRoot 'publish.ps1') -OutputPath (Join-Path $package 'runtime\publish')
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
foreach($directory in @('src\typora-plugin','tools','docs')){
    $destination=Join-Path $package $directory
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -Path (Join-Path $projectRoot ($directory+'\*')) -Destination $destination -Recurse -Force
}
# The installer project is build tooling, not part of the installed package.
Remove-Item -LiteralPath (Join-Path $package 'tools\installer') -Recurse -Force -ErrorAction SilentlyContinue
foreach($file in @('README.md','LICENSE','config.example.json','start.cmd','install.cmd')){Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $package}
Set-Content -LiteralPath (Join-Path $package 'VERSION.txt') -Value $Version -Encoding ASCII
$archive=Join-Path $projectRoot "artifacts\$name.zip"
New-Item -ItemType Directory -Path (Split-Path $archive -Parent) -Force | Out-Null
Compress-Archive -LiteralPath $package -DestinationPath $archive -Force
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archive+'.sha256') -Value "$hash  $name.zip" -Encoding ASCII
Write-Host "Release: $archive"
