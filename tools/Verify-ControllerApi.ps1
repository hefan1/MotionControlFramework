# 本脚本只启动仓库已有的独立软件控制器，并通过适配器 API 检查轴动作。
# 不创建 FakeAxis、内存设备或新运动模型；不连接厂商 SDK 和现场控制器。
param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "tmp/plcopen-controller-api"
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
$controllerProcess = $null
$secondControllerProcess = $null

# 与运行中的其他项目隔离：由系统分配临时本机 TCP 端口，然后立即释放给本次控制器。
function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

# 仅发送明确的状态读取请求，不创建控制器或修改运动命令。
function Read-ControllerApi([int]$Port, [string]$Request) {
    $client = [System.Net.Sockets.TcpClient]::new()
    $client.ReceiveTimeout = 1500
    $client.SendTimeout = 1500
    try {
        $client.Connect("127.0.0.1", $Port)
        $stream = $client.GetStream()
        $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false))
        $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($stream)
        $writer.WriteLine($Request)
        $response = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($response)) {
            throw "控制器没有返回 API 响应。"
        }
        return $response | ConvertFrom-Json
    }
    finally {
        $client.Dispose()
    }
}

# 连接前先确认本轮启动的是预期控制器程序及协议，而不是误连其他服务。
function Confirm-ControllerReady([int]$Port, [System.Diagnostics.Process]$ControllerProcess) {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $ControllerProcess.Refresh()
        if ($ControllerProcess.HasExited) {
            throw "软件控制器在端口 $Port 上提前退出，退出码 $($ControllerProcess.ExitCode)。"
        }
        try {
            $identity = Read-ControllerApi -Port $Port -Request '{"command":"ping"}'
            if ($identity.ok -eq $true -and $identity.controllerType -eq "MotionControl.VirtualController"
                -and $identity.protocolVersion -eq 2) {
                return
            }
        }
        catch {
            # 本进程刚启动时服务可能尚未就绪。继续重试，不能把连接失败当作成功。
        }
        Start-Sleep -Milliseconds 150
    }
    throw "软件控制器端口 $Port 没有返回预期身份与协议，拒绝下发运动命令。"
}

# 只读核对实际控制器反馈。速度无效、未停稳时不能报告任何测试成功。
function Confirm-ControllerAxisStopped([int]$Port, [int]$AxisNumber) {
    $query = '{"command":"readAxis","axis":' + $AxisNumber + '}'
    $feedback = Read-ControllerApi -Port $Port -Request $query
    if ($feedback.ok -ne $true -or $null -eq $feedback.axisState) {
        throw "控制器端口 $Port 没有返回轴 $AxisNumber 的可信状态。"
    }
    if ($null -eq $feedback.axisState.velocity -or $feedback.axisState.moving -ne $false) {
        throw "控制器端口 $Port 的轴 $AxisNumber 速度未提供或仍在运动。"
    }
    $velocity = [double]$feedback.axisState.velocity
    if ([double]::IsNaN($velocity) -or [double]::IsInfinity($velocity)
        -or [Math]::Abs($velocity) -gt 0.001) {
        throw "控制器端口 $Port 的轴 $AxisNumber 速度无效或尚未停稳。"
    }
}

try {
    if ($env:OS -ne "Windows_NT") {
        throw "本验证需要 Windows 和 .NET Framework 4.8。"
    }

    $controllerOutput = Join-Path $OutputRoot "controller"
    $adapterOutput = Join-Path $OutputRoot "adapter"
    New-Item -ItemType Directory -Path $controllerOutput -Force | Out-Null
    New-Item -ItemType Directory -Path $adapterOutput -Force | Out-Null

    Write-Host "[开始] 构建现有独立软件控制器"
    dotnet build tools/MotionControl.VirtualController/MotionControl.VirtualController.csproj -c $Configuration -o $controllerOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw "软件控制器构建失败，退出码 $LASTEXITCODE。" }
    Write-Host "[开始] 构建调用正式适配器 API 的检查程序"
    dotnet build tools/MotionControl.VirtualAdapter.Checks/MotionControl.VirtualAdapter.Checks.csproj -c $Configuration -o $adapterOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw "运动适配器检查程序构建失败，退出码 $LASTEXITCODE。" }

    $controllerExe = (Resolve-Path (Join-Path $controllerOutput "MotionControl.VirtualController.exe")).Path
    $checkExe = (Resolve-Path (Join-Path $adapterOutput "MotionControl.VirtualAdapter.Checks.exe")).Path
    $port = Get-FreeLoopbackPort
    Write-Host "[开始] 启动隔离软件控制器：127.0.0.1:$port"
    $controllerProcess = Start-Process -FilePath $controllerExe -ArgumentList @("$port") -WorkingDirectory (Split-Path $controllerExe) -PassThru -WindowStyle Hidden

    Confirm-ControllerReady -Port $port -ControllerProcess $controllerProcess

    Write-Host "[开始] 走 IMotionCard / IAxis 的真实 API 路径"
    & $checkExe "--controller-api" "$port"
    $checkExitCode = $LASTEXITCODE

    # 原有单控制器检查使用轴 1、2，仍严格按本轮控制器状态验收。
    foreach ($axisNumber in @(1, 2)) {
        Confirm-ControllerAxisStopped -Port $port -AxisNumber $axisNumber
    }
    if ($checkExitCode -ne 0) { throw "原适配器 API 检查失败，退出码 $checkExitCode。" }
    Write-Host "[通过] 单控制器正式 API 与停止反馈"

    # 再启动第二份同一个现有软件控制器程序，不创建新的仿真轴实现或服务协议。
    $secondPort = Get-FreeLoopbackPort
    Write-Host "[开始] 启动第二份隔离软件控制器：127.0.0.1:$secondPort"
    $secondControllerProcess = Start-Process -FilePath $controllerExe -ArgumentList @("$secondPort") -WorkingDirectory (Split-Path $controllerExe) -PassThru -WindowStyle Hidden
    Confirm-ControllerReady -Port $secondPort -ControllerProcess $secondControllerProcess

    Write-Host "[开始] 通过 MotionCardGroup 连接两份独立软件控制器"
    & $checkExe "--multi-controller-api" "$port" "$secondPort"
    $multiExitCode = $LASTEXITCODE
    # 两个控制器各自拥有独立轴号和数据，不能用第一个控制器的反馈代替第二个。
    Confirm-ControllerAxisStopped -Port $port -AxisNumber 3
    Confirm-ControllerAxisStopped -Port $secondPort -AxisNumber 4
    if ($multiExitCode -ne 0) { throw "多控制器 API 检查失败，退出码 $multiExitCode。" }
    Write-Host "[通过] 多控制器隔离、统一轴接口和停轴反馈"
}
finally {
    if ($secondControllerProcess -ne $null) {
        $secondControllerProcess.Refresh()
        if (-not $secondControllerProcess.HasExited) {
            Stop-Process -Id $secondControllerProcess.Id -Force
            $secondControllerProcess.WaitForExit(5000) | Out-Null
        }
        $secondControllerProcess.Dispose()
    }
    if ($controllerProcess -ne $null) {
        $controllerProcess.Refresh()
        if (-not $controllerProcess.HasExited) {
            # 此进程只由本脚本创建；停止前的轴状态已由检查程序与上方只读查询核对。
            Stop-Process -Id $controllerProcess.Id -Force
            $controllerProcess.WaitForExit(5000) | Out-Null
        }
        $controllerProcess.Dispose()
    }
    Pop-Location
}
