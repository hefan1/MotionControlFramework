param(
    [string]$ConfigurationRoot = (Join-Path $env:LOCALAPPDATA 'MotionControlFramework\Configurations'),
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$taskRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskControllerExe = Join-Path $taskRepository 'tools\MotionControl.VirtualController\bin\Release\net48\MotionControl.VirtualController.exe'
$taskHostExe = Join-Path $taskRepository 'src\MotionControl.Host.Wpf\bin\Release\net48\MotionControl.Host.Wpf.exe'
$taskCheckExe = Join-Path $taskRepository 'tools\MotionControl.TaskEngine.Checks\bin\Release\net48\MotionControl.TaskEngine.Checks.exe'
$taskRoot = (Resolve-Path -LiteralPath $ConfigurationRoot).Path
$taskDefaultRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'MotionControlFramework\Configurations'))
$taskMachineDirectory = Join-Path $taskRoot 'Machine01'
$taskStartedController = $null
$taskStartedHost = $null
$taskPort = 18520
$taskLease = $null
. (Join-Path $PSScriptRoot 'ScenarioIoMapping.ps1')

# 从指定的 Machine01 读取配置；找不到或损坏时明确失败。
function Read-ScenarioConfig([string]$RelativePath) {
    $taskPath = Join-Path $taskMachineDirectory $RelativePath
    if (-not (Test-Path -LiteralPath $taskPath -PathType Leaf)) { throw "缺少配置文件：$taskPath" }
    try { return Get-Content -LiteralPath $taskPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "配置文件无法解析：$taskPath。$($_.Exception.Message)" }
}

# 只向本机控制器发送一条命令，保留拒绝信息。
function Send-ScenarioRequest([hashtable]$Request, [switch]$AllowRejected) {
    $taskClient = [Net.Sockets.TcpClient]::new()
    try {
        $taskClient.ReceiveTimeout = 3000
        $taskClient.SendTimeout = 3000
        $taskClient.Connect('127.0.0.1', $taskPort)
        $taskEncoding = [Text.UTF8Encoding]::new($false)
        $taskStream = $taskClient.GetStream()
        $taskWriter = [IO.StreamWriter]::new($taskStream, $taskEncoding)
        $taskReader = [IO.StreamReader]::new($taskStream, $taskEncoding)
        $taskWriter.AutoFlush = $true
        $taskWriter.WriteLine(($Request | ConvertTo-Json -Compress))
        $taskLine = $taskReader.ReadLine()
        if ($null -eq $taskLine) { throw '控制器没有返回结果。' }
        $taskResponse = $taskLine | ConvertFrom-Json
        if (-not $taskResponse.ok -and -not $AllowRejected) { throw "控制器拒绝 $($Request.command)：$($taskResponse.error)" }
        return $taskResponse
    }
    finally { $taskClient.Dispose() }
}

# 检查端口是否已经由任何进程监听，避免接管已有测试会话。
function Get-ScenarioListener {
    return @(Get-NetTCPConnection -LocalPort $taskPort -ErrorAction SilentlyContinue | Where-Object { $_.RemotePort -eq 0 })
}

# 运行前后都不关闭用户已有 Host。
function Assert-ScenarioHostStopped {
    if (@(Get-Process -Name 'MotionControl.Host.Wpf' -ErrorAction SilentlyContinue).Count -gt 0) {
        throw '检测到上位机已运行；请先安全停止并正常关闭，脚本不会接管或重复启动。'
    }
}

