# PLCopen 架构重构第一阶段：明确轴运动状态与冻结 UI 基线

日期：2026-11-09。基线提交：`5a4d038ec113d5af9dfd8a7f3e5fb133c06c8784`。
参考：PLCopen Motion Control Part 1 V2.0，<https://www.plcopen.org/standards/motion-control/>。
本阶段仅做 PLCopen 状态语义的最小兼容增量，**不是 PLCopen 功能块实现或认证**。

## 为什么改

原来的 `AxisStatus` 有 `ServoEnabled`、`Moving`、`Homed` 和报警标识，但这些值不能明确区分回零、有限定位和连续运动。例如 `Moving=true` 不能证明正在进行哪一种运动。直接用旧字段推导 PLCopen 状态会造成虚假的诊断，特别是在厂商适配器不提供运动模式时。

## 这次实际改动

1. `Framework.Contracts/Motion/AxisMotionState.cs`：新增直白的运动状态枚举；`Unknown` 是缺少可信来源时的默认值。状态仅补充诊断，不作为安全准入证据。
2. `Framework.Contracts/Motion/AxisStatus.cs`：在原构造函数末尾增加可选参数 `motionState`，保留已有调用方式；新增只读属性 `MotionState`。
3. `VirtualController/VirtualAxis.cs`：只依据软件控制器内部明确维护的使能、回零、定位、点动和报警阶段，提供 `motionState`。控制器采用即时停止模型，不虚构 `Stopping` 状态；尚无同步运动模型，不虚构 `SynchronizedMotion`。
4. `Virtual/Adapter/VirtualAxis.cs`：显式解析控制器的状态名称。旧版控制器没有该字段、控制器返回未知值时保留 `Unknown`，不从其他布尔位猜测。原先的位置、速度、回零和报警字段以及运动命令均不变。
5. `MachineDevice.Checks`：增加无需控制器的向后兼容检查（未提供=未知、Moving 不推断、明确来源可保留）。
6. `VirtualAdapter.Checks`：扩充控制器往返检查中的未使能、待机、取消停止与连续点动状态检查；执行时须单独启动本机测试控制器。
7. `Verify-FrozenUi.ps1` 与 Windows 工作流：检查五个已确认布局文件的提交 Blob SHA，没有用户单独批准不得改布局。

## 本次明确不做的事

- 不修改轴映射、IO 映射、轴手动、IO 监控四页的 XAML 或后台事件，不修改主窗口布局及操作。
- 不调整 `IAxis` 现有方法签名、不改变控制器启停或运动命令、不把轴状态枚举用作自动运行放行条件。
- 不宣称现有 `VirtualAxis.StopAsync` 与 PLCopen 的 `MC_Stop`、`MC_Halt` 等价。原模拟器停止是即时停止，受控减速的建模与停止模式支持仍是下一阶段需要独立验证的风险。
- 不根据软件测试推断任何正运动、雷赛、ADLINK 实物驱动的 PLCopen 状态或安全能力。

## 验收边界

- Windows CI 应通过正式 Host 构建与既有检查，另外需通过冻结 UI Blob 检查。
- 使用独立软件控制器运行 `MotionControl.VirtualAdapter.Checks` 后才能宣称往返状态检查通过；CI 默认不启动该控制器。
- 四个受保护页面以及主窗口的可见行为，必须由正式 Host 另外进行现场/隔离桌面截图与交互回归。
- 设备或任务切换时旧数据隔离、运动状态未知、停止未确认、任务资源恢复等风险继续保留后续审计；本阶段不宣称解决。
