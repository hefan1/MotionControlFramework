param([int]$Port = 19624, [string]$ControllerExe = '')

$ErrorActionPreference = 'Stop'
$exePath = if ($ControllerExe) { $ControllerExe } else { Join-Path $PSScriptRoot 'bin\Release\net48\MotionControl.VirtualController.exe' }
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw '请先构建软件控制器。' }

# 仅向本次独立控制器发送请求；失败响应保留供互斥与异常检查。
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

# 检查成功请求，并让测试运动会话保持有效。
function Send-Ok([hashtable]$Request) {
    if ($script:LeaseId) {
        if (-not (Send-Request @{command='heartbeat'; leaseId=$script:LeaseId}).ok) { throw '测试租约已失效。' }
        if ($Request.command -eq 'writeOutput') { $Request.leaseId = $script:LeaseId }
    }
    $response = Send-Request $Request
    if (-not $response.ok) { throw "测试请求被拒绝：$($response.error)" }
    return $response
}

# 比较设备协议实际报告的两路电平，不从命令状态推断。
function Assert-Material([string]$Name, [bool]$Present, [bool]$Empty) {
    $sensor = (Send-Ok @{command='readMaterialSensors'; name=$Name}).materialState
    if ($sensor.present -ne $Present -or $sensor.empty -ne $Empty) { throw "物料反馈不匹配：$Name。" }
    if ((Send-Ok @{command='readInput'; channel=$sensor.presentInputChannel}).value -ne $Present) { throw "有料 DI 不匹配：$Name。" }
    if ((Send-Ok @{command='readInput'; channel=$sensor.emptyInputChannel}).value -ne $Empty) { throw "无料 DI 不匹配：$Name。" }
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
    foreach ($item in @(
        @{name='A夹位'; present=69; empty=70}, @{name='B夹位'; present=71; empty=72},
        @{name='缓存位'; present=73; empty=74}, @{name='下料位'; present=76; empty=77})) {
        Send-Ok @{command='configureMaterialSensors'; name=$item.name; presentInputChannel=$item.present; emptyInputChannel=$item.empty} | Out-Null
        Assert-Material $item.name $false $false
    }
    if ((Send-Request @{command='setInput'; channel=69; value=$true}).ok) { throw '物料模型拥有的 DI 被直接覆盖。' }
    if ((Send-Request @{command='configureFeedback'; inputChannel=69; outputChannel=4; delayMs=0; activeHigh=$true}).ok) { throw '普通反馈重用物料 DI。' }
    if ((Send-Request @{command='configureMaterialSensors'; name='冲突'; presentInputChannel=71; emptyInputChannel=75}).ok) { throw '不同位置错误共享物料 DI。' }
    foreach ($case in @(
        @{state='present'; present=$true; empty=$false}, @{state='empty'; present=$false; empty=$true},
        @{state='conflict'; present=$true; empty=$true}, @{state='unknown'; present=$false; empty=$false})) {
        Send-Ok @{command='setMaterialSensorState'; name='A夹位'; state=$case.state} | Out-Null
        Assert-Material 'A夹位' $case.present $case.empty
        Assert-Material 'B夹位' $false $false
        Assert-Material '缓存位' $false $false
        Assert-Material '下料位' $false $false
    }
    Send-Ok @{command='setMaterialSensorState'; name='B夹位'; state='present'} | Out-Null
    Send-Ok @{command='setMaterialSensorState'; name='缓存位'; state='empty'} | Out-Null
    Send-Ok @{command='configureActuator'; outputChannel=51; clampInputChannel=51; releaseInputChannel=75; delayMs=25} | Out-Null
    Send-Ok @{command='writeOutput'; channel=41; value=$true} | Out-Null
    Send-Ok @{command='writeOutput'; channel=51; value=$true} | Out-Null
    Start-Sleep -Milliseconds 60
    if (-not (Send-Ok @{command='readInput'; channel=51}).value -or (Send-Ok @{command='readInput'; channel=75}).value) { throw '下料夹具未产生独立夹紧到位反馈。' }
    Assert-Material 'A夹位' $false $false
    Assert-Material 'B夹位' $true $false
    Assert-Material '缓存位' $false $true
    Assert-Material '下料位' $false $false
    Send-Ok @{command='writeOutput'; channel=41; value=$false} | Out-Null
    Send-Ok @{command='writeOutput'; channel=51; value=$false} | Out-Null
    if ((Send-Request @{command='setMaterialSensorState'; name='B夹位'; state='bad'}).ok) { throw '错误物料状态被接受。' }
    Assert-Material 'B夹位' $true $false
    Send-Ok @{command='releaseLease'; leaseId=$script:LeaseId} | Out-Null
    $script:LeaseId = $null
    Write-Host 'A/B、缓存及下料位独立物料反馈、未知/冲突、通道占用及输出独立性检查通过。'
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
