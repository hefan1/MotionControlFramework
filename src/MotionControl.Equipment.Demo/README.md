# 单工位闭环示例

`EquipmentTemplates/Parameters/DeviceVariables.json` 展示设备项目如何提供变量表默认模板。Host 首次为 MachineId 建立生效配置时复制此文件，后续现场变量定义由“监控 → 设备变量”页面维护。

`DemoStationFlow` 是可选的设备工艺示例。Host 优先读取 `Parameters/DemoStations.json`，用于一个设备内多个并行工位；若该文件不存在，则兼容读取旧的 `Parameters/DemoStation.json` 单工位配置。两个配置均默认关闭，不会因连接控制卡而自动发运动或输出命令。并行工位组默认样例在 [Samples/DemoStations.json](Samples/DemoStations.json)，单工位兼容样例在 [Samples/DemoStation.json](Samples/DemoStation.json)。启用的并行工位必须使用互不重复的逻辑轴和逻辑 DO，避免两个流程控制同一机构或输出。多个工位可以读取同一个逻辑 DI，例如共享到位或共用安全状态输入；DI 是只读信号，不按写资源处理。

流程使用明确的整数步骤编号和 `switch` 分派：10 轴定位 → 20 等待逻辑 DI → 30 逻辑 DO 脉冲 → 40 等待 DI 复位 → 回到 10。每次转步通过 `SetStep(步骤号, 说明)` 同时更新执行入口和现场可读说明，因此 OK/NG 分支可以直接跳到不同编号。步骤编号是显式跳转目标，不是数组位置。每个工位通过 `timeoutAlarmCodes` 关联一条或多条设备报警代码，定义必须存在于 `Parameters/AlarmCatalog.json`。等待 DI 超时时会同时报告全部关联报警：停机报警暂停整机并保留锁存记录，非停机报警保持在当前报警列表，直到 DI 满足等待条件后自动清除。流程保留在步骤 20，输出不动作；复位后从步骤 20 重试。未写该字段的旧配置默认使用 `DEMO_SENSOR_TIMEOUT`。若暂停发生在步骤 30 的输出保持期间，会先请求关闭 DO，保留步骤 30 供复位后重试。

步骤编号和说明保存在工位流程对象内存中，可用于暂停、报警复位后的续跑。示例实现 `IRecoverableStationFlow`，Host 在符合恢复条件时可保存并恢复步骤 10、20、30、40；恢复只回到步骤起点，不会自动动作。重新运行前仍须核对设备配置、变量、物料和机构状态，步骤号本身不能证明现场状态适合续跑。

配置中的轴、DI、DO 都填轴映射与 IO 映射页上的**逻辑名称**。定位位置、速度和加减速度按轴工程单位填写。Host 会先检查逻辑名称和目标是否落在轴映射行程内。设备自动启动仍要求安全联锁页配置可读的急停 DI；真实设备的硬件安全回路不由此示例替代。

离屏检查运行 `MotionControl.ZMotionSimulatorCheck --runtime-regression`，其中的示例工位用内存轴与 IO 验证正常循环、停机/非停机报警多关联、报警恢复和复位、输出期间暂停关闭 DO。它不发送厂商 SDK 指令；现场轴反馈、DI 极性和 DO 接线需另行核对。任务列表和本示例工位各自维护报警关联；正式 Host 已接入任务入口，任务报警的设备端到端链路仍需验证。
