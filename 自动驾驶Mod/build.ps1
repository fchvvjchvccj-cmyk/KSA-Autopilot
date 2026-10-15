param([string]$GamePath = '', [string]$CompilerPath = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$game = Get-GameDirectory $GamePath
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$compiler = Get-LegacyCompiler $CompilerPath
if ($compiler) {
    $refs = @('System.Private.CoreLib.dll','System.Runtime.dll','System.Console.dll','System.Collections.dll','System.Memory.dll','System.IO.FileSystem.dll','System.Text.Encoding.dll','Brutal.ImGui.dll','Brutal.Glfw.dll','Brutal.Core.Numerics.dll','Brutal.Core.Strings.dll','BepuUtilities.dll','KSA.dll')
    $arguments = @('/nologo','/noconfig','/nostdlib','/target:library','/optimize+','/deterministic+','/langversion:latest',('/out:'+(Join-Path $out 'KsaAutopilot.dll')))
    $arguments += $refs | ForEach-Object { '/reference:'+(Join-Path $game $_) }
    $arguments += @(Join-Path $PSScriptRoot 'src/Guidance.cs'; Join-Path $PSScriptRoot 'src/Autopilot.cs')
    & $compiler @arguments
} else {
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '请安装 .NET 10 SDK，或通过 -CompilerPath 指定 Roslyn csc.exe。' }
    & dotnet build (Join-Path $PSScriptRoot 'KsaAutopilot.csproj') --configuration Release --output $out "-p:GamePath=$game" --nologo
}
if ($LASTEXITCODE -ne 0) { throw '自动驾驶运行库编译失败；使用 dotnet 时需要 .NET 10 SDK。' }
if (!(Test-Path -LiteralPath (Join-Path $out 'Mono.Cecil.dll'))) { throw '缺少随仓库分发的 Mono.Cecil.dll，请完整下载仓库。' }
Write-Output '自动驾驶运行库编译完成'
