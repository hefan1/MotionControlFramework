# 在 Windows 使用正式 net48 目标检查本次相关模块；不会连接控制卡或启动生产。
param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "tmp/v18-checks"
)

$ErrorActionPreference = "Stop"
# 由脚本统一汇总原生命令退出码，避免首个失败就跳过后续独立检查。
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repositoryRoot

if ($env:OS -ne "Windows_NT") {
    throw "本检查入口需要 Windows、.NET Framework 4.8 和可构建 WPF 的 .NET SDK。"
}

python tools/Check-CommandBoundaries.py
if ($LASTEXITCODE -ne 0) { throw "任务命令职责边界检查失败。" }

# 先构建正式主程序，发现 XAML、页面事件和跨程序集调用的编译错误。
$failures = [System.Collections.Generic.List[string]]::new()
# 保留每个项目的实际构建、检查结果；某项目失败不代表其他项目失败。
$resultRows = [System.Collections.Generic.List[string]]::new()
dotnet --info
Write-Host "[开始] 正式 Host 构建"
dotnet build src/MotionControl.Host.Wpf/MotionControl.Host.Wpf.csproj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    $failures.Add("正式 Host 构建失败（退出码 $LASTEXITCODE）")
    $resultRows.Add("| 正式 Host | 失败 | 不适用 |")
    Write-Host "[失败] 正式 Host 构建（退出码 $LASTEXITCODE）"
}
else {
    $resultRows.Add("| 正式 Host | 通过 | 不适用 |")
    Write-Host "[通过] 正式 Host 构建"
}

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

# 序号帮助定位当前阶段，通过数只计入已构建并且实际运行成功的检查项目。
$projectNumber = 0
$passedProjects = 0
foreach ($projectName in $checkProjects) {
    $projectNumber++
    $projectFile = Join-Path "tools/$projectName" "$projectName.csproj"
    $outputDirectory = Join-Path $OutputRoot $projectName
    Write-Host "[$projectNumber/$($checkProjects.Count)] [开始构建] $projectName"
    dotnet build $projectFile -c $Configuration -o $outputDirectory --nologo
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("$projectName 构建失败（退出码 $LASTEXITCODE）")
        $resultRows.Add("| $projectName | 失败 | 未运行 |")
        Write-Host "[$projectNumber/$($checkProjects.Count)] [构建失败] $projectName（内部检查未运行）"
        # 构建失败时不能运行可能遗留的旧检查程序；其他独立项目仍继续检查。
        continue
    }

    $checkProgram = Join-Path $outputDirectory "$projectName.exe"
    if (-not (Test-Path -LiteralPath $checkProgram -PathType Leaf)) {
        $failures.Add("$projectName 构建后未找到检查程序：$checkProgram")
        $resultRows.Add("| $projectName | 通过 | 未运行：检查程序缺失 |")
        Write-Host "[$projectNumber/$($checkProjects.Count)] [未运行] $projectName（检查程序缺失）"
        continue
    }

    Write-Host "[$projectNumber/$($checkProjects.Count)] [开始检查] $projectName"
    try {
        if ($projectName -in @("MotionControl.TaskEditing.Checks", "MotionControl.TaskMonitor.Checks")) {
            # 这两个离线检查均出现过长期不返回；直接启动本次构建产物并继承标准流。
            $checkStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
            $checkStartInfo.FileName = (Resolve-Path -LiteralPath $checkProgram).ProviderPath
            $checkStartInfo.WorkingDirectory = $repositoryRoot
            $checkStartInfo.UseShellExecute = $false
            $checkProcess = [System.Diagnostics.Process]::new()
            $checkProcess.StartInfo = $checkStartInfo
            try {
                if (-not $checkProcess.Start()) { throw "未能启动本次检查程序：$projectName。" }
                if ($checkProcess.WaitForExit(180000)) {
                    $checkExitCode = $checkProcess.ExitCode
                }
                else {
                    Write-Host "[$projectNumber/$($checkProjects.Count)] [检查超时] $projectName 超过 180 秒，仅停止本次检查进程 PID $($checkProcess.Id)。"
                    # 只操作本次启动返回的进程对象，不按名称查找，也不终止子进程树。
                    try { if (-not $checkProcess.HasExited) { $checkProcess.Kill() } }
                    catch { if (-not $checkProcess.HasExited) { throw } }
                    if (-not $checkProcess.WaitForExit(10000)) {
                        throw "$projectName 检查超时，停止后 10 秒内仍未确认进程 PID $($checkProcess.Id) 已退出。"
                    }
                    $checkExitCode = 124
                }
            }
            finally { $checkProcess.Dispose() }
        }
        else {
            # 其余检查保留原入口；这里不改变它们已有的子进程或收尾行为。
            & $checkProgram
            $checkExitCode = $LASTEXITCODE
        }
    }
    catch {
        # 启动错误也属于该项目失败；记录原始原因后继续其他独立项目。
        $failures.Add("$projectName 检查程序执行异常：$($_.Exception.Message)")
        $resultRows.Add("| $projectName | 通过 | 执行异常 |")
        Write-Host "[$projectNumber/$($checkProjects.Count)] [执行异常] $projectName：$($_.Exception.Message)"
        continue
    }
    if ($checkExitCode -ne 0) {
        $failures.Add("$projectName 内部检查失败（退出码 $checkExitCode）")
        $resultRows.Add("| $projectName | 通过 | 失败 |")
        Write-Host "[$projectNumber/$($checkProjects.Count)] [检查失败] $projectName（退出码 $checkExitCode）"
    }
    else {
        $passedProjects++
        $resultRows.Add("| $projectName | 通过 | 通过 |")
        Write-Host "[$projectNumber/$($checkProjects.Count)] [检查通过] $projectName"
    }
}

# 控制台和 Actions 摘要展示同一结果；任何构建或检查失败仍导致脚本最终失败。
$summary = @(
    "## V1.8 构建与内部检查结果",
    "",
    "检查项目共 $($checkProjects.Count) 个：通过 $passedProjects 个，未通过 $($checkProjects.Count - $passedProjects) 个。Host 构建结果单独列出。",
    "",
    "| 项目 | 构建 | 内部检查 |",
    "| --- | --- | --- |"
) + $resultRows.ToArray()
if ($failures.Count -gt 0) {
    $summary += @("", "### 失败原因", "")
    $summary += $failures | ForEach-Object { "- $_" }
}
$summary += @("", "该结果不代表正式 Host 屏幕操作、厂商 SDK 或实物设备验收。")
$summary | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) {
    try { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8 }
    catch {
        # 摘要写入失败不能覆盖构建和检查的原始失败原因，控制台仍保留完整结果。
        Write-Warning "Actions 摘要写入失败，结果已输出到控制台：$($_.Exception.Message)" -WarningAction Continue
    }
}
if ($failures.Count -gt 0) {
    throw ("本次检查存在失败，不能报告为全部通过：`n" + ($failures -join "`n"))
}
Write-Host "正式 Host 已构建，所列内部检查全部通过。"
