param(
    [switch]$CheckOnly,
    [string]$ConfigurationRoot = (Join-Path $env:LOCALAPPDATA 'MotionControlFramework\Configurations')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$controllerExe = Join-Path $repositoryRoot 'tools\MotionControl.VirtualController\bin\Release\net48\MotionControl.VirtualController.exe'
$hostExe = Join-Path $repositoryRoot 'src\MotionControl.Host.Wpf\bin\Release\net48\MotionControl.Host.Wpf.exe'
$configurationRootPath = [IO.Path]::GetFullPath($ConfigurationRoot)
$defaultConfigurationRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'MotionControlFramework\Configurations'))
$machineDirectory = Join-Path $configurationRootPath 'Machine01'
$backendPath = Join-Path $machineDirectory 'Parameters\ControllerBackend.json'
$ioMappingPath = Join-Path $machineDirectory 'HardwareConfig\IoMapping.json'
$cardMappingPath = Join-Path $machineDirectory 'HardwareConfig\MotionCards.json'
. (Join-Path $PSScriptRoot 'ScenarioIoMapping.ps1')
$startedController = $null
$port = 18520

# 本机端口能否建立连接；只做连接探测，不发送控制命令。
function Test-ControllerPort([int]$TargetPort) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $pending = $client.BeginConnect('127.0.0.1', $TargetPort, $null, $null)
        try {
            if (-not $pending.AsyncWaitHandle.WaitOne(500)) { return $false }
            try { $client.EndConnect($pending); return $true }
            catch [System.Net.Sockets.SocketException] { return $false }
        }
        finally { $pending.AsyncWaitHandle.Dispose() }
    }
    finally { $client.Dispose() }
}

# 对本机控制器发送单行 JSON，服务端拒绝或通讯中断时明确报错。
function Send-ControllerRequest([hashtable]$Request) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $client.ReceiveTimeout = 3000
        $client.SendTimeout = 3000
        $client.Connect('127.0.0.1', $port)
        $encoding = [System.Text.UTF8Encoding]::new($false)
        $stream = $client.GetStream()
        $writer = [System.IO.StreamWriter]::new($stream, $encoding)
        $reader = [System.IO.StreamReader]::new($stream, $encoding)
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

