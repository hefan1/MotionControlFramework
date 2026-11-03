param([int]$Port = 19645, [string]$ControllerExe = '', [switch]$FaultOnly, [switch]$ConflictOnly)
$ErrorActionPreference = 'Stop'
$exePath = if ($ControllerExe) { $ControllerExe } else { Join-Path $PSScriptRoot 'bin\Release\net48\MotionControl.VirtualController.exe' }
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw '请先构建软件控制器。' }

# 只连接本次独立控制器，保留失败响应供故障验证。
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
        if ($null -eq $line) { throw '控制器没有返回结果。' }
        return $line | ConvertFrom-Json
    } finally { $client.Dispose() }
}

# 测试运动命令带本次租约，任何被拒绝的请求立即失败。
function Send-Ok([hashtable]$Request) {
    if ($script:LeaseId) {
        if (-not (Send-Request @{command='heartbeat'; leaseId=$script:LeaseId}).ok) { throw '测试租约已失效。' }
        if ($Request.command -in @('servo','home','move','stop','writeOutput')) { $Request.leaseId = $script:LeaseId }
    }
    $response = Send-Request $Request
    if (-not $response.ok) { throw "测试请求 $($Request.command) 被拒绝：$($response.error)" }
    return $response
}

# 等待模型轴停稳并检查实际反馈位置。
function Wait-Axis([int]$Axis, [double]$Target) {
    for ($i=0; $i -lt 100; $i++) {
        $state = (Send-Ok @{command='readAxis'; axis=$Axis}).axisState
        if (-not $state.moving -and [Math]::Abs([double]$state.position - $Target) -lt 0.01) { return }
        Start-Sleep -Milliseconds 20
    }
    throw "轴 $Axis 未到达 $Target；当前：$($state | ConvertTo-Json -Compress)。"
}

# 等待设备模型明确报告指定交接阶段。
function Wait-Stage([string]$Stage) {
    for ($i=0; $i -lt 100; $i++) {
        $state = (Send-Ok @{command='readRotaryClearancePlant'}).clearancePlant
        if ($state.fault) { throw "交接模型故障：$($state.fault)" }
        if ($state.stage -eq $Stage) { return }
        Start-Sleep -Milliseconds 20
    }
    throw "交接未到阶段 $Stage。"
}

