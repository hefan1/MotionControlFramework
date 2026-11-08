# V1.8 内部检查修复说明

## 修改依据与范围

用户要求修复 V1.8 分支的内部检查，并继续保持中文说明、直接调用和初级 C# 开发者可维护的代码。

本轮基线为 `f42e3bdcc9c135ea7cbfc4bfbb810ae38a3a28e2`。基线 [Windows 检查记录](https://github.com/hefan1/MotionControlFramework/actions/runs/42429578674) 中，正式 `net48` Host 与 15 个检查项目均编译成功；5 个检查项目合计报告 11 个失败用例。编译成功、内部检查通过和正式界面/设备验收分别记录。

## 已定位并修改的问题

### 1. 相对运动诊断检查的同步时机

入口：`tools/MotionControl.TaskEngine.Checks/ExecutionChecks.Diagnostics.cs` 的 `DiagnosticAcceptedTarget`。

原检查只等待“出现诊断”，就修改目标变量。V1.8 在申请轴资源时已经发布诊断，此时轴命令尚未接受；检查可能在程序第一次读取目标前，就把相对距离从 3 改为 30。

现在先等待明确的轴等待类别与 X 轴对象采样，再检查命令接受次数和绝对终点 8，然后修改变量。最终仍严格核对旧快照、完成快照、实际轴位置、到位结果和目录提供的 mm 单位。没有改变运动指令或目标冻结规则。

### 2. 结构化诊断夹具的变量标识

入口：`tools/MotionControl.StructuredDiagnostics.Checks/Fixtures.cs`、`DiagnosticChecks.cs`。

原夹具把带分号、等号的文字当作变量稳定名称，触发现有变量命名校验，四项检查尚未进入待验证行为就失败。

现在使用合法的稳定名称，把原特殊文字保留在 `DisplayName`，并增加二者分别保留的断言。输入和资源名称中的特殊标点继续参与诊断检查；未知、未读取及完整对象引用的断言保留。没有放宽生产变量命名规则。

### 3. 资源会话清理的证据保留与系统语言

入口：`src/MotionControl.TaskEngine/Resources/TaskResourceJournal.cs` 的 `Save`、`ClearSafeSession`；对应检查为 `tools/MotionControl.MachineDevice.Checks/ResourceJournalChecks.cs` 的 `ResourceJournalCloseChecks`。

旧检查依赖 Windows 的中文异常片段，英文系统返回的目录错误导致断言失败。进一步核对发现原清理方法先删除记录文件、再删除会话目录；存在未知子目录时，关闭虽然失败，原始记录却已被删除。

现在本实例只登记成功写入或核对一致的记录路径。清理时先完整检查会话目录和条目，发现未知文件、子目录或重解析点时，在删除任何记录前拒绝清理；不会递归删除未知内容。调用方继续保留控制权，排除障碍后可重试关闭。

检查改为验证异常类型和真实状态，依次覆盖未知子目录、普通未知文件及 `unknown.guard`。每次都核对未知内容、原始记录字节和控制权保留，最后核对关闭成功及重复关闭行为。

这不是文件系统事务。预检后若发生外部并发修改、文件访问拒绝或其他磁盘错误，仍可能部分清理；异常继续上抛，不能声称任意 I/O 失败都能原子保留全部文件。重解析点拒绝条件已做代码复核，本轮普通目录用例不代表已创建真实重解析点运行验证。

### 4. 工位状态、变量列与工程预览检查

入口：`tools/MotionControl.OperatorPages.Checks/Program.cs` 的 `StartReadinessConsistency`、`V14FilterLayoutChecks.cs`、`EquipmentProjectPageChecks.cs`。

旧工位检查仍要求工位显示整机状态。现在分别核对总览和流程页的整机启动阻断，并验证不同工位的等待、暂停、未提供状态不会被整机状态覆盖。

变量页面已增加工程单位列，旧检查仍把第六列当作“人工可写”。现在按实际绑定属性定位人工可写列和工程单位列，继续检查中英文表头及原有尺寸下的布局。

旧工程预览检查手工构造的数据缺少 `MergedFiles`、`RetainedFiles` 和应用目标，因而触发空引用。现在补齐服务实际输出的预览契约，明确检查包的导出来源、本机应用目标、包内文件与包外保留文件的区别；没有把缺失字段默认成一个有效空工程。

同时修复 `src/MotionControl.Host.Wpf/Configuration/EquipmentProjectView.xaml.cs`：选择未改变时，语言切换也主动刷新文件对照说明。新检查验证切换英文后保留文件的说明更新，而左侧原配置内容保持原样。这一变化只刷新只读预览，不触发配置应用。

### 5. 新建编辑预览的示教点归属

入口：`src/MotionControl.TaskEngine.Wpf/Simulation/TaskEditingPreviewWindow.cs` 的 `CreatePreviewStore`；检查入口为 `Program.cs` 的 `EditorIntegration`。

旧示例生成器没有声明点位组的逻辑轴归属，V1.8 会如实提示归属未确认，导致原本期望正常保存检查通过的示例出现提醒。现在只为新建独立预览文件写入由该示例轴定义提供的 `logicalAxisName`，并先检查目录中点位归属已明确，再保留原来的保存、检查、任务行刷新和修改后检查失效断言。

已有预览文件按原规则保留，不自动迁移或替现场配置确认归属。原有等待超时提醒检查继续要求显示提醒，不能把所有提醒当作通过。

### 6. 监控页面的完整等待与采样质量

入口：`tools/MotionControl.TaskMonitor.Checks/Program.cs` 的 `ExerciseOperatorWaitDescriptions`。

旧断言要求提示只剩一句短文案，与 V1.8 保留完整等待对象的行为不一致。现在依据真实运行快照核对等待类别、原因码、请求与完成两个信号、期望值、实际值和采样质量，并继续核对操作员提示与原始工程诊断。

检查保留条件等待、未知反馈、中英文切换和完成后的交接阶段；没有采样的阶段必须显示未提供，不能借用上一阶段的有效值。生产等待语义和动作许可没有为适配旧文案而回退。

## 检查入口的可读性

入口：`tools/Verify-V18.ps1`。

保留正式 Host 和原有 15 个检查项目，增加中文阶段序号、每项构建/执行结果，以及控制台和 GitHub Actions 摘要中的结果表。构建失败、检查失败、程序缺失和执行异常分别记录；一个项目失败后仍检查其余独立项目，最后只要存在失败就返回失败。

摘要写入异常只影响摘要展示，不覆盖构建和内部检查的原始结果。没有删除检查、忽略失败或放宽控制条件。

## 验证记录

提交前已核对差异、调用顺序和中文说明，并执行 `git diff --check`。本轮开发环境不能运行 Windows WPF；修复提交的正式编译和全部内部检查以对应 GitHub Actions 运行结果为准，实际结果在后续验证记录中补充。

本轮没有操作正式 Host 桌面、厂商仿真器或实物控制器。内部检查使用内存设备、替身对象和临时配置，不等同于正式界面或设备验收。
