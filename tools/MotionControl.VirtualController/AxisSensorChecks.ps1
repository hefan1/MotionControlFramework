param([int]$Port = 19576, [string]$ControllerExe = '')

$ErrorActionPreference = 'Stop'
$exePath = if ($ControllerExe) { $ControllerExe } else { Join-Path $PSScriptRoot 'bin\Release\net48\MotionControl.VirtualController.exe' }
if (-not (Test-Path -LiteralPath $exePath)) { throw '请先构建软件控制器。' }

# 向独立本机控制器发送一条请求，保留原始失败响应。
function Send-Request([hashtable]$Request) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.ReceiveTimeout = 3000
        $client.Connect('127.0.0.1', $Port)
        $stream = $client.GetStream()
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
        $writer.AutoFlush = $true
        $writer.WriteLine(($Request | ConvertTo-Json -Compress))
        $line = $reader.ReadLine()
        if ($null -eq $line) { throw '控制器未返回响应。' }
        return $line | ConvertFrom-Json
    }
    finally { $client.Dispose() }
}

# 自动保持会话；对运动指令附加已有租约。
function Send-Ok([hashtable]$Request) {
    if ($script:LeaseId) {
        $heartbeat = Send-Request @{command='heartbeat'; leaseId=$script:LeaseId}
        if (-not $heartbeat.ok) { throw "控制器会话已失效：$($heartbeat.error)" }
        if ($Request.command -in @('servo','home','move','jog','stop','resetAlarm','writeOutput')) { $Request.leaseId = $script:LeaseId }
    }
    $result = Send-Request $Request
    if (-not $result.ok) { throw "$($Request.command) 失败：$($result.error)" }
    return $result
}

# 只通过 readInput 等待感应电平，验证不需要先读轴状态才能推进轴。
function Wait-Input([int]$Channel, [bool]$Value) {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ((Send-Ok @{command='readInput'; channel=$Channel}).value -eq $Value) { return }
        Start-Sleep -Milliseconds 25
    }
    throw "DI$Channel 未变为 $Value。"
}

# 在限定时间内等指定轴停止，保留全部轴状态供断言。
function Wait-Stop([int]$Axis) {
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        $state = (Send-Ok @{command='readAxis'; axis=$Axis}).axisState
        if (-not $state.moving) { return $state }
        Start-Sleep -Milliseconds 25
    }
    throw "轴 $Axis 未在限定时间内停止。"
}

