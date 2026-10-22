# Host WPF

Host 是设备上位机的组合入口：它创建当前设备配置、通用页面、运行时和本次运行选择的运动卡连接实现。

“监控 → 设备变量”显示本机变量表。首次启动时 Host 从当前设备项目的 `EquipmentTemplates/Parameters/DeviceVariables.json` 复制默认变量；之后只读写本机 `%LOCALAPPDATA%/MotionControlFramework/Configurations/<MachineId>/Parameters/DeviceVariables.json`，不会覆盖已存在的现场配置。变量运行值在内存中供设备流程和监控页共用。

Host 在工位受控暂停后保存设备变量值、可恢复工位步骤和初始化完成标记到本机 `%LOCALAPPDATA%/MotionControlFramework/Configurations/<MachineId>/Runtime/RecoverySnapshot.json`。软件关闭时会再次保存最新变量值；运行中、暂停中或报警处理中会阻止关闭，要求先安全停止。重启后连接卡，确认恢复提示，再按设备复位和启动；不会自动发运动或输出命令。流程断点只在设备配置一致且工位显式实现 `IRecoverableStationFlow` 时续跑。新增、删除或改类型的变量按名称和 .NET 类型逐项校验；不匹配的变量使用当前配置初值，并阻止旧流程自动续跑。

启动初始化默认在新连接后重新执行。设备流程如要在软件重启后避免重复回零，必须显式实现 `IRecoverableEquipmentInitializationFlow`；Runtime 会在启动时调用 `CanSkipAfterRestartAsync`，由设备流程读取当前轴、传感器和工艺状态。只有该实时检查确认安全，且 Runtime 再次确认轴已停稳、无报警/限位并满足回零要求，才跳过初始化；检查返回 false 时完整执行原初始化，检查异常时禁止启动。恢复文件本身不会把轴标记为已回零。配置发生变化、变量定义不匹配或暂停发生在初始化中时，不允许使用旧初始化完成标记。

## 运动卡连接位置

- `App.xaml.cs` 的 `CreateMotionCardConnection()` 是当前 Host 选择控制卡实现的位置。设备流程和通用页面不在这里选择厂商。
- `IMotionCardHostConnection` 给 Host 提供通用的 `IMotionCard`、`ILogicalIo` 和已加载硬件映射。Host 运行逻辑通过这些契约创建运行时及手动调试页。
- “IO 监控”页由 Host 组合根注入当前连接、IO 能力范围、物理通道监控可选接口和 DI/DO 映射；页面本身不连接或重连控制卡。
- `ZMotionAxisMappingCardConnection` 是当前正运动仿真器的 Host 接入实现，负责读取设备配置、建立 ZMotion 适配器并提供轴映射页需要的参数读写。
- 轴映射页使用 `IAxisMappingCardConnection`；可读取卡上 IO 能力时，由连接实现另外提供 `IMotionCardIoCapabilityProvider`。适配器还可以单独实现 `IAxisMappingCardOnlineApply`，在卡型、连接资料和物理轴集合未变化时保留卡连接并在线应用轴映射；其他情况由页面提示后走重连路径。
- 当前正运动适配器支持相同物理轴集合的在线更新。参数确有变化时先确认已映射轴全部停止，再通过正运动 API 更新需要改变的参数，并复用现有轴对象；如果只改显示名称等运行时映射资料，不会重复写卡参数。新增或移除物理轴仍需重连，重连会清除回零完成状态。
- IO 映射保存会检查轴已停止后，在当前卡实例内替换 DI/DO 逻辑映射，不重连控制卡。配置已保存但在线应用失败时，页面显示错误并按实际运行状态刷新手动调试页。

## 单工位流程核对

- Host 的启动、暂停、停止按钮现在调用同一个 `MachineRuntime`，与轴手动、IO 手动页面共享控制卡及逻辑映射；示例流程运行时，手动操作由 Runtime 拒绝。当前“停止”和“暂停”都请求受控暂停并保留工位步骤。
- 当前设备目录中的 `Parameters/DemoStation.json` 显式启用后，Host 根据其中的逻辑轴、逻辑 DI、逻辑 DO 和工程单位参数创建 `MotionControl.Equipment.Demo/DemoStationFlow`。没有该文件或 `enabled=false` 时不创建自动工位，启动按钮不可用，手动调试照常使用；配置错误时会提示原因并关闭示例自动流程，不影响手动页面。配置字段和流程顺序见 [示例工位说明](../MotionControl.Equipment.Demo/README.md)。
- 当前示例工位明确保存 10、20、30、40 步编号；暂停后重启会从保存步骤起点执行。若暂停发生在有外部副作用的步骤中，设备项目必须确认该步骤可以安全重试，或在流程中补充可恢复的工艺状态判断。
- 在“监控 → 流程状态”查看整机状态、当前步骤、超时报警和最近故障。操作顺序为连接控制卡 → 整机复位 → 启动 → 观察轴定位与 DI → 观察 DO 脉冲；保持 DI 无效可核对超时报警，再复位重试。

## 增加另一种控制卡

为新卡型建立对应的 VendorApi 与驱动适配器，在 Host 中实现 `IMotionCardHostConnection`，提供该卡型选项、配置转换和逻辑 IO。随后在 `CreateMotionCardConnection()` 选择新的实现，并更新 Host 的项目引用、设备硬件配置和匹配的原生运行库。

设备流程继续使用逻辑轴、逻辑 IO 和工程单位。新适配器需要按目标卡型和 SDK 版本单独验证；API 声明或其他卡型的仿真结果不能代替验证。

当前 Host 只接入一张 ZDevelop `VPLC5xx-Simu` 仿真卡。Runtime 的 `MotionCardGroup` 虽然可以管理多张卡，但当前 Host 连接接口和轴/IO 配置页只接入一张卡；连接时发现 `MotionCards.json` 配置多张卡会明确拒绝，避免只连接第一张卡而遗漏其余卡。雷赛、固高、ADLINK 等目录中的原生 API 声明不代表已有可运行的驱动适配器。
