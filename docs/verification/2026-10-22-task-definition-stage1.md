# 任务模块阶段 1 验证记录

日期：2026-10-22。基线：`b7601ce`。

## 实施范围

- 新增独立任务定义、只读配置目录、定义校验器、严格 JSON 读取和任务存储。
- 新增离线检查项目、可读取的样例和初级开发者阅读说明。
- 解决方案中加入新项目；Host、Runtime、WPF 页面、厂商适配器和原生 API 的代码未修改。
- 本阶段不创建执行器、不调用动作、不读写控制器；现场配置只读。

## 构建

- 使用 `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe` 构建 `tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj`，带 `/restore /t:Build`。
- Debug 和 Release 均成功，包含 Contracts、Runtime、TaskEngine 和 Checks 的依赖构建。不是整套 WPF 解决方案的 UI/运行验收。
- 初次使用 VS2022 BuildTools 的 MSBuild 未解析到当前环境 SDK；切换已安装的 VS18 MSBuild 后成功。没有修改全局 SDK 配置或项目 net48/C#7.3 基线。
- 构建环境选中 .NET SDK 11 预览版本并显示 NETSDK1057 信息；输出目标仍为 .NET Framework 4.8。

## 离线检查

最终代码的 Debug、Release 各连续运行 3 轮。每轮 22 组检查均通过，失败数为零。

覆盖：

1. 仓库 JSON 样例的读取与引用校验。
2. 现有八种变量常量类型及往返保存。
3. 全部已支持节点的 JSON 往返。
4. 示教点存在性、轴归属、未示教状态和相对距离误用。
5. 定位行程、非有限数、速度、加减速度和时限。
6. 变量名称、类型和 UInt32 边界；动态值待运行检查提示。
7. DI/DO 方向、停止方式和延时。
8. 协调定义中的重复轴、跨卡和混合单位。
9. 条件类型、组合结构和条件循环。
10. 重复节点 ID、节点对象循环、分支结构和资源名称。
11. 子任务缺失、直接递归和间接递归，含节点定位。
12. 树深度限制和数据来源互斥。
13. 未知版本/节点/字段、数字枚举和 CLR 类型元数据。
14. 重复 JSON 字段、多根对象和损坏文件。
15. DO 目标状态缺失和非布尔类型。
16. 新建、覆盖、旧文件备份和设备隔离。
17. 无效定义、未递增修订、不同任务 ID 覆盖时保留原文件。
18. 未来版本、损坏旧文件和越目录路径保护。
19. 现有硬件/变量/示教 JSON 格式的兼容读取。
20. 可选文件缺失与损坏的区别，旧点位无法映射的提示。
21. 目录重复名称和非法范围。
22. 新项目无 WPF、Host 和厂商程序集引用，输出中没有厂商原生 DLL。

## 现场配置兼容检查

使用检查程序的 `--catalog` 模式只读加载：

`%LOCALAPPDATA%\MotionControlFramework\Configurations\Machine01`

读取结果：2 根逻辑轴、2 个 DI、2 个 DO、2 个设备变量、20 个示教点。
在 Release 读取前后计算该目录 8 份 JSON 文件的 SHA256，结果全部一致。

## 未覆盖范围

没有执行器、资源仲裁运行、插补、真实 SDK、ZDevelop 画面、WPF 页面或实体设备测试。本记录不能作为这些功能已完成的证明。模型中的 TimeoutMs、资源范围和动作定义在后续实现执行器时仍需逐项落实。
