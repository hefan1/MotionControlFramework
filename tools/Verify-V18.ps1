# 在 Windows 使用正式 net48 目标检查本次相关模块；不会连接控制卡或启动生产。
param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "tmp/v18-checks"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repositoryRoot

if ($env:OS -ne "Windows_NT") {
    throw "本检查入口需要 Windows、.NET Framework 4.8 和可构建 WPF 的 .NET SDK。"
}

# 先构建正式主程序，发现 XAML、页面事件和跨程序集调用的编译错误。
$failures = [System.Collections.Generic.List[string]]::new()
dotnet --info
dotnet build src/MotionControl.Host.Wpf/MotionControl.Host.Wpf.csproj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { $failures.Add("正式 Host 构建失败") }

# 每个项目是已有的独立检查程序；分别输出，避免同名依赖和旧二进制相互覆盖。
$checkProjects = @(
    "MotionControl.TaskEngine.Checks",
    "MotionControl.MachineDevice.Checks",
    "MotionControl.TaskPreparation.Checks",
    "MotionControl.Preparation.Checks",
    "MotionControl.TaskEditing.Checks",
    "MotionControl.MachineProject.Checks",
    "MotionControl.Metadata.Checks",
    "MotionControl.StructuredDiagnostics.Checks",
    "MotionControl.TaskProjection.Checks",
    "MotionControl.TaskMonitor.Checks",
    "MotionControl.OperatorPages.Checks",
    "MotionControl.HostIsolation.Checks",
    "MotionControl.ProjectGate.Checks",
    "MotionControl.ConfigurationChange.Checks",
    "MotionControl.ConfigurationChangeUi.Checks"
)

foreach ($projectName in $checkProjects) {
    $projectFile = Join-Path "tools/$projectName" "$projectName.csproj"
    $outputDirectory = Join-Path $OutputRoot $projectName
    dotnet build $projectFile -c $Configuration -o $outputDirectory --nologo
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("$projectName 构建失败")
        # 构建失败时不能运行可能遗留的旧检查程序；其他独立项目仍继续检查。
        continue
    }

    $checkProgram = Join-Path $outputDirectory "$projectName.exe"
    & $checkProgram
    if ($LASTEXITCODE -ne 0) { $failures.Add("$projectName 内部检查失败") }
}

if ($failures.Count -gt 0) {
    throw ("本次检查存在失败，不能报告为全部通过：`n" + ($failures -join "`n"))
}
Write-Host "正式 Host 已构建，所列内部检查已完成。该结果不代表正式 Host 屏幕操作、厂商 SDK 或实物设备验收。"
