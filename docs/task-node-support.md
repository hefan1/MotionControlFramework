# 任务节点支持矩阵与修改清单

本文按 V1.8 手册第 807–826、831–832 页对应的源码边界整理。它描述当前代码接入情况；每次执行仍须核对实际后端、有效配置、设备连接、回零、报警、资源和准备状态。

## 1. 三种检查分别回答什么

| 检查 | 主要入口 | 能回答的问题 |
| --- | --- | --- |
| 定义与引用 | `TaskDefinitionValidator`、`TaskMachineConfigurationValidator`、`TaskStationPreparationValidator` | 节点字段、任务调用、准备任务及逻辑对象引用是否一致 |
| 当前后端能力 | `TaskDeviceCapabilityValidator`、`TaskDeviceCapabilities` | 当前实际任务设备是否已实现组定位及资源进入/退出条件；插补是否已接入执行器 |
| 运行许可与现场状态 | `TaskMachineController`、`MachineRuntime`、`MachineTaskDevice` | 当前连接及控制权是否有效，准备、回零、报警、资源和动作反馈是否允许继续 |

正式 Host 向任务配置和工程导入服务传入 `MachineTaskDevice.ExecutionCapabilities`。实际任务启动再次从设备接口读取能力。未提供后端能力的独立编辑工具只完成定义检查，必须明确说明“未核对当前后端可执行性”。配置文件不能通过设置 `supports...` 获得未实现的驱动能力。

## 2. 节点支持矩阵

下表“已接入”仅表示对应执行路径存在，不表示已在真实设备通过验收。“普通设备动作”还受实际适配器、现场信号及运行许可约束。

| 节点 Kind | JSON/定义 | 编辑器 | 内存执行器 | 正式任务设备边界 |
| --- | --- | --- | --- | --- |
| `Sequence` | 已接入 | 可编辑 | 已接入 | 按顺序执行；子节点能力须全部满足 |
| `ParallelGroup` | 已接入 | 可编辑 | 已接入 | 独立分支并行并汇合；不承诺插补或同步到达 |
| `ConditionBranch` | 已接入 | 可编辑 | 已接入 | 两个分支均参与准入检查；本次未选分支也不能隐藏未实现能力 |
| `CallTask` | 已接入 | 可编辑 | 已接入 | 整个加载任务库参与检查，拒绝缺失引用与递归 |
| `ResourceScope` | 已接入 | 可编辑 | 支持资源和条件 | 无条件资源范围可用；配置进入互锁、退出互锁或旧退出条件时准入拒绝 |
| `MoveAxis` | 已接入 | 可编辑 | 已接入 | 普通设备动作；位置、速度使用轴工程单位 |
| `MoveAxisGroup` | 已接入 | 可编辑并保存草稿 | 已实现多轴分别定位 | **未接入，任何前序动作之前拒绝**；不能退化成顺序单轴 |
| `CoordinatedMove` | 保留定义与严格解析 | 已有定义只读，不提供新建 | 未接入 | **未接入，执行前拒绝** |
| `AxisCommand` | 已接入 | 回零/停轴入口 | 已接入 | 实际回零方式由后端提供；Virtual 实现固定零位回零，但没有厂商回零模式选项，映射页不能虚构可选模式 |
| `SetOutput` | 已接入 | 可编辑 | 已接入 | 逻辑输出命令；不等同于机构物理反馈 |
| `WaitInput` | 已接入 | 可编辑 | 已接入 | 读取逻辑输入；未知或读取失败不能作为 false 放行 |
| `WaitOutput` | 已接入 | 可编辑 | 已接入 | 等待输出命令读回；不能代替夹紧/松开到位输入 |
| `ReadInput` | 已接入 | 可编辑 | 已接入 | 已知输入才写入目标变量 |
| `ReadInputWord` | 已接入 | 可编辑 | 已接入 | 按显式位序读取锁存编码，验证有效/成功信号及采样一致性 |
| `WaitExclusiveInputs` | 已接入 | 可编辑 | 已接入 | 等待互斥输入组合并原子发布结果；无效组合不放行 |
| `WaitAxisPosition` | 已接入 | 可编辑 | 已接入 | 目标、实际、容差沿用具体轴的工程单位；这里只等待，不发定位动作 |
| `WaitCondition` | 已接入 | 可编辑 | 已接入 | 按明确类型及量纲检查条件，未知值不放行 |
| `Delay` | 已接入 | 可编辑 | 已接入 | 工艺延时；暂停期间不消耗活动计时 |
| `SetVariable` | 已接入 | 可编辑 | 已接入 | 按类型、已声明单位/量纲和范围校验；不会自动换算单位 |
| `WriteRecord` | 已接入 | 可编辑 | 有记录存储入口 | 正式 Host 配置本地记录存储；缺少存储或写入失败须报错，不等同于 MES 已接入 |

