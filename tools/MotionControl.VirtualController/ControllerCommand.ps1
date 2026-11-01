param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Status', 'StartFeeder', 'StopFeeder', 'MissingMaterial', 'RestoreFeeder', 'SetInput', 'AxisAlarm', 'ConfigureFeedback', 'FeedbackStatus', 'FeedbackStuck', 'FeedbackRestore', 'RemoveFeedback')]
    [string]$Action,
    [int]$Port = 18520,
    [int]$InputChannel = 4,
    [int]$OutputChannel = 0,
    [int]$FeedDelayMs = 1000,
    [int]$EmptyDelayMs = 1500,
    [int]$FeedbackDelayMs = 300,
    [bool]$FeedbackActiveHigh = $true,
    [int]$Axis = 1,
    [bool]$Value = $false
)

$ErrorActionPreference = 'Stop'

# 本脚本只连接本机测试控制器，失败时返回非零退出码。
try {
    switch ($Action) {
        'Status' { $request = @{ command = 'readFeeder' } }
        'StartFeeder' {
            $request = @{ command = 'configureFeeder'; enabled = $true; inputChannel = $InputChannel; outputChannel = $OutputChannel; feedDelayMs = $FeedDelayMs; emptyDelayMs = $EmptyDelayMs }
        }
        'StopFeeder' {
            $request = @{ command = 'configureFeeder'; enabled = $false; inputChannel = $InputChannel; outputChannel = $OutputChannel; feedDelayMs = $FeedDelayMs; emptyDelayMs = $EmptyDelayMs }
        }
        'MissingMaterial' { $request = @{ command = 'setFeederFault'; value = $true } }
        'RestoreFeeder' { $request = @{ command = 'setFeederFault'; value = $false } }
        'SetInput' { $request = @{ command = 'setInput'; channel = $InputChannel; value = $Value } }
        'AxisAlarm' { $request = @{ command = 'setAxisAlarm'; axis = $Axis; value = $true } }
        'ConfigureFeedback' { $request = @{ command = 'configureFeedback'; inputChannel = $InputChannel; outputChannel = $OutputChannel; delayMs = $FeedbackDelayMs; activeHigh = $FeedbackActiveHigh } }
        'FeedbackStatus' { $request = @{ command = 'readFeedback'; inputChannel = $InputChannel } }
        'FeedbackStuck' { $request = @{ command = 'setFeedbackFault'; inputChannel = $InputChannel; value = $true } }
        'FeedbackRestore' { $request = @{ command = 'setFeedbackFault'; inputChannel = $InputChannel; value = $false } }
        'RemoveFeedback' { $request = @{ command = 'removeFeedback'; inputChannel = $InputChannel } }
    }

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $client.ReceiveTimeout = 5000
        $client.SendTimeout = 5000
        $client.Connect('127.0.0.1', $Port)
        $encoding = [System.Text.UTF8Encoding]::new($false)
        $stream = $client.GetStream()
        $writer = [System.IO.StreamWriter]::new($stream, $encoding)
        $reader = [System.IO.StreamReader]::new($stream, $encoding)
        $writer.AutoFlush = $true
        $writer.WriteLine('{"command":"ping"}')
        $identityLine = $reader.ReadLine()
        if ($null -eq $identityLine) { throw '控制器没有返回身份。' }
        $identity = $identityLine | ConvertFrom-Json
        if (-not $identity.ok -or $identity.controllerType -ne 'MotionControl.VirtualController' -or $identity.protocolVersion -ne 2) { throw '控制器身份或协议版本不匹配。' }
        $writer.WriteLine(($request | ConvertTo-Json -Compress))
        $line = $reader.ReadLine()
        if ($null -eq $line) { throw '控制器没有返回结果。' }
        $response = $line | ConvertFrom-Json
        if (-not $response.ok) { throw "控制器拒绝请求：$($response.error)" }
        if ($Action -eq 'Status') {
            $response.feederState | Format-List
        }
        elseif ($Action -eq 'FeedbackStatus') {
            $response.feedbackState | Format-List
        }
        else { Write-Host "已完成：$Action" }
    }
    finally { $client.Dispose() }
}
catch {
    Write-Error "控制器命令失败：$($_.Exception.Message)"
    exit 1
}
