param(
    [string]$ConfigurationRoot = (Join-Path $env:LOCALAPPDATA 'MotionControlFramework\Configurations'),
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$controllerExe = Join-Path $repository 'tools\MotionControl.VirtualController\bin\Release\net48\MotionControl.VirtualController.exe'
$hostExe = Join-Path $repository 'src\MotionControl.Host.Wpf\bin\Release\net48\MotionControl.Host.Wpf.exe'
$checkExe = Join-Path $repository 'tools\MotionControl.TaskEngine.Checks\bin\Release\net48\MotionControl.TaskEngine.Checks.exe'
$root = (Resolve-Path -LiteralPath $ConfigurationRoot).Path
$defaultRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'MotionControlFramework\Configurations'))
$machine = Join-Path $root 'Machine01'
$controller = $null
$hostProcess = $null
$lease = $null
$port = 18520
. (Join-Path $PSScriptRoot 'ScenarioIoMapping.ps1')

# 加载指定设备的 JSON，缺失或格式错误时停止，绝不创建设备配置。
function Read-ClipConfig([string]$RelativePath) {
    $path = Join-Path $machine $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少配置文件：$path" }
    try { return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "配置文件无法解析：$path。$($_.Exception.Message)" }
}

# 只请求本机指定端口的控制器；任何拒绝都原样报告。
function Send-ClipRequest([hashtable]$Request) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.ReceiveTimeout = 3000
        $client.SendTimeout = 3000
        $client.Connect('127.0.0.1', $port)
        $stream = $client.GetStream()
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false))
        $writer.AutoFlush = $true
        $writer.WriteLine(($Request | ConvertTo-Json -Compress))
        $line = $reader.ReadLine()
        if ($null -eq $line) { throw '控制器没有返回结果。' }
        $response = $line | ConvertFrom-Json
        if (-not $response.ok) { throw "控制器拒绝 $($Request.command)：$($response.error)" }
        return $response
    }
    finally { $client.Dispose() }
}

# 只观察监听端口，禁止接管已有控制器或其他服务。
function Get-ClipListener {
    return @(Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue | Where-Object { $_.RemotePort -eq 0 })
}

# 启动前和启动期间不与任何已有 Host 并行接管设备。
function Assert-ClipHostStopped {
    if (@(Get-Process -Name 'MotionControl.Host.Wpf' -ErrorAction SilentlyContinue).Count -gt 0) {
        throw '检测到上位机已运行；请先安全停止并正常关闭，脚本不会接管或重复启动。'
    }
}