try {
    if (-not (Test-Path -LiteralPath $taskMachineDirectory -PathType Container)) { throw '指定配置根目录下没有 Machine01。' }
    if (-not $PrepareOnly -and -not [string]::Equals($taskRoot, $taskDefaultRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw '正式 Host 使用本机默认配置目录；其他目录只能用于 PrepareOnly 检查。'
    }
    foreach ($taskExe in @($taskControllerExe, $taskCheckExe)) {
        if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw "缺少 Release 程序，请先构建：$taskExe" }
    }
    if (-not $PrepareOnly -and -not (Test-Path -LiteralPath $taskHostExe -PathType Leaf)) { throw '缺少正式 Host Release 程序。' }
    Assert-ScenarioHostStopped

    $taskBackend = Read-ScenarioConfig 'Parameters\ControllerBackend.json'
    if ($taskBackend.backend -ne 'Virtual') { throw 'Machine01 当前不是 Virtual 后端；脚本不会更改后端。' }
    if ($null -ne $taskBackend.port) {
        $parsed = 0
        if (-not [int]::TryParse([string]$taskBackend.port, [ref]$parsed) -or $parsed -lt 1 -or $parsed -gt 65535) { throw '软件控制器端口无效。' }
        $taskPort = $parsed
    }
    $taskEquipment = Read-ScenarioConfig 'Parameters\Equipment.json'
    if ($taskEquipment.executionOwner -ne 'TaskEngine') { throw '当前不是任务列表入口；脚本不会切换运行方式。' }
    $taskMachine = Read-ScenarioConfig 'Parameters\TaskMachine.json'
    if ($taskMachine.machineId -ne 'Machine01' -or $taskMachine.initializationTaskId -ne 'FourInit') { throw '当前设备清单不是四工位示例。' }
    foreach ($taskExpected in @(@{ Id='Load'; Task='FourLoad'; Mode='Cyclic' }, @{ Id='Press'; Task='FourPress'; Mode='OnRequest'; Signal='Press' }, @{ Id='Inspect'; Task='FourInspect'; Mode='OnRequest'; Signal='Inspect' }, @{ Id='Unload'; Task='FourUnload'; Mode='OnRequest'; Signal='Unload' })) {
        $taskMatches = @($taskMachine.stations | Where-Object { $_.stationId -eq $taskExpected.Id -and $_.taskId -eq $taskExpected.Task -and $_.mode -eq $taskExpected.Mode -and $_.enabled -eq $true })
        if ($taskMatches.Count -ne 1) { throw "工位清单与四工位示例不符：$($taskExpected.Id)" }
        if ($taskExpected.Signal -and ($taskMatches[0].requestVariable -ne ($taskExpected.Signal + '.Request') -or $taskMatches[0].completedVariable -ne ($taskExpected.Signal + '.Complete'))) { throw "工位交接变量与四工位样例不符：$($taskExpected.Id)" }
    }
    if (@($taskMachine.stations).Count -ne 4) { throw '当前任务清单不止四个工位，不会套用样例规则。' }
    $taskIo = Read-ScenarioConfig 'HardwareConfig\IoMapping.json'
    $taskCards = Read-ScenarioConfig 'HardwareConfig\MotionCards.json'
    $taskCardList = @($taskCards.motionCards)
    if ($taskCardList.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$taskCardList[0].controllerId)) { throw '四工位样例要求一张明确配置的控制器。' }
    $taskInputs = @{}
    foreach ($name in @('上料到位', '压装完成', '检测完成', '下料到位')) {
        $taskInputs[$name] = Get-ScenarioIoChannel -Items @($taskIo.digitalInputs) -Name $name -ControllerId $taskCardList[0].controllerId
    }
    $taskOutputs = @{}
    foreach ($name in @('夹具夹紧', '压头动作', '相机触发', '下料电磁阀')) {
        $taskOutputs[$name] = Get-ScenarioIoChannel -Items @($taskIo.digitalOutputs) -Name $name -ControllerId $taskCardList[0].controllerId
    }
    $taskPairs = @(
        @{ Input=$taskInputs['压装完成']; Output=$taskOutputs['压头动作'] },
        @{ Input=$taskInputs['检测完成']; Output=$taskOutputs['相机触发'] },
        @{ Input=$taskInputs['下料到位']; Output=$taskOutputs['下料电磁阀'] }
    )
    & $taskCheckExe --validate-machine $taskRoot Machine01
    if ($LASTEXITCODE -ne 0) { throw '当前设备任务清单校验失败。' }
    Assert-ScenarioHostStopped
    if (@(Get-ScenarioListener).Count -ne 0) { throw "端口 $taskPort 已有进程监听；不会接管已有控制器或覆盖规则。" }

    $taskStartedController = Start-Process -FilePath $taskControllerExe -ArgumentList $taskPort -WorkingDirectory (Split-Path -Parent $taskControllerExe) -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($taskStartedController.HasExited) { throw '软件控制器启动后退出。' }
        $listeners = @(Get-ScenarioListener)
        if ($listeners.Count -gt 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) { throw "软件控制器没有在端口 $taskPort 就绪。" }
    $listeners = @(Get-ScenarioListener)
    if ($listeners.Count -ne 1 -or $listeners[0].OwningProcess -ne $taskStartedController.Id -or $listeners[0].LocalAddress -ne '127.0.0.1') { throw '端口监听进程不是本次启动的本机软件控制器。' }
    $identity = Send-ScenarioRequest @{command='ping'}
    if ($identity.controllerType -ne 'MotionControl.VirtualController' -or $identity.protocolVersion -ne 2) { throw '软件控制器身份或协议不匹配。' }
    $taskLease = Send-ScenarioRequest @{command='acquireLease'}
    if ([string]::IsNullOrWhiteSpace([string]$taskLease.leaseId)) { throw '控制器没有返回运动会话。' }
    Send-ScenarioRequest @{command='heartbeat'; leaseId=[string]$taskLease.leaseId} | Out-Null
    Assert-ScenarioHostStopped
    $feeder = (Send-ScenarioRequest @{command='readFeeder'}).feederState
    if ($null -eq $feeder -or $feeder.enabled) { throw '控制器已有上料规则或无法核对，不会覆盖。' }
    foreach ($channel in @($taskOutputs['夹具夹紧'], $taskOutputs['压头动作'], $taskOutputs['相机触发'], $taskOutputs['下料电磁阀'])) {
        $output = Send-ScenarioRequest @{command='readOutput'; channel=$channel}
        if ($output.value -ne $false) { throw "DO$channel 未明确关闭，不允许配置样例规则。" }
    }
    foreach ($pair in $taskPairs) {
        $feedback = Send-ScenarioRequest @{command='readFeedback'; inputChannel=$pair.Input} -AllowRejected
        if ($feedback.ok -or $feedback.error -ne '该输入没有配置输出反馈规则。') { throw "DI$($pair.Input) 已有反馈规则或状态无法确认，不会覆盖。" }
    }
    foreach ($pair in $taskPairs) {
        Send-ScenarioRequest @{command='configureFeedback'; inputChannel=$pair.Input; outputChannel=$pair.Output; delayMs=300; activeHigh=$true} | Out-Null
    }
    Send-ScenarioRequest @{command='configureFeeder'; enabled=$true; inputChannel=$taskInputs['上料到位']; outputChannel=$taskOutputs['夹具夹紧']; feedDelayMs=1000; emptyDelayMs=1500} | Out-Null
    $feeder = (Send-ScenarioRequest @{command='readFeeder'}).feederState
    if (-not $feeder.enabled -or $feeder.inputChannel -ne $taskInputs['上料到位'] -or $feeder.outputChannel -ne $taskOutputs['夹具夹紧']) { throw '上料规则读回不一致。' }
    foreach ($pair in $taskPairs) {
        $feedback = (Send-ScenarioRequest @{command='readFeedback'; inputChannel=$pair.Input}).feedbackState
        if ($feedback.inputChannel -ne $pair.Input -or $feedback.outputChannel -ne $pair.Output -or $feedback.stuck -eq $true) { throw "DI$($pair.Input) 反馈规则读回不一致。" }
    }
    Send-ScenarioRequest @{command='releaseLease'; leaseId=[string]$taskLease.leaseId} | Out-Null
    $taskLease = $null
    Write-Host "上料规则：DI$($taskInputs['上料到位'])，夹具命令 DO$($taskOutputs['夹具夹紧'])。"
    foreach ($pair in $taskPairs) { Write-Host "工位反馈规则：DO$($pair.Output) -> DI$($pair.Input)。" }
    if ($PrepareOnly) {
        Write-Host "四工位软件控制器已准备，进程号 $($taskStartedController.Id)，端口 $taskPort；未启动 Host。"
        return
    }
    Assert-ScenarioHostStopped
    $taskStartedHost = Start-Process -FilePath $taskHostExe -WorkingDirectory (Split-Path -Parent $taskHostExe) -PassThru
    Write-Host "已启动本次软件控制器（PID $($taskStartedController.Id)，端口 $taskPort）和正式上位机（PID $($taskStartedHost.Id)）。"
    Write-Host '请在上位机核对状态并手动使能、回零、复位与启动；本脚本不会自动发出这些命令。'
}
catch {
    [Console]::Error.WriteLine("四工位启动失败：$($_.Exception.Message)")
    if ($null -ne $taskStartedController -and -not $taskStartedController.HasExited -and $null -eq $taskStartedHost) {
        try {
            $owned = Get-Process -Id $taskStartedController.Id -ErrorAction Stop
            if (-not [string]::Equals($owned.Path, $taskControllerExe, [StringComparison]::OrdinalIgnoreCase)) { throw '进程路径已变化，未关闭。' }
            if (@(Get-Process -Name 'MotionControl.Host.Wpf' -ErrorAction SilentlyContinue).Count -gt 0) { throw '已有上位机运行，保留控制器供其检查。' }
            if ($null -eq $taskLease) { $taskLease = Send-ScenarioRequest @{command='acquireLease'} }
            if ([string]::IsNullOrWhiteSpace([string]$taskLease.leaseId)) { throw '无法确认独占会话，保留控制器。' }
            Send-ScenarioRequest @{command='heartbeat'; leaseId=[string]$taskLease.leaseId} | Out-Null
            Stop-Process -Id $owned.Id -ErrorAction Stop
            [Console]::Error.WriteLine("已关闭本次创建的软件控制器 PID $($owned.Id)。")
        }
        catch { [Console]::Error.WriteLine("本次创建的控制器未能自动关闭：$($_.Exception.Message)") }
    }
    exit 1
}
