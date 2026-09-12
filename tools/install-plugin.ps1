param([string]$TyporaPath='C:\Program Files\Typora',[switch]$Uninstall)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$resources=Join-Path $TyporaPath 'resources'
$windowPath=Join-Path $resources 'window.html'
if(!(Test-Path -LiteralPath $windowPath)){throw '找不到 Typora resources/window.html'}
$html=[IO.File]::ReadAllText($windowPath)
$tagPattern='<!-- typora-asr:start -->[\s\S]*?<!-- typora-asr:end -->'
if($Uninstall){
    $updated=[regex]::Replace($html,$tagPattern,'')
    [IO.File]::WriteAllText($windowPath,$updated,[Text.UTF8Encoding]::new($false))
    Write-Host '已移除插件加载入口，重启 Typora 生效。录音和模型文件保留。'
    exit
}
$package=Join-Path $resources 'typora-asr'
if(!(Test-Path -LiteralPath ($windowPath+'.before-asr'))){Copy-Item -LiteralPath $windowPath -Destination ($windowPath+'.before-asr')}
New-Item -ItemType Directory -Force -Path $package | Out-Null
Copy-Item -Path (Join-Path $projectRoot 'src\typora-plugin\*') -Destination $package -Force
$connectionFile=Join-Path $projectRoot '.asr\connection.json'
@{connectionFile=$connectionFile} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'config.json') -Encoding utf8
$escapedRoot=[System.Net.WebUtility]::HtmlEncode($package)
$tag='<!-- typora-asr:start --><script src="./typora-asr/bootstrap.js" data-asr-root="'+$escapedRoot+'" defer></script><!-- typora-asr:end -->'
$updated=[regex]::Replace($html,$tagPattern,'')
if(!$updated.Contains('</body>')){throw 'window.html 格式不兼容'}
$updated=$updated.Replace('</body>',$tag+'</body>')
[IO.File]::WriteAllText($windowPath,$updated,[Text.UTF8Encoding]::new($false))
Write-Host "插件已安装到 $package。重启 Typora 后可看到语音记录面板。"
