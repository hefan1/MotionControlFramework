param(
    [Parameter(Mandatory = $true)] [string]$ConfigurationRoot
)

$ErrorActionPreference = 'Stop'
$taskMachineId = 'Machine01'
$taskRoot = (Resolve-Path -LiteralPath $ConfigurationRoot).Path
$taskDirectory = Join-Path $taskRoot $taskMachineId
if (-not (Test-Path -LiteralPath $taskDirectory -PathType Container)) { throw '配置根目录中没有 Machine01，拒绝创建或猜测设备。' }
$taskDirectory = (Resolve-Path -LiteralPath $taskDirectory).Path
$taskExpectedDirectory = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskMachineId))
if (-not [string]::Equals($taskDirectory, $taskExpectedDirectory, [StringComparison]::OrdinalIgnoreCase)) { throw '设备目录不属于指定的配置根目录。' }

# 检查指定目录内的文件存在后才读取，避免静默创建生产配置。
function Read-TaskJson([string]$RelativePath) {
    $taskPath = Join-Path $taskDirectory $RelativePath
    if (-not (Test-Path -LiteralPath $taskPath -PathType Leaf)) { throw "缺少配置文件：$taskPath" }
    try { return Get-Content -LiteralPath $taskPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "配置文件无法解析：$taskPath。$($_.Exception.Message)" }
}

# 必须关闭上位机后再安装，防止运行中的设备继续使用旧配置。
function Assert-HostStopped {
    $taskHosts = @(Get-CimInstance Win32_Process -Filter "Name = 'MotionControl.Host.Wpf.exe'")
    if ($taskHosts.Count -ne 0) { throw '正式上位机仍在运行，请先正常停止并退出；本脚本不会关闭它。' }
}

