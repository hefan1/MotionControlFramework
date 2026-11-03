param([int]$Port = 19578, [string]$ControllerExe = '')

$ErrorActionPreference = 'Stop'
$exePath = if ($ControllerExe) { $ControllerExe } else { Join-Path $PSScriptRoot 'bin\Release\net48\MotionControl.VirtualController.exe' }
if (-not (Test-Path -LiteralPath $exePath)) { throw '请先构建软件控制器。' }

# 仅访问本次测试控制器，保留失败响应供冲突检查。
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
    }
    finally { $client.Dispose() }
}

# 续租并检查成功；输出写入显式附带本次租约。
function Send-Ok([hashtable]$Request) {
    if ($script:LeaseId) {
        $heartbeat = Send-Request @{command='heartbeat'; leaseId=$script:LeaseId}
        if (-not $heartbeat.ok) { throw '测试租约已失效。' }
        if ($Request.command -eq 'writeOutput') { $Request.leaseId = $script:LeaseId }
    }
    $response = Send-Request $Request
    if (-not $response.ok) { throw "$($Request.command) 失败：$($response.error)" }
    return $response
}

# 有界轮询检测完成，避免把处理中的旧值当新结果。
function Wait-Scan {
    for ($i = 0; $i -lt 100; $i++) {
        $state = (Send-Ok @{command='readScanner'}).scannerState
        if ($state.complete) { return $state }
        Start-Sleep -Milliseconds 20
    }
    throw '扫码未在限定时间完成。'
}

# 计算 16 路输入当前值，并核对相邻数据位没有串线。
function Read-ScanBits {
    $value = 0
    for ($bit = 0; $bit -lt 16; $bit++) {
        if ((Send-Ok @{command='readInput'; channel=(200 + $bit)}).value) { $value = $value -bor (1 -shl $bit) }
    }
    return $value
}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $listener.Start() } catch { throw "测试端口 $Port 已占用。" } finally { $listener.Stop() }
$process = Start-Process -FilePath $exePath -ArgumentList $Port -WindowStyle Hidden -PassThru
try {
    $connected = $false
    for ($i = 0; $i -lt 50; $i++) {
        if ($process.HasExited) { throw '测试控制器启动后退出。' }
        try { $identity = Send-Ok @{command='ping'}; $connected = $true; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $connected -or $identity.controllerType -ne 'MotionControl.VirtualController' -or $identity.protocolVersion -ne 2) { throw '控制器身份不符。' }
    $script:LeaseId = (Send-Ok @{command='acquireLease'}).leaseId
    if (-not $script:LeaseId) { throw '无法获取测试租约。' }
    Send-Ok @{command='configureScanner'; triggerOutputChannel=43; completeInputChannel=63; successInputChannel=44; dataInputStart=200; processingDelayMs=100} | Out-Null
    if ((Send-Request @{command='setInput'; channel=200; value=$true}).ok) { throw '受控数据位允许手工写入。' }
    if ((Send-Request @{command='configureFeedback'; inputChannel=201; outputChannel=1; delayMs=10; activeHigh=$true}).ok) { throw '普通反馈占用了扫码位。' }
    if ((Send-Request @{command='configureActuator'; outputChannel=43; clampInputChannel=300; releaseInputChannel=301; delayMs=10}).ok) { throw '夹具占用了扫码触发。' }
    Send-Ok @{command='writeOutput'; channel=43; value=$true} | Out-Null
    $first = Wait-Scan
    if (-not $first.success -or $first.value -ne 1 -or (Read-ScanBits) -ne 1) { throw '第一次扫码数据错误。' }
    Start-Sleep -Milliseconds 150
    if ((Read-ScanBits) -ne 1 -or (Send-Ok @{command='readScanner'}).scannerState.value -ne 1) { throw '高电平期间结果没有锁存。' }
    Send-Ok @{command='writeOutput'; channel=43; value=$false} | Out-Null
    if ((Read-ScanBits) -ne 0 -or (Send-Ok @{command='readScanner'}).scannerState.complete) { throw '触发复位未清除旧结果。' }
    Send-Ok @{command='writeOutput'; channel=43; value=$true} | Out-Null
    $second = Wait-Scan
    if (-not $second.success -or $second.value -ne 2 -or (Read-ScanBits) -ne 2) { throw '下次触发未取得新码。' }
    Send-Ok @{command='writeOutput'; channel=43; value=$false} | Out-Null
    Send-Ok @{command='setScannerFault'; mode='fail'} | Out-Null
    Send-Ok @{command='writeOutput'; channel=43; value=$true} | Out-Null
    $failed = Wait-Scan
    if ($failed.success -or $failed.value -ne 0 -or (Read-ScanBits) -ne 0) { throw '扫码失败产生了有效码。' }
    Send-Ok @{command='writeOutput'; channel=43; value=$false} | Out-Null
    Send-Ok @{command='setScannerFault'; mode='noCompletion'} | Out-Null
    Send-Ok @{command='writeOutput'; channel=43; value=$true} | Out-Null
    Start-Sleep -Milliseconds 180
    if ((Send-Ok @{command='readScanner'}).scannerState.complete) { throw '无完成故障仍完成。' }
    Send-Ok @{command='writeOutput'; channel=43; value=$false} | Out-Null
    Send-Ok @{command='setScannerFault'; mode='normal'} | Out-Null
    Send-Ok @{command='writeOutput'; channel=43; value=$true} | Out-Null
    $third = Wait-Scan
    if (-not $third.success -or $third.value -ne 3) { throw '恢复扫码后编号错误。' }
    Send-Ok @{command='writeOutput'; channel=43; value=$false} | Out-Null
    Send-Ok @{command='removeScanner'} | Out-Null
    Send-Ok @{command='releaseLease'; leaseId=$script:LeaseId} | Out-Null
    $script:LeaseId = $null
    Write-Host '扫码器触发、16 位锁存、复位、独立通道、失败、不完成和恢复检查通过。'
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