# 仅启动并关闭本检查自己的软件控制器，不接管已有端口。
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $listener.Start() } catch { throw "测试端口 $Port 已占用。" } finally { $listener.Stop() }
$process = Start-Process -FilePath $exePath -ArgumentList $Port -WindowStyle Hidden -PassThru
try {
    $connected = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($process.HasExited) { throw '软件控制器启动失败。' }
        try { $identity = Send-Ok @{command='ping'}; $connected = $true; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $connected -or $identity.controllerType -ne 'MotionControl.VirtualController' -or $identity.protocolVersion -ne 2) { throw '测试控制器身份不匹配。' }
    $script:LeaseId = (Send-Ok @{command='acquireLease'}).leaseId
    if (-not $script:LeaseId) { throw '无法获得测试会话。' }

    # 无模型时保留旧行为；显式配置一根具有独立原点和正负限位输入的轴。
    Send-Ok @{command='servo'; axis=1; value=$true} | Out-Null
    Send-Ok @{command='home'; axis=1} | Out-Null
    if (-not (Send-Ok @{command='readAxis'; axis=1}).axisState.homed) { throw '未配置模型的旧回零行为发生变化。' }
    Send-Ok @{command='configureAxisSensors'; axis=0; originInputChannel=101; negativeInputChannel=102; positiveInputChannel=103; originPosition=2; originHalfWidth=0.1; negativeLimit=-1; positiveLimit=4} | Out-Null
    if ((Send-Ok @{command='readInput'; channel=101}).value) { throw '未到原点时原点信号错误有效。' }
    if ((Send-Request @{command='setInput'; channel=101; value=$true}).ok) { throw '轴传感器输入不应允许手动覆盖。' }
    if ((Send-Request @{command='configureFeedback'; outputChannel=30; inputChannel=102; delayMs=10; activeHigh=$true}).ok) { throw '普通反馈占用了轴传感器输入。' }
    if ((Send-Request @{command='configureFeeder'; enabled=$true; outputChannel=30; inputChannel=103; feedDelayMs=10; emptyDelayMs=10}).ok) { throw '供料规则占用了轴传感器输入。' }
    if ((Send-Request @{command='configureActuator'; outputChannel=30; clampInputChannel=101; releaseInputChannel=110; delayMs=10}).ok) { throw '夹具占用了轴传感器输入。' }
    if ((Send-Request @{command='configureCamera'; triggerOutputChannel=30; acknowledgeInputChannel=101; completeInputChannel=110; processingDelayMs=10}).ok) { throw '相机占用了轴传感器输入。' }
    Send-Ok @{command='configureFeedback'; outputChannel=30; inputChannel=110; delayMs=10; activeHigh=$true} | Out-Null
    if ((Send-Request @{command='configureAxisSensors'; axis=2; originInputChannel=110; negativeInputChannel=111; positiveInputChannel=112; originPosition=1; originHalfWidth=0.1; negativeLimit=-1; positiveLimit=2}).ok) { throw '轴传感器覆盖了已有普通反馈输入。' }
    Send-Ok @{command='removeFeedback'; inputChannel=110} | Out-Null

    Send-Ok @{command='servo'; axis=0; value=$true} | Out-Null
    Send-Ok @{command='home'; axis=0} | Out-Null
    $moving = (Send-Ok @{command='readAxis'; axis=0}).axisState
    if (-not $moving.moving -or $moving.homed) { throw '搜索原点前不应已经回零或停止。' }
    Wait-Input 101 $true
    $homed = (Send-Ok @{command='readAxis'; axis=0}).axisState
    if (-not $homed.homed -or $homed.moving -or [Math]::Abs($homed.position) -gt 0.0001 -or -not $homed.originInputActive) { throw '原点到位后状态或逻辑坐标错误。' }
    $physical = (Send-Ok @{command='readAxisSensors'; axis=0}).axisSensors.physicalPosition
    if ([Math]::Abs($physical - 1.9) -gt 0.0001) { throw '回零没有在原点感应边沿停下。' }
    Send-Ok @{command='move'; axis=0; position=-1; speed=5} | Out-Null
    $stopped = Wait-Stop 0
    if ($stopped.alarmActive -or [Math]::Abs($stopped.position + 1) -gt 0.0001 -or (Send-Ok @{command='readInput'; channel=101}).value) { throw '逻辑定位或离开原点反馈错误。' }

    # 原点始终未触发时，搜索至限位并报警，不能仅因到达预定坐标就认为回零成功。
    Send-Ok @{command='setAxisSensorFault'; axis=0; sensor='origin'; mode='stuckOff'} | Out-Null
    Send-Ok @{command='home'; axis=0} | Out-Null
    Wait-Input 103 $true
    $failed = Wait-Stop 0
    if ($failed.homed -or -not $failed.alarmActive -or -not $failed.positiveLimitActive -or -not $failed.requiresHomingAfterLimitAlarm) { throw '原点未触发时未按正限位失败。' }
    Send-Ok @{command='setAxisSensorFault'; axis=0; sensor='origin'; mode='normal'} | Out-Null
    Send-Ok @{command='resetAlarm'; axis=0} | Out-Null
    Send-Ok @{command='home'; axis=0} | Out-Null
    Wait-Input 101 $true
    $homed = Wait-Stop 0
    if (-not $homed.homed -or $homed.alarmActive -or $homed.requiresHomingAfterLimitAlarm) { throw '修复原点后未能从限位向反方向回零。' }

    # 普通运动触发硬限位必须停止报警；停轴后允许人工处理而非虚报到位。
    Send-Ok @{command='jog'; axis=0; speed=10} | Out-Null
    Wait-Input 103 $true
    $limited = Wait-Stop 0
    if (-not $limited.alarmActive -or $limited.homed -or -not $limited.requiresHomingAfterLimitAlarm) { throw '正限位未停止并撤销坐标参考。' }
    Send-Ok @{command='resetAlarm'; axis=0} | Out-Null
    Send-Ok @{command='jog'; axis=0; speed=-5} | Out-Null
    Start-Sleep -Milliseconds 100
    Send-Ok @{command='stop'; axis=0} | Out-Null
    Send-Ok @{command='setAxisSensorFault'; axis=0; sensor='origin'; mode='stuckOn'} | Out-Null
    Send-Ok @{command='home'; axis=0} | Out-Null
    $stuck = Wait-Stop 0
    if (-not $stuck.alarmActive -or $stuck.homed) { throw '原点卡在有效时不应伪报回零。' }

    # 原点和限位同时失效时，搜索边界仍会明确报原点未找到，且不伪造限位信号。
    Send-Ok @{command='configureAxisSensors'; axis=2; originInputChannel=111; negativeInputChannel=112; positiveInputChannel=113; originPosition=1; originHalfWidth=0.1; negativeLimit=-1; positiveLimit=2} | Out-Null
    Send-Ok @{command='setAxisSensorFault'; axis=2; sensor='origin'; mode='stuckOff'} | Out-Null
    Send-Ok @{command='setAxisSensorFault'; axis=2; sensor='positive'; mode='stuckOff'} | Out-Null
    Send-Ok @{command='servo'; axis=2; value=$true} | Out-Null
    Send-Ok @{command='home'; axis=2} | Out-Null
    $missing = Wait-Stop 2
    if (-not $missing.alarmActive -or $missing.homed -or $missing.positiveLimitActive -or $missing.alarmMessage -notmatch '原点未触发') { throw '原点与限位均未触发时没有正确报告搜索失败。' }

    # 未完成回零的主动停止不能留下已回零状态。
    Send-Ok @{command='configureAxisSensors'; axis=3; originInputChannel=120; negativeInputChannel=121; positiveInputChannel=122; originPosition=3; originHalfWidth=0.1; negativeLimit=-1; positiveLimit=5} | Out-Null
    Send-Ok @{command='servo'; axis=3; value=$true} | Out-Null
    Send-Ok @{command='home'; axis=3} | Out-Null
    Send-Ok @{command='stop'; axis=3} | Out-Null
    $interrupted = (Send-Ok @{command='readAxis'; axis=3}).axisState
    if ($interrupted.moving -or $interrupted.homed) { throw '中断回零不应判定为完成。' }

    # 定位周期可能跨越目标：目标在限位内时不能因过冲计算伪报限位；目标越限仍须报警。
    Send-Ok @{command='configureAxisSensors'; axis=5; originInputChannel=140; negativeInputChannel=141; positiveInputChannel=142; originPosition=0; originHalfWidth=0.1; negativeLimit=-100; positiveLimit=100} | Out-Null
    Send-Ok @{command='servo'; axis=5; value=$true} | Out-Null
    Send-Ok @{command='home'; axis=5} | Out-Null
    $null = Wait-Stop 5
    foreach ($target in @(90,-90)) {
        Send-Ok @{command='move'; axis=5; position=$target; speed=10000} | Out-Null
        Start-Sleep -Milliseconds 100
        $positioned = Wait-Stop 5
        if ($positioned.alarmActive -or [Math]::Abs($positioned.position - $target) -gt 0.001) { throw "高速定位 $target 时误报限位或未到目标。" }
    }
    Send-Ok @{command='move'; axis=5; position=110; speed=10000} | Out-Null
    Start-Sleep -Milliseconds 100
    $beyond = Wait-Stop 5
    if (-not $beyond.alarmActive -or -not $beyond.positiveLimitActive) { throw '目标越过正限位时没有报警。' }

    # 调试命令必须要求显式轴号和传感器参数，不能悄悄使用默认轴。
    $command = Join-Path $PSScriptRoot 'ControllerCommand.ps1'
    $ErrorActionPreference = 'Continue'
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Port $Port -Action AxisSensorsStatus 2>&1 | Out-String
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -eq 0 -or $output -notmatch '轴传感器命令') { throw '缺少轴号时调试命令没有明确拒绝。' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Port $Port -Action ConfigureAxisSensors -Axis 4 -AxisOriginInput 130 -AxisNegativeInput 131 -AxisPositiveInput 132 -AxisOriginPosition 1 -AxisOriginHalfWidth 0.1 -AxisNegativeLimit -1 -AxisPositiveLimit 2 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '轴传感器调试命令未成功配置模型。' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Port $Port -Action AxisSensorFault -Axis 4 -AxisSensor origin -AxisSensorMode stuckOff | Out-Null
    if ($LASTEXITCODE -ne 0 -or (Send-Ok @{command='readAxisSensors'; axis=4}).axisSensors.originFault -ne 'stuckOff') { throw '轴传感器故障命令未正确下发。' }
    Send-Ok @{command='releaseLease'; leaseId=$script:LeaseId} | Out-Null
    $script:LeaseId = $null
    Write-Host '轴传感器：原点搜索、输入独立刷新、限位、未触发/卡滞、停止及模型占用检查通过。'
}
finally { if (-not $process.HasExited) { Stop-Process -Id $process.Id } }
