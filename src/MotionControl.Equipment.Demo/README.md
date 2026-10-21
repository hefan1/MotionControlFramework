# 单工位闭环示例

`DemoStationFlow` 是可选的设备工艺示例。Host 只有在当前设备目录存在 `Parameters/DemoStation.json` 且 `enabled` 为 `true` 时才创建它。默认样例在 [Samples/DemoStation.json](Samples/DemoStation.json)，默认关闭，不会因连接控制卡而自动发运动或输出命令。

流程按顺序执行：逻辑轴绝对定位 → 等待逻辑 DI 有效 → 逻辑 DO 打开指定时间并关闭 → 等待 DI 复位后进入下一轮。每一步完成后才更新 `CurrentStepName`。暂停时保留步骤；若暂停发生在输出保持期间，先请求关闭 DO。等待 DI 超时会产生 `DEMO_SENSOR_TIMEOUT` 停机报警，Runtime 受控暂停，输出不动作；该超时属于一次性事件，报警保留到操作员复位，复位后从等待 DI 的步骤重试。

配置中的轴、DI、DO 都填轴映射与 IO 映射页上的**逻辑名称**。定位位置、速度和加减速度按轴工程单位填写。Host 会先检查逻辑名称和目标是否落在轴映射行程内。设备自动启动仍要求安全联锁页配置可读的急停 DI；真实设备的硬件安全回路不由此示例替代。

离屏检查运行 `MotionControl.ZMotionSimulatorCheck --runtime-regression`，其中的示例工位用内存轴与 IO 验证正常循环、传感器超时报警与复位、输出期间暂停关闭 DO。它不发送厂商 SDK 指令；现场或仿真器的轴反馈、DI 极性和 DO 接线需另行核对。
