param([string]$GamePath = '', [string]$OriginalGamePath = '')
$ErrorActionPreference='Stop'
$mod=Split-Path $PSScriptRoot -Parent
. (Join-Path $mod 'common.ps1')
$gameSource=Get-GameDirectory $GamePath
$suite=Join-Path $PSScriptRoot ('install-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $suite -Force | Out-Null
$count=0
function Assert($ok,[string]$message) { if (!$ok) { throw $message }; $script:count++ }
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function Run([string]$package,[string]$game,[string]$action,[bool]$success=$true) {
    # 独立捕获退出码，避免 PowerShell 5.1 将预期的拒绝消息提前当成终止异常。
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Command powershell.exe).Source)
    $start.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $package 'install.ps1')+'" -Action '+$action+' -GamePath "'+$game+'"'
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
    if(!$process.WaitForExit(30000)) { $process.Kill(); throw '安装测试子进程超时' }
    $output=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()
    $code=$process.ExitCode; $process.Dispose()
    Assert (($code -eq 0) -eq $success) "$action 结果不符合预期：$output"
}
$compatibility=Get-Content -Raw -LiteralPath (Join-Path $mod 'compatibility.json')|ConvertFrom-Json
$currentHash=(Hash (Join-Path $gameSource 'KSA.dll')).ToLowerInvariant()
$currentProfile=@($compatibility.profiles|Where-Object ksa -eq $currentHash)
if($currentProfile.Count -ne 1){throw '测试源游戏不属于兼容配置'}
$cases=@([pscustomobject]@{language=$currentProfile[0].name;source=$gameSource})
if($OriginalGamePath -and $currentProfile[0].name -ne 'en-US'){
    $cases += [pscustomobject]@{language='en-US';source=(Resolve-Path -LiteralPath $OriginalGamePath).Path}
}
foreach ($caseInfo in $cases) {
    $language=$caseInfo.language
    $case=Join-Path $suite $language
    $package=Join-Path $case 'mod'; $game=Join-Path $case 'game'
    New-Item -ItemType Directory -Path (Join-Path $package 'bin'),(Join-Path $game 'Content') -Force | Out-Null
    foreach ($name in @('install.ps1','common.ps1','compatibility.json')) { Copy-Item -LiteralPath (Join-Path $mod $name) -Destination (Join-Path $package $name) }
    foreach ($name in @('KsaAutopilot.dll','Mono.Cecil.dll')) { Copy-Item -LiteralPath (Join-Path $mod "bin/$name") -Destination (Join-Path $package "bin/$name") }
    $source=$caseInfo.source
    $before=@{}
    foreach ($name in @('KSA.dll','Brutal.ImGui.dll','KSA.deps.json')) {
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $game $name)
        $before[$name]=Hash (Join-Path $game $name)
    }
    foreach ($reference in @(Get-ChildItem -LiteralPath $gameSource -Filter '*.dll' -File)) {
        if ($reference.Name -in @('KSA.dll','Brutal.ImGui.dll')) { continue }
        New-Item -ItemType HardLink -Path (Join-Path $game $reference.Name) -Target $reference.FullName | Out-Null
    }
    $marker=Join-Path $game 'Content/存档保护标记.txt'
    [IO.File]::WriteAllText($marker,'preserve-content')
    Run $package $game Install
    Run $package $game Verify
    $installed=Hash (Join-Path $package 'runtime/KSA.dll')
    Run $package $game Install
    Assert ((Hash (Join-Path $package 'runtime/KSA.dll')) -eq $installed) "$language 重复安装不稳定"
    foreach ($name in $before.Keys) { Assert ((Hash (Join-Path $game $name)) -eq $before[$name]) "$language 源游戏文件发生改变" }
    $deps=Get-Content -Raw -LiteralPath (Join-Path $package 'runtime/KSA.deps.json')|ConvertFrom-Json
    Assert ($null -ne $deps.targets.($deps.runtimeTarget.name).'KsaAutopilot/1.0.0') '自动驾驶依赖缺失'
    if ($language -eq 'zh-Hans') { Assert ($null -ne $deps.targets.($deps.runtimeTarget.name).'KsaChinese/1.0.0') '汉化依赖丢失' }
    Run $package $game Uninstall
    Assert ((Get-Content -Raw -LiteralPath $marker) -eq 'preserve-content') '卸载损坏联接目标'
    Assert (!(Test-Path -LiteralPath (Join-Path $package 'runtime/Content'))) '卸载未移除目录联接'
    foreach ($name in $before.Keys) { Assert ((Hash (Join-Path $game $name)) -eq $before[$name]) '卸载损坏源游戏' }
    # 破坏测试副本，确认未知程序在任何运行目录写入前被拒绝。
    [IO.File]::WriteAllBytes((Join-Path $game 'KSA.dll'),[byte[]]@(1,2,3,4))
    Run $package $game Install $false
    Assert (!(Test-Path -LiteralPath (Join-Path $package 'runtime/KSA.dll'))) '未知程序仍被安装'
}
[ordered]@{checks=$count; result='passed'; powershell='Windows PowerShell 5.1'; gameProcessStarted=$false; fixture=$suite} | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $mod '安装测试.json') -Encoding UTF8
Write-Output "安装测试通过：$count 项；已检查配置 $($cases.language -join ', ')。"
