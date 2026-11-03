# 按逻辑名称解析示例场景使用的物理通道；只允许唯一、同卡和高有效映射。
function Get-ScenarioIoChannel {
    param(
        [Parameter(Mandatory = $true)]$Items,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ControllerId
    )
    $found = @($Items | Where-Object { $_.name -eq $Name })
    if ($found.Count -ne 1) { throw "IO 逻辑名称不存在或不唯一：$Name" }
    $point = $found[0]
    $channel = 0
    if ($point.controllerId -ne $ControllerId -or $point.activeHigh -ne $true -or
        -not [int]::TryParse([string]$point.channel, [ref]$channel) -or $channel -lt 0 -or $channel -gt 4095) {
        throw "IO 映射不符合示例要求：$Name；需为同一控制器的高有效通道（0–4095）。"
    }
    $sameChannel = @($Items | Where-Object { $_.controllerId -eq $ControllerId -and $_.channel -eq $channel })
    if ($sameChannel.Count -ne 1) { throw "IO 物理通道存在重复映射：$Name，通道 $channel。" }
    return $channel
}