# 核对服务身份和协议，绝不把其他本机程序当作控制器。
function Assert-ControllerIdentity {
    $identity = Send-ControllerRequest @{ command = 'ping' }
    if ($identity.controllerType -ne 'MotionControl.VirtualController' -or $identity.protocolVersion -ne 2) {
        throw '端口上的程序不是受支持的虚拟控制器，或协议版本不兼容。'
    }
    # 协议身份不能代替进程身份；拒绝其他目录中的同名程序。
    $listeners = @(Get-NetTCPConnection -LocalPort $port -ErrorAction Stop |
        Where-Object { $_.LocalAddress -eq '127.0.0.1' -and $_.RemotePort -eq 0 })
    if ($listeners.Count -ne 1) { throw "端口 $port 的本机监听进程不唯一或不是本地地址。" }
    $ownerId = [int]$listeners[0].OwningProcess
    $owner = Get-Process -Id $ownerId -ErrorAction Stop
    if (-not $owner.Path -or -not [string]::Equals($owner.Path, $controllerExe, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "端口 $port 不属于本项目的 Release 控制器；进程号 $ownerId，路径：$($owner.Path)。"
    }
    if ($null -ne $startedController -and $ownerId -ne $startedController.Id) {
        throw "本次启动的控制器与端口监听进程不一致；监听进程号 $ownerId。"
    }
}

try {
    if (-not $CheckOnly -and -not [string]::Equals($configurationRootPath, $defaultConfigurationRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw '正式 Host 使用本机默认配置目录；其他目录只能用于 CheckOnly 检查。'
    }
    if (-not (Test-Path -LiteralPath $backendPath -PathType Leaf)) {
        throw "未找到 Machine01 后端配置：$backendPath。请先显式选择 Virtual；本脚本不会创建或覆盖配置。"
    }
    try { $backend = Get-Content -LiteralPath $backendPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "Machine01 后端配置不是有效 JSON：$backendPath。$($_.Exception.Message)" }
    if ($null -eq $backend -or $backend.backend -ne 'Virtual') {
        throw 'Machine01 后端没有明确设置为 Virtual，本脚本不会启动或修改配置。'
    }
    if ($null -ne $backend.port) {
        $parsedPort = 0
        if (-not [int]::TryParse([string]$backend.port, [ref]$parsedPort) -or $parsedPort -lt 1 -or $parsedPort -gt 65535) {
            throw 'Machine01 后端端口无效，必须是 1 到 65535 的整数。'
        }
        $port = $parsedPort
    }
    if (-not (Test-Path -LiteralPath $controllerExe -PathType Leaf)) { throw "未找到 Release 控制器，请先构建：$controllerExe" }
    if (-not (Test-Path -LiteralPath $hostExe -PathType Leaf)) { throw "未找到 Release 正式上位机，请先构建：$hostExe" }
    if (-not (Test-Path -LiteralPath $ioMappingPath -PathType Leaf)) { throw "找不到 Machine01 IO 映射：$ioMappingPath" }
    try { $ioMapping = Get-Content -LiteralPath $ioMappingPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "Machine01 IO 映射不是有效 JSON：$($_.Exception.Message)" }
    if (-not (Test-Path -LiteralPath $cardMappingPath -PathType Leaf)) { throw "找不到 Machine01 控制卡配置：$cardMappingPath" }
    try { $cardMapping = Get-Content -LiteralPath $cardMappingPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "Machine01 控制卡配置不是有效 JSON：$($_.Exception.Message)" }
    $cards = @($cardMapping.motionCards)
    if ($cards.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$cards[0].controllerId)) { throw '上料样例要求一张明确配置的控制器。' }
    $feedInputChannel = Get-ScenarioIoChannel -Items @($ioMapping.digitalInputs) -Name '上料到位' -ControllerId $cards[0].controllerId
    $clampOutputChannel = Get-ScenarioIoChannel -Items @($ioMapping.digitalOutputs) -Name '夹具夹紧' -ControllerId $cards[0].controllerId

    if ($CheckOnly) {
        if (-not (Test-ControllerPort $port)) { throw "配置检查通过，但端口 $port 上没有控制器。检查模式不会启动进程。" }
        Assert-ControllerIdentity
        $status = (Send-ControllerRequest @{ command = 'readFeeder' }).feederState
        Write-Host "Machine01 后端：Virtual；控制器协议：2；本机端口：$port；上料到位 DI$feedInputChannel / 夹具夹紧 DO$clampOutputChannel。"
        $status | Format-List
        exit 0
    }

    $existingHosts = @(Get-Process -Name 'MotionControl.Host.Wpf' -ErrorAction SilentlyContinue)
    if ($existingHosts.Count -gt 0) {
        throw '检测到上位机已经运行，本脚本不会重复启动或改变测试状态。请先正常关闭旧窗口后再执行。'
    }

    if (-not (Test-ControllerPort $port)) {
        $existingControllers = @(Get-Process -Name 'MotionControl.VirtualController' -ErrorAction SilentlyContinue)
        if ($existingControllers.Count -gt 0) {
            throw "已存在控制器进程，但配置端口 $port 无法连接；为避免重复启动，请先检查现有控制器与端口。"
        }
        $startedController = Start-Process -FilePath $controllerExe -ArgumentList $port -WorkingDirectory (Split-Path -Parent $controllerExe) -WindowStyle Hidden -PassThru
        $ready = $false
        for ($attempt = 0; $attempt -lt 50; $attempt++) {
            if ($startedController.HasExited) { throw "控制器启动后退出，请检查端口 $port 和运行库。" }
            if (Test-ControllerPort $port) { $ready = $true; break }
            Start-Sleep -Milliseconds 100
        }
        if (-not $ready) { throw "控制器启动后未在端口 $port 就绪。" }
        Write-Host "已启动本次控制器，进程号 $($startedController.Id)，端口 $port。"
    }
    else { Write-Host "检测到端口 $port 已有服务，正在检查身份。" }
    Assert-ControllerIdentity

    # 只读取现有规则；已有规则不得自动复位、覆盖或清除缺料故障。
    $feeder = (Send-ControllerRequest @{ command = 'readFeeder' }).feederState
    if ($null -eq $feeder) { throw '控制器没有返回上料规则状态。' }
    if ($feeder.enabled -and ($feeder.inputChannel -ne $feedInputChannel -or $feeder.outputChannel -ne $clampOutputChannel)) {
        throw "已有上料规则使用 DI$($feeder.inputChannel) / DO$($feeder.outputChannel)，本脚本不会覆盖。"
    }

    # 短暂申请独占租约，确认没有其他程序正在控制设备；规则设置期间保持独占。
    $lease = Send-ControllerRequest @{ command = 'acquireLease' }
    if ([string]::IsNullOrWhiteSpace([string]$lease.leaseId)) { throw '控制器没有返回有效的运动会话。' }
    try {
        Send-ControllerRequest @{ command = 'heartbeat'; leaseId = [string]$lease.leaseId } | Out-Null
        # 获得租约后再次检查，避免准备期间覆盖另一操作者启用的规则。
        $feeder = (Send-ControllerRequest @{ command = 'readFeeder' }).feederState
        if ($feeder.enabled) {
            if ($feeder.inputChannel -ne $feedInputChannel -or $feeder.outputChannel -ne $clampOutputChannel) {
                throw '上料规则在准备期间发生变化，本脚本不会覆盖。'
            }
            Write-Host "保留现有上料规则：DI$feedInputChannel / DO$clampOutputChannel；缺料故障=$($feeder.fault)，首次延迟=$($feeder.feedDelayMs) ms，后续延迟=$($feeder.emptyDelayMs) ms。"
        }
        else {
            $clampOutput = (Send-ControllerRequest @{ command = 'readOutput'; channel = $clampOutputChannel }).value
            if ($clampOutput) { throw '夹具输出当前已开启，不允许启用上料规则。' }
            if (@(Get-Process -Name 'MotionControl.Host.Wpf' -ErrorAction SilentlyContinue).Count -gt 0) {
                throw '准备期间检测到上位机已启动，不会改变上料规则。'
            }
            Send-ControllerRequest @{ command = 'configureFeeder'; enabled = $true; inputChannel = $feedInputChannel; outputChannel = $clampOutputChannel; feedDelayMs = 1000; emptyDelayMs = 1500 } | Out-Null
            Write-Host "已启用上料规则：DI$feedInputChannel 上料到位，DO$clampOutputChannel 夹具；首次 1000 ms、后续 1500 ms。"
        }
    }
    finally {
        Send-ControllerRequest @{ command = 'releaseLease'; leaseId = [string]$lease.leaseId } | Out-Null
    }

    if (@(Get-Process -Name 'MotionControl.Host.Wpf' -ErrorAction SilentlyContinue).Count -gt 0) {
        throw '准备期间检测到上位机已启动，本脚本不会再启动一个窗口。'
    }
    $startedHost = Start-Process -FilePath $hostExe -WorkingDirectory (Split-Path -Parent $hostExe) -PassThru
    Write-Host "已启动正式上位机，进程号 $($startedHost.Id)。请在上位机确认连接与设备状态后手动操作；脚本不会启动任务或伺服。"
}
catch {
    [Console]::Error.WriteLine("启动上料测试失败：$($_.Exception.Message)")
    if ($null -ne $startedController -and -not $startedController.HasExited) {
        [Console]::Error.WriteLine("本次启动的控制器仍在运行，进程号 $($startedController.Id)；脚本没有关闭任何进程。")
    }
    exit 1
}
