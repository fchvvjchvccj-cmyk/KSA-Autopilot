param(
    [ValidateSet('Install','Launch','Uninstall','Verify')][string]$Action = 'Install',
    [string]$GamePath = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$game = Get-GameDirectory $GamePath
$runtime = Join-Path $PSScriptRoot 'runtime'
$statePath = Join-Path $PSScriptRoot 'installation.json'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Check-Stopped {
    foreach ($process in @(Get-Process -Name KSA -ErrorAction SilentlyContinue)) {
        if ($process.Path -and $process.Path.StartsWith($runtime,[StringComparison]::OrdinalIgnoreCase)) {
            throw '自动驾驶游戏进程仍在运行，请正常退出后再安装或卸载。'
        }
    }
}
if ($Action -eq 'Uninstall') {
    Check-Stopped
    # 只删除独立目录中登记的文件；目录联接先作为链接删除，绝不递归遍历游戏内容。
    if (!(Test-Path -LiteralPath $statePath)) { Write-Output '未发现自动驾驶安装记录'; return }
    $state = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
    foreach ($name in @($state.files)) {
        if ([IO.Path]::GetFileName($name) -ne $name) { throw '安装记录中的文件名无效' }
        $path = Join-Path $runtime $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    foreach ($name in @($state.links)) {
        if ([IO.Path]::GetFileName($name) -ne $name) { throw '安装记录中的目录名无效' }
        $path = Join-Path $runtime $name
        if (Test-Path -LiteralPath $path) {
            $item = Get-Item -LiteralPath $path -Force
            if (!($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "拒绝删除普通目录：$path" }
            [IO.Directory]::Delete($path)
        }
    }
    Remove-Item -LiteralPath $statePath
    Write-Output '自动驾驶已卸载。原游戏和汉化文件保持原样；运行日志保留。'
    return
}
$manifest = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'compatibility.json') | ConvertFrom-Json
$sourceHash = Hash (Join-Path $game 'KSA.dll')
$profile = @($manifest.profiles | Where-Object { $_.ksa -eq $sourceHash })
if ($profile.Count -ne 1) { throw '不支持的 KSA.dll：仅支持 2026.10.13.5602 原版及本工作区对应汉化版，未修改任何游戏文件。' }
$profile = $profile[0]
if ((Hash (Join-Path $game 'Brutal.ImGui.dll')) -ne $profile.imgui -or (Hash (Join-Path $game 'KSA.deps.json')) -ne $profile.deps) {
    throw '游戏、ImGui 与依赖清单不匹配，请先使用原有汉化工具修复安装。'
}
$dll = Join-Path $PSScriptRoot 'bin/KsaAutopilot.dll'
$modHash = Hash $dll
if ($Action -eq 'Verify') {
    if (!(Test-Path -LiteralPath $statePath)) { throw '尚未安装' }
    $state = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
    if ($state.sourceHash -ne $sourceHash -or $state.modHash -ne $modHash -or
        (Hash (Join-Path $runtime 'KSA.dll')) -ne $state.patchedHash -or
        (Hash (Join-Path $runtime 'KsaAutopilot.dll')) -ne $modHash) { throw '运行目录与当前源文件不一致，请重新安装。' }
    Write-Output "自动驾驶校验通过：$($profile.name)"
    return
}
Check-Stopped
Add-Type -Path (Join-Path $PSScriptRoot 'bin/Mono.Cecil.dll')
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($game)
$resolver.AddSearchDirectory((Join-Path $PSScriptRoot 'bin'))
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.AssemblyResolver = $resolver
$module = [Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $game 'KSA.dll'),$reader)
$helper = [Mono.Cecil.ModuleDefinition]::ReadModule($dll,$reader)
try {
    if ($module.Assembly.Name.Version.ToString() -ne '2026.10.13.5602') { throw '游戏版本不兼容' }
    $api = $helper.Types | Where-Object FullName -eq 'KsaAutopilot.Autopilot'
    function Ref([string]$name) { $module.ImportReference(($api.Methods | Where-Object Name -eq $name)) }
    function Method([string]$type,[string]$name) {
        $matches = @(($module.Types | Where-Object FullName -eq $type).Methods | Where-Object Name -eq $name)
        if ($matches.Count -ne 1 -or !$matches[0].HasBody) { throw "接口不匹配：$type::$name" }
        $matches[0]
    }
    function Prefix($method,$instructions) {
        $il = $method.Body.GetILProcessor(); $first = $method.Body.Instructions[0]
        foreach ($instruction in $instructions) { $il.InsertBefore($first,$instruction) }
    }
    function Postfix($method,$instructions) {
        $il = $method.Body.GetILProcessor()
        foreach ($ret in @($method.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ret' })) {
            $ret.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
            $cursor = $ret
            foreach ($instruction in $instructions) { $il.InsertAfter($cursor,$instruction); $cursor = $instruction }
            $il.InsertAfter($cursor,$il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
        }
    }
    $op = [Mono.Cecil.Cil.OpCodes]
    $prepare = Method 'KSA.Vehicle' 'PrepareWorker'
    $il = $prepare.Body.GetILProcessor()
    $manual = ($module.Types | Where-Object FullName -eq 'KSA.Vehicle').Fields | Where-Object Name -eq '_manualControlInputs'
    Prefix $prepare @($il.Create($op::Ldarg_0),$il.Create($op::Ldarg_0),$il.Create($op::Ldflda,$manual),$il.Create($op::Call,(Ref 'BeforePrepare')))
    $bind = Method 'KSA.VehicleUpdateState' 'PrepareFromVehicle'
    $il = $bind.Body.GetILProcessor()
    Postfix $bind @($il.Create($op::Ldarg_0),$il.Create($op::Call,(Ref 'Bind')))
    $control = Method 'KSA.FlightComputer' 'ComputeControl'
    $il = $control.Body.GetILProcessor()
    $at = @($control.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'UpdateActiveEnginePerformance' })
    if ($at.Count -ne 1) { throw '发动机状态刷新接口不匹配' }
    $cursor = $at[0]
    foreach ($i in @($il.Create($op::Ldarg_0),$il.Create($op::Ldarg_1),$il.Create($op::Ldarg_2),$il.Create($op::Call,(Ref 'Control')))) {
        $il.InsertAfter($cursor,$i); $cursor = $i
    }
    Postfix $control @($il.Create($op::Ldarg_0),$il.Create($op::Ldarg_3),$il.Create($op::Call,(Ref 'AfterControl')))
    $attitude = Method 'KSA.FlightComputer' 'UpdateAttitudeTarget'
    $il = $attitude.Body.GetILProcessor()
    Postfix $attitude @($il.Create($op::Ldarg_0),$il.Create($op::Ldarg_1),$il.Create($op::Call,(Ref 'Attitude')))
    $input = Method 'KSA.Vehicle' 'ProcessInput'
    $il = $input.Body.GetILProcessor()
    Prefix $input @($il.Create($op::Ldarg_0),$il.Create($op::Ldarg_1),$il.Create($op::Call,(Ref 'PlayerInput')))
    $ui = Method 'KSA.Universe' 'OnDrawUi'
    $il = $ui.Body.GetILProcessor()
    Postfix $ui @($il.Create($op::Call,(Ref 'Draw')))
    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    $patchedPath = Join-Path $runtime 'KSA.dll.new'
    $module.Write($patchedPath)
}
finally { $helper.Dispose(); $module.Dispose(); $resolver.Dispose() }
$files = @('KSA.dll','KSA.deps.json','KsaAutopilot.dll')
foreach ($file in @(Get-ChildItem -LiteralPath $game -File)) {
    if ($file.Name -in @('KSA.dll','KSA.deps.json')) { continue }
    $dest = Join-Path $runtime $file.Name
    # 替换链接本身，绝不向已有硬链接写入数据。
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Force }
    try { New-Item -ItemType HardLink -Path $dest -Target $file.FullName -ErrorAction Stop | Out-Null }
    catch { Copy-Item -LiteralPath $file.FullName -Destination $dest -Force }
    $files += $file.Name
}
$links = @()
foreach ($dir in @(Get-ChildItem -LiteralPath $game -Directory)) {
    $dest = Join-Path $runtime $dir.Name
    if (Test-Path -LiteralPath $dest) {
        $item = Get-Item -LiteralPath $dest -Force
        if (!($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "运行目录存在普通目录：$dest" }
        [IO.Directory]::Delete($dest)
    }
    New-Item -ItemType Junction -Path $dest -Target $dir.FullName | Out-Null
    $links += $dir.Name
}
$deps = Get-Content -Raw -LiteralPath (Join-Path $game 'KSA.deps.json') | ConvertFrom-Json
$target = $deps.targets.($deps.runtimeTarget.name)
$entry = @{ runtime = @{ 'KsaAutopilot.dll' = @{} } }
$target | Add-Member -MemberType NoteProperty -Name 'KsaAutopilot/1.0.0' -Value $entry -Force
$target.'KSA/1.0.0'.dependencies | Add-Member -MemberType NoteProperty -Name 'KsaAutopilot' -Value '1.0.0' -Force
$deps.libraries | Add-Member -MemberType NoteProperty -Name 'KsaAutopilot/1.0.0' -Value @{type='project';serviceable=$false;sha512=''} -Force
$deps | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $runtime 'KSA.deps.json') -Encoding UTF8
Copy-Item -LiteralPath $dll -Destination (Join-Path $runtime 'KsaAutopilot.dll') -Force
Move-Item -LiteralPath $patchedPath -Destination (Join-Path $runtime 'KSA.dll') -Force
[ordered]@{ game=$game; sourceHash=$sourceHash; modHash=$modHash; patchedHash=(Hash (Join-Path $runtime 'KSA.dll')); profile=$profile.name; files=@($files|Select-Object -Unique); links=$links } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $statePath -Encoding UTF8
Write-Output "自动驾驶安装完成：$($profile.name)，原游戏文件未修改。"
if ($Action -eq 'Launch') {
    $previous = $env:KSA_LANGUAGE
    try {
        if ($profile.name -eq 'zh-Hans') { $env:KSA_LANGUAGE='zh-Hans' }
        Start-Process -FilePath (Join-Path $runtime 'KSA.exe') -WorkingDirectory $game | Out-Null
    } finally { $env:KSA_LANGUAGE=$previous }
}
