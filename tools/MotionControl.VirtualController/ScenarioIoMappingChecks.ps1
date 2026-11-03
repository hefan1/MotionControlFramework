$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ScenarioIoMapping.ps1')

# 检查旧编号与同号示例编号均按名称解析，不依赖固定通道。
function Assert-Channel($Items, [string]$Name, [int]$Expected) {
    $actual = Get-ScenarioIoChannel -Items $Items -Name $Name -ControllerId 'card1'
    if ($actual -ne $Expected) { throw "$Name 解析错误：期望 $Expected，实际 $actual。" }
}

# 不符合设备示例约束的映射必须在下发规则前拒绝。
function Assert-Rejected($Items, [string]$Name) {
    $rejected = $false
    try { Get-ScenarioIoChannel -Items $Items -Name $Name -ControllerId 'card1' | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw "无效映射未被拒绝：$Name。" }
}

$old = @([pscustomobject]@{name='压头动作'; channel=1; controllerId='card1'; activeHigh=$true})
$new = @([pscustomobject]@{name='压头动作'; channel=5; controllerId='card1'; activeHigh=$true})
Assert-Channel $old '压头动作' 1
Assert-Channel $new '压头动作' 5
Assert-Rejected $new '不存在的点'
Assert-Rejected @($new[0], $new[0]) '压头动作'
Assert-Rejected @($new[0], [pscustomobject]@{name='其他输出'; channel=5; controllerId='card1'; activeHigh=$true}) '压头动作'
Assert-Rejected @([pscustomobject]@{name='压头动作'; channel=5; controllerId='card2'; activeHigh=$true}) '压头动作'
Assert-Rejected @([pscustomobject]@{name='压头动作'; channel=5; controllerId='card1'; activeHigh=$false}) '压头动作'
Assert-Rejected @([pscustomobject]@{name='压头动作'; channel=4096; controllerId='card1'; activeHigh=$true}) '压头动作'
Write-Host 'IO 场景映射检查通过：旧、新通道及无效配置拒绝。'
