param([int]$Port = 19577, [string]$ControllerExe = '')

$ErrorActionPreference = 'Stop'
$exePath = if ($ControllerExe) { $ControllerExe } else { Join-Path $PSScriptRoot 'bin\Release\net48\MotionControl.VirtualController.exe' }
if (-not (Test-Path -LiteralPath $exePath)) { throw '请先构建软件控制器。' }

# 在独立控制器本机端口发送一条请求，保留拒绝响应。
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

# 自动续期，并给运动类命令附加本次会话。
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

# 在有限时间内等物料输入，不用固定延时假定已经上料。
function Wait-Material([bool]$Expected) {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ((Send-Ok @{command='readInput'; channel=4}).value -eq $Expected) { return }
        Start-Sleep -Milliseconds 25
    }
    throw "物料输入未在限定时间变为 $Expected。"
}

# 等待轴停止，返回状态以检查具体位置。
function Wait-AxisStop {
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        $state = (Send-Ok @{command='readAxis'; axis=0}).axisState
        if (-not $state.moving) { return $state }
        Start-Sleep -Milliseconds 25
    }
    throw '上料轴未在限定时间内停止。'
}

# 模拟已到位物料被夹具取走；下一件必须重新满足上料条件。
function Take-Material {
    if (-not (Send-Ok @{command='readInput'; channel=4}).value) { throw '没有待取走的物料。' }
    Send-Ok @{command='writeOutput'; channel=12; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=12; value=$false} | Out-Null
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '物料取走后输入没有复位。' }
}

