param(
    [Parameter(Mandatory=$true)][string]$DestinationRoot,
    [ValidateSet('TaskEngine','StationFlows')][string]$ExecutionOwner = 'TaskEngine',
    [ValidateRange(1024,65535)][int]$Port = 19580,
    [switch]$MotionOnly,
    [string]$SourceRoot = (Join-Path $env:LOCALAPPDATA 'MotionControlFramework\Configurations')
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$source = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $SourceRoot).Path)
$destination = [IO.Path]::GetFullPath($DestinationRoot)
$sourceMachine = Join-Path $source 'Machine01'
$machine = Join-Path $destination 'Machine01'
if (-not (Test-Path -LiteralPath $sourceMachine -PathType Container)) { throw '源目录中没有 Machine01。' }
if (Test-Path -LiteralPath $destination) { throw '目标根目录已存在；不会覆盖或删除。' }
if ($destination.StartsWith($source.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '测试目录不能位于源配置之内。' }
if (@(Get-Process -Name 'MotionControl.Host.Wpf' -ErrorAction SilentlyContinue).Count -gt 0) { throw '正式上位机正在运行，不复制可能变化的配置。' }
$sampleName = if ($MotionOnly) { 'RotaryMotionValidation' } else { 'RotaryWelding' }
$sample = Join-Path $repo ('src\MotionControl.TaskEngine\Samples\' + $sampleName)
$taskPrefix = if ($MotionOnly) { 'RotaryMotion' } else { 'Rotary' }
$taskFiles = @('Init','Prepare','Table','Weld','Press','Inspect') | ForEach-Object { $taskPrefix + $_ + '.json' }
foreach ($relative in (@('TaskMachine.json','StationPreparation.json','Variables.additions.json') + $taskFiles)) {
    if (-not (Test-Path -LiteralPath (Join-Path $sample $relative) -PathType Leaf)) { throw "缺少样例文件：$relative" }
}

# 读取已经复制的配置；禁止缺失时悄悄猜测格式。
function Read-RotaryJson([string]$RelativePath) {
    $file = Join-Path $machine $RelativePath
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "缺少配置：$file" }
    return Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
}

# 使用 UTF-8 写入隔离副本，不修改源文件。
function Write-RotaryJson([string]$RelativePath, $Value) {
    $file = Join-Path $machine $RelativePath
    [IO.File]::WriteAllText($file, ($Value | ConvertTo-Json -Depth 80), [Text.UTF8Encoding]::new($false))
}

# 增加或调整本测试专属逻辑名；遇到他人占用的物理通道明确报错。
function Set-RotaryIo([Collections.ArrayList]$Items, [string]$Name, [int]$Channel, [string]$Card) {
    $sameName = @($Items | Where-Object { $_.name -eq $Name })
    if ($sameName.Count -gt 1) { throw "IO 名称重复：$Name" }
    $collision = @($Items | Where-Object { $_.controllerId -eq $Card -and [int]$_.channel -eq $Channel -and $_.name -ne $Name })
    if ($collision.Count -gt 0) { throw "通道被其他名称占用：$Name = $Channel，已有 $($collision[0].name)" }
    if ($sameName.Count -eq 1) {
        $sameName[0].controllerId = $Card
        $sameName[0].channel = $Channel
        $sameName[0].activeHigh = $true
    } else {
        [void]$Items.Add([pscustomobject]@{name=$Name; controllerId=$Card; channel=$Channel; activeHigh=$true})
    }
}

# 仅复制运行所需配置，不复制恢复记录、草稿或备份；源目录保持原样。
New-Item -ItemType Directory -Path $machine -Force | Out-Null
foreach ($name in @('HardwareConfig','Parameters','Tasks')) {
    $from = Join-Path $sourceMachine $name
    if (-not (Test-Path -LiteralPath $from -PathType Container)) { throw "源目录缺少 $name，已保留隔离目录供检查。" }
    Copy-Item -LiteralPath $from -Destination (Join-Path $machine $name) -Recurse
}
$axisConfig = Read-RotaryJson 'HardwareConfig\AxisParameters.json'
$io = Read-RotaryJson 'HardwareConfig\IoMapping.json'
$axesByName = @('Unload_X','Load_X','Press_Z','Transfer_Y','Camera_X','Camera_Z','Feeder_X','Feeder_Z','Buffer_X','Buffer_Y','Reject_Z','Conveyor_R','Weld_Z')
$card = 'MotionCard01'
$inputs = [Collections.ArrayList]::new()
$outputs = [Collections.ArrayList]::new()
foreach ($item in @($io.digitalInputs)) { [void]$inputs.Add($item) }
foreach ($item in @($io.digitalOutputs)) { [void]$outputs.Add($item) }
for ($index = 0; $index -lt $axesByName.Count; $index++) {
    $name = $axesByName[$index]
    $match = @($axisConfig.axes | Where-Object { $_.name -eq $name })
    if ($match.Count -ne 1 -or $match[0].controllerId -ne $card -or [int]$match[0].axisNumber -ne $index) { throw "轴映射不符合当前设备：$name" }
    $unit = if ($name -eq 'Conveyor_R') { 'deg' } else { 'mm' }
    if ($match[0].engineeringUnit -ne $unit -or $match[0].softLimitEnabled -ne $true) { throw "轴单位或软限位不符合测试要求：$name" }
    if ($name -eq 'Conveyor_R' -and ([double]$match[0].minimumPosition -gt -90 -or [double]$match[0].maximumPosition -lt 90)) { throw '旋转轴软件行程不包含 ±90°。' }
    if ($name -ne 'Conveyor_R' -and ([double]$match[0].minimumPosition -gt 0 -or [double]$match[0].maximumPosition -lt 2)) { throw "直线轴行程不包含测试位置 0–2：$name" }
    $base = 100 + $index * 3
    Set-RotaryIo $inputs ($name + '原点') $base $card
    Set-RotaryIo $inputs ($name + '负限位') ($base + 1) $card
    Set-RotaryIo $inputs ($name + '正限位') ($base + 2) $card
    if ($null -eq $match[0].PSObject.Properties['datumInputChannel']) { $match[0] | Add-Member -NotePropertyName datumInputChannel -NotePropertyValue $base }
    else { $match[0].datumInputChannel = $base }
}
$inputMap = [ordered]@{
    '取料夹紧到位'=40; 'A夹紧到位'=41; 'B夹紧到位'=42; '扫码成功'=43; '压装完成'=45;
    '剔除完成'=48; '缓存载具到位'=49; '合格出料完成'=50; '取料松开到位'=60;
    'A松开到位'=61; 'B松开到位'=62; '扫码完成'=63; '压装合格'=64;
    '缓存接收允许'=65; '合格出口允许'=66; '不良出口允许'=67; '焊接合格'=68
}
$outputMap = [ordered]@{
    '取料夹紧'=40; 'A夹紧'=41; 'B夹紧'=42; '扫码触发'=43; '压装启动'=45;
    '剔除启动'=48; '缓存交接'=49; '合格出料'=50
}
if ($MotionOnly) {
    $inputMap.Remove('扫码成功')
    $inputMap.Remove('扫码完成')
    $outputMap.Remove('扫码触发')
    # 物料状态采用互相独立的有料/空料反馈；没有对应输出，不占用同号 DO。
    $inputMap['A夹位有料'] = 69
    $inputMap['A夹位无料'] = 70
    $inputMap['B夹位有料'] = 71
    $inputMap['B夹位无料'] = 72
    $inputMap['缓存位有料'] = 73
    $inputMap['缓存位无料'] = 74
    $inputMap['下料夹紧到位'] = 51
    $inputMap['下料松开到位'] = 75
    $inputMap['下料位有料'] = 76
    $inputMap['下料位无料'] = 77
    $inputMap['取料夹爪有料'] = 78
    $inputMap['取料夹爪无料'] = 79
    $inputMap['上料滑台有料'] = 80
    $inputMap['上料滑台无料'] = 81
    $inputMap['旋转区清料允许'] = 82
    $inputMap['缓存区清料允许'] = 83
    $inputMap['焊接模组空闲'] = 84
    $inputMap['压合模组空闲'] = 85
    $inputMap['上料模组空闲'] = 86
    $inputMap['视觉模组空闲'] = 87
    $inputMap['出料模组空闲'] = 88
    $outputMap['下料夹紧'] = 51
}
foreach ($name in $inputMap.Keys) { Set-RotaryIo $inputs $name $inputMap[$name] $card }
foreach ($name in $outputMap.Keys) { Set-RotaryIo $outputs $name $outputMap[$name] $card }
if ($MotionOnly) {
    # 独立物料与松开反馈没有相应输出，若源配置已有同号 DO 则保守报错，不覆盖用户映射。
    $unrelated = @($outputs | Where-Object { $_.controllerId -eq $card -and [int]$_.channel -ge 69 -and [int]$_.channel -le 88 })
    if ($unrelated.Count -gt 0) { throw "清料反馈对应同号 DO 已有配置，请核对隔离测试映射：$($unrelated[0].name)。" }
}
if (-not $MotionOnly) {
    for ($bit=0; $bit -lt 16; $bit++) { Set-RotaryIo $inputs ('扫码数据{0:D2}' -f $bit) (200 + $bit) $card }
}
foreach ($name in @('上料到位','焊接完成','相机触发反馈','检测完成','检测OK','检测NG')) {
    if (@($inputs | Where-Object { $_.name -eq $name -and $_.controllerId -eq $card -and $_.activeHigh -eq $true }).Count -ne 1) { throw "缺少既有输入：$name" }
}
foreach ($name in @('焊接启动','相机触发','相机结果复位')) {
    if (@($outputs | Where-Object { $_.name -eq $name -and $_.controllerId -eq $card -and $_.activeHigh -eq $true }).Count -ne 1) { throw "缺少既有输出：$name" }
}
$io.digitalInputs = @($inputs.ToArray())
$io.digitalOutputs = @($outputs.ToArray())
Write-RotaryJson 'HardwareConfig\AxisParameters.json' $axisConfig
Write-RotaryJson 'HardwareConfig\IoMapping.json' $io
Write-RotaryJson 'Parameters\ControllerBackend.json' ([pscustomobject]@{backend='Virtual'; port=$Port})
$definition = if ($ExecutionOwner -eq 'StationFlows') { if ($MotionOnly) { 'RotaryMotionValidation' } else { 'RotaryWelding' } } else { 'WeldingExample' }
Write-RotaryJson 'Parameters\Equipment.json' ([pscustomobject]@{schemaVersion=1; definition=$definition; executionOwner=$ExecutionOwner})
$markerPath = Join-Path $machine 'Parameters\RotaryMotionValidation.json'
if (Test-Path -LiteralPath $markerPath) { throw '源配置已有纯运动模式标记；请核对设备，不会覆盖或误用该标记。' }
if ($MotionOnly) { Write-RotaryJson 'Parameters\RotaryMotionValidation.json' ([pscustomobject]@{schemaVersion=1; motionOnly=$true}) }
$variables = Read-RotaryJson 'Parameters\DeviceVariables.json'
$additions = Get-Content -LiteralPath (Join-Path $sample 'Variables.additions.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($item in @($additions.variables)) {
    if (@($variables.variables | Where-Object { $_.name -eq $item.name }).Count -gt 0) { throw "变量已存在，不覆盖：$($item.name)" }
}
$variables.variables = @($variables.variables) + @($additions.variables)
Write-RotaryJson 'Parameters\DeviceVariables.json' $variables
foreach ($name in $taskFiles) {
    $target = Join-Path $machine ('Tasks\' + $name)
    if (Test-Path -LiteralPath $target) { throw "任务已存在，不覆盖：$target" }
    Copy-Item -LiteralPath (Join-Path $sample $name) -Destination $target
}
Copy-Item -LiteralPath (Join-Path $sample 'TaskMachine.json') -Destination (Join-Path $machine 'Parameters\TaskMachine.json') -Force
Copy-Item -LiteralPath (Join-Path $sample 'StationPreparation.json') -Destination (Join-Path $machine 'Parameters\StationPreparation.json') -Force
Write-Host "已创建隔离测试配置：$destination；入口=$ExecutionOwner；端口=$Port。未修改源配置、草稿或恢复记录。"
