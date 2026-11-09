param([int]$Port = 18521, [string]$ControllerExe = '')

$ErrorActionPreference = 'Stop'
$exePath = if ($ControllerExe) { $ControllerExe } else { Join-Path $PSScriptRoot 'bin\Release\net48\MotionControl.VirtualController.exe' }
if (-not (Test-Path -LiteralPath $exePath)) { throw '请先构建 Release 虚拟控制器。' }

# 通过软件控制器的本地 TCP 测试接口发送一条请求。
function Send-Request([hashtable]$Request) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $client.ReceiveTimeout = 3000
        $client.SendTimeout = 3000
        $client.Connect('127.0.0.1', $Port)
        $stream = $client.GetStream()
        $encoding = [System.Text.UTF8Encoding]::new($false)
        $writer = [System.IO.StreamWriter]::new($stream, $encoding)
        $reader = [System.IO.StreamReader]::new($stream, $encoding)
        $writer.AutoFlush = $true
        $writer.WriteLine(($Request | ConvertTo-Json -Compress))
        $line = $reader.ReadLine()
        if ($null -eq $line) { throw '控制器没有返回结果。' }
        return ($line | ConvertFrom-Json)
    }
    finally { $client.Dispose() }
}

# 检查成功响应，保留错误详情供定位。
function Send-Ok([hashtable]$Request) {
    if ($script:LeaseId -and $script:AutoHeartbeat) {
        $heartbeat = Send-Request @{command='heartbeat'; leaseId=$script:LeaseId}
        if (-not $heartbeat.ok) { throw "测试会话心跳失败：$($heartbeat.error)" }
    }
    if ($script:LeaseId -and $Request.command -in @('servo','home','move','jog','stop','resetAlarm','writeOutput')) {
        $Request.leaseId = $script:LeaseId
    }
    $result = Send-Request $Request
    if (-not $result.ok) { throw "命令 $($Request.command) 失败：$($result.error)" }
    return $result
}

# 在有限时间内等待数字输入达到预期值。
function Wait-Input([int]$Channel, [bool]$Value) {
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ((Send-Ok @{ command='readInput'; channel=$Channel }).value -eq $Value) { return }
        Start-Sleep -Milliseconds 50
    }
    throw "输入 DI$Channel 未在规定时间变为 $Value。"
}

