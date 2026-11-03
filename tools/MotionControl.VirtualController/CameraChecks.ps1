param([int]$Port = 18523, [string]$ControllerExe = '')

$ErrorActionPreference = 'Stop'
$exePath = if ($ControllerExe) { $ControllerExe } else { Join-Path $PSScriptRoot 'bin\Release\net48\MotionControl.VirtualController.exe' }
if (-not (Test-Path -LiteralPath $exePath)) { throw '请先构建 Release 软件控制器。' }

# 向隔离测试控制器发送一条请求，不复用正式 Host 会话。
function Send-Request([hashtable]$Request) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.ReceiveTimeout = 3000
        $client.Connect('127.0.0.1', $Port)
        $stream = $client.GetStream()
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false))
        $writer.AutoFlush = $true
        $writer.WriteLine(($Request | ConvertTo-Json -Compress))
        $line = $reader.ReadLine()
        if ($null -eq $line) { throw '控制器未返回结果。' }
        return ($line | ConvertFrom-Json)
    }
    finally { $client.Dispose() }
}

# 检查成功响应；轴及输出命令使用本测试进程持有的租约。
function Send-Ok([hashtable]$Request) {
    if ($script:LeaseId -and $Request.command -eq 'writeOutput') {
        $heartbeat = Send-Request @{ command='heartbeat'; leaseId=$script:LeaseId }
        if (-not $heartbeat.ok) { throw '测试心跳失败。' }
        $Request.leaseId = $script:LeaseId
    }
    $result = Send-Request $Request
    if (-not $result.ok) { throw "$($Request.command) 失败：$($result.error)" }
    return $result
}

# 在有限时间内等待测试输入，不将超时误判为完成。
function Wait-Input([int]$Channel, [bool]$Expected) {
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ((Send-Ok @{command='readInput'; channel=$Channel}).value -eq $Expected) { return }
        Start-Sleep -Milliseconds 50
    }
    throw "DI$Channel 未变为 $Expected。"
}