任务定义 JSON 的具体类型分派位于 `src/MotionControl.TaskEngine/Configuration/TaskNodeJsonConverter.cs`；执行分派位于 `Execution/TaskNodeExecutor.cs`；编辑新建位于 `src/MotionControl.TaskEngine.Wpf/Editing/TaskEditorNodes.cs`。使用显式类型分支，便于沿调用链逐项阅读和调试。

## 3. 增加一种节点时逐项核对

| 环节 | 对应文件或目录 | 必须核对 |
| --- | --- | --- |
| 模型 | `TaskEngine/Definitions/` | 稳定 Kind、字段默认值、工程单位、中文注释；未知值不能默认成成功 |
| 序列化 | `Configuration/TaskNodeJsonConverter.cs` | 必填字段、枚举、未知字段拒绝、旧格式兼容、JSON 往返 |
| 定义检查 | `Validation/TaskNodeValidator*` | 类型、单位/量纲、数值范围、逻辑对象和报警引用 |
| 后端能力 | `Validation/TaskDeviceCapabilityValidator.cs`、`Device/` | 实际设备是否明确实现；整个库及所有分支在动作前检查 |
| 执行 | `Execution/TaskNodeExecutor*`、设备实现 | 接受、等待完成、暂停、取消、超时、停止收尾、故障隔离；失败不伪装成功 |
| 编辑 | `TaskEngine.Wpf/Editing/` | 新建、表单、读回、默认选择、检查定位、中英文；显示选项不能暗示后端可执行 |
| 状态和诊断 | `TaskStepDiagnostics`、`TaskOperatorText`、监控页 | 稳定等待类别、对象键、实际采样、质量、单位和实际时限；不能解析中文文案决定行为 |
| 工程投影 | `Projection/TaskProjectionBuilder*`、`Views/TaskProjectionDetailCache` | 导航目标必须具体且同源；切换后不能继续显示旧任务对象 |
| 资源及准备 | `Validation/TaskResourceUsageValidator.cs`、`Machine/TaskMachineController.AxisReadiness.cs`、`Preparation/` | 轴/IO/区域声明、准备任务引用、回零需求和恢复边界 |
| 树操作 | `TaskEditorNodes.Children/CopyNode/RenewIds` 及各显式遍历 | 所有容器均覆盖；移动或改显示名保留 ID，复制内部节点递归换 ID，CallTask 目标保持原值 |
| 验证 | `tools/*Checks` | 真实入口的正向、拒绝、取消、异常和旧配置兼容；有 IO 或运动副作用时验证拒绝发生在第一条动作之前 |

路径表中的 `TaskEngine/` 指 `src/MotionControl.TaskEngine/`，`TaskEngine.Wpf/` 指 `src/MotionControl.TaskEngine.Wpf/`。新增节点不要求建立反射注册框架；直接补齐当前显式分支，并说明修改入口即可。

## 4. 当前 Host 与厂商范围

Host 的已安装后端选择仍是 `Virtual` 与 `ZMotion`。工程包卡型预检由 `HostEquipmentProjectCardValidation` 读取实际支持选项，当前只接入一张控制卡。Runtime 存在多卡契约，并不代表当前 Host 已完成多卡装配。

正运动适配器仍限定本机 ZDevelop 的 `VPLC5xx-Simu` 仿真身份。实体控制器、其他卡型、雷赛和 ADLINK 不能凭已有原生 API 声明被认定为可用。24 轴案例也不能据此推导为某种现场总线或某张控制卡已通过验收。

## 5. 版本和身份不要混用

| 字段/概念 | 含义 |
| --- | --- |
| `MachineId` | 当前应用配置和运行数据的设备隔离标识 |
| `SourceMachineId` | 工程包导出的来源设备；不代表当前运行设备 |
| `TargetMachineId` | 本次工程应用的目标设备 |
| 任务 `Revision` | 任务或设备清单内容修订号 |
| `schemaVersion` | 具体文件的数据格式版本 |
| 源文件签名 | 文件字节及存在性的检查依据；用于拒绝外部变化后的旧检查结果 |
| 连接/任务会话 | 本次运行对象的有效期；重连或重载后旧异步结果不得继续成为当前值 |
| 变量 `SourceId` | 变量实际数据来源的稳定标识；显示说明文字另用 `SourceDescription` |

完整工程实际进入 Apply 后必须正常关闭并重开 Host。更新同一设备任务则按“保存草稿 → 检查 → 关闭任务会话并断开 → 更新任务文件 → 重连并重新读取”执行，不把所有任务保存都扩大成必须重启整个 Host。
