$script:AutopilotModRoot = $PSScriptRoot
function Get-GameDirectory([string]$GamePath) {
    if ($GamePath) {
        $resolved = (Resolve-Path -LiteralPath $GamePath).Path
        if (!(Test-Path -LiteralPath (Join-Path $resolved 'KSA.dll'))) { throw '指定目录中没有 KSA.dll' }
        return $resolved
    }
    $repository = Split-Path $script:AutopilotModRoot -Parent
    $candidates = @(
        $env:KSA_GAME_PATH
        (Join-Path $repository 'Kitten Space Agency')
        (Join-Path (Split-Path $repository -Parent) 'Kitten Space Agency')
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate 'KSA.dll'))) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    throw '找不到游戏目录。请设置 KSA_GAME_PATH，或运行 install.ps1 时指定 -GamePath。'
}
function Get-LegacyCompiler([string]$CompilerPath) {
    if ($CompilerPath) { return (Resolve-Path -LiteralPath $CompilerPath).Path }
    $repository = Split-Path $script:AutopilotModRoot -Parent
    foreach ($base in @($repository,(Split-Path $repository -Parent))) {
        $candidate = Join-Path $base 'scripts/tools/roslyn/tasks/net472/csc.exe'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}
