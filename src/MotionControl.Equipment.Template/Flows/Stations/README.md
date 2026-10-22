# 工位流程编写方式

一个工位对应一个普通 C# 类，并实现 `IStationFlow`。流程类自己保存当前步骤；Runtime 只负责并行调用各工位的 `ExecuteCurrentStepAsync`，不把工艺步骤塞进通用流程引擎。

每次调用只处理当前一步。动作或等待条件完成后再切换到下一步。收到暂停取消时，让取消继续向外传递，当前步骤保持不变；运动接口会在动作取消时执行受控停止。复位只清理可复位报警，不改当前步骤。Runtime 在后台线程调用流程，不要直接访问 WPF 控件；界面状态通过 Runtime 事件更新。

示意写法（仅说明步骤组织，不是可直接运行的设备程序）：

```csharp
private int _step = 10;
private string _stepInfo = "等待来料";

public string CurrentStepName => _step + " - " + _stepInfo;

/// <summary>执行上料工位当前步骤；每次分派一个步骤，结果决定明确的下一步编号。</summary>
public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
{
    switch (_step)
    {
        case 10:
            if (!await _io.ReadInputAsync("PartReady", cancellationToken))
            {
                await Task.Delay(50, cancellationToken);
                return;
            }

            SetStep(20, "移动到取料位");
            return;

        case 20:
            await _xAxis.MoveAbsoluteAsync(
                _parameters.PickPositionMm,
                _parameters.PickMoveProfile,
                cancellationToken);

            SetStep(30, "执行取料并判断结果");
            return;

        case 30:
            PickResult result = await PickPartAsync(cancellationToken);
            if (result.IsOk)
            {
                SetStep(80, "取料成功，进入正常流程");
            }
            else if (result.FailureCode == "Vacuum")
            {
                SetStep(90, "真空异常，进入真空处理流程");
            }
            else
            {
                SetStep(100, "其他取料异常，进入异常处理流程");
            }
            return;

        case 80:
            await ContinueNormalFlowAsync(cancellationToken);
            return;

        case 90:
            await HandleVacuumFailureAsync(cancellationToken);
            return;

        case 100:
            await HandleOtherPickFailureAsync(cancellationToken);
            return;

        default:
            throw new InvalidOperationException("未知工位步骤：" + _step);
    }
}

/// <summary>一起更新步骤编号和操作员可读说明。</summary>
private void SetStep(int step, string info)
{
    _step = step;
    _stepInfo = info;
}
```

步骤编号是显式跳转目标，不是数组下标或“当前编号加一”；有多个 OK/NG 结果时，在判断处直接写出各自目标步骤。编号之间可以留出空档，便于后续插入步骤。

步骤粒度按现场需要决定：需要单独显示进度、暂停恢复、超时或报警处理的动作应拆成独立步骤；固定且可复用的短动作可封装成方法。复杂工序可以拆为连续的若干步骤，但不要把每条 API 调用都做成一个步骤。当前步骤保存在流程对象内存中，支持 Runtime 暂停、复位后的续跑；进程关闭再启动后的断点续跑还需要单独持久化流程上下文，并在恢复前核对设备实际状态。

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
