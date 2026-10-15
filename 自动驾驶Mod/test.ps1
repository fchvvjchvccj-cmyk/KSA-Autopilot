param([string]$GamePath = '', [string]$CompilerPath = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$game = Get-GameDirectory $GamePath
$runtime = Join-Path $PSScriptRoot 'runtime'
$refs = @('System.Private.CoreLib.dll','System.Runtime.dll','System.Console.dll','System.Collections.dll','System.Memory.dll','System.Reflection.dll','System.Runtime.CompilerServices.Unsafe.dll','Brutal.Core.Numerics.dll','BepuUtilities.dll','KSA.dll','KsaAutopilot.dll')
$argsList = @('/nologo','/noconfig','/nostdlib','/target:exe','/optimize+','/langversion:latest',('/out:'+(Join-Path $runtime 'AutopilotTests.dll')))
$argsList += $refs | ForEach-Object { '/reference:'+(Join-Path $runtime $_) }
$argsList += Join-Path $PSScriptRoot 'tests/Tests.cs'
$compiler=Get-LegacyCompiler $CompilerPath
if ($compiler) { & $compiler @argsList }
else {
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '运行后台测试需要 .NET 10 SDK 或 -CompilerPath。' }
    & dotnet build (Join-Path $PSScriptRoot 'tests/AutopilotTests.csproj') --configuration Release --output $runtime "-p:GamePath=$runtime" --nologo
}
if ($LASTEXITCODE -ne 0) { throw '测试程序编译失败' }
# 将独立宿主明确绑定测试入口；不运行 KSA 游戏入口。
$bytes = [IO.File]::ReadAllBytes((Join-Path $game 'KSA.exe'))
$text = [Text.Encoding]::ASCII.GetString($bytes)
$index = $text.IndexOf("KSA.dll`0",[StringComparison]::Ordinal)
if ($index -lt 0 -or $text.IndexOf("KSA.dll`0",$index+1,[StringComparison]::Ordinal) -ge 0) { throw '无法唯一定位测试宿主入口' }
$entry = [Text.Encoding]::UTF8.GetBytes('AutopilotTests.dll')
for ($i=0;$i -lt $entry.Length+1;$i++) { if ($i -gt 7 -and $bytes[$index+$i] -ne 0) { throw '宿主入口空间不足' } }
[Array]::Copy($entry,0,$bytes,$index,$entry.Length); $bytes[$index+$entry.Length]=0
$exe = Join-Path $runtime 'AutopilotTests.exe'
[IO.File]::WriteAllBytes($exe,$bytes)
Copy-Item -LiteralPath (Join-Path $game 'KSA.runtimeconfig.json') -Destination (Join-Path $runtime 'AutopilotTests.runtimeconfig.json') -Force
Copy-Item -LiteralPath (Join-Path $runtime 'KSA.deps.json') -Destination (Join-Path $runtime 'AutopilotTests.deps.json') -Force
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute=$false; $start.CreateNoWindow=$true
$start.WorkingDirectory=$runtime; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
$start.StandardOutputEncoding=[Text.Encoding]::UTF8; $start.StandardErrorEncoding=[Text.Encoding]::UTF8
$process=[Diagnostics.Process]::Start($start)
$stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
if (!$process.WaitForExit(60000)) { $process.Kill(); throw '测试超时，已结束测试进程' }
$output=$stdout.GetAwaiter().GetResult(); $errors=$stderr.GetAwaiter().GetResult()
$output | Set-Content -LiteralPath (Join-Path $PSScriptRoot '测试结果.txt') -Encoding UTF8
Write-Output $output
if ($process.ExitCode -ne 0) { throw $errors }
Write-Output '无窗口测试通过'
