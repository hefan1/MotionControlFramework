param(
    [Parameter(Mandatory = $true)][string]$HostDirectory,
    [int]$Port = 19713,
    [string]$ResultDirectory = $env:TEMP,
    [switch]$FakeServer,
    [int]$ClaimedProcessId = 0,
    [string]$ClaimedSession = ''
)

$ErrorActionPreference = 'Stop'
$pipeName = 'MotionControl.VirtualController.' + $Port
if (-not $FakeServer) {
    $ResultDirectory = Join-Path ([IO.Path]::GetFullPath($ResultDirectory)) ('HostPipeChecks-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($ResultDirectory) | Out-Null
}
$readyPath = Join-Path $ResultDirectory 'fake-pipe-ready.txt'
$requestPath = Join-Path $ResultDirectory 'fake-pipe-requests.txt'
if ($FakeServer) {
    while ($true) {
        $pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::InOut, 4, [IO.Pipes.PipeTransmissionMode]::Byte)
        [IO.File]::WriteAllText($readyPath, [string]$PID)
        try {
            $pipe.WaitForConnection()
            $reader = [IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8)
            $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false)); $writer.AutoFlush = $true
            $line = $reader.ReadLine()
            if ($null -ne $line) {
                [IO.File]::AppendAllText($requestPath, $line + [Environment]::NewLine)
                $writer.WriteLine((@{ok=$true;controllerType='MotionControl.VirtualController';protocolVersion=2;processId=$ClaimedProcessId;projectSession=$ClaimedSession;leaseId='fake'} | ConvertTo-Json -Compress))
            }
        } catch {} finally { $pipe.Dispose() }
    }
}

# 只关闭本检查启动的软件控制器；先确认进程路径和控制台成员。
function Stop-TestController($Process, [string]$ExpectedPath) {
    $current = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $Process.Id)
    if ($null -eq $current) { return }
    if ($current.ExecutablePath -ne $ExpectedPath) { throw '测试控制器进程身份已改变，未关闭。' }
    [void][TestPipeConsole]::FreeConsole()
    if (-not [TestPipeConsole]::AttachConsole([uint32]$Process.Id)) { throw '无法连接测试控制器控制台。' }
    try {
        $ids=New-Object 'UInt32[]' 16; $count=[TestPipeConsole]::GetConsoleProcessList($ids,[uint32]$ids.Length)
        if ($count -lt 1 -or $count -gt $ids.Length -or @($ids[0..($count-1)] | Where-Object { $_ -ne $Process.Id -and $_ -ne $PID }).Count) { throw '测试控制台包含未知进程。' }
        if (-not [TestPipeConsole]::SetConsoleCtrlHandler([IntPtr]::Zero,$true) -or -not [TestPipeConsole]::GenerateConsoleCtrlEvent(0,0)) { throw '无法正常关闭测试控制器。' }
        if (-not $Process.WaitForExit(10000)) { throw '测试控制器没有退出。' }
    } finally {
        [void][TestPipeConsole]::FreeConsole()
        # 避免后续测试进程继承忽略 Ctrl+C 的设置。
        [void][TestPipeConsole]::SetConsoleCtrlHandler([IntPtr]::Zero,$false)
    }
}

