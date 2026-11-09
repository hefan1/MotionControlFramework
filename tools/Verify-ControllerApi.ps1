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

    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $controllerProcess.Refresh()
        if ($controllerProcess.HasExited) { throw "软件控制器提前退出，退出码 $($controllerProcess.ExitCode)。" }
        try {
            $identity = Read-ControllerApi -Port $port -Request '{"command":"ping"}'
            if ($identity.ok -eq $true -and $identity.controllerType -eq "MotionControl.VirtualController" -and $identity.protocolVersion -eq 2) {
                $ready = $true
                break
            }
        }
        catch {
            # 控制器刚启动时端口可能尚未就绪，短暂重试，不能当作连接成功。
        }
        Start-Sleep -Milliseconds 150
    }
    if (-not $ready) { throw "软件控制器没有返回预期身份和协议版本，拒绝下发轴动作。" }

    Write-Host "[开始] 走 IMotionCard / IAxis 的真实 API 路径"
    & $checkExe "--controller-api" "$port"
    $checkExitCode = $LASTEXITCODE

    # 不论检查结果如何，先通过只读 API 核对测试轴没有继续运动。
    $feedback = Read-ControllerApi -Port $port -Request '{"command":"readAxis","axis":1}'
    if ($feedback.ok -ne $true -or $null -eq $feedback.axisState) {
        throw "软件控制器没有返回可信的轴状态。"
    }
    if ($null -eq $feedback.axisState.velocity -or $feedback.axisState.moving -ne $false) {
        throw "测试轴速度未提供或仍在运动，不能确认停止。"
    }
    $velocity = [double]$feedback.axisState.velocity
    if ([double]::IsNaN($velocity) -or [double]::IsInfinity($velocity) -or [Math]::Abs($velocity) -gt 0.001) {
        throw "API 检查结束后，测试轴速度无效或尚未停稳，不得报告为通过。"
    }
    if ($checkExitCode -ne 0) { throw "适配器 API 检查失败，退出码 $checkExitCode。" }
    Write-Host "[通过] 独立软件控制器 API 往返、停止反馈和安全收尾"
}
finally {
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
