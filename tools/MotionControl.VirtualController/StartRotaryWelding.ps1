param(
    [Parameter(Mandatory=$true)][string]$ConfigurationRoot,
    [switch]$LaunchHost
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$root = (Resolve-Path -LiteralPath $ConfigurationRoot).Path
$defaultRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'MotionControlFramework\Configurations'))
if ([string]::Equals([IO.Path]::GetFullPath($root), $defaultRoot, [StringComparison]::OrdinalIgnoreCase)) { throw '此脚本只接受隔离测试配置，不接管默认设备。' }
$machine = Join-Path $root 'Machine01'
$controllerExe = Join-Path $PSScriptRoot 'bin\Release\net48\MotionControl.VirtualController.exe'
$hostExe = Join-Path $repo 'src\MotionControl.Host.Wpf\bin\Release\net48\MotionControl.Host.Wpf.exe'
if (-not (Test-Path -LiteralPath $machine -PathType Container)) { throw '配置根目录缺少 Machine01。' }
foreach ($file in @($controllerExe, $hostExe)) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "请先构建 Release 程序：$file" } }
if (@(Get-Process -Name 'MotionControl.Host.Wpf' -ErrorAction SilentlyContinue).Count -gt 0) { throw '正式上位机正在运行；不会启动第二个窗口或接管控制器。' }
. (Join-Path $PSScriptRoot 'ScenarioIoMapping.ps1')

# 读取隔离设备文件，缺失或损坏均直接失败。
function Read-RotaryConfig([string]$RelativePath) {
    $file = Join-Path $machine $RelativePath
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "缺少配置：$file" }
    return Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
}

$backend = Read-RotaryConfig 'Parameters\ControllerBackend.json'
if ($backend.backend -ne 'Virtual' -or [int]$backend.port -lt 1024 -or [int]$backend.port -gt 65535) { throw '只接受有效本机 Virtual 测试后端。' }
$port = [int]$backend.port
$equipment = Read-RotaryConfig 'Parameters\Equipment.json'
if ($equipment.executionOwner -notin @('TaskEngine','StationFlows')) { throw '设备运行入口不受支持。' }
$motionOnly = $false
$motionMarker = Join-Path $machine 'Parameters\RotaryMotionValidation.json'
if (Test-Path -LiteralPath $motionMarker -PathType Leaf) {
    $motionSettings = Read-RotaryConfig 'Parameters\RotaryMotionValidation.json'
    if ($motionSettings.schemaVersion -ne 1 -or $motionSettings.motionOnly -ne $true) { throw '纯运动验证配置无效。' }
    $motionOnly = $true
}
if ($equipment.executionOwner -eq 'StationFlows' -and (($motionOnly -and $equipment.definition -ne 'RotaryMotionValidation') -or (-not $motionOnly -and $equipment.definition -ne 'RotaryWelding'))) { throw '设备定义与运动验证模式不一致。' }
$axes = @( (Read-RotaryConfig 'HardwareConfig\AxisParameters.json').axes )
$io = Read-RotaryConfig 'HardwareConfig\IoMapping.json'
$card = 'MotionCard01'
$axisNames = @('Unload_X','Load_X','Press_Z','Transfer_Y','Camera_X','Camera_Z','Feeder_X','Feeder_Z','Buffer_X','Buffer_Y','Reject_Z','Conveyor_R','Weld_Z')
$inputNames = @('上料到位','取料夹紧到位','取料松开到位','A夹紧到位','A松开到位','B夹紧到位','B松开到位','扫码完成','扫码成功','焊接完成','焊接合格','压装完成','压装合格','相机触发反馈','检测完成','检测OK','检测NG','缓存接收允许','合格出口允许','不良出口允许','剔除完成','缓存载具到位','合格出料完成')
$outputNames = @('取料夹紧','A夹紧','B夹紧','扫码触发','焊接启动','压装启动','相机触发','相机结果复位','剔除启动','缓存交接','合格出料')
if ($motionOnly) {
    $inputNames = @($inputNames | Where-Object { $_ -notin @('扫码完成','扫码成功') })
    $inputNames += @('A夹位有料','A夹位无料','B夹位有料','B夹位无料','缓存位有料','缓存位无料','下料夹紧到位','下料松开到位','下料位有料','下料位无料')
    $outputNames = @($outputNames | Where-Object { $_ -ne '扫码触发' }) + @('下料夹紧')
}
$inputs = @{}
$outputs = @{}
foreach ($name in $inputNames) { $inputs[$name] = Get-ScenarioIoChannel -Items @($io.digitalInputs) -Name $name -ControllerId $card }
foreach ($name in $outputNames) { $outputs[$name] = Get-ScenarioIoChannel -Items @($io.digitalOutputs) -Name $name -ControllerId $card }
for ($bit=0; -not $motionOnly -and $bit -lt 16; $bit++) {
    $name = '扫码数据{0:D2}' -f $bit
    $inputs[$name] = Get-ScenarioIoChannel -Items @($io.digitalInputs) -Name $name -ControllerId $card
    if ($inputs[$name] -ne 200 + $bit) { throw "扫码数据位必须连续且低位在前：$name。" }
}
$sensors = @()
for ($index=0; $index -lt $axisNames.Count; $index++) {
    $name = $axisNames[$index]
    $matching = @($axes | Where-Object { $_.name -eq $name -and $_.controllerId -eq $card -and [int]$_.axisNumber -eq $index })
    if ($matching.Count -ne 1) { throw "轴映射不符合测试配置：$name" }
    $origin = Get-ScenarioIoChannel -Items @($io.digitalInputs) -Name ($name + '原点') -ControllerId $card
    $negative = Get-ScenarioIoChannel -Items @($io.digitalInputs) -Name ($name + '负限位') -ControllerId $card
    $positive = Get-ScenarioIoChannel -Items @($io.digitalInputs) -Name ($name + '正限位') -ControllerId $card
    if ([int]$matching[0].datumInputChannel -ne $origin) { throw "轴回零输入与映射不一致：$name" }
    $sensors += @{name=$name; axis=$index; origin=$origin; negative=$negative; positive=$positive}
}

