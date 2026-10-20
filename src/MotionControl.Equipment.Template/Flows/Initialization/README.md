# 设备启动初始化流程

本目录用于存放本设备启动工位流程前必须完成的准备步骤，例如轴回零、移动到安全位置或初始化设备输出。设备不需要启动准备时，不实现也不注入初始化流程。

初始化流程实现 `IEquipmentInitializationFlow`，由 Host 创建后传给 `MachineRuntime`。Runtime 在启动自动运行后，先按顺序调用初始化流程，再并行启动各工位。每次 `ExecuteCurrentStepAsync` 只执行当前一步；动作成功完成后才切换到下一步，全部完成后将 `IsCompleted` 设为 `true`。

暂停会取消当前步骤并等待正在进行的运动受控停稳，流程保留当前步骤。复位时 `ResetAsync` 清理可复位报警但保留步骤和完成状态；控制卡断开后重新连接时，Runtime 调用 `ResetForNewConnection`，从第一步重新开始。

流程由 Runtime 在后台线程执行。只通过 Contracts 中的轴、IO 和设备服务完成操作，不要直接访问 WPF 控件或调用厂商原生 API。产品循环和工艺动作写在 `Flows/Stations/` 下的工位流程中；初始化流程只做工位启动前的准备。