# 用哈希确认草稿及待修改文件没有被并发更新。
function Get-TaskHash([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '<不存在>' }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

Assert-HostStopped
$taskBackend = Read-TaskJson 'Parameters\ControllerBackend.json'
if ($taskBackend.backend -ne 'Virtual') { throw '只允许明确选择 Virtual 的 Machine01；本脚本不会切换控制器。' }
$taskEquipment = Read-TaskJson 'Parameters\Equipment.json'
if ($taskEquipment.executionOwner -ne 'TaskEngine') { throw '当前不是任务列表入口；请先确认设备配置，本脚本不会切换运行方式。' }
$taskMachine = Read-TaskJson 'Parameters\TaskMachine.json'
if ($taskMachine.schemaVersion -ne 1 -or $taskMachine.machineId -ne $taskMachineId -or [int]$taskMachine.revision -lt 1) { throw '现有任务清单版本或设备标识无效。' }
if (Test-Path -LiteralPath (Join-Path $taskDirectory 'Drafts\TaskApplyPending.json')) { throw '设备存在未完成的任务应用，请先处理；本脚本不会改动恢复状态。' }
$taskVariables = Read-TaskJson 'Parameters\DeviceVariables.json'
$taskAlarms = Read-TaskJson 'Parameters\AlarmCatalog.json'
$taskAxes = Read-TaskJson 'HardwareConfig\AxisParameters.json'
$taskIo = Read-TaskJson 'HardwareConfig\IoMapping.json'
if ($taskVariables.schemaVersion -ne 1 -or $taskAlarms.schemaVersion -ne 1 -or $taskAxes.schemaVersion -ne 1 -or $taskIo.schemaVersion -ne 1) { throw '配置版本不受支持。' }

$taskChannels = @{}
foreach ($axisName in @('Load_X','Weld_Z','Press_Z','Camera_X','Unload_X')) {
    $matchingAxes = @($taskAxes.axes | Where-Object { $_.name -eq $axisName })
    if ($matchingAxes.Count -ne 1) { throw "轴映射缺失或重复：$axisName" }
    $axis = $matchingAxes[0]
    if ($axis.engineeringUnit -ne 'mm' -or $null -eq $axis.minimumPosition -or $null -eq $axis.maximumPosition -or [double]$axis.minimumPosition -gt 0 -or [double]$axis.maximumPosition -lt 1) { throw "轴单位或 0–1 mm 行程不符合样例：$axisName" }
    $channel = "$($axis.controllerId)/$($axis.axisNumber)"
    if ($taskChannels.ContainsKey($channel)) { throw "样例轴映射到相同通道：$channel" }
    $taskChannels[$channel] = $axisName
}
$taskInputNames = @('上料到位', '夹具夹紧到位', '夹具松开到位', '焊接完成', '压装完成', '相机触发反馈', '检测完成', '检测OK', '检测NG', '下料到位')
$taskOutputNames = @('夹具夹紧', '焊接启动', '压头动作', '相机触发', '相机结果复位', '下料电磁阀')
# 示例按逻辑名称访问 IO，物理通道由设备配置决定，不强制旧版编号。
foreach ($direction in @(@{ Items = $taskIo.digitalInputs; Names = $taskInputNames }, @{ Items = $taskIo.digitalOutputs; Names = $taskOutputNames })) {
    $items = @($direction.Items)
    foreach ($name in $direction.Names) {
        $matched = @($items | Where-Object { $_.name -eq $name })
        if ($matched.Count -ne 1) { throw "IO 映射缺失或逻辑名称重复：$name" }
        $ioChannel = 0
        if (-not [int]::TryParse([string]$matched[0].channel, [ref]$ioChannel) -or $ioChannel -lt 0 -or $ioChannel -gt 4095 -or $matched[0].activeHigh -ne $true -or [string]::IsNullOrWhiteSpace([string]$matched[0].controllerId)) { throw "IO 映射与样例不符：$name；通道须为 0–4095 且高有效。" }
        $duplicate = @($items | Where-Object { $_.controllerId -eq $matched[0].controllerId -and $_.channel -eq $matched[0].channel })
        if ($duplicate.Count -ne 1) { throw "IO 物理通道存在重复映射：$name" }
    }
}
$taskControllerIds = @(@($taskAxes.axes | Where-Object { $_.name -in @('Load_X','Weld_Z','Press_Z','Camera_X','Unload_X') }) + @($taskIo.digitalInputs | Where-Object { $_.name -in $taskInputNames }) + @($taskIo.digitalOutputs | Where-Object { $_.name -in $taskOutputNames }) | ForEach-Object { $_.controllerId } | Select-Object -Unique)
if ($taskControllerIds.Count -ne 1) { throw '样例轴和 IO 必须映射到同一张控制器。' }

$taskAddVariables = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Variables.additions.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($addition in $taskAddVariables.variables) {
    $existing = @($taskVariables.variables | Where-Object { $_.name -eq $addition.name })
    if ($existing.Count -gt 1) { throw "设备变量名称重复：$($addition.name)" }
    if ($existing.Count -ne 0) { throw "设备已存在同名变量：$($addition.name)；拒绝复用或覆盖。" }
    $taskVariables.variables = @($taskVariables.variables) + @($addition)
}
$taskAddAlarms = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Alarms.additions.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($addition in $taskAddAlarms.alarms) {
    $existing = @($taskAlarms.alarms | Where-Object { $_.code -eq $addition.code })
    if ($existing.Count -gt 1) { throw "报警代码重复：$($addition.code)" }
    if ($existing.Count -eq 1 -and ($existing[0].kind -ne $addition.kind -or $existing[0].message -ne $addition.message)) { throw "报警代码与样例冲突：$($addition.code)" }
    if ($existing.Count -eq 0) { $taskAlarms.alarms = @($taskAlarms.alarms) + @($addition) }
}
foreach ($required in @(@('DEMO_SENSOR_TIMEOUT','StopMachine'),@('FOUR_HANDSHAKE_TIMEOUT','StopMachine'))) {
    $existing = @($taskAlarms.alarms | Where-Object { $_.code -eq $required[0] -and $_.kind -eq $required[1] })
    if ($existing.Count -ne 1) { throw "需要已有报警及正确类别：$($required[0])" }
}

$taskNames = @('ClipInit','ClipScan','ClipWeld','ClipPress','ClipInspect')
$taskFilesDirectory = Join-Path $taskDirectory 'Tasks'
if (-not (Test-Path -LiteralPath $taskFilesDirectory -PathType Container)) { throw '设备 Tasks 目录不存在。' }
foreach ($existingFile in @(Get-ChildItem -LiteralPath $taskFilesDirectory -Filter '*.json' -File)) {
    $existingTask = Get-Content -LiteralPath $existingFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($existingTask.taskId -in $taskNames) { throw "已有相同任务标识：$($existingTask.taskId)；拒绝覆盖或替换。" }
}
foreach ($taskName in $taskNames) {
    if (Test-Path -LiteralPath (Join-Path $taskFilesDirectory ($taskName + '.json'))) { throw "已有同名任务文件：$taskName" }
}

# 安装后使用框架自身校验器验证，失败时回滚；不会修改任务草稿。
$taskRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$taskCheck = Join-Path $taskRepoRoot 'tools\MotionControl.TaskEngine.Checks\bin\Release\net48\MotionControl.TaskEngine.Checks.exe'
if (-not (Test-Path -LiteralPath $taskCheck -PathType Leaf)) { throw '请先构建 MotionControl.TaskEngine.Checks 的 Release。' }

$taskDraft = Join-Path $taskDirectory 'Drafts\TaskWorkspace.json'
$draftHash = Get-TaskHash $taskDraft
$targetPaths = @('Parameters\TaskMachine.json','Parameters\DeviceVariables.json','Parameters\AlarmCatalog.json')
$originalHashes = @{}
foreach ($relative in $targetPaths) { $originalHashes[$relative] = Get-TaskHash (Join-Path $taskDirectory $relative) }
$taskNewMachine = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'TaskMachine.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskNewMachine.revision = [int]$taskMachine.revision + 1
$backupDirectory = Join-Path $taskDirectory ('Backups\ClipWelding-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backupDirectory -ErrorAction Stop | Out-Null
$staged = @{}
foreach ($item in @(@('Parameters\TaskMachine.json',$taskNewMachine),@('Parameters\DeviceVariables.json',$taskVariables),@('Parameters\AlarmCatalog.json',$taskAlarms))) {
    $relative = $item[0]; $filename = Split-Path -Leaf $relative
    $backup = Join-Path $backupDirectory $filename
    $candidate = Join-Path $backupDirectory ($filename + '.new')
    Copy-Item -LiteralPath (Join-Path $taskDirectory $relative) -Destination $backup
    $item[1] | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath $candidate -Encoding UTF8
    $staged[$relative] = $candidate
}
foreach ($taskName in $taskNames) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot ($taskName + '.json')) -Destination (Join-Path $backupDirectory ($taskName + '.new')) }