$hostRoot = [IO.Path]::GetFullPath($HostDirectory)
$controllerPath = Join-Path $hostRoot 'MotionControl.VirtualController.exe'
$adapterPath = Join-Path $hostRoot 'MotionControl.Adapter.MotionCard.Virtual.dll'
$contractsPath = Join-Path $hostRoot 'MotionControl.Framework.Contracts.dll'
[IO.Directory]::CreateDirectory($ResultDirectory) | Out-Null
[Reflection.Assembly]::LoadFrom($contractsPath) | Out-Null
[Reflection.Assembly]::LoadFrom($adapterPath) | Out-Null
$adapterType = [MotionControl.Adapter.MotionCard.Virtual.VirtualMotionCard]
if (-not @($adapterType.GetConstructors() | Where-Object { $_.GetParameters().Count -eq 8 }).Count) { throw '适配器没有提供可校验项目会话的连接入口。' }
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class TestPipeConsole {
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool FreeConsole();
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool AttachConsole(uint p);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern uint GetConsoleProcessList(uint[] p,uint n);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool SetConsoleCtrlHandler(IntPtr h,bool a);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GenerateConsoleCtrlEvent(uint s,uint g);
}
"@
$controller=$null; $otherController=$null; $fake=$null
$session='a' * 64
try {
    foreach ($checkPort in @($Port, ($Port+1))) {
        $probe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$checkPort); $probe.ExclusiveAddressUse=$true
        try {$probe.Start()} finally {$probe.Stop()}
    }
    $controller=Start-Process -FilePath $controllerPath -ArgumentList @([string]$Port,'--project-session',$session) -WorkingDirectory $hostRoot -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 700
    if ($controller.HasExited) { throw '测试控制器启动失败。' }
    $axes=[MotionControl.Framework.Contracts.Configuration.AxisHardwareDefinition[]]@()
    $inputs=[MotionControl.Framework.Contracts.Configuration.DigitalIoHardwareDefinition[]]@()
    $outputs=[MotionControl.Framework.Contracts.Configuration.DigitalIoHardwareDefinition[]]@()
    $card=[MotionControl.Adapter.MotionCard.Virtual.VirtualMotionCard]::new('test',$axes,$inputs,$outputs,$Port,$ResultDirectory,$controllerPath,$session)
    $card.ConnectAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if (-not $card.IsConnected) { throw '本程序的软件控制器没有连接。' }
    $card.DisconnectAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Stop-TestController $controller $controllerPath
    $controller=$null

    # 同一路径的另一项目控制器也不能取得当前项目的租约。
    $controller=Start-Process -FilePath $controllerPath -ArgumentList @([string]$Port,'--project-session',('b'*64)) -WorkingDirectory $hostRoot -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 700
    if ($controller.HasExited) { throw '另一项目测试控制器启动失败。' }
    $wrongProject=[MotionControl.Adapter.MotionCard.Virtual.VirtualMotionCard]::new('test',$axes,$inputs,$outputs,$Port,$ResultDirectory,$controllerPath,$session)
    $rejected=$false
    try { $wrongProject.ConnectAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult() } catch { $rejected=$true }
    if (-not $rejected -or $wrongProject.IsConnected) { throw '另一项目的软件控制器被错误接管。' }
    $client=[Net.Sockets.TcpClient]::new('127.0.0.1',$Port)
    try {
        $stream=$client.GetStream();$stream.ReadTimeout=3000
        $reader=[IO.StreamReader]::new($stream,[Text.Encoding]::UTF8)
        $writer=[IO.StreamWriter]::new($stream,[Text.UTF8Encoding]::new($false));$writer.AutoFlush=$true
        $writer.WriteLine('{"command":"acquireLease"}')
        $lease=$reader.ReadLine()|ConvertFrom-Json
        if (-not $lease.ok -or -not $lease.leaseId) { throw '另一项目的租约在拒绝后仍被占用。' }
        $writer.WriteLine((@{command='releaseLease';leaseId=$lease.leaseId}|ConvertTo-Json -Compress))
        if (-not (($reader.ReadLine()|ConvertFrom-Json).ok)) { throw '测试租约未释放。' }
    } finally { $client.Dispose() }
    Stop-TestController $controller $controllerPath
    $controller=$null

    # 假服务声称另一个真实控制器的 PID；客户端必须在发送命令前核验管道实际进程。
    $otherController=Start-Process -FilePath $controllerPath -ArgumentList @([string]($Port+1),'--project-session',$session) -WorkingDirectory $hostRoot -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 500
    $args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-HostDirectory',('"'+$hostRoot+'"'),'-Port',[string]$Port,'-ResultDirectory',('"'+$ResultDirectory+'"'),'-FakeServer','-ClaimedProcessId',[string]$otherController.Id,'-ClaimedSession',$session)
    $fake=Start-Process -FilePath 'powershell.exe' -ArgumentList $args -WindowStyle Hidden -PassThru
    for($i=0;$i -lt 80 -and -not (Test-Path -LiteralPath $readyPath);$i++){Start-Sleep -Milliseconds 100}
    if (-not (Test-Path -LiteralPath $readyPath)) { throw '假服务没有启动。' }
    $replacement=[MotionControl.Adapter.MotionCard.Virtual.VirtualMotionCard]::new('test',$axes,$inputs,$outputs,$Port,$ResultDirectory,$controllerPath,$session)
    $rejected=$false
    try { $replacement.ConnectAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult() } catch { $rejected=$true }
    Start-Sleep -Milliseconds 200
    if (-not $rejected -or $replacement.IsConnected -or (Test-Path -LiteralPath $requestPath)) { throw '替换服务没有在命令发送前被拒绝。' }
    Write-Output '通过：本项目控制器可连接；其他项目控制器及伪造真实 PID 的替换服务均被拒绝。'
} finally {
    if ($fake -and -not $fake.HasExited) {
        $current=Get-CimInstance Win32_Process -Filter ('ProcessId=' + $fake.Id)
        if ($current -and $current.Name -eq 'powershell.exe' -and $current.CommandLine -like '*HostPipeChecks.ps1*' -and $current.CommandLine -like '*-FakeServer*') { Stop-Process -Id $fake.Id }
        else { throw '假服务进程身份变化，未关闭。' }
    }
    if ($controller) { Stop-TestController $controller $controllerPath }
    if ($otherController) { Stop-TestController $otherController $controllerPath }
}