try {
    if (-not (Test-Path -LiteralPath $machine -PathType Container)) { throw '配置根目录下没有 Machine01。' }
    if (-not $PrepareOnly -and -not [string]::Equals($root, $defaultRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw '正式 Host 使用默认设备目录；其他目录仅允许 PrepareOnly。'
    }
    foreach ($exe in @($controllerExe, $hostExe, $checkExe)) {
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "缺少 Release 程序：$exe" }
    }
    Assert-ClipHostStopped
    $backend = Read-ClipConfig 'Parameters\ControllerBackend.json'
    if ($backend.backend -ne 'Virtual') { throw '仅允许 Virtual 后端，脚本不会更改控制卡。' }
    if ($null -ne $backend.port) {
        $parsed = 0
        if (-not [int]::TryParse([string]$backend.port, [ref]$parsed) -or $parsed -lt 1 -or $parsed -gt 65535) { throw '软件控制器端口无效。' }
        $port = $parsed
    }
    $equipment = Read-ClipConfig 'Parameters\Equipment.json'
    if ($equipment.definition -ne 'WeldingExample' -or $equipment.executionOwner -notin @('TaskEngine', 'StationFlows')) {
        throw '设备定义或运行入口不是夹具焊接测试样例。'
    }
    if ($equipment.executionOwner -eq 'TaskEngine') {
        $manifest = Read-ClipConfig 'Parameters\TaskMachine.json'
        if ($manifest.machineId -ne 'Machine01' -or $manifest.initializationTaskId -ne 'ClipInit') { throw '当前任务清单不是夹具焊接样例。' }
        foreach ($id in @('ClipScan', 'ClipWeld', 'ClipPress', 'ClipInspect')) {
            if (@($manifest.stations | Where-Object { $_.stationId -eq $id -and $_.taskId -eq $id -and $_.enabled -eq $true }).Count -ne 1) { throw "工位未按样例启用：$id" }
        }
        if (@($manifest.stations).Count -ne 4) { throw '当前任务清单不是四工位样例。' }
        & $checkExe --validate-machine $root Machine01
        if ($LASTEXITCODE -ne 0) { throw '任务设备配置校验失败。' }
    }
    $cards = @( (Read-ClipConfig 'HardwareConfig\MotionCards.json').motionCards )
    if ($cards.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$cards[0].controllerId)) { throw '样例要求一张明确配置的控制器。' }
    $card = [string]$cards[0].controllerId
    $axisConfig = Read-ClipConfig 'HardwareConfig\AxisParameters.json'
    $axisChannels = @{}
    foreach ($name in @('Load_X', 'Weld_Z', 'Press_Z', 'Camera_X', 'Unload_X')) {
        $axes = @($axisConfig.axes | Where-Object { $_.name -eq $name })
        if ($axes.Count -ne 1 -or $axes[0].controllerId -ne $card -or $axes[0].engineeringUnit -ne 'mm' -or
            $axes[0].softLimitEnabled -ne $true -or $null -eq $axes[0].minimumPosition -or $null -eq $axes[0].maximumPosition -or
            [double]$axes[0].minimumPosition -gt 0 -or [double]$axes[0].maximumPosition -lt 1) { throw "轴映射或 0–1 mm 测试行程不符合样例：$name" }
        $minimum = [double]$axes[0].minimumPosition
        $maximum = [double]$axes[0].maximumPosition
        if ([double]::IsNaN($minimum) -or [double]::IsInfinity($minimum) -or
            [double]::IsNaN($maximum) -or [double]::IsInfinity($maximum) -or $minimum -ge $maximum) { throw "轴行程不为有效有限区间：$name" }
        $number = 0
        if (-not [int]::TryParse([string]$axes[0].axisNumber, [ref]$number) -or $number -lt 0 -or $number -ge 64 -or $axisChannels.ContainsKey($number)) { throw "轴号无效或重复：$name" }
        if (@($axisConfig.axes | Where-Object { $_.controllerId -eq $card -and $_.axisNumber -eq $number }).Count -ne 1) { throw "轴物理通道被重复映射：$name" }
        $axisChannels[$number] = $name
    }
    $io = Read-ClipConfig 'HardwareConfig\IoMapping.json'
    $inputs = @{}
    foreach ($name in @('上料到位', '夹具夹紧到位', '夹具松开到位', '焊接完成', '压装完成', '相机触发反馈', '检测完成', '检测OK', '检测NG', '下料到位')) {
        $inputs[$name] = Get-ScenarioIoChannel -Items @($io.digitalInputs) -Name $name -ControllerId $card
    }
    # 根据各轴实际逻辑映射读取独立的原点和正负限位，不能推测 DI 与轴号相同。
    $axisSensors = @()
    foreach ($axisNumber in $axisChannels.Keys) {
        $axisName = [string]$axisChannels[$axisNumber]
        $axis = @($axisConfig.axes | Where-Object { $_.name -eq $axisName })[0]
        $origin = Get-ScenarioIoChannel -Items @($io.digitalInputs) -Name ($axisName + '原点') -ControllerId $card
        $negative = Get-ScenarioIoChannel -Items @($io.digitalInputs) -Name ($axisName + '负限位') -ControllerId $card
        $positive = Get-ScenarioIoChannel -Items @($io.digitalInputs) -Name ($axisName + '正限位') -ControllerId $card
        $configuredDatum = 0
        if (-not [int]::TryParse([string]$axis.datumInputChannel, [ref]$configuredDatum) -or $configuredDatum -ne $origin) {
            throw "轴原点参数与 IO 映射不一致：$axisName，参数=$($axis.datumInputChannel)，映射=DI$origin。"
        }
        $axisSensors += @{Name=$axisName; Axis=[int]$axisNumber; Origin=$origin; Negative=$negative; Positive=$positive}
    }
    $outputs = @{}
    foreach ($name in @('夹具夹紧', '焊接启动', '压头动作', '相机触发', '相机结果复位', '下料电磁阀')) {
        $outputs[$name] = Get-ScenarioIoChannel -Items @($io.digitalOutputs) -Name $name -ControllerId $card
    }
    Assert-ClipHostStopped
    if (@(Get-ClipListener).Count -ne 0) { throw "端口 $port 已被占用；不会接管已有服务。" }
    $controller = Start-Process -FilePath $controllerExe -ArgumentList $port -WorkingDirectory (Split-Path -Parent $controllerExe) -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($controller.HasExited) { throw '软件控制器启动后退出。' }
        if (@(Get-ClipListener).Count -gt 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) { throw "软件控制器没有在端口 $port 就绪。" }
    $listener = @(Get-ClipListener)
    if ($listener.Count -ne 1 -or $listener[0].OwningProcess -ne $controller.Id -or $listener[0].LocalAddress -ne '127.0.0.1') { throw '监听进程不是本次启动的本机软件控制器。' }
    $identity = Send-ClipRequest @{ command='ping' }
    if ($identity.controllerType -ne 'MotionControl.VirtualController' -or $identity.protocolVersion -ne 2) { throw '软件控制器身份或协议不匹配。' }
    $lease = Send-ClipRequest @{ command='acquireLease' }
    if ([string]::IsNullOrWhiteSpace([string]$lease.leaseId)) { throw '控制器没有返回运动会话。' }
    Send-ClipRequest @{ command='heartbeat'; leaseId=[string]$lease.leaseId } | Out-Null
    Assert-ClipHostStopped
    foreach ($channel in $outputs.Values) {
        if ((Send-ClipRequest @{command='readOutput'; channel=$channel}).value -ne $false) { throw "DO$channel 未明确关闭。" }
    }
    foreach ($channel in $inputs.Values) {
        if ((Send-ClipRequest @{command='readInput'; channel=$channel}).value -ne $false) { throw "DI$channel 初始不为低电平。" }
    }
    foreach ($sensor in $axisSensors) {
        foreach ($channel in @($sensor.Origin, $sensor.Negative, $sensor.Positive)) {
            if ((Send-ClipRequest @{command='readInput'; channel=$channel}).value -ne $false) { throw "轴传感器 DI$channel 初始不为低电平。" }
        }
    }
    if ((Send-ClipRequest @{command='readFeeder'}).feederState.enabled) { throw '控制器已有供料规则。' }
    if ((Send-ClipRequest @{command='readCamera'}).configured) { throw '控制器已有相机规则。' }
    Send-ClipRequest @{command='configureActuator'; outputChannel=$outputs['夹具夹紧']; clampInputChannel=$inputs['夹具夹紧到位']; releaseInputChannel=$inputs['夹具松开到位']; delayMs=300} | Out-Null
    foreach ($pair in @(@{Input='焊接完成'; Output='焊接启动'}, @{Input='压装完成'; Output='压头动作'}, @{Input='下料到位'; Output='下料电磁阀'})) {
        Send-ClipRequest @{command='configureFeedback'; inputChannel=$inputs[$pair.Input]; outputChannel=$outputs[$pair.Output]; delayMs=300; activeHigh=$true} | Out-Null
    }
    Send-ClipRequest @{command='configureCamera'; triggerOutputChannel=$outputs['相机触发']; acknowledgeInputChannel=$inputs['相机触发反馈']; completeInputChannel=$inputs['检测完成']; okInputChannel=$inputs['检测OK']; ngInputChannel=$inputs['检测NG']; resetOutputChannel=$outputs['相机结果复位']; result='OK'; processingDelayMs=500} | Out-Null
    # 仅本测试场景采用固定物理原点和限位；配置本身不会使能、回零或启动运动。
    foreach ($sensor in $axisSensors) {
        Send-ClipRequest @{command='configureAxisSensors'; axis=$sensor.Axis; originInputChannel=$sensor.Origin; negativeInputChannel=$sensor.Negative; positiveInputChannel=$sensor.Positive; originPosition=-1.0; originHalfWidth=0.05; negativeLimit=-10.0; positiveLimit=10.0} | Out-Null
        $actual = (Send-ClipRequest @{command='readAxisSensors'; axis=$sensor.Axis}).axisSensors
        if ($actual.originInputChannel -ne $sensor.Origin -or $actual.negativeInputChannel -ne $sensor.Negative -or
            $actual.positiveInputChannel -ne $sensor.Positive -or $actual.originPosition -ne -1.0 -or
            $actual.originHalfWidth -ne 0.05 -or $actual.negativeLimit -ne -10.0 -or $actual.positiveLimit -ne 10.0 -or
            $actual.originFault -ne 'normal' -or $actual.negativeFault -ne 'normal' -or $actual.positiveFault -ne 'normal') {
            throw "轴传感器规则读回不一致：$($sensor.Name)。"
        }
        Write-Host "$($sensor.Name) 轴 $($sensor.Axis)：原点 DI$($sensor.Origin)、负限位 DI$($sensor.Negative)、正限位 DI$($sensor.Positive)。"
    }
    $actuator = (Send-ClipRequest @{command='readActuator'; outputChannel=$outputs['夹具夹紧']}).actuatorState
    $camera = (Send-ClipRequest @{command='readCamera'}).cameraState
    if ($actuator.clampInputChannel -ne $inputs['夹具夹紧到位'] -or $actuator.releaseInputChannel -ne $inputs['夹具松开到位'] -or
        $camera.triggerOutputChannel -ne $outputs['相机触发'] -or $camera.acknowledgeInputChannel -ne $inputs['相机触发反馈'] -or
        $camera.completeInputChannel -ne $inputs['检测完成'] -or $camera.okInputChannel -ne $inputs['检测OK'] -or
        $camera.ngInputChannel -ne $inputs['检测NG'] -or $camera.resetOutputChannel -ne $outputs['相机结果复位']) { throw '模型通道读回不一致。' }
    foreach ($pair in @(@{Input='焊接完成'; Output='焊接启动'}, @{Input='压装完成'; Output='压头动作'}, @{Input='下料到位'; Output='下料电磁阀'})) {
        $feedback = (Send-ClipRequest @{command='readFeedback'; inputChannel=$inputs[$pair.Input]}).feedbackState
        if ($feedback.outputChannel -ne $outputs[$pair.Output] -or $feedback.stuck -eq $true) { throw "反馈规则读回不一致：$($pair.Input)" }
    }
    Send-ClipRequest @{command='releaseLease'; leaseId=[string]$lease.leaseId} | Out-Null
    $lease = $null
    Write-Host "已准备夹具、焊接、压合、相机和下料规则；软件控制器 PID $($controller.Id)，端口 $port。相机测试结果默认 OK。"
    Write-Host "供料尚未启用。请先在 Host 完成设备准备，再执行："
    $loadAxisNumber = @($axisChannels.Keys | Where-Object { $axisChannels[$_] -eq 'Load_X' })[0]
    Write-Host "  & '.\tools\MotionControl.VirtualController\ControllerCommand.ps1' -Port $port -Action StartFeeder -InputChannel $($inputs['上料到位']) -OutputChannel $($outputs['夹具夹紧']) -FeederSafeAxis $loadAxisNumber -FeederSafePosition 0 -FeederSafeTolerance 0.01"
    if ($PrepareOnly) { Write-Host 'PrepareOnly 未启动 Host；请按进程号辨认本次控制器。'; return }
    Assert-ClipHostStopped
    $hostProcess = Start-Process -FilePath $hostExe -WorkingDirectory (Split-Path -Parent $hostExe) -PassThru
    Write-Host "已启动正式上位机 PID $($hostProcess.Id)；请核对现场后自行使能、回零、复位及启动。"
}
catch {
    [Console]::Error.WriteLine("夹具焊接测试准备失败：$($_.Exception.Message)")
    if ($null -ne $controller -and -not $controller.HasExited -and $null -eq $hostProcess) {
        try {
            $owned = Get-Process -Id $controller.Id -ErrorAction Stop
            if (-not [string]::Equals($owned.Path, $controllerExe, [StringComparison]::OrdinalIgnoreCase)) { throw '进程路径已变化。' }
            if (@(Get-Process -Name 'MotionControl.Host.Wpf' -ErrorAction SilentlyContinue).Count -gt 0) { throw '已有上位机运行，保留本次控制器。' }
            if ($null -eq $lease) { $lease = Send-ClipRequest @{command='acquireLease'} }
            if ([string]::IsNullOrWhiteSpace([string]$lease.leaseId)) { throw '无法确认独占会话。' }
            Send-ClipRequest @{command='heartbeat'; leaseId=[string]$lease.leaseId} | Out-Null
            Stop-Process -Id $owned.Id -ErrorAction Stop
            [Console]::Error.WriteLine("已关闭本次创建的软件控制器 PID $($owned.Id)。")
        }
        catch { [Console]::Error.WriteLine("本次控制器未自动关闭：$($_.Exception.Message)") }
    }
    exit 1
}