# 通过本地协议发送单条请求，不吞掉设备拒绝原因。
function Send-RotaryRaw([hashtable]$Request) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.ReceiveTimeout = 3000
        $client.Connect('127.0.0.1', $port)
        $stream = $client.GetStream()
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
        $writer.AutoFlush = $true
        $writer.WriteLine(($Request | ConvertTo-Json -Depth 20 -Compress))
        $line = $reader.ReadLine()
        if ($null -eq $line) { throw '控制器未返回结果。' }
        $result = $line | ConvertFrom-Json
        if (-not $result.ok) { throw "控制器拒绝 $($Request.command)：$($result.error)" }
        return $result
    }
    finally { $client.Dispose() }
}

# 每次操作续期本次租约，避免准备十三轴期间租约意外超时。
function Send-Rotary([hashtable]$Request) {
    if ($script:LeaseId -and $Request.command -notin @('heartbeat','releaseLease')) {
        $null = Send-RotaryRaw @{command='heartbeat'; leaseId=$script:LeaseId}
    }
    return Send-RotaryRaw $Request
}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
try { $listener.Start() } catch { throw "端口 $port 已占用，不会接管。" } finally { $listener.Stop() }
$controller = Start-Process -FilePath $controllerExe -ArgumentList $port -WindowStyle Hidden -PassThru
$hostProcess = $null
try {
    $ready = $false
    for ($attempt=0; $attempt -lt 50; $attempt++) {
        if ($controller.HasExited) { throw '软件控制器启动后退出。' }
        try { $identity = Send-RotaryRaw @{command='ping'}; $ready=$true; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready -or $identity.controllerType -ne 'MotionControl.VirtualController' -or $identity.protocolVersion -ne 2) { throw '本机控制器身份不匹配。' }
    $actual = @(Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue | Where-Object { $_.RemotePort -eq 0 })
    if ($actual.Count -ne 1 -or $actual[0].OwningProcess -ne $controller.Id -or $actual[0].LocalAddress -ne '127.0.0.1') { throw '监听进程不是本次创建的本机控制器。' }
    $script:LeaseId = (Send-RotaryRaw @{command='acquireLease'}).leaseId
    if (-not $script:LeaseId) { throw '无法获取本次控制器租约。' }
    foreach ($channel in $outputs.Values) {
        if ((Send-Rotary @{command='readOutput'; channel=$channel}).value) { throw "模型准备前 DO$channel 未关闭。" }
    }
    foreach ($name in $inputs.Keys) {
        if ((Send-Rotary @{command='readInput'; channel=$inputs[$name]}).value) { throw "模型准备前输入不为低电平：$name。" }
    }
    $actuatorPairs = @(
        @{Output='取料夹紧'; Clamped='取料夹紧到位'; Released='取料松开到位'},
        @{Output='A夹紧'; Clamped='A夹紧到位'; Released='A松开到位'},
        @{Output='B夹紧'; Clamped='B夹紧到位'; Released='B松开到位'})
    if ($motionOnly) { $actuatorPairs += @{Output='下料夹紧'; Clamped='下料夹紧到位'; Released='下料松开到位'} }
    foreach ($pair in $actuatorPairs) {
        Send-Rotary @{command='configureActuator'; outputChannel=$outputs[$pair.Output]; clampInputChannel=$inputs[$pair.Clamped]; releaseInputChannel=$inputs[$pair.Released]; delayMs=250} | Out-Null
    }
    foreach ($pair in @(
        @{Output='焊接启动'; Input='焊接完成'; Delay=300},
        @{Output='焊接启动'; Input='焊接合格'; Delay=350},
        @{Output='压装启动'; Input='压装完成'; Delay=300},
        @{Output='压装启动'; Input='压装合格'; Delay=350},
        @{Output='剔除启动'; Input='剔除完成'; Delay=300},
        @{Output='缓存交接'; Input='缓存载具到位'; Delay=300},
        @{Output='合格出料'; Input='合格出料完成'; Delay=300})) {
        Send-Rotary @{command='configureFeedback'; outputChannel=$outputs[$pair.Output]; inputChannel=$inputs[$pair.Input]; delayMs=$pair.Delay; activeHigh=$true} | Out-Null
    }
    if (-not $motionOnly) {
        Send-Rotary @{command='configureScanner'; triggerOutputChannel=$outputs['扫码触发']; completeInputChannel=$inputs['扫码完成']; successInputChannel=$inputs['扫码成功']; dataInputStart=$inputs['扫码数据00']; processingDelayMs=300} | Out-Null
    }
    if ($motionOnly) {
        foreach ($pair in @(
            @{Name='A夹位'; Present='A夹位有料'; Empty='A夹位无料'},
            @{Name='B夹位'; Present='B夹位有料'; Empty='B夹位无料'},
            @{Name='缓存位'; Present='缓存位有料'; Empty='缓存位无料'},
            @{Name='下料位'; Present='下料位有料'; Empty='下料位无料'})) {
            # 初始状态为未知；由测试场景明确注入，不能从夹具或输出电平推测有料。
            Send-Rotary @{command='configureMaterialSensors'; name=$pair.Name; presentInputChannel=$inputs[$pair.Present]; emptyInputChannel=$inputs[$pair.Empty]} | Out-Null
        }
    }
    Send-Rotary @{command='configureCamera'; triggerOutputChannel=$outputs['相机触发']; acknowledgeInputChannel=$inputs['相机触发反馈']; completeInputChannel=$inputs['检测完成']; okInputChannel=$inputs['检测OK']; ngInputChannel=$inputs['检测NG']; resetOutputChannel=$outputs['相机结果复位']; result='OK'; processingDelayMs=500} | Out-Null
    foreach ($sensor in $sensors) {
        $bound = if ($sensor.name -eq 'Conveyor_R') { 95.0 } else { 10.0 }
        Send-Rotary @{command='configureAxisSensors'; axis=$sensor.axis; originInputChannel=$sensor.origin; negativeInputChannel=$sensor.negative; positiveInputChannel=$sensor.positive; originPosition=-1.0; originHalfWidth=0.05; negativeLimit=-$bound; positiveLimit=$bound} | Out-Null
        $actualSensor = (Send-Rotary @{command='readAxisSensors'; axis=$sensor.axis}).axisSensors
        if ($actualSensor.originInputChannel -ne $sensor.origin -or $actualSensor.negativeInputChannel -ne $sensor.negative -or $actualSensor.positiveInputChannel -ne $sensor.positive) { throw "轴传感器读回不一致：$($sensor.name)" }
    }
    foreach ($name in @('缓存接收允许','合格出口允许','不良出口允许')) {
        Send-Rotary @{command='setInput'; channel=$inputs[$name]; value=$true} | Out-Null
        if (-not (Send-Rotary @{command='readInput'; channel=$inputs[$name]}).value) { throw "测试允许信号未建立：$name" }
    }
    Send-RotaryRaw @{command='releaseLease'; leaseId=$script:LeaseId} | Out-Null
    $script:LeaseId = $null
    $modeLabel = if ($motionOnly) { '十三轴运动验证（无扫码及逐件记录）' } else { '十三轴双夹位扫码样例' }
    $fixtureCount = if ($motionOnly) { '四套夹具及四处独立物料反馈' } else { '三套夹具' }
    Write-Host "已配置 $modeLabel、$fixtureCount、加工、视觉和出料规则；控制器 PID $($controller.Id)，端口 $port。"
    Write-Host '供料未启用，脚本没有使能或回零轴，也没有启动生产。请在 Host 完成设备准备后再按以下命令开启供料：'
    Write-Host "  & '.\tools\MotionControl.VirtualController\ControllerCommand.ps1' -Port $port -Action StartFeeder -InputChannel $($inputs['上料到位']) -OutputChannel $($outputs['取料夹紧']) -FeederSafeAxis 6 -FeederSafePosition 0 -FeederSafeTolerance 0.01 -FeederAdditionalSafeAxes @(7,1,3)"
    if ($LaunchHost) {
        if (@(Get-Process -Name 'MotionControl.Host.Wpf' -ErrorAction SilentlyContinue).Count -gt 0) { throw '准备后检测到上位机，不能再启动。' }
        $hostProcess = Start-Process -FilePath $hostExe -ArgumentList @('--simulation-config-root', ('"' + $root + '"')) -WorkingDirectory (Split-Path -Parent $hostExe) -PassThru
        Write-Host "已启动正式上位机 PID $($hostProcess.Id)；请核对设备状态。"
    }
}
catch {
    [Console]::Error.WriteLine("双夹位测试准备失败：$($_.Exception.Message)")
    if ($null -eq $hostProcess -and -not $controller.HasExited -and @(Get-Process -Name 'MotionControl.Host.Wpf' -ErrorAction SilentlyContinue).Count -eq 0) {
        $owned = Get-Process -Id $controller.Id -ErrorAction SilentlyContinue
        if ($owned -and [string]::Equals($owned.Path, $controllerExe, [StringComparison]::OrdinalIgnoreCase)) { Stop-Process -Id $owned.Id }
    }
    throw
}
