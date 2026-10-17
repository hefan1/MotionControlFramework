# 正运动 ZDevelop 仿真适配器

本项目实现框架的 `IMotionCard`、`IAxis` 和 `ILogicalIo` 契约，底层调用同级 `VendorApi` 目录中的 `MotionControl.VendorApi.ZMotion` P/Invoke 声明。

## 当前支持范围

- 只连接本机 ZDevelop 仿真器：`127.0.0.1`。
- 连接后读取控制器身份，并且只接受 `VPLC5xx-Simu`。实体控制卡会被拒绝；实体型号确认后再增加对应支持。
- 连接配置、轴参数和 IO 映射分开保存：卡地址在 `ZDevelopSimulation.json`，轴参数在 `AxisParameters.json`，IO 映射在 `IoMapping.json`。
- 设备项目的 `HardwareConfig/AxisParameters.json` 与 `HardwareConfig/IoMapping.json` 是设备运行时的唯一参数来源；适配器样例中的同名文件只用于仿真验证。
- 已在本机 ZDevelop V3.10.10 仿真器验证连接：控制器类型 `VPLC5xx-Simu`、版本 `20180521`、身份 `1234`。真实厂商 SDK 调用已验证轴状态、正负限位，以及 DATUM 模式 3、4、13；虚拟输入通过 `ZSIMU_IN` 切换并在测试后恢复。该结果验证仿真器和适配器调用链，不等于实体卡接线验收。
- 每个逻辑轴配置一个所属控制卡标识、0 起始的正运动轴号、`UNITS` 换算、仿真软限位和点动加减速度。
- DI/DO 映射到所属控制卡的正运动单个物理 IO 点号；适配器通过厂商单点 API 读写，不按 32 位端口自行拆位。
- 支持轴状态、伺服使能、绝对/相对运动、连续点动、停止、DATUM 回零、轴报警清除和逻辑 DI/DO。
- `HomeAsync` 在 ZDevelop 仿真器上调用真实的 `ZAux_Direct_Single_Datum`，按轴参数中的模式、搜索速度、爬行速度和加减速度执行回零；轴停止后读取控制器回零结果，`HOMESTATUS=1` 或 `2` 才确认完成。
- 仿真回零依赖 ZDevelop 中轴的 `DATUM_IN`、限位输入和 `INVERT_IN` 配置，以及仿真器的虚拟输入信号；运行前请确认模式与输入映射匹配。

| 正运动 DATUM 模式 | 大白话动作 | 仿真验证需要的信号 |
| --- | --- | --- |
| 3 | 先正向快速找原点，碰到原点后反向慢速离开原点 | 原点输入 |
| 4 | 先反向快速找原点，碰到原点后正向慢速离开原点 | 原点输入 |
| 13 | 先正向找正限位，碰到后反向找原点，再慢速离开原点 | 正限位输入、原点输入 |

模式定义依据随 ZDevelop V3.10.10 安装的正运动帮助文档。其他模式应按实际控制器型号的 DATUM 说明配置。

仿真配置样例位于 `Samples/`。里面的轴号、单位换算、限位和点位均是仿真样例值，不能作为生产设备参数。

## 使用顺序

1. Host 从设备配置加载卡连接、轴参数和 IO 映射，并创建 `ZMotionCard`；同一个卡实例同时交给 `MotionCardGroup` 和 `ILogicalIo` 使用。
2. 调用 `ConnectAsync`。此时只连接并读取身份，不写轴参数。
3. 确认 `ActualControllerType` 为 `VPLC5xx-Simu` 后，显式调用 `ApplyAxisParametersAsync`。
4. 轴参数应用成功后，才可以使能、调用 DATUM 回零、点动或定位。
5. 流程或调试页面通过 `IAxis` 与 `ILogicalIo` 操作，不直接引用厂商 API。

`ApplyAxisParametersAsync` 会写入仿真轴的 `UNITS` 和软件限位；调用前应确认仿真轴空闲。运动结束等待受配置的超时约束；取消或超时时适配器会请求受控停止并等待停止反馈。

## SDK 部署

仓库包含适配器源代码和托管 P/Invoke 声明，不包含正运动原生 SDK。运行时需由用户按正运动 SDK 许可与位数要求提供 `zauxdll.dll` 及其依赖库。当前解决方案没有配置特定进程位数；Host 的进程位数必须与实际部署 DLL 相符。

当前状态位解释沿用参考工程对 `AXISSTATUS` 的处理：bit 4/5 为正/负限位、bit 22 为伺服报警。正负限位映射已在 ZDevelop 仿真器验证；实体卡仍需按所用控制器和接线验收。

## 仿真连接检查

`tools/MotionControl.ZMotionSimulatorCheck` 提供可重复运行的连接验证。默认仅连接并读取控制器身份，不移动轴，也不写 IO。使用时将与程序位数匹配的 `zauxdll.dll` 和 `zmotion.dll` 放到检查程序目录，或放入 `PATH` 可搜索的目录，然后运行检查程序。

传入 `--read-status` 可额外读取样例逻辑轴、DI 和 DO 状态；传入 `--exercise-motion` 或 `--exercise-io` 会主动操作仿真轴或 DO，只应在确认目标是仿真器时使用。传入 `--exercise-home --home-mode 3`（也可用模式 4、13）会调用框架的真实 DATUM 回零方法；测试时要按上述流程触发仿真原点和限位输入。
