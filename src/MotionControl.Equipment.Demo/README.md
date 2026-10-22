# 单工位闭环示例

`EquipmentTemplates/Parameters/DeviceVariables.json` 展示设备项目如何提供变量表默认模板。Host 首次为 MachineId 建立生效配置时复制此文件，后续现场变量定义由“监控 → 设备变量”页面维护。

`DemoStationFlow` 是可选的设备工艺示例。Host 只有在当前设备目录存在 `Parameters/DemoStation.json` 且 `enabled` 为 `true` 时才创建它。默认样例在 [Samples/DemoStation.json](Samples/DemoStation.json)，默认关闭，不会因连接控制卡而自动发运动或输出命令。

流程使用明确的整数步骤编号和 `switch` 分派：10 轴定位 → 20 等待逻辑 DI → 30 逻辑 DO 脉冲 → 40 等待 DI 复位 → 回到 10。每次转步通过 `SetStep(步骤号, 说明)` 同时更新执行入口和现场可读说明，因此 OK/NG 分支可以直接跳到不同编号。步骤编号是显式跳转目标，不是数组位置。等待 DI 超时会产生 `DEMO_SENSOR_TIMEOUT` 停机报警，流程保留在步骤 20，Runtime 受控暂停，输出不动作；报警复位后从步骤 20 重试。若暂停发生在步骤 30 的输出保持期间，会先请求关闭 DO，保留步骤 30 供复位后重试。

步骤编号和说明当前保存在工位流程对象内存中，可覆盖 Runtime 的暂停、报警复位后续跑；关闭并重启 Host 后会重新从步骤 10 开始，不提供进程重启后的断点续跑。要增加这种能力，需要持久化步骤上下文，并在恢复前核对设备实际状态，不能只恢复一个步骤号。

配置中的轴、DI、DO 都填轴映射与 IO 映射页上的**逻辑名称**。定位位置、速度和加减速度按轴工程单位填写。Host 会先检查逻辑名称和目标是否落在轴映射行程内。设备自动启动仍要求安全联锁页配置可读的急停 DI；真实设备的硬件安全回路不由此示例替代。

离屏检查运行 `MotionControl.ZMotionSimulatorCheck --runtime-regression`，其中的示例工位用内存轴与 IO 验证正常循环、传感器超时报警与复位、输出期间暂停关闭 DO。它不发送厂商 SDK 指令；现场或仿真器的轴反馈、DI 极性和 DO 接线需另行核对。
