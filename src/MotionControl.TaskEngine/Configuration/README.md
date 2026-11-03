# 单机设备工程包

`MachineProjectPackageService` 只管理当前单台设备的配置，不连接控制卡、不执行工位动作。工程包是带版本和逐文件 SHA256 的 UTF-8 JSON；导入前必须先预览，校验设备定义、控制器、轴/IO、急停和任务引用。工程包不包含 C# 程序代码，所需设备定义必须由当前 Host 明确登记。

Host 创建服务时应传入 `validateCard` 回调，用已有运动卡连接的实际能力检查后端、厂商、卡型、硬件适配器、轴数量和卡数量。软件控制器后端与设备硬件映射使用的适配器名称是两个字段；未提供该回调时，工程只能导出，预览和应用会保守拒绝。

包内包含已登记的 `Parameters` 配置、轴/IO/安全配置、运动卡明确引用的连接配置以及 `Tasks` 任务 JSON。`Runtime`、`Drafts` 和 `Backups` 不入包也不被导入覆盖；目标设备中包外的 JSON 保留，预览会提示。完整工程导出时若发现没有登记的 `Parameters/*.json`，会列出文件并拒绝导出，不能把可能缺少设备参数的文件当成完整工程。`*.before-*.json` 等历史备份命名不会被当作工程参数。

新增设备自有工程参数时，由 Host 在创建服务时通过 `additionalConfigurationPaths` 明确登记 `Parameters/文件名.json`；该清单必须来自可信的程序代码，不能依赖可能处于半应用状态的 `Equipment.json`。不要登记密码、运行状态、产品记录或恢复证据。同一套登记清单须用于导出、预览、导入与启动后的中断恢复。

例如，假设某设备项目定义了 `Parameters/MyEquipmentSettings.json`，Host 组合入口可以保留一个固定的配置路径清单，再传给服务（实际名称由设备项目决定）：

```csharp
// 此清单由已安装程序确定；不能从现场待恢复的配置文件推断。
string[] equipmentFiles = { "Parameters/MyEquipmentSettings.json" };
var projects = new MachineProjectPackageService(store, machineId,
    supportedEquipmentDefinitions, supportedBackends, equipmentFiles, validateCard);
```

调用 Host 必须在整个导入或恢复期间独占连接及运行命令，确认控制器已断开且没有未确认的停止或恢复状态；服务的 `canApply` 回调会重复核查，但它不能代替 Host 的原子命令门禁。导入前后显示轴、IO、安全和设备参数由工程师核对；导入只保存配置，不自动连接、回零或启动。

应用前在 `Backups/EquipmentProject-<GUID>` 保存当前已登记的完整工程配置、旧卡引用的硬件连接文件及所有将被覆盖的同名文件，保留原始字节和签名；未登记的其他文件仍保留在原处，不冒称包含在完整工程中。备份清单的 `WillWrite` 标识本次被覆盖的文件，恢复时只处理这些文件，其他保留文件不会被回滚覆盖。随后持久写入 `Runtime/EquipmentProjectApplyPending.json`。如果进程在多文件更新中退出，下次启动应在读取设备配置或连接之前检查 `HasPendingApply`，阻止设备控制并通过 `RecoverPendingApply` 恢复。恢复前同时核对事务标记、备份清单和文件签名；损坏时保留标记并提示维护人员。只在应用失败或处置中断时恢复备份，正常测试结束后不自动恢复旧配置。

每次写文件前再次对比预览时原始字节，提交前复核工程包中的所有目标文件；如果有未参与工程门禁的其他进程同时改写，保留当前文件、备份和未完成标记，不自动覆盖第三方修改。系统不能阻止外部程序在检查与文件替换之间竞争写入；设备交付时仍应禁止其他程序同时修改现场配置。

回归入口：`tools/MotionControl.MachineProject.Checks`；传入已有隔离配置根目录作为第一个参数，可额外只读导出并在临时目录验证现有设备工程。该检查不等于正式 Host 屏幕验收或实体设备验证。
