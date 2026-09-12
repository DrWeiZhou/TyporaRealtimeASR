$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$report=Join-Path $projectRoot 'artifacts\firewall-result.json'
try{
    $programs=@((Join-Path $projectRoot 'runtime\dotnet\dotnet.exe'),(Join-Path $projectRoot 'runtime\publish\TyporaAsr.Service.exe'))
    $applied=@()
    foreach($program in $programs){
        if(!(Test-Path -LiteralPath $program)){continue}
        $ruleName='TyporaRealtimeASR-Loopback-'+[IO.Path]::GetFileNameWithoutExtension($program)
        $existing=Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
        if(!$existing){
            New-NetFirewallRule -Name $ruleName -DisplayName ('TyporaRealtimeASR 本机转写 '+[IO.Path]::GetFileName($program)) -Direction Inbound -Action Allow -Program $program -Protocol TCP -LocalPort 18082 -LocalAddress 127.0.0.1 -RemoteAddress 127.0.0.1 -Profile Any | Out-Null
        }
        $applied+=$ruleName
    }
    @{ok=$true;rules=$applied;scope='127.0.0.1 TCP 18082'} | ConvertTo-Json | Set-Content -LiteralPath $report -Encoding utf8
}catch{
    @{ok=$false;error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath $report -Encoding utf8
    throw
}
