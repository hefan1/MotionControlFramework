# 任务模块：先从这些文件读起

当前完成的是阶段 1：任务定义、现有配置引用、JSON 存储和离线校验。
这里没有执行器、编辑器页面或控制器连接代码；定义校验通过不等于允许设备运行。

## 文件如何定位

| 文件夹 | 要找什么 |
|---|---|
| `Definitions` | 任务由哪些节点组成，每种节点有哪些参数 |
| `Catalog` | 现有轴、DI/DO、变量、示教点从哪里来 |
| `Validation` | 为什么任务不能保存，哪个节点或字段有问题 |
| `Configuration` | JSON 如何读取，版本如何检查，旧文件如何保留 |
| `Samples` | 可直接阅读的示例任务；不会自动写入现场目录 |

建议阅读顺序：`TaskDefinition` → `SequenceNode` → `MoveAxisNode` → `TaskResourceCatalogLoader.Load` → `TaskDefinitionValidator.Validate` → `TaskDefinitionStore.Save`。

调用关系只有一条明确主线：

```text
已有设备 JSON → TaskResourceCatalogLoader → 只读目录
任务定义 + 只读目录 → TaskDefinitionValidator → 中文错误/提示
通过定义检查 → TaskDefinitionStore → 现有 MachineConfigurationStore 原子保存
```

## 现有配置怎么复用

以下路径都位于 `MachineConfigurationStore.GetMachineDirectory(machineId)` 返回的设备目录内：

- `HardwareConfig/AxisParameters.json`：逻辑轴、所属卡、工程单位和行程；不猜测缺失单位。
- `HardwareConfig/IoMapping.json`：分别读取逻辑 DI 和 DO，不混用方向。
- `Parameters/DeviceVariables.json`：变量名称和类型；不读取或写入当前生产值。
- `Parameters/ManualAxisPositions.json`：按现有 `ControllerId.Axis轴号` 找到逻辑轴，再按点位 ID 引用。

前两个文件必须存在。后两个可选文件缺失时返回空列表并记录提示；存在但损坏时会报错。
旧示教点找不到对应物理轴键时只报告未匹配，不跨卡猜测替换关系。
目录是本次读取的只读副本。现场修改映射、点位或变量定义后，需要重新读取并重新校验；它不是实时设备状态快照。

## 最小使用方法

```csharp
// 只读加载现有设备配置，不连接控制器。
var configuration = new MachineConfigurationStore(configurationRoot);
var catalog = new TaskResourceCatalogLoader(configuration).Load("Machine01");
var tasks = new TaskDefinitionStore(configuration);

// 正常读取入口会检查当前目录中的引用。
TaskDefinition definition = tasks.Load("Machine01", "TransferCycle.json", catalog);

// 修改后先查看全部提示；Warning 也需要在后续执行阶段处理。
var issues = new TaskDefinitionValidator().Validate(definition, catalog);
foreach (TaskValidationIssue issue in issues)
{
    Console.WriteLine(issue.ToString());
}

// 修改已保存定义时递增修订号；有定义错误时 Save 不覆盖旧文件。
definition.Revision++;
tasks.Save("Machine01", "TransferCycle.json", definition, catalog);
```

上例类型分别位于 `MotionControl.Framework.Runtime.Configuration`、本项目的 `Catalog`、`Configuration`、`Definitions`、`Validation` 命名空间。
任务保存位置是设备目录下的 `Tasks/文件名.json`。不能用文件名跨目录写入。
覆盖成功时 `Save` 返回旧文件备份路径；首次保存或内容完全相同时返回 null。
旧文件损坏或版本不支持时拒绝覆盖，先人工核实备份后处理。

有子任务时，先用 `LoadForEditing` 读取完整任务库，再将其他任务作为 `dependencies` 传入 `Validate`、`Load` 或 `Save`。`LoadForEditing` 只检查 JSON 格式，不能代替引用校验。主任务不要再次放入 dependencies。

## 节点规则

- 一个任务有一个 `Sequence` 根节点。节点 ID 不随名称和排序改变。
- `ParallelGroup` 至少两个 `Sequence` 分支，含义固定为全部成功后汇合；它不是插补。
- `CoordinatedMove` 只定义同卡、统一 mm 单位的多轴绝对直线运动。卡能力、最大轴数、组完成和组停止尚未验证，因此总是给出待核实提示。
- `MoveAxis` 必须明确 Absolute 或 Relative，并填写工艺速度/加减速度。不能把手动页速度暗中当作生产速度。
- 位置可来自常量、变量或同一轴的示教点；相对距离不能引用绝对示教点。
- 条件只有 Compare、All、Any、Not；不允许任意脚本。字符串和布尔只支持相等/不等。
- 数值常量使用不受界面语言影响的小数点格式。DateTime 常量使用 ISO 8601 往返格式，例如 `2026-10-22T10:30:00.0000000+08:00`。
- 变量类型沿用已有配置名称：Boolean、Integer、Number、String、Byte、Character、DateTime、UInt32；后续 UI 可显示 bool、int、double、string、byte、char、DateTime、uint。
- 赋值仅允许同类型或当前整数类型到 double；不把浮点数隐式截断为整数。
- 轴资源名称为 `Axis:逻辑轴名`；区域等命名资源由 Host 明确传给目录。此阶段不申请资源，不实现互斥或自动避让。
- JSON 拒绝未知节点、未知字段、数字枚举、重复字段和 `$type` 等类型元数据。

## 后续执行阶段仍要做什么

实时回零/位置参考、伺服、报警、限位、目标变量有效值、采集周期、区域清空、运动完成、取消后的停稳确认，均未由本模块执行。
相对定位重启恢复、并行分支检查点、资源死锁诊断属于后续阶段。
通讯、计算动作、子任务参数绑定暂不接受对应 kind，避免保存一个会被默默跳过的动作。

## 离线检查

使用已安装的 Visual Studio 18 MSBuild：

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj /restore /t:Build /p:Configuration=Debug /nologo
& tools/MotionControl.TaskEngine.Checks/bin/Debug/net48/MotionControl.TaskEngine.Checks.exe
```

将 Debug 改成 Release 可重复检查发布构建。检查只在自己的随机临时目录写文件并清理，不使用现场配置。
也可传 `--catalog 配置根目录 MachineId`，只读查看现有配置是否能形成共享目录，不写文件。
