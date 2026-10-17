# 工位流程编写方式

一个工位对应一个普通 C# 类，并实现 `IStationFlow`。流程类自己保存当前步骤；Runtime 只负责并行调用各工位的 `ExecuteCurrentStepAsync`，不把工艺步骤塞进通用流程引擎。

每次调用只处理当前一步。动作或等待条件完成后再切换到下一步。收到暂停取消时，让取消继续向外传递，当前步骤保持不变；运动接口会在动作取消时执行受控停止。复位只清理可复位报警，不改当前步骤。

示意写法（仅说明步骤组织，不是可直接运行的设备程序）：

```csharp
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