# 校验两路独立物料输入的实际电平。
function Assert-Material([string]$Name, [bool]$Present, [bool]$Empty) {
    $state = (Send-Ok @{command='readMaterialSensors'; name=$Name}).materialState
    if ($state.present -ne $Present -or $state.empty -ne $Empty) { throw "物料状态不符：$Name。" }
}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $listener.Start() } catch { throw "测试端口 $Port 已占用。" } finally { $listener.Stop() }
$process = Start-Process -FilePath $exePath -ArgumentList $Port -WindowStyle Hidden -PassThru
try {
    $connected = $false
    for ($i=0; $i -lt 50; $i++) {
        if ($process.HasExited) { throw '测试控制器启动后退出。' }
        try { $identity = Send-Ok @{command='ping'}; $connected=$true; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $connected -or $identity.controllerType -ne 'MotionControl.VirtualController' -or $identity.protocolVersion -ne 2) { throw '控制器身份不符。' }
    $script:LeaseId = (Send-Ok @{command='acquireLease'}).leaseId
    foreach ($item in @(
        @{name='A夹位'; present=69; empty=70}, @{name='B夹位'; present=71; empty=72},
        @{name='缓存位'; present=73; empty=74}, @{name='下料位'; present=76; empty=77})) {
        Send-Ok @{command='configureMaterialSensors'; name=$item.name; presentInputChannel=$item.present; emptyInputChannel=$item.empty} | Out-Null
        Send-Ok @{command='setMaterialSensorState'; name=$item.name; state='empty'} | Out-Null
    }
    Send-Ok @{command='setMaterialSensorState'; name='A夹位'; state='present'} | Out-Null
    foreach ($fixture in @(
        @{output=41; closed=41; open=61}, @{output=42; closed=42; open=62}, @{output=51; closed=51; open=75})) {
        Send-Ok @{command='configureActuator'; outputChannel=$fixture.output; clampInputChannel=$fixture.closed; releaseInputChannel=$fixture.open; delayMs=25} | Out-Null
    }
    foreach ($channel in @(48,49)) { Send-Ok @{command='configureFeedback'; outputChannel=$channel; inputChannel=$channel; delayMs=25; activeHigh=$true} | Out-Null }
    Send-Ok @{command='setInput'; channel=67; value=$true} | Out-Null
    foreach ($axis in @(0,1,2,3,5,6,7,8,9,10,11,12)) {
        $base = 100 + 3 * $axis
        $bound = if ($axis -eq 11) { 100 } else { 10 }
        Send-Ok @{command='configureAxisSensors'; axis=$axis; originInputChannel=$base; negativeInputChannel=($base+1); positiveInputChannel=($base+2); originPosition=0; originHalfWidth=0.05; negativeLimit=(-$bound); positiveLimit=$bound} | Out-Null
        Send-Ok @{command='servo'; axis=$axis; value=$true} | Out-Null
        Send-Ok @{command='home'; axis=$axis} | Out-Null
        Wait-Axis $axis 0
    }
    Send-Ok @{command='configureRotaryClearancePlant'} | Out-Null
    if ((Send-Request @{command='removeActuator'; outputChannel=41}).ok -or
        (Send-Request @{command='removeFeedback'; inputChannel=49}).ok -or
        (Send-Request @{command='setInput'; channel=41; value=$true}).ok) {
        throw '已启用交接模型时，不应允许移除或直接篡改依赖反馈。'
    }
    if ($FaultOnly) {
        Send-Ok @{command='setCommandFault'; targetCommand='readInput'; targetIndex=69; mode='reject'; delayMs=0} | Out-Null
        $faulted = (Send-Ok @{command='readRotaryClearancePlant'}).clearancePlant
        if (-not $faulted.fault -or $faulted.stage -ne 'Idle') { throw '读取故障未锁存并保持交接状态。' }
        if ((Send-Request @{command='readInput'; channel=69}).ok) { throw '输入读取故障未返回失败。' }
        Send-Ok @{command='clearCommandFault'; targetCommand='readInput'; targetIndex=69} | Out-Null
        if (-not (Send-Ok @{command='readRotaryClearancePlant'}).clearancePlant.fault) { throw '读取恢复后故障不应自动解除。' }
        Assert-Material 'A夹位' $true $false
        Assert-Material '下料位' $false $true
        Write-Host '清料交接模型：读取故障锁存，恢复通信后不自动迁移。'
        return
    }
    Send-Ok @{command='writeOutput'; channel=41; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=51; value=$true} | Out-Null
    Start-Sleep -Milliseconds 50
    Send-Ok @{command='move'; axis=11; position=90; speed=900} | Out-Null
    Wait-Axis 11 90
    Assert-Material 'A夹位' $true $false
    Assert-Material '下料位' $false $true
    Send-Ok @{command='setMaterialSensorState'; name='下料位'; state='unknown'} | Out-Null
    Send-Ok @{command='move'; axis=0; position=1; speed=2} | Out-Null
    Start-Sleep -Milliseconds 100
    Assert-Material '下料位' $false $false
    Wait-Axis 0 1
    if ((Send-Ok @{command='readRotaryClearancePlant'}).clearancePlant.stage -ne 'Idle') { throw '承接占料未知时不应交接。' }
    Assert-Material 'A夹位' $true $false
    Send-Ok @{command='setMaterialSensorState'; name='下料位'; state='empty'} | Out-Null
    Wait-Stage 'Shared'
    Assert-Material 'A夹位' $true $false
    Assert-Material '下料位' $true $false
    if ($ConflictOnly) {
        Send-Ok @{command='setMaterialSensorState'; name='B夹位'; state='conflict'} | Out-Null
        Send-Ok @{command='writeOutput'; channel=41; value=$false} | Out-Null
        Start-Sleep -Milliseconds 60
        $faulted = (Send-Ok @{command='readRotaryClearancePlant'}).clearancePlant
        if (-not $faulted.fault -or $faulted.stage -ne 'Shared') { throw '交接中物料反馈冲突未锁存。' }
        Assert-Material 'A夹位' $true $false
        Assert-Material '下料位' $true $false
        Write-Host '清料交接模型：交接中反馈冲突锁存，保留两侧物料状态。'
        return
    }
    Send-Ok @{command='writeOutput'; channel=41; value=$false} | Out-Null
    Wait-Stage 'Transit'
    Assert-Material 'A夹位' $false $true
    Send-Ok @{command='move'; axis=0; position=0; speed=10} | Out-Null
    Wait-Axis 0 0
    Send-Ok @{command='writeOutput'; channel=49; value=$true} | Out-Null
    Start-Sleep -Milliseconds 50
    Send-Ok @{command='writeOutput'; channel=51; value=$false} | Out-Null
    Wait-Stage 'Idle'
    Assert-Material '下料位' $false $true
    Assert-Material '缓存位' $true $false
    Send-Ok @{command='writeOutput'; channel=49; value=$false} | Out-Null
    foreach ($axis in @(8,9,10)) {
        $target = if ($axis -eq 10) { 1 } else { 2 }
        Send-Ok @{command='move'; axis=$axis; position=$target; speed=20} | Out-Null
        Wait-Axis $axis $target
    }
    Send-Ok @{command='writeOutput'; channel=48; value=$true} | Out-Null
    for ($i=0; $i -lt 50; $i++) {
        if ((Send-Ok @{command='readMaterialSensors'; name='缓存位'}).materialState.empty) { break }
        Start-Sleep -Milliseconds 20
    }
    Assert-Material '缓存位' $false $true
    Send-Ok @{command='writeOutput'; channel=48; value=$false} | Out-Null
    Send-Ok @{command='releaseLease'; leaseId=$script:LeaseId} | Out-Null
    $script:LeaseId=$null
    Write-Host '十三轴样例清料交接模型：位置、共同夹持、下料、缓存及剔除的独立反馈链通过。'
} finally { if (-not $process.HasExited) { Stop-Process -Id $process.Id } }
