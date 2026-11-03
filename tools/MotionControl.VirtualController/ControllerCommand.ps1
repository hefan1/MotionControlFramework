param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Status', 'StartFeeder', 'StopFeeder', 'MissingMaterial', 'RestoreFeeder', 'SetInput', 'AxisAlarm', 'ConfigureAxisSensors', 'AxisSensorsStatus', 'AxisSensorFault', 'RemoveAxisSensors', 'ConfigureFeedback', 'FeedbackStatus', 'FeedbackStuck', 'FeedbackRestore', 'RemoveFeedback', 'ConfigureCamera', 'CameraStatus', 'CameraResult', 'CameraResolve', 'CameraNoCompletion', 'CameraRestore', 'CameraResume', 'CameraReset', 'RemoveCamera', 'ConfigureActuator', 'ActuatorStatus', 'ActuatorStuck', 'ActuatorRestore', 'RemoveActuator', 'SetCommandFault', 'CommandFaultStatus', 'ClearCommandFault')]
    [string]$Action,
    [int]$Port = 18520,
    [int]$InputChannel = 4,
    [int]$OutputChannel = 0,
    [int]$FeedDelayMs = 1000,
    [int]$EmptyDelayMs = 1500,
    [int]$FeederSafeAxis,
    [double]$FeederSafePosition,
    [double]$FeederSafeTolerance,
    [int]$FeedbackDelayMs = 300,
    [bool]$FeedbackActiveHigh = $true,
    [int]$CameraTriggerOutput,
    [int]$CameraAcknowledgeInput,
    [int]$CameraCompleteInput,
    [int]$CameraResetOutput = -1,
    [int]$CameraProcessingDelayMs = 500,
    [int]$CameraOkInput = -1,
    [int]$CameraNgInput = -1,
    [ValidateSet('OK', 'NG', 'Invalid')]
    [string]$CameraResult = 'Invalid',
    [int]$ActuatorClampedInput,
    [int]$ActuatorReleasedInput,
    [int]$ActuatorDelayMs = 300,
    [int]$Axis = 1,
    [int]$AxisOriginInput,
    [int]$AxisNegativeInput,
    [int]$AxisPositiveInput,
    [double]$AxisOriginPosition,
    [double]$AxisOriginHalfWidth,
    [double]$AxisNegativeLimit,
    [double]$AxisPositiveLimit,
    [ValidateSet('origin', 'negative', 'positive')]
    [string]$AxisSensor,
    [ValidateSet('normal', 'stuckOff', 'stuckOn')]
    [string]$AxisSensorMode,
    [bool]$Value = $false,
    [ValidateSet('readInput', 'readOutput', 'writeOutput', 'readAxis', 'stop')]
    [string]$TargetCommand = 'writeOutput',
    [int]$TargetIndex = 0,
    [ValidateSet('reject', 'errorAfter', 'delayReply', 'delayBefore')]
    [string]$FaultMode = 'reject',
    [int]$FaultDelayMs = 0
)

$ErrorActionPreference = 'Stop'

