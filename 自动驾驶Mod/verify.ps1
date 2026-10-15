param([string]$GamePath = '')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$game=Get-GameDirectory $GamePath
$runtime=Join-Path $PSScriptRoot 'runtime'
& (Join-Path $PSScriptRoot 'install.ps1') -Action Verify -GamePath $game
Add-Type -Path (Join-Path $PSScriptRoot 'bin/Mono.Cecil.dll')
$source=[Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $game 'KSA.dll'))
$patched=[Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $runtime 'KSA.dll'))
function AllTypes($items) { foreach($type in $items) { $type; AllTypes $type.NestedTypes } }
function Calls($module,[string]$name) {
    @(foreach($type in (AllTypes $module.Types)) {
        foreach($method in $type.Methods | Where-Object HasBody) {
            foreach($instruction in $method.Body.Instructions) {
                if ($instruction.Operand -is [Mono.Cecil.MethodReference] -and $instruction.Operand.DeclaringType.FullName -eq $name) {
                    [pscustomobject]@{ caller=$method.FullName; target=$instruction.Operand.Name }
                }
            }
        }
    })
}
try {
    $hooks=@(Calls $patched 'KsaAutopilot.Autopilot')
    foreach($name in @('BeforePrepare','Bind','Control','AfterControl','Attitude','PlayerInput','Draw')) {
        if (@($hooks | Where-Object target -eq $name).Count -ne 1) { throw "自动驾驶接入点缺失或重复：$name" }
    }
    $sourceChinese=@(Calls $source 'KsaChinese.Translator')
    $patchedChinese=@(Calls $patched 'KsaChinese.Translator')
    if ($sourceChinese.Count -ne $patchedChinese.Count) { throw '汉化调用未保留' }
    if ($sourceChinese.Count -gt 0) {
        $diff=Compare-Object -ReferenceObject ($sourceChinese|ConvertTo-Json -Compress) -DifferenceObject ($patchedChinese|ConvertTo-Json -Compress)
        if ($diff) { throw '汉化调用位置发生改变' }
    }
    $hashes=@{}
    foreach($name in @('KSA.dll','Brutal.ImGui.dll','KSA.deps.json')) {
        $hashes[$name]=(Get-FileHash -LiteralPath (Join-Path $game $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $result=Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '测试结果.txt')
    if ($result -notmatch 'PASS: (\d+) checks') { throw '缺少明确的后台测试结果' }
    $headless=[int]$Matches[1]
    $installation=Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '安装测试.json')|ConvertFrom-Json
    if ($installation.result -ne 'passed') { throw '安装测试尚未通过' }
    [ordered]@{
        modVersion='0.1.0'; gameBuild='2026.10.13.5602'; result='passed'; gameWindowOpened=$false
        headlessChecks=$headless; installerChecks=$installation.checks; hooks=$hooks
        chineseCallsPreserved=$sourceChinese.Count; originalLocalizedFileHashes=$hashes
        simulation='中心引力、有限推力、燃料消耗；地球 200km、120km 和月球 50km'
        limitation='未进行游戏内实飞、界面、大气、真实执行器及多级分离验证；多级火箭需要手动分级'

    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $PSScriptRoot '验证报告.json') -Encoding UTF8
    Write-Output "验证完成：$headless 项后台检查、$($installation.checks) 项安装检查、7 个接入点、$($sourceChinese.Count) 处原有汉化调用保留。"
} finally { $source.Dispose(); $patched.Dispose() }
