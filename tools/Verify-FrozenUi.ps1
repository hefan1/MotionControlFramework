# 使用提交中的 Git Blob 身份冻结已确认页面的布局文件，避免重构时误改。
# 若以后需要调整 UI，必须先单独取得用户确认，再在专门提交中更新基线。
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent

$protectedFiles = @(
    @{ Path = "src/MotionControl.Framework.Wpf/Configuration/AxisMappingConfigurationView.xaml"; Sha = "b73db02aa87bec27114ec3b870f5900dce573cc7" },
    @{ Path = "src/MotionControl.Framework.Wpf/Configuration/IoMappingConfigurationView.xaml"; Sha = "2cbcb768b8ffc387a92e7d45c8910291a704035b" },
    @{ Path = "src/MotionControl.Framework.Wpf/ManualDebug/ManualAxisControl.xaml"; Sha = "e4f1db1f452f30c88122c95a52eee612088c440a" },
    @{ Path = "src/MotionControl.Framework.Wpf/ManualDebug/IoMonitorView.xaml"; Sha = "62c6cffc275617a9b2579f00574c2183644e0651" },
    @{ Path = "src/MotionControl.Host.Wpf/MainWindow.xaml"; Sha = "35ffe663685d3dab74e8d6cdcf82c0d931959c1c" }
)

Push-Location $repoRoot
try {
    $failed = 0
    foreach ($item in $protectedFiles) {
        $actual = git rev-parse "HEAD:$($item.Path)"
        if ($LASTEXITCODE -ne 0 -or ($actual | Out-String).Trim() -ne $item.Sha) {
            Write-Host "[失败] 已冻结 UI 文件发生变化：$($item.Path)"
            $failed++
        }
        else {
            Write-Host "[通过] 已冻结 UI 未变化：$($item.Path)"
        }
    }
    if ($failed -ne 0) {
        throw "有 $failed 处已冻结布局与批准基线不一致。请勿借重构擅自修改页面。"
    }
    Write-Host "五个已冻结 UI 布局文件均与批准基线一致。该检查不代替实际屏幕和动作门禁验收。"
}
finally {
    Pop-Location
}
