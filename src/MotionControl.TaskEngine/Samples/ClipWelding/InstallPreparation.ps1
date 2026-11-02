param([Parameter(Mandatory = $true)][string]$ConfigurationRoot)

$ErrorActionPreference = 'Stop'
$preparationRoot = (Resolve-Path -LiteralPath $ConfigurationRoot).Path
$preparationMachine = Join-Path $preparationRoot 'Machine01'
if (-not (Test-Path -LiteralPath $preparationMachine -PathType Container)) { throw '缺少 Machine01，拒绝创建现场配置。' }
if (@(Get-CimInstance Win32_Process -Filter "Name = 'MotionControl.Host.Wpf.exe'").Count -ne 0) { throw '请先正常停止并退出正式上位机。' }
$preparationBackend = Get-Content -LiteralPath (Join-Path $preparationMachine 'Parameters\ControllerBackend.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($preparationBackend.backend -ne 'Virtual') { throw '本样例只允许明确配置 Virtual 的设备，不修改控制器选择。' }
$preparationMachineConfig = Get-Content -LiteralPath (Join-Path $preparationMachine 'Parameters\TaskMachine.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($stationId in @('ClipWeld','ClipPress')) {
    if (@($preparationMachineConfig.stations | Where-Object { $_.stationId -eq $stationId }).Count -ne 1) { throw "不是既有夹具焊接任务清单，缺少工位：$stationId" }
}
$preparationFiles = @{
    'ClipWeldPrepare.json' = 'Tasks\ClipWeldPrepare.json'
    'ClipPressPrepare.json' = 'Tasks\ClipPressPrepare.json'
    'StationPreparation.json' = 'Parameters\StationPreparation.json'
}
foreach ($file in $preparationFiles.Keys) {
    $source = Join-Path $PSScriptRoot $file
    $destination = Join-Path $preparationMachine $preparationFiles[$file]
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) { throw "已有不同内容，拒绝覆盖：$destination。请先核对和备份。" }
    }
}
$preparationBackup = Join-Path $preparationMachine ('Backups\ClipPreparation-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $preparationBackup | Out-Null
$preparationAdded = [Collections.Generic.List[string]]::new()
try {
    foreach ($file in $preparationFiles.Keys) {
        $destination = Join-Path $preparationMachine $preparationFiles[$file]
        if (Test-Path -LiteralPath $destination) { Copy-Item -LiteralPath $destination -Destination (Join-Path $preparationBackup $file) }
        else {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $destination
            $preparationAdded.Add($destination)
        }
    }
    $preparationFiles.Values | Set-Content -LiteralPath (Join-Path $preparationBackup 'InstalledFiles.txt') -Encoding UTF8
    Write-Host "准备配置已保留：$preparationMachine。记录目录：$preparationBackup"
    Write-Host '未修改设备入口、生产任务、草稿、变量和恢复文件；正式 Host 读取任务时执行完整配置校验。'
}
catch {
    # 仅移除本次添加的明确文件，不递归删除目录，也不恢复用户原配置。
    foreach ($file in $preparationAdded) { Remove-Item -LiteralPath $file -Force }
    throw
}
