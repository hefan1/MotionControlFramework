# 工位流程编写方式

一个工位对应一个普通 C# 类，并实现 `IStationFlow`。流程类自己保存当前步骤；Runtime 只负责并行调用各工位的 `ExecuteCurrentStepAsync`，不把工艺步骤塞进通用流程引擎。

每次调用只处理当前一步。动作或等待条件完成后再切换到下一步。收到暂停取消时，让取消继续向外传递，当前步骤保持不变；运动接口会在动作取消时执行受控停止。复位只清理可复位报警，不改当前步骤。Runtime 在后台线程调用流程，不要直接访问 WPF 控件；界面状态通过 Runtime 事件更新。

示意写法（仅说明步骤组织，不是可直接运行的设备程序）：

```csharp
/// <summary>执行上料工位当前步骤。</summary>
public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
{
    switch (_currentStep)
    {
        case StationStep.WaitForPart:
            if (!await _io.ReadInputAsync("PartReady", cancellationToken))
            {
                await Task.Delay(50, cancellationToken);
                return;
            }

            _currentStep = StationStep.MoveToPick;
            return;

        case StationStep.MoveToPick:
            await _xAxis.MoveAbsoluteAsync(
                _parameters.PickPositionMm,
                _parameters.PickMoveProfile,
                cancellationToken);

            _currentStep = StationStep.WaitForPickComplete;
            return;

        default:
            throw new InvalidOperationException("未知工位步骤：" + _currentStep);
    }
}
```

轴对象由 Host 通过 `MotionCardGroup.GetAxis("逻辑轴名")` 显式交给工位；IO 使用逻辑点位名；动作位置和速度从设备工艺参数读取。不要在流程里写物理轴号、脉冲换算或厂商 API 调用。

## 多工位共用轴

如果两个工位可能同时使用同一根轴，Host 应把同一个 `MotionCardGroup.AxisResources` 交给相关工位。工位在共享动作前申请逻辑轴；多轴动作一次申请整组轴。持有占用凭证时不要嵌套申请其他轴资源，避免并行流程各持有一部分资源后互相等待。用 `using` 覆盖完整动作过程，取消时等轴受控停稳后再释放：

```csharp
using (IDisposable axisLease = await _axisResources
    .AcquireAxisAsync(_xAxis.Name, cancellationToken)
    .ConfigureAwait(false))
{
    await _xAxis.MoveAbsoluteAsync(
        _parameters.PickPositionMm,
        _parameters.PickMoveProfile,
        cancellationToken);
}
```

占用协调只防止并行流程同时操作同一轴；碰撞区域判断、让位条件和安全位置要由设备工艺流程明确编写。持续点动或其他长动作必须在动作停止后才退出 `using`。