# 仅启动并清理当前检查创建的控制器进程，不接管正在使用的端口。
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

    if ((Send-Request @{command='configureFeeder'; enabled=$true; inputChannel=4; outputChannel=12; feedDelayMs=300; emptyDelayMs=500; safeAxis=0; safePosition=0}).ok) { throw '不完整安全位参数不应接受。' }
    if ((Send-Request @{command='configureFeeder'; enabled=$true; inputChannel=4; outputChannel=12; feedDelayMs=300; emptyDelayMs=500; safeAxis=0; safePosition=0; safeTolerance=0}).ok) { throw '零容差不应接受。' }
    Send-Ok @{command='configureFeeder'; enabled=$true; inputChannel=4; outputChannel=12; feedDelayMs=300; emptyDelayMs=500; safeAxis=0; safePosition=0; safeTolerance=0.01} | Out-Null
    Start-Sleep -Milliseconds 400
    $state = (Send-Ok @{command='readFeeder'}).feederState
    if ($state.inputValue -or -not $state.feedPending -or $state.gateSatisfied -or $state.delayCounting) { throw '未使能、未回零时错误开始供料。' }
    if ((Send-Request @{command='setInput'; channel=4; value=$true}).ok) { throw '安全门禁控制的供料输入不应允许直接覆盖。' }
    Send-Ok @{command='servo'; axis=0; value=$true} | Out-Null
    Send-Ok @{command='home'; axis=0} | Out-Null
    Start-Sleep -Milliseconds 100
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '首次上料没有等待完整安全位延时。' }
    Wait-Material $true

    # 夹具在工作位释放后，轴移动、途中停止和远离安全位均不得生成下一件。
    Send-Ok @{command='writeOutput'; channel=12; value=$true} | Out-Null
    Send-Ok @{command='move'; axis=0; position=1; speed=1} | Out-Null
    Start-Sleep -Milliseconds 100
    Send-Ok @{command='writeOutput'; channel=12; value=$false} | Out-Null
    Start-Sleep -Milliseconds 180
    Send-Ok @{command='stop'; axis=0} | Out-Null
    Start-Sleep -Milliseconds 650
    $state = (Send-Ok @{command='readFeeder'}).feederState
    if ($state.inputValue -or -not $state.feedPending -or $state.gateSatisfied -or $state.delayCounting) { throw '轴途中暂停且离开安全位时错误供料。' }
    Send-Ok @{command='move'; axis=0; position=1; speed=5} | Out-Null
    $away = Wait-AxisStop
    Start-Sleep -Milliseconds 150
    if ((Send-Ok @{command='readInput'; channel=4}).value -or $away.position -lt 0.99) { throw '轴停止在工作位时错误供料。' }
    Send-Ok @{command='move'; axis=0; position=0; speed=2} | Out-Null
    Start-Sleep -Milliseconds 200
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '轴返回途中错误供料。' }
    $returnedAxis = Wait-AxisStop
    if ([Math]::Abs($returnedAxis.position) -gt 0.01) { throw '轴没有返回安全位。' }
    Start-Sleep -Milliseconds 180
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '轴到安全位后没有等待完整供料延时。' }
    Wait-Material $true

    # 条件中途失效时，已累计延时必须丢弃，安全位恢复后重新完整计时。
    Take-Material
    Start-Sleep -Milliseconds 180
    Send-Ok @{command='move'; axis=0; position=1; speed=5} | Out-Null
    $null = Wait-AxisStop
    Send-Ok @{command='move'; axis=0; position=0; speed=5} | Out-Null
    $null = Wait-AxisStop
    Start-Sleep -Milliseconds 200
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '安全条件中断后复用了旧的供料延时。' }
    Wait-Material $true

    # 缺料、轴报警和会话释放均会废止当前计时；解除后重新等待。
    Take-Material
    Start-Sleep -Milliseconds 100
    Send-Ok @{command='setFeederFault'; value=$true} | Out-Null
    Start-Sleep -Milliseconds 650
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '缺料故障期间错误供料。' }
    Send-Ok @{command='setFeederFault'; value=$false} | Out-Null
    Start-Sleep -Milliseconds 100
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '缺料解除后没有重新延时。' }
    Wait-Material $true
    Take-Material
    Send-Ok @{command='setAxisAlarm'; axis=0; value=$true} | Out-Null
    Start-Sleep -Milliseconds 650
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '轴报警期间错误供料。' }
    Send-Ok @{command='resetAlarm'; axis=0} | Out-Null
    Start-Sleep -Milliseconds 100
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '轴报警复位后没有重新延时。' }
    Wait-Material $true
    Take-Material
    Start-Sleep -Milliseconds 100
    Send-Ok @{command='releaseLease'; leaseId=$script:LeaseId} | Out-Null
    $script:LeaseId = $null
    Start-Sleep -Milliseconds 650
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '会话释放后错误供料。' }
    $script:LeaseId = (Send-Ok @{command='acquireLease'}).leaseId
    Start-Sleep -Milliseconds 150
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '会话恢复后没有重新延时。' }
    Wait-Material $true
    Take-Material
    Send-Ok @{command='configureFeeder'; enabled=$false} | Out-Null
    Start-Sleep -Milliseconds 650
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '停止规则后旧供料计时仍生效。' }

    # 显式 CLI 参数及旧无门禁模式均继续可用。
    $command = Join-Path $PSScriptRoot 'ControllerCommand.ps1'
    $ErrorActionPreference = 'Continue'
    $null = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Port $Port -Action StartFeeder -InputChannel 4 -OutputChannel 12 -FeederSafeAxis 0 2>&1 | Out-String
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -eq 0) { throw '缺少安全位置和容差时 CLI 没有拒绝。' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Port $Port -Action StartFeeder -InputChannel 4 -OutputChannel 12 -FeedDelayMs 300 -EmptyDelayMs 500 -FeederSafeAxis 0 -FeederSafePosition 0 -FeederSafeTolerance 0.01 | Out-Null
    if ($LASTEXITCODE -ne 0 -or (Send-Ok @{command='readFeeder'}).feederState.safeAxis -ne 0) { throw 'CLI 未正确配置安全位门禁。' }
    Send-Ok @{command='configureFeeder'; enabled=$false} | Out-Null
    Send-Ok @{command='releaseLease'; leaseId=$script:LeaseId} | Out-Null
    $script:LeaseId = $null
    Send-Ok @{command='configureFeeder'; enabled=$true; inputChannel=4; outputChannel=12; feedDelayMs=100; emptyDelayMs=100} | Out-Null
    Wait-Material $true
    if ($null -ne (Send-Ok @{command='readFeeder'}).feederState.safeAxis) { throw '旧模式错误启用了安全位门禁。' }
    Send-Ok @{command='configureFeeder'; enabled=$false} | Out-Null
    Write-Host '上料安全位门禁：未回零、途中暂停、返回、连续延时、缺料、轴报警、会话、停用和旧模式检查通过。'
}
finally { if (-not $process.HasExited) { Stop-Process -Id $process.Id } }
