# 重构期间锁定用户确认的 UI 布局。只有用户另行同意修改时才更新基线。
# 使用 Git Blob SHA 而非文件字节大小，避免换行或空白变化悄悄通过。
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $protected = [ordered]@{
        "src/MotionControl.Framework.Wpf/Configuration/AxisMappingConfigurationView.xaml" = "b73db02aa87bec27114ec3b870f5900dce573cc7"
        "src/MotionControl.Framework.Wpf/Configuration/IoMappingConfigurationView.xaml" = "2cbcb768b8ffc387a92e7d45c8910291a704035b"
        "src/MotionControl.Framework.Wpf/ManualDebug/ManualAxisControl.xaml" = "e4f1db1f452f30c88122c95a52eee612088c440a"
        "src/MotionControl.Framework.Wpf/ManualDebug/IoMonitorView.xaml" = "62c6cffc275617a9b2579f00574c2183644e0651"
        "src/MotionControl.Host.Wpf/MainWindow.xaml" = "35ffe663685d3dab74e8d6cdcf82c0d931959c1c"
    }
    $failures = 0
    foreach ($path in $protected.Keys) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Write-Error "受保护 UI 文件丢失：$path" -ErrorAction Continue
            $failures++
            continue
        }
        # 使用仓库属性执行文本归一化，避免 Windows 的 CRLF 换行产生误报。
        $actual = (& git hash-object "--path=$path" -- $path).Trim()
        if ($LASTEXITCODE -ne 0 -or $actual -ne $protected[$path]) {
            Write-Host "[不通过] 受保护 UI 发生变化：$path"
            Write-Host "  基线：$($protected[$path])"
            Write-Host "  当前：$actual"
            $failures++
        }
        else {
            Write-Host "[通过] UI 布局未变化：$path"
        }
    }
    if ($failures -gt 0) {
        throw "$failures 个受保护 UI 文件与基线不一致。须先取得用户明确许可，再单独审查修改并更新基线。"
    }
    Write-Host "五个受保护 UI 的 XAML 均与基线一致。注意：后台代码行为仍需单独回归。"
}
finally {
    Pop-Location
}