# 本脚本只连接本机测试控制器，失败时返回非零退出码。
try {
    $cameraResultValue = if ($CameraResult -ieq 'Invalid') { 'Invalid' } else { $CameraResult.ToUpperInvariant() }
    if ($Action -in @('ActuatorStatus', 'ActuatorStuck', 'ActuatorRestore', 'RemoveActuator') -and -not $PSBoundParameters.ContainsKey('OutputChannel')) {
        throw '夹具命令必须显式指定 OutputChannel。'
    }
    if ($Action -in @('ConfigureAxisSensors', 'AxisSensorsStatus', 'AxisSensorFault', 'RemoveAxisSensors') -and -not $PSBoundParameters.ContainsKey('Axis')) {
        throw '轴传感器命令必须显式指定 Axis。'
    }
    switch ($Action) {
        'Status' { $request = @{ command = 'readFeeder' } }
        'StartFeeder' {
            $request = @{ command = 'configureFeeder'; enabled = $true; inputChannel = $InputChannel; outputChannel = $OutputChannel; feedDelayMs = $FeedDelayMs; emptyDelayMs = $EmptyDelayMs }
            $safeParameters = @('FeederSafeAxis', 'FeederSafePosition', 'FeederSafeTolerance')
            $supplied = @($safeParameters | Where-Object { $PSBoundParameters.ContainsKey($_) })
            if ($supplied.Count -ne 0 -and $supplied.Count -ne 3) { throw '上料安全轴、安全位置和容差必须全部明确填写。' }
            if ($supplied.Count -eq 3) {
                $request.safeAxis = $FeederSafeAxis
                $request.safePosition = $FeederSafePosition
                $request.safeTolerance = $FeederSafeTolerance
            }
        }
        'StopFeeder' {
            $request = @{ command = 'configureFeeder'; enabled = $false; inputChannel = $InputChannel; outputChannel = $OutputChannel; feedDelayMs = $FeedDelayMs; emptyDelayMs = $EmptyDelayMs }
        }
        'MissingMaterial' { $request = @{ command = 'setFeederFault'; value = $true } }
        'RestoreFeeder' { $request = @{ command = 'setFeederFault'; value = $false } }
        'SetInput' { $request = @{ command = 'setInput'; channel = $InputChannel; value = $Value } }
        'AxisAlarm' { $request = @{ command = 'setAxisAlarm'; axis = $Axis; value = $true } }
        'ConfigureAxisSensors' {
            foreach ($name in @('AxisOriginInput', 'AxisNegativeInput', 'AxisPositiveInput', 'AxisOriginPosition', 'AxisOriginHalfWidth', 'AxisNegativeLimit', 'AxisPositiveLimit')) {
                if (-not $PSBoundParameters.ContainsKey($name)) { throw "配置轴传感器必须显式指定 $name。" }
            }
            $request = @{ command='configureAxisSensors'; axis=$Axis; originInputChannel=$AxisOriginInput; negativeInputChannel=$AxisNegativeInput; positiveInputChannel=$AxisPositiveInput; originPosition=$AxisOriginPosition; originHalfWidth=$AxisOriginHalfWidth; negativeLimit=$AxisNegativeLimit; positiveLimit=$AxisPositiveLimit }
        }
        'AxisSensorsStatus' { $request = @{ command='readAxisSensors'; axis=$Axis } }
        'AxisSensorFault' {
            if (-not $PSBoundParameters.ContainsKey('AxisSensor') -or -not $PSBoundParameters.ContainsKey('AxisSensorMode')) { throw '必须显式指定 AxisSensor 和 AxisSensorMode。' }
            $request = @{ command='setAxisSensorFault'; axis=$Axis; sensor=$AxisSensor; mode=$AxisSensorMode }
        }
        'RemoveAxisSensors' { $request = @{ command='removeAxisSensors'; axis=$Axis } }
        'ConfigureFeedback' { $request = @{ command = 'configureFeedback'; inputChannel = $InputChannel; outputChannel = $OutputChannel; delayMs = $FeedbackDelayMs; activeHigh = $FeedbackActiveHigh } }
        'FeedbackStatus' { $request = @{ command = 'readFeedback'; inputChannel = $InputChannel } }
        'FeedbackStuck' { $request = @{ command = 'setFeedbackFault'; inputChannel = $InputChannel; value = $true } }
        'FeedbackRestore' { $request = @{ command = 'setFeedbackFault'; inputChannel = $InputChannel; value = $false } }
        'RemoveFeedback' { $request = @{ command = 'removeFeedback'; inputChannel = $InputChannel } }
        'ConfigureCamera' {
            foreach ($name in @('CameraTriggerOutput', 'CameraAcknowledgeInput', 'CameraCompleteInput')) {
                if (-not $PSBoundParameters.ContainsKey($name)) { throw "配置相机必须显式指定 $name。" }
            }
            if (($CameraOkInput -ge 0 -or $CameraNgInput -ge 0) -and -not $PSBoundParameters.ContainsKey('CameraResult')) { throw '配置相机结果输入时必须显式指定 CameraResult。' }
            $request = @{ command = 'configureCamera'; triggerOutputChannel = $CameraTriggerOutput; acknowledgeInputChannel = $CameraAcknowledgeInput; completeInputChannel = $CameraCompleteInput; resetOutputChannel = $CameraResetOutput; okInputChannel = $CameraOkInput; ngInputChannel = $CameraNgInput; result = $cameraResultValue; processingDelayMs = $CameraProcessingDelayMs }
        }
        'CameraStatus' { $request = @{ command = 'readCamera' } }
        'CameraResult' {
            if (-not $PSBoundParameters.ContainsKey('CameraResult')) { throw '请显式指定 CameraResult。' }
            $request = @{ command = 'setCameraResult'; result = $cameraResultValue }
        }
        'CameraResolve' {
            if (-not $PSBoundParameters.ContainsKey('CameraResult')) { throw '请显式指定 CameraResult 为 OK 或 NG。' }
            $request = @{ command = 'resolveCameraResult'; result = $cameraResultValue }
        }
        'CameraNoCompletion' { $request = @{ command = 'setCameraFault'; value = $true } }
        'CameraRestore' { $request = @{ command = 'setCameraFault'; value = $false } }
        'CameraResume' { $request = @{ command = 'resumeCamera' } }
        'CameraReset' { $request = @{ command = 'resetCamera' } }
        'RemoveCamera' { $request = @{ command = 'removeCamera' } }
        'ConfigureActuator' {
            foreach ($name in @('OutputChannel', 'ActuatorClampedInput', 'ActuatorReleasedInput')) {
                if (-not $PSBoundParameters.ContainsKey($name)) { throw "配置夹具必须显式指定 $name。" }
            }
            $request = @{ command = 'configureActuator'; outputChannel = $OutputChannel; clampInputChannel = $ActuatorClampedInput; releaseInputChannel = $ActuatorReleasedInput; delayMs = $ActuatorDelayMs }
        }
        'ActuatorStatus' { $request = @{ command = 'readActuator'; outputChannel = $OutputChannel } }
        'ActuatorStuck' { $request = @{ command = 'setActuatorFault'; outputChannel = $OutputChannel; value = $true } }
        'ActuatorRestore' { $request = @{ command = 'setActuatorFault'; outputChannel = $OutputChannel; value = $false } }
        'RemoveActuator' { $request = @{ command = 'removeActuator'; outputChannel = $OutputChannel } }
        'SetCommandFault' { $request = @{ command = 'setCommandFault'; targetCommand = $TargetCommand; targetIndex = $TargetIndex; mode = $FaultMode; delayMs = $FaultDelayMs } }
        'CommandFaultStatus' { $request = @{ command = 'readCommandFault'; targetCommand = $TargetCommand; targetIndex = $TargetIndex } }
        'ClearCommandFault' { $request = @{ command = 'clearCommandFault'; targetCommand = $TargetCommand; targetIndex = $TargetIndex } }
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
        elseif ($Action -eq 'CameraStatus') {
            if ($response.configured) { $response.cameraState | Format-List }
            else { Write-Host '相机规则未配置。' }
        }
        elseif ($Action -eq 'ActuatorStatus') {
            $response.actuatorState | Format-List
        }
        elseif ($Action -eq 'AxisSensorsStatus') {
            $response.axisSensors | Format-List
        }
        elseif ($Action -eq 'CommandFaultStatus') {
            $response | Format-List configured, mode, delayMs
        }
        else { Write-Host "已完成：$Action" }
    }
    finally { $client.Dispose() }
}
catch {
    Write-Error "控制器命令失败：$($_.Exception.Message)"
    exit 1
}