# 端口已被其他控制器使用时禁止测试，避免误操作其他进程。
$portCheck = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
try { $portCheck.Start() }
catch { throw "测试端口 $Port 已被占用，请改用空闲端口。" }
finally { $portCheck.Stop() }
$process = Start-Process -FilePath $exePath -ArgumentList $Port -WindowStyle Hidden -PassThru
try {
    $connected = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($process.HasExited) { throw '控制器进程启动失败，检查测试端口是否被占用。' }
        try { $connected = (Send-Ok @{command='ping'}).ok; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $connected) { throw '无法连接测试控制器。' }
    $identity = Send-Ok @{command='ping'}
    if ($identity.controllerType -ne 'MotionControl.VirtualController' -or $identity.protocolVersion -ne 2) { throw '测试端口连接到了错误的控制器。' }
    if ([int]$identity.processId -ne $process.Id) { throw '控制器身份没有返回本次进程号。' }

    if ((Send-Request @{command='unsupported'}).ok) { throw '未知命令不得报告成功。' }
    if ((Send-Request @{command='servo'; axis=1; value=$true}).ok) { throw '没有租约不允许使能轴。' }
    $script:LeaseId = (Send-Ok @{command='acquireLease'}).leaseId
    if (-not $script:LeaseId) { throw '控制器未返回有效会话。' }
    $script:AutoHeartbeat = $true
    if ((Send-Request @{command='acquireLease'}).ok) { throw '现有租约不能被抢占。' }
    if ((Send-Request @{command='heartbeat'; leaseId='wrong'}).ok) { throw '其他会话不能续期。' }
    Send-Ok @{command='servo'; axis=1; value=$true} | Out-Null
    Send-Ok @{command='home'; axis=1} | Out-Null
    $initialAxis = Send-Ok @{command='readAxis'; axis=1}
    if (-not $initialAxis.axisState.homed) { throw '零位回零未完成。' }
    Send-Ok @{command='move'; axis=1; position=2.0; speed=2.0} | Out-Null
    Start-Sleep -Milliseconds 200
    $moving = (Send-Ok @{command='readAxis'; axis=1}).axisState
    if (-not $moving.moving -or $moving.position -le 0 -or $moving.position -ge 2) { throw '移动期间位置或运动状态不正确。' }
    Send-Ok @{command='stop'; axis=1} | Out-Null
    if ((Send-Ok @{command='readAxis'; axis=1}).axisState.moving) { throw '停止后轴仍在运动。' }

    # 未执行失败、执行后失败和延迟应答必须区分，且只影响指定通道的一次请求。
    Send-Ok @{command='setCommandFault'; targetCommand='writeOutput'; targetIndex=15; mode='reject'; delayMs=0} | Out-Null
    if ((Send-Request @{command='writeOutput'; channel=15; value=$true; leaseId=$script:LeaseId}).ok) { throw '执行前拒绝未返回失败。' }
    if ((Send-Ok @{command='readOutput'; channel=15}).value) { throw '执行前拒绝不应改变输出。' }
    if ((Send-Ok @{command='readCommandFault'; targetCommand='writeOutput'; targetIndex=15}).configured) { throw '一次性故障未消费。' }
    Send-Ok @{command='setCommandFault'; targetCommand='writeOutput'; targetIndex=15; mode='errorAfter'; delayMs=0} | Out-Null
    Send-Ok @{command='writeOutput'; channel=16; value=$true} | Out-Null
    if (-not (Send-Ok @{command='readCommandFault'; targetCommand='writeOutput'; targetIndex=15}).configured) { throw '其他通道不应消费故障。' }
    if ((Send-Request @{command='writeOutput'; channel=15; value=$true; leaseId=$script:LeaseId}).ok) { throw '执行后故障未返回失败。' }
    if (-not (Send-Ok @{command='readOutput'; channel=15}).value) { throw '执行后失败应保留已经写入的输出。' }
    Send-Ok @{command='writeOutput'; channel=15; value=$false} | Out-Null
    Send-Ok @{command='writeOutput'; channel=16; value=$false} | Out-Null

    Send-Ok @{command='move'; axis=1; position=10.0; speed=1.0} | Out-Null
    Send-Ok @{command='setCommandFault'; targetCommand='stop'; targetIndex=1; mode='reject'; delayMs=0} | Out-Null
    if ((Send-Request @{command='stop'; axis=1; leaseId=$script:LeaseId}).ok) { throw '停轴失败未返回错误。' }
    if (-not (Send-Ok @{command='readAxis'; axis=1}).axisState.moving) { throw '拒绝停轴时不应假装停稳。' }
    Send-Ok @{command='setCommandFault'; targetCommand='stop'; targetIndex=1; mode='errorAfter'; delayMs=0} | Out-Null
    if ((Send-Request @{command='stop'; axis=1; leaseId=$script:LeaseId}).ok) { throw '停轴已执行后的失败未返回错误。' }
    if ((Send-Ok @{command='readAxis'; axis=1}).axisState.moving) { throw '执行后失败应保留已停止的轴状态。' }

    # 延迟应答期间可从另一连接读取和发送心跳，不能把故障扩散成控制器全局死锁。
    Send-Ok @{command='setCommandFault'; targetCommand='readInput'; targetIndex=17; mode='delayReply'; delayMs=1000} | Out-Null
    $delayedClient = [System.Net.Sockets.TcpClient]::new()
    try {
        $delayedClient.ReceiveTimeout = 3000
        $delayedClient.Connect('127.0.0.1', $Port)
        $delayedStream = $delayedClient.GetStream()
        $delayedWriter = [System.IO.StreamWriter]::new($delayedStream, [System.Text.UTF8Encoding]::new($false))
        $delayedReader = [System.IO.StreamReader]::new($delayedStream, [System.Text.UTF8Encoding]::new($false))
        $delayedWriter.AutoFlush = $true
        $delayedWriter.WriteLine('{"command":"readInput","channel":17}')
        $untilAccepted = [System.Diagnostics.Stopwatch]::StartNew()
        while ((Send-Ok @{command='readCommandFault'; targetCommand='readInput'; targetIndex=17}).configured) {
            if ($untilAccepted.ElapsedMilliseconds -gt 700) { throw '延迟请求未被控制器接收。' }
            Start-Sleep -Milliseconds 10
        }
        $probe = [System.Diagnostics.Stopwatch]::StartNew()
        Send-Ok @{command='heartbeat'; leaseId=$script:LeaseId} | Out-Null
        Send-Ok @{command='readInput'; channel=18} | Out-Null
        if ($probe.ElapsedMilliseconds -gt 500) { throw '延迟应答阻塞了无关连接。' }
        if (-not (($delayedReader.ReadLine() | ConvertFrom-Json).ok)) { throw '延迟应答没有成功返回。' }
    }
    finally { $delayedClient.Dispose() }
    # 延迟执行期间会话若被释放，迟到写入不能借用新的控制权。
    Send-Ok @{command='setCommandFault'; targetCommand='writeOutput'; targetIndex=15; mode='delayBefore'; delayMs=1000} | Out-Null
    $lateClient = [System.Net.Sockets.TcpClient]::new()
    try {
        $lateClient.ReceiveTimeout = 3000
        $lateClient.Connect('127.0.0.1', $Port)
        $lateStream = $lateClient.GetStream()
        $lateWriter = [System.IO.StreamWriter]::new($lateStream, [System.Text.UTF8Encoding]::new($false))
        $lateReader = [System.IO.StreamReader]::new($lateStream, [System.Text.UTF8Encoding]::new($false))
        $lateWriter.AutoFlush = $true
        $lateWriter.WriteLine((@{command='writeOutput'; channel=15; value=$true; leaseId=$script:LeaseId} | ConvertTo-Json -Compress))
        $untilAccepted = [System.Diagnostics.Stopwatch]::StartNew()
        while ((Send-Ok @{command='readCommandFault'; targetCommand='writeOutput'; targetIndex=15}).configured) {
            if ($untilAccepted.ElapsedMilliseconds -gt 700) { throw '迟到写入未被控制器接收。' }
            Start-Sleep -Milliseconds 10
        }
        if ((Send-Ok @{command='readOutput'; channel=15}).value) { throw '延迟执行前不应改变输出。' }
        if (-not (Send-Request @{command='releaseLease'; leaseId=$script:LeaseId}).ok) { throw '无法释放旧会话。' }
        $script:LeaseId = $null
        $script:LeaseId = (Send-Ok @{command='acquireLease'}).leaseId
        if (($lateReader.ReadLine() | ConvertFrom-Json).ok) { throw '旧会话的迟到写入不能成功。' }
        if ((Send-Ok @{command='readOutput'; channel=15}).value) { throw '迟到写入改变了新会话输出。' }
    }
    finally { $lateClient.Dispose() }
    if ((Send-Request @{command='setCommandFault'; targetCommand='heartbeat'; targetIndex=0; mode='reject'; delayMs=0}).ok) { throw '不允许注入心跳故障。' }
    Send-Ok @{command='setCommandFault'; targetCommand='readInput'; targetIndex=17; mode='reject'; delayMs=0} | Out-Null
    Send-Ok @{command='clearCommandFault'; targetCommand='readInput'; targetIndex=17} | Out-Null
    if (-not (Send-Ok @{command='readInput'; channel=17}).ok) { throw '清除故障后读入应恢复。' }

    Send-Ok @{command='servo'; axis=1; value=$false} | Out-Null

    Send-Ok @{command='configureFeedback'; inputChannel=5; outputChannel=1; delayMs=150; activeHigh=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=1; value=$true} | Out-Null
    if ((Send-Ok @{command='readInput'; channel=5}).value) { throw '反馈不应在配置延迟前触发。' }
    Wait-Input 5 $true
    Send-Ok @{command='setFeedbackFault'; inputChannel=5; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=1; value=$false} | Out-Null
    Start-Sleep -Milliseconds 200
    if (-not (Send-Ok @{command='readInput'; channel=5}).value) { throw '反馈故障未保持输入。' }
    if (-not (Send-Ok @{command='readFeedback'; inputChannel=5}).feedbackState.stuck) { throw '反馈状态未显示故障。' }
    Send-Ok @{command='setFeedbackFault'; inputChannel=5; value=$false} | Out-Null
    Wait-Input 5 $false
    Send-Ok @{command='removeFeedback'; inputChannel=5} | Out-Null
    if ((Send-Request @{command='readFeedback'; inputChannel=5}).ok) { throw '已删除的反馈规则仍可读取。' }

    Send-Ok @{command='configureFeeder'; enabled=$true; inputChannel=4; outputChannel=0; feedDelayMs=100; emptyDelayMs=300} | Out-Null
    Wait-Input 4 $true
    Send-Ok @{command='writeOutput'; channel=0; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=0; value=$false} | Out-Null
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '夹具释放后物料应离开。' }
    Wait-Input 4 $true
    Send-Ok @{command='setFeederFault'; value=$true} | Out-Null
    if (-not (Send-Ok @{command='readInput'; channel=4}).value) { throw '缺料不应抹除当前物料。' }
    Send-Ok @{command='writeOutput'; channel=0; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=0; value=$false} | Out-Null
    Start-Sleep -Milliseconds 400
    if ((Send-Ok @{command='readInput'; channel=4}).value) { throw '缺料后不应产生新物料。' }
    $batch = Send-Ok @{command='readInputs'; channels=@(4,5)}
    if ($batch.values.Count -ne 2 -or $batch.values[0] -or $batch.values[1]) { throw '批量输入读取结果不正确。' }
    # 停止心跳后持续发送只读请求，确认它们不会延长租约。
    Send-Ok @{command='servo'; axis=1; value=$true} | Out-Null
    Send-Ok @{command='move'; axis=1; position=100.0; speed=1.0} | Out-Null
    Send-Ok @{command='writeOutput'; channel=2; value=$true} | Out-Null
    $oldLease = $script:LeaseId
    $script:AutoHeartbeat = $false
    for ($attempt = 0; $attempt -lt 26; $attempt++) {
        Send-Request @{command='readAxis'; axis=1} | Out-Null
        Start-Sleep -Milliseconds 100
    }
    $timedOutAxis = (Send-Request @{command='readAxis'; axis=1}).axisState
    if ($timedOutAxis.moving) { throw '心跳超时后轴未停止。' }
    if (-not (Send-Request @{command='readOutput'; channel=2}).value) { throw '心跳超时不得擅自改变输出。' }
    if ((Send-Request @{command='heartbeat'; leaseId=$oldLease}).ok) { throw '过期租约不能续期。' }
    if ((Send-Request @{command='move'; axis=1; position=3; speed=1; leaseId=$oldLease}).ok) { throw '过期租约不能操作轴。' }
    $script:LeaseId = $null
    $script:LeaseId = (Send-Ok @{command='acquireLease'}).leaseId
    $script:AutoHeartbeat = $true
    $startPosition = (Send-Ok @{command='readAxis'; axis=1}).axisState.position
    Send-Ok @{command='move'; axis=1; position=100.0; speed=1.0} | Out-Null
    $script:AutoHeartbeat = $false
    Start-Sleep -Milliseconds 3300
    $idleAxis = (Send-Request @{command='readAxis'; axis=1}).axisState
    if ($idleAxis.moving -or ($idleAxis.position - $startPosition) -gt 2.8) { throw '客户端完全失联后，看门狗未及时主动停止轴。' }
    $script:LeaseId = $null
    $script:LeaseId = (Send-Ok @{command='acquireLease'}).leaseId
    $script:AutoHeartbeat = $true
    Send-Ok @{command='move'; axis=1; position=100.0; speed=1.0} | Out-Null
    (Send-Request @{command='releaseLease'; leaseId=$script:LeaseId}) | Out-Null
    $script:AutoHeartbeat = $false
    if ((Send-Request @{command='readAxis'; axis=1}).axisState.moving) { throw '释放租约后轴未停止。' }
    if (-not (Send-Request @{command='readOutput'; channel=2}).value) { throw '释放租约不得擅自改变输出。' }
    $script:LeaseId = $null
    $script:LeaseId = (Send-Ok @{command='acquireLease'}).leaseId
    $script:AutoHeartbeat = $true
    Send-Ok @{command='writeOutput'; channel=2; value=$false} | Out-Null
    (Send-Request @{command='releaseLease'; leaseId=$script:LeaseId}) | Out-Null
    Write-Host '虚拟控制器 TCP、运动/停止、租约看门狗、延迟反馈与缺料检查通过。'
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