$changed = [Collections.Generic.List[string]]::new()
$added = [Collections.Generic.List[string]]::new()
try {
    Assert-HostStopped
    if ((Get-TaskHash $taskDraft) -ne $draftHash) { throw '任务草稿在准备期间发生变化，拒绝写入。' }
    foreach ($relative in $targetPaths) {
        if ((Get-TaskHash (Join-Path $taskDirectory $relative)) -ne $originalHashes[$relative]) { throw "配置在准备期间发生变化：$relative" }
    }
    # 各文件先写到同一磁盘的备份目录，再逐个替换；中途失败按记录恢复。
    foreach ($taskName in $taskNames) {
        $destination = Join-Path $taskFilesDirectory ($taskName + '.json')
        [IO.File]::Move((Join-Path $backupDirectory ($taskName + '.new')), $destination)
        $added.Add($destination)
    }
    foreach ($relative in $targetPaths) {
        $destination = Join-Path $taskDirectory $relative
        $changed.Add($relative)
        $replaceBackup = Join-Path $backupDirectory ((Split-Path -Leaf $relative) + '.replace-backup')
        [IO.File]::Replace($staged[$relative], $destination, $replaceBackup)
    }
    & $taskCheck --validate-machine $taskRoot $taskMachineId
    if ($LASTEXITCODE -ne 0) { throw '安装后的设备清单校验失败。' }
    if ((Get-TaskHash $taskDraft) -ne $draftHash) { throw '任务草稿在安装期间发生变化；拒绝保留本次安装。' }
    Write-Host "夹具焊接样例任务已安装。原文件备份：$backupDirectory"
    Write-Host '任务草稿未修改；草稿与运行文件不同步时，请在任务配置页按提示处理。'
}
catch {
    $rollbackErrors = [Collections.Generic.List[string]]::new()
    foreach ($relative in $changed) {
        try { Copy-Item -LiteralPath (Join-Path $backupDirectory (Split-Path -Leaf $relative)) -Destination (Join-Path $taskDirectory $relative) -Force }
        catch { $rollbackErrors.Add("恢复 $relative 失败：$($_.Exception.Message)") }
    }
    foreach ($file in $added) {
        try { Remove-Item -LiteralPath $file -Force }
        catch { $rollbackErrors.Add("移除新增文件 $file 失败：$($_.Exception.Message)") }
    }
    if ($rollbackErrors.Count -ne 0) { throw "安装失败：$($_.Exception.Message)；回滚不完整：$($rollbackErrors -join '；')。备份：$backupDirectory" }
    throw "安装失败并已恢复原配置：$($_.Exception.Message)。备份：$backupDirectory"
}
