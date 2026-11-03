param([int]$Port = 19575, [string]$ControllerExe = '')

$ErrorActionPreference = 'Stop'
$exePath = if ($ControllerExe) { $ControllerExe } else { Join-Path $PSScriptRoot 'bin\Release\net48\MotionControl.VirtualController.exe' }
if (-not (Test-Path -LiteralPath $exePath)) { throw '请先构建软件控制器。' }

# 向独立本机控制器发送一条请求，保留拒绝响应供检查。
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

# 自动续期并检查命令结果；只有写输出需要租约。
function Send-Ok([hashtable]$Request) {
    if ($script:LeaseId) {
        $heartbeat = Send-Request @{command='heartbeat'; leaseId=$script:LeaseId}
        if (-not $heartbeat.ok) { throw "控制器会话已失效：$($heartbeat.error)" }
        if ($Request.command -eq 'writeOutput') { $Request.leaseId = $script:LeaseId }
    }
    $result = Send-Request $Request
    if (-not $result.ok) { throw "$($Request.command) 失败：$($result.error)" }
    return $result
}

# 检查输入对及未完成状态，避免将运动中的两个低电平视为到位。
function Assert-Actuator([bool]$Clamped, [bool]$Released, [bool]$Pending) {
    $state = (Send-Ok @{command='readActuator'; outputChannel=21}).actuatorState
    if ($state.clampInputValue -ne $Clamped -or $state.releaseInputValue -ne $Released -or $state.pending -ne $Pending) {
        throw "夹具状态不符：夹紧=$($state.clampInputValue)，松开=$($state.releaseInputValue)，待完成=$($state.pending)。"
    }
    if ((Send-Ok @{command='readInput'; channel=101}).value -ne $Clamped -or (Send-Ok @{command='readInput'; channel=102}).value -ne $Released) { throw '规则状态与实际输入不一致。' }
}

# 在有限时间内等待夹具目标位置并验证互斥到位。
function Wait-Actuator([bool]$Clamped, [bool]$Released) {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $state = (Send-Ok @{command='readActuator'; outputChannel=21}).actuatorState
        if (-not $state.pending -and $state.clampInputValue -eq $Clamped -and $state.releaseInputValue -eq $Released) {
            Assert-Actuator $Clamped $Released $false
            return
        }
        Start-Sleep -Milliseconds 25
    }
    throw '夹具未在限定时间到达目标位置。'
}

# 只启动并关闭自己创建的独立进程，不接管已有端口。
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

    # 未配置规则时，输出不会自动改变同号输入。
    Send-Ok @{command='writeOutput'; channel=99; value=$true} | Out-Null
    if ((Send-Ok @{command='readInput'; channel=99}).value) { throw '未配置规则时输入被自动修改。' }
    Send-Ok @{command='writeOutput'; channel=99; value=$false} | Out-Null

    Send-Ok @{command='configureActuator'; outputChannel=21; clampInputChannel=101; releaseInputChannel=102; delayMs=300} | Out-Null
    Assert-Actuator $false $false $true
    Wait-Actuator $false $true
    # 其他规则不应获得被占用的输入；夹具也不应覆盖现有输入。
    if ((Send-Request @{command='configureFeedback'; outputChannel=20; inputChannel=101; delayMs=10; activeHigh=$true}).ok) { throw '普通反馈占用了夹具输入。' }
    if ((Send-Request @{command='configureFeeder'; enabled=$true; outputChannel=20; inputChannel=102; feedDelayMs=10; emptyDelayMs=10}).ok) { throw '供料占用了夹具输入。' }
    if ((Send-Request @{command='configureCamera'; triggerOutputChannel=20; acknowledgeInputChannel=102; completeInputChannel=105; processingDelayMs=10}).ok) { throw '相机占用了夹具输入。' }
    if ((Send-Request @{command='configureActuator'; outputChannel=22; clampInputChannel=101; releaseInputChannel=104; delayMs=10}).ok) { throw '两个夹具占用了同一输入。' }
    if ((Send-Request @{command='configureActuator'; outputChannel=22; clampInputChannel=103; releaseInputChannel=103; delayMs=10}).ok) { throw '夹紧和松开使用了同一输入。' }

    Send-Ok @{command='writeOutput'; channel=21; value=$true} | Out-Null
    Assert-Actuator $false $false $true
    # 夹具动作由设备执行；释放会话不会撤销已写出的电磁阀命令。
    Send-Ok @{command='releaseLease'; leaseId=$script:LeaseId} | Out-Null
    $script:LeaseId = $null
    Wait-Actuator $true $false
    $script:LeaseId = (Send-Ok @{command='acquireLease'}).leaseId
    # 快速反向时，第一动作的延迟完成不得覆盖第二动作。
    Send-Ok @{command='writeOutput'; channel=21; value=$false} | Out-Null
    Start-Sleep -Milliseconds 80
    Send-Ok @{command='writeOutput'; channel=21; value=$true} | Out-Null
    Start-Sleep -Milliseconds 240
    Assert-Actuator $false $false $true
    Wait-Actuator $true $false

    # 卡滞时命令切换清除旧到位；解除故障后重新经历完整延时。
    Send-Ok @{command='setActuatorFault'; outputChannel=21; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=21; value=$false} | Out-Null
    Start-Sleep -Milliseconds 380
    Assert-Actuator $false $false $true
    Send-Ok @{command='setActuatorFault'; outputChannel=21; value=$false} | Out-Null
    Assert-Actuator $false $false $true
    Wait-Actuator $false $true
    # 独立注入两个到位同时有效，解除后必须重新经过动作延时。
    Send-Ok @{command='setActuatorConflict'; outputChannel=21; value=$true} | Out-Null
    Assert-Actuator $true $true $true
    Send-Ok @{command='setActuatorConflict'; outputChannel=21; value=$false} | Out-Null
    Assert-Actuator $false $false $true
    Wait-Actuator $false $true
    Send-Ok @{command='writeOutput'; channel=21; value=$true} | Out-Null
    if ((Send-Request @{command='removeActuator'; outputChannel=21}).ok) { throw '输出仍有效时不应移除规则。' }
    Send-Ok @{command='writeOutput'; channel=21; value=$false} | Out-Null
    Send-Ok @{command='removeActuator'; outputChannel=21} | Out-Null
    Start-Sleep -Milliseconds 380
    if ((Send-Ok @{command='readInput'; channel=101}).value -or (Send-Ok @{command='readInput'; channel=102}).value) { throw '已移除规则的旧延迟动作仍修改输入。' }
    # 调试命令必须显式指定输出，且可配置、查询及移除独立夹具。
    $command = Join-Path $PSScriptRoot 'ControllerCommand.ps1'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Port $Port -Action ConfigureActuator -OutputChannel 21 -ActuatorClampedInput 101 -ActuatorReleasedInput 102 -ActuatorDelayMs 300 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '夹具命令行配置失败。' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Port $Port -Action ActuatorStatus -OutputChannel 21 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '夹具命令行查询失败。' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Port $Port -Action RemoveActuator -OutputChannel 21 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '夹具命令行移除失败。' }
    Send-Ok @{command='releaseLease'; leaseId=$script:LeaseId} | Out-Null
    $script:LeaseId = $null
    Write-Host '夹具独立双到位、互斥占用、反向、故障恢复和移除检查通过。'
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