$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $probe.Start() } catch { throw "测试端口 $Port 已被占用。" } finally { $probe.Stop() }
$process = Start-Process -FilePath $exePath -ArgumentList $Port -WindowStyle Hidden -PassThru
try {
    $connected = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($process.HasExited) { throw '控制器启动失败。' }
        try { $identity = Send-Ok @{command='ping'}; $connected = $true; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $connected -or $identity.controllerType -ne 'MotionControl.VirtualController' -or $identity.protocolVersion -ne 2) { throw '测试端口上的控制器身份不符。' }
    if ((Send-Ok @{command='readCamera'}).configured) { throw '默认不应启用相机。' }
    $script:LeaseId = (Send-Ok @{command='acquireLease'}).leaseId
    if (-not $script:LeaseId) { throw '未取得测试租约。' }

    # 不能覆盖已有 DI 规则；反向配置也不能抢占相机输入。
    Send-Ok @{command='configureFeedback'; inputChannel=21; outputChannel=19; delayMs=0; activeHigh=$true} | Out-Null
    $configuration = @{command='configureCamera'; triggerOutputChannel=20; acknowledgeInputChannel=21; completeInputChannel=22; okInputChannel=24; ngInputChannel=25; result='OK'; resetOutputChannel=23; processingDelayMs=500}
    if ((Send-Request $configuration).ok) { throw '已有反馈的输入不能被相机占用。' }
    Send-Ok @{command='removeFeedback'; inputChannel=21} | Out-Null
    $missingResultInput = @{command='configureCamera'; triggerOutputChannel=20; acknowledgeInputChannel=21; completeInputChannel=22; okInputChannel=24; result='OK'; processingDelayMs=500}
    if ((Send-Request $missingResultInput).ok) { throw '不能只配置一个相机结果输入。' }
    Send-Ok @{command='configureActuator'; outputChannel=20; clampInputChannel=26; releaseInputChannel=27; delayMs=500} | Out-Null
    if ((Send-Request $configuration).ok) { throw '相机触发不能占用夹具输出。' }
    Send-Ok @{command='removeActuator'; outputChannel=20} | Out-Null
    Send-Ok @{command='configureActuator'; outputChannel=23; clampInputChannel=26; releaseInputChannel=27; delayMs=500} | Out-Null
    if ((Send-Request $configuration).ok) { throw '相机复位不能占用夹具输出。' }
    Send-Ok @{command='removeActuator'; outputChannel=23} | Out-Null
    Send-Ok @{command='configureFeeder'; enabled=$true; outputChannel=20; inputChannel=28; feedDelayMs=500; emptyDelayMs=500} | Out-Null
    if ((Send-Request $configuration).ok) { throw '相机触发不能占用供料输出。' }
    Send-Ok @{command='configureFeeder'; enabled=$false} | Out-Null
    Send-Ok $configuration | Out-Null
    if ((Send-Request $configuration).ok) { throw '不能覆盖已有相机规则。' }
    if ((Send-Request @{command='configureActuator'; outputChannel=20; clampInputChannel=26; releaseInputChannel=27; delayMs=10}).ok) { throw '夹具不能占用相机触发输出。' }
    if ((Send-Request @{command='configureActuator'; outputChannel=23; clampInputChannel=26; releaseInputChannel=27; delayMs=10}).ok) { throw '夹具不能占用相机复位输出。' }
    if ((Send-Request @{command='configureActuator'; outputChannel=19; clampInputChannel=24; releaseInputChannel=27; delayMs=10}).ok) { throw '夹具不能占用相机结果输入。' }
    if ((Send-Request @{command='configureFeedback'; inputChannel=22; outputChannel=19; delayMs=0; activeHigh=$true}).ok) { throw '普通反馈不能抢占相机输入。' }
    if ((Send-Request @{command='configureFeedback'; inputChannel=24; outputChannel=19; delayMs=0; activeHigh=$true}).ok) { throw '普通反馈不能抢占相机结果输入。' }
    if ((Send-Request @{command='configureFeeder'; enabled=$true; inputChannel=21; outputChannel=19; feedDelayMs=0; emptyDelayMs=0}).ok) { throw '供料规则不能抢占相机输入。' }
    if ((Send-Request @{command='configureFeeder'; enabled=$true; inputChannel=28; outputChannel=20; feedDelayMs=0; emptyDelayMs=0}).ok) { throw '供料规则不能占用相机触发输出。' }
    if ((Send-Request @{command='configureFeeder'; enabled=$true; inputChannel=28; outputChannel=23; feedDelayMs=0; emptyDelayMs=0}).ok) { throw '供料规则不能占用相机复位输出。' }

    # 短脉冲结束后仍处理；忙碌时重新触发不能被接受。
    Send-Ok @{command='writeOutput'; channel=20; value=$true} | Out-Null
    if (-not (Send-Ok @{command='readInput'; channel=21}).value) { throw '接受触发后应有反馈。' }
    if ((Send-Ok @{command='readInput'; channel=22}).value) { throw '触发不能立即等同检测完成。' }
    Send-Ok @{command='writeOutput'; channel=20; value=$false} | Out-Null
    if ((Send-Ok @{command='readInput'; channel=21}).value) { throw '短脉冲结束后触发反馈未复位。' }
    Send-Ok @{command='writeOutput'; channel=20; value=$true} | Out-Null
    if ((Send-Ok @{command='readInput'; channel=21}).value) { throw '忙碌时不能接受重复触发。' }
    if ((Send-Request @{command='resetCamera'}).ok) { throw '触发输出有效时不能通过命令复位。' }
    Send-Ok @{command='writeOutput'; channel=23; value=$true} | Out-Null
    if (-not (Send-Ok @{command='readCamera'}).cameraState.processing) { throw '触发输出有效时不能由复位输出取消处理。' }
    Send-Ok @{command='writeOutput'; channel=23; value=$false} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$false} | Out-Null
    Wait-Input 22 $true
    if (-not (Send-Ok @{command='readCamera'}).cameraState.completed) { throw '处理完成未锁存。' }
    if (-not (Send-Ok @{command='readInput'; channel=24}).value -or (Send-Ok @{command='readInput'; channel=25}).value) { throw 'OK 结果输入不正确。' }
    if ((Send-Request @{command='setCameraResult'; result='NG'}).ok) { throw '完成结果未复位时不能改写。' }
    Send-Ok @{command='writeOutput'; channel=20; value=$true} | Out-Null
    if ((Send-Ok @{command='readInput'; channel=21}).value) { throw '完成未复位时不能接受新触发。' }
    Send-Ok @{command='writeOutput'; channel=20; value=$false} | Out-Null
    Send-Ok @{command='writeOutput'; channel=23; value=$true} | Out-Null
    if ((Send-Ok @{command='readInput'; channel=22}).value) { throw '复位输出未清除完成。' }
    if ((Send-Ok @{command='readInput'; channel=24}).value -or (Send-Ok @{command='readInput'; channel=25}).value) { throw '复位输出未清除结果。' }
    Send-Ok @{command='writeOutput'; channel=20; value=$true} | Out-Null
    if ((Send-Ok @{command='readInput'; channel=21}).value) { throw '复位输出有效时不能接受触发。' }
    Send-Ok @{command='writeOutput'; channel=20; value=$false} | Out-Null
    Send-Ok @{command='writeOutput'; channel=23; value=$false} | Out-Null

    # 下一周期可选择 NG 或无效结果，完成锁存不等于一定有可用结果。
    Send-Ok @{command='setCameraResult'; result='NG'} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$true} | Out-Null
    if ((Send-Request @{command='setCameraResult'; result='OK'}).ok) { throw '处理中不能改写结果。' }
    Send-Ok @{command='writeOutput'; channel=20; value=$false} | Out-Null
    Wait-Input 22 $true
    if ((Send-Ok @{command='readInput'; channel=24}).value -or -not (Send-Ok @{command='readInput'; channel=25}).value) { throw 'NG 结果输入不正确。' }
    Send-Ok @{command='resetCamera'} | Out-Null
    Send-Ok @{command='setCameraResult'; result='Invalid'} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$false} | Out-Null
    Wait-Input 22 $true
    if ((Send-Ok @{command='readInput'; channel=24}).value -or (Send-Ok @{command='readInput'; channel=25}).value) { throw '无效结果不应产生 OK 或 NG。' }
    if ((Send-Request @{command='resolveCameraResult'; result='Invalid'}).ok) { throw '人工确认不能选择无效结果。' }
    Send-Ok @{command='resolveCameraResult'; result='NG'} | Out-Null
    if (-not (Send-Ok @{command='readInput'; channel=22}).value -or (Send-Ok @{command='readInput'; channel=24}).value -or -not (Send-Ok @{command='readInput'; channel=25}).value) { throw '人工确认未保留完成或正确锁存 NG。' }
    if ((Send-Request @{command='resolveCameraResult'; result='OK'}).ok) { throw '不能覆盖已人工确认的结果。' }
    Send-Ok @{command='resetCamera'} | Out-Null

    # 故障后即使解除故障，旧处理也不会迟到完成；复位后重新触发才恢复。
    Send-Ok @{command='setCameraFault'; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$false} | Out-Null
    Send-Ok @{command='setCameraFault'; value=$false} | Out-Null
    Start-Sleep -Milliseconds 650
    if ((Send-Ok @{command='readInput'; channel=22}).value) { throw '解除故障不能使旧处理悄悄完成。' }
    if (-not (Send-Ok @{command='readCamera'}).cameraState.processing) { throw '未完成故障应保留处理状态直到复位。' }
    Send-Ok @{command='resetCamera'} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$false} | Out-Null
    Wait-Input 22 $true
    Send-Ok @{command='resetCamera'} | Out-Null

    # 外设可显式继续已接受但中断的作业，不向 Host 伪造第二次触发。
    Send-Ok @{command='setCameraResult'; result='NG'} | Out-Null
    Send-Ok @{command='setCameraFault'; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$false} | Out-Null
    if ((Send-Request @{command='resumeCamera'}).ok) { throw '故障未解除不能继续相机作业。' }
    Send-Ok @{command='setCameraFault'; value=$false} | Out-Null
    if (-not (Send-Ok @{command='readCamera'}).cameraState.awaitingResume) { throw '故障后没有保留待继续作业。' }
    Send-Ok @{command='resumeCamera'} | Out-Null
    if ((Send-Request @{command='resumeCamera'}).ok) { throw '正在继续的作业不能重复继续。' }
    if ((Send-Ok @{command='readInput'; channel=22}).value) { throw '继续作业不能跳过处理延迟。' }
    Wait-Input 22 $true
    if (-not (Send-Ok @{command='readInput'; channel=25}).value) { throw '继续作业没有保留原定 NG 结果。' }
    if ((Send-Request @{command='resumeCamera'}).ok) { throw '完成后的作业不能继续。' }
    Send-Ok @{command='resetCamera'} | Out-Null

    # 相机处理是外部设备过程：控制租约释放不会撤销已接受的触发。
    Send-Ok @{command='setCameraResult'; result='OK'} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$false} | Out-Null
    Send-Ok @{command='releaseLease'; leaseId=$script:LeaseId} | Out-Null
    $script:LeaseId = $null
    Wait-Input 22 $true
    if (-not (Send-Ok @{command='readInput'; channel=24}).value) { throw '释放会话后，相机未完成已接受的检测。' }
    $script:LeaseId = (Send-Ok @{command='acquireLease'}).leaseId
    Send-Ok @{command='resetCamera'} | Out-Null

    # 处理开始之后注入故障，旧的延迟结果也不能在故障解除后生效。
    Send-Ok @{command='writeOutput'; channel=20; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$false} | Out-Null
    Send-Ok @{command='setCameraFault'; value=$true} | Out-Null
    Send-Ok @{command='setCameraFault'; value=$false} | Out-Null
    Start-Sleep -Milliseconds 650
    if ((Send-Ok @{command='readInput'; channel=22}).value) { throw '处理中注入故障后旧处理不能完成。' }
    Send-Ok @{command='resetCamera'} | Out-Null

    # 处理中复位后，过期的延迟任务不能把完成信号重新置位。
    Send-Ok @{command='writeOutput'; channel=20; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=20; value=$false} | Out-Null
    Send-Ok @{command='writeOutput'; channel=23; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=23; value=$false} | Out-Null
    Start-Sleep -Milliseconds 650
    if ((Send-Ok @{command='readInput'; channel=22}).value) { throw '复位后的旧处理不应完成。' }
    Send-Ok @{command='removeCamera'} | Out-Null
    if ((Send-Ok @{command='readCamera'}).configured) { throw '相机规则未移除。' }
    Send-Ok @{command='configureFeedback'; inputChannel=22; outputChannel=19; delayMs=0; activeHigh=$true} | Out-Null
    Send-Ok @{command='removeFeedback'; inputChannel=22} | Out-Null

    # 验证面向调试人员的 PowerShell 命令同样能够配置并读取相机规则。
    $command = Join-Path $PSScriptRoot 'ControllerCommand.ps1'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Port $Port -Action ConfigureCamera -CameraTriggerOutput 20 -CameraAcknowledgeInput 21 -CameraCompleteInput 22 -CameraOkInput 24 -CameraNgInput 25 -CameraResult OK -CameraResetOutput 23 -CameraProcessingDelayMs 500 | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Send-Ok @{command='readCamera'}).configured) { throw '相机命令行配置失败。' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Port $Port -Action CameraStatus | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '相机命令行读取失败。' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Port $Port -Action CameraResult -CameraResult NG | Out-Null
    if ($LASTEXITCODE -ne 0 -or (Send-Ok @{command='readCamera'}).cameraState.nextResult -ne 'NG') { throw '相机命令行选择结果失败。' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $command -Port $Port -Action RemoveCamera | Out-Null
    if ($LASTEXITCODE -ne 0 -or (Send-Ok @{command='readCamera'}).configured) { throw '相机命令行移除失败。' }
    Write-Host '相机短触发、独立完成、OK/NG/无效结果、忙碌拒绝、输入占用、故障和复位检查通过。'
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
