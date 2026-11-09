param(
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [string]$ProjectName = 'RecentSandboxProject'
)

$ErrorActionPreference = 'Stop'

# 只有明确不存在才返回 false；权限和其他文件错误不能当成没有待恢复事务。
function Test-ExistingPath([string]$Path) {
    try { [IO.File]::GetAttributes($Path) | Out-Null; return $true }
    catch [IO.FileNotFoundException] { return $false }
    catch [IO.DirectoryNotFoundException] { return $false }
}

# 仿真配置不能借目录联接或文件链接访问源项目以外的数据。
function Assert-NoLinks([string]$Path) {
    if (-not (Test-ExistingPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "不能使用联接或符号链接：$Path" }
    if ($item.PSIsContainer) {
        foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force)) { Assert-NoLinks $child.FullName }
    }
}

# 未完成的多文件应用必须由源项目恢复，不能复制其中间状态作为新项目。
function Assert-NoPendingApply([string]$Machine) {
    foreach ($folder in @('Drafts', 'Runtime')) {
        $path = Join-Path $Machine $folder
        if (Test-ExistingPath $path) {
            $attributes = [IO.File]::GetAttributes($path)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw "源目录不能是联接或符号链接：$path" }
        }
    }
    foreach ($relative in @('Drafts\TaskApplyPending.json', 'Runtime\EquipmentProjectApplyPending.json')) {
        if (Test-ExistingPath (Join-Path $Machine $relative)) { throw "源项目有未完成的配置应用：$relative" }
    }
}

# 建立配置文件清单与哈希，用于发现复制前后可观察到的源文件变化。
function Get-SourceSnapshot([string]$Machine) {
    $files = @()
    foreach ($folder in @('HardwareConfig', 'Parameters', 'Tasks', 'Drafts')) {
        $path = Join-Path $Machine $folder
        if (-not (Test-ExistingPath $path)) {
            if ($folder -eq 'Drafts') { continue }
            throw "源配置缺少目录：$folder"
        }
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "源配置不是目录：$folder" }
        Assert-NoLinks $path
        foreach ($file in @(Get-ChildItem -LiteralPath $path -File -Recurse -Force | Where-Object {
            $_.Extension -eq '.json' -and $_.Name -notmatch '\.before[-.]'
        })) {
            $relative = $file.FullName.Substring($Machine.Length).TrimStart('\')
            $files += [PSCustomObject]@{ Relative=$relative; FullName=$file.FullName; Hash=(Get-FileHash -LiteralPath $file.FullName).Hash }
        }
    }
    return $files | Sort-Object Relative
}

# 只在端口空闲时创建独立的新软件控制器会话。
function Assert-FreePort([int]$Port) {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    $probe.ExclusiveAddressUse = $true
    try { $probe.Start() }
    catch { throw "软件控制器端口 $Port 已被占用；新项目不能假定该控制器是全新状态。" }
    finally { $probe.Stop() }
}

$appRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'MotionControlFramework'))
$projectsRoot = [IO.Path]::GetFullPath((Join-Path $appRoot 'SimulationProjects'))
$productionRoot = [IO.Path]::GetFullPath((Join-Path $appRoot 'Configurations'))
$preference = Join-Path $appRoot 'DefaultSimulationProject.txt'
if ([string]::IsNullOrWhiteSpace($ProjectName) -or $ProjectName -notmatch '^[A-Za-z0-9_-]+$') {
    throw '仿真项目名称只能包含英文字母、数字、下划线和连字符。'
}
$destination = [IO.Path]::GetFullPath((Join-Path $projectsRoot $ProjectName))
if (-not $destination.StartsWith($projectsRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '仿真项目必须位于本机仿真项目目录。' }
if ($destination.StartsWith($productionRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '不能将仿真项目写入原设备配置目录。' }
if (Test-ExistingPath $destination) { throw "目标项目已经存在，未覆盖：$destination" }
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$sourceMachine = Join-Path $source 'Machine01'
if (-not (Test-Path -LiteralPath $sourceMachine -PathType Container)) { throw '源配置缺少 Machine01。' }

# 检查源和目标的已有祖先，防止写入意外目录。
foreach ($candidate in @($sourceMachine, $projectsRoot, $appRoot)) {
    $directory = [IO.DirectoryInfo]::new($candidate)
    while ($null -ne $directory) {
        if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "目录不能是联接或符号链接：$($directory.FullName)" }
        $directory = $directory.Parent
    }
}

# 与正式 Host 使用同一单实例锁；只读取已经停止使用的源快照。
$mutex = [Threading.Mutex]::new($false, 'Local\MotionControl.Host.Wpf.Machine01')
$ownsMutex = $false
$stage = $null
$stageMoved = $false
try {
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true; throw '旧上位机会话异常结束，请先核查后再安装配置。' }
    if (-not $ownsMutex) { throw '上位机正在运行，请正常关闭后再安装配置。' }
    Assert-NoPendingApply $sourceMachine
    $before = @(Get-SourceSnapshot $sourceMachine)
    if ($before.Count -eq 0) { throw '没有可复制的仿真配置。' }
    $sourceBackend = Get-Content -LiteralPath (Join-Path $sourceMachine 'Parameters\ControllerBackend.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $port = 0
    if ($sourceBackend.backend -ne 'Virtual' -or -not [int]::TryParse([string]$sourceBackend.port, [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
        throw '只允许从有效的本机软件控制器配置创建新仿真项目。'
    }
    Assert-FreePort $port
    [IO.Directory]::CreateDirectory($projectsRoot) | Out-Null
    $stageName = '.install-' + [Guid]::NewGuid().ToString('N')
    $stage = [IO.Path]::GetFullPath((Join-Path $projectsRoot $stageName))
    if (Test-ExistingPath $stage) { throw '安装临时目录已存在，未覆盖。' }
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    $machine = Join-Path $stage 'Machine01'
    foreach ($file in $before) {
        $target = Join-Path $machine $file.Relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
        if ((Get-FileHash -LiteralPath $target).Hash -ne $file.Hash) { throw "源配置在复制期间发生变化：$($file.Relative)" }
    }
    Assert-NoPendingApply $sourceMachine
    $after = @(Get-SourceSnapshot $sourceMachine)
    if ($after.Count -ne $before.Count) { throw '源配置文件清单在复制期间发生变化。' }
    for ($i = 0; $i -lt $before.Count; $i++) {
        if ($before[$i].Relative -ne $after[$i].Relative -or $before[$i].Hash -ne $after[$i].Hash) { throw "源配置在复制期间发生变化：$($before[$i].Relative)" }
    }

    $axes = Get-Content -LiteralPath (Join-Path $machine 'HardwareConfig\AxisParameters.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $io = Get-Content -LiteralPath (Join-Path $machine 'HardwareConfig\IoMapping.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($axes.axes -isnot [array] -or $io.digitalInputs -isnot [array] -or $io.digitalOutputs -isnot [array] -or
        $axes.axes.Count -eq 0 -or $io.digitalInputs.Count -eq 0 -or $io.digitalOutputs.Count -eq 0 -or
        @($axes.axes | Where-Object { $null -eq $_ }).Count -gt 0 -or
        @($io.digitalInputs | Where-Object { $null -eq $_ }).Count -gt 0 -or
        @($io.digitalOutputs | Where-Object { $null -eq $_ }).Count -gt 0) {
        throw '复制后的轴或 IO 映射无效，未设置默认项目。'
    }

    # 每个新项目拥有独立编号，Host 还会把它与项目绝对路径绑定。
    $sessionStream = [IO.FileStream]::new((Join-Path $stage 'ProjectSession.id'), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $sessionBytes=[Text.Encoding]::UTF8.GetBytes([Guid]::NewGuid().ToString('N')); $sessionStream.Write($sessionBytes,0,$sessionBytes.Length); $sessionStream.Flush($true) }
    finally { $sessionStream.Dispose() }
    # 仅为没有旧运行记录的新软件仿真会话建立初始状态；不修改源报警记录。
    $runtime = Join-Path $machine 'Runtime'
    [IO.Directory]::CreateDirectory($runtime) | Out-Null
    $stream = [IO.FileStream]::new((Join-Path $runtime 'AlarmContinuity.clean'), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $bytes = [Text.Encoding]::UTF8.GetBytes('MC-ALARM-CONTINUITY|1|Machine01'); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    $origin = @{
        source=$source; createdAtUtc=[DateTime]::UtcNow.ToString('o'); copiedJsonFiles=$before.Count
        axisCount=$axes.axes.Count; inputCount=$io.digitalInputs.Count; outputCount=$io.digitalOutputs.Count
        oldRuntimeCopied=$false; description='独立软件仿真项目；源报警及恢复记录仍在原目录。'
    }
    [IO.File]::WriteAllText((Join-Path $stage 'ProjectOrigin.json'), ($origin | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    Assert-NoPendingApply $sourceMachine
    Assert-FreePort $port
    # 目录重命名要求目标不存在；即使其他程序在检查后创建目标，也不会覆盖它。
    [IO.Directory]::Move($stage, $destination)
    $stageMoved = $true

    [IO.Directory]::CreateDirectory($appRoot) | Out-Null
    $temporary = $preference + '.new-' + [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($temporary, $destination, [Text.UTF8Encoding]::new($false))
    if (Test-ExistingPath $preference) {
        $backup = $preference + '.backup-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
        [IO.File]::Replace($temporary, $preference, $backup)
    } else { [IO.File]::Move($temporary, $preference) }
    Write-Output "默认仿真项目：$destination"
    Write-Output "已复制轴 $($axes.axes.Count)、输入 $($io.digitalInputs.Count)、输出 $($io.digitalOutputs.Count)，保留源运行记录。"
} finally {
    if ($stage -and -not $stageMoved -and (Test-ExistingPath $stage)) {
        # 只清理本次生成且仍位于项目根目录的临时目录；遇到联接时保留待核查。
        $checked = [IO.Path]::GetFullPath($stage).TrimEnd('\')
        if ([IO.Path]::GetDirectoryName($checked) -eq $projectsRoot.TrimEnd('\') -and [IO.Path]::GetFileName($checked) -eq $stageName) {
            try { Assert-NoLinks $checked; [IO.Directory]::Delete($checked, $true) } catch { Write-Warning "临时目录未清理，请核查：$checked" }
        }
    }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
