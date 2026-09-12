param([switch]$Live)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try{
 foreach($file in Get-ChildItem src/typora-plugin -File | Where-Object {$_.Extension -in '.cjs','.js'}){node --check $file.FullName;if($LASTEXITCODE -ne 0){throw "语法检查失败: $file"}}
 node --test tests/editor/controller.test.cjs tests/editor/adapter.test.cjs tests/editor/launcher.test.cjs
 if($LASTEXITCODE -ne 0){throw '编辑保护测试失败'}
 $dotnet=Join-Path $projectRoot 'runtime\dotnet\dotnet.exe'
 if(!(Test-Path $dotnet)){$dotnet=(Get-Command dotnet).Source}
 $arguments=@('run','--project','tests/service')
 if($Live){$arguments+=@('--','--live','artifacts/controlled-zh.pcm')}
 & $dotnet @arguments
 if($LASTEXITCODE -ne 0){throw '服务测试失败'}
}finally{Pop-Location}
