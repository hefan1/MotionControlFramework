# Host WPF

Host 是设备上位机的组合入口：它创建当前设备配置、通用页面、运行时和本次运行选择的运动卡连接实现。

## 运动卡连接位置

- `App.xaml.cs` 的 `CreateMotionCardConnection()` 是当前 Host 选择控制卡实现的位置。设备流程和通用页面不在这里选择厂商。
- `IMotionCardHostConnection` 给 Host 提供通用的 `IMotionCard`、`ILogicalIo` 和已加载硬件映射。Host 运行逻辑通过这些契约创建运行时及手动调试页。
- `ZMotionAxisMappingCardConnection` 是当前正运动仿真器的 Host 接入实现，负责读取设备配置、建立 ZMotion 适配器并提供轴映射页需要的参数读写。
- 轴映射页使用 `IAxisMappingCardConnection`；可读取卡上 IO 能力时，由连接实现另外提供 `IMotionCardIoCapabilityProvider`。适配器还可以单独实现 `IAxisMappingCardOnlineApply`，在卡型、连接资料和物理轴集合未变化时保留卡连接并在线应用轴映射；其他情况由页面提示后走重连路径。
- 当前正运动适配器支持相同物理轴集合的在线更新。参数确有变化时先确认已映射轴全部停止，再通过正运动 API 更新需要改变的参数，并复用现有轴对象；如果只改显示名称等运行时映射资料，不会重复写卡参数。新增或移除物理轴仍需重连，重连会清除回零完成状态。
- IO 映射保存会检查轴已停止后，在当前卡实例内替换 DI/DO 逻辑映射，不重连控制卡。配置已保存但在线应用失败时，页面显示错误并按实际运行状态刷新手动调试页。

## 增加另一种控制卡

为新卡型建立对应的 VendorApi 与驱动适配器，在 Host 中实现 `IMotionCardHostConnection`，提供该卡型选项、配置转换和逻辑 IO。随后在 `CreateMotionCardConnection()` 选择新的实现，并更新 Host 的项目引用、设备硬件配置和匹配的原生运行库。

设备流程继续使用逻辑轴、逻辑 IO 和工程单位。新适配器需要按目标卡型和 SDK 版本单独验证；API 声明或其他卡型的仿真结果不能代替验证。

当前 Host 只接入一张 ZDevelop `VPLC5xx-Simu` 仿真卡。Runtime 的 `MotionCardGroup` 虽然可以管理多张卡，但当前 Host 连接接口和轴/IO 配置页只接入一张卡；连接时发现 `MotionCards.json` 配置多张卡会明确拒绝，避免只连接第一张卡而遗漏其余卡。雷赛、固高、ADLINK 等目录中的原生 API 声明不代表已有可运行的驱动适配器。
