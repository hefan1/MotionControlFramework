# 任务运行监控页验证记录

## 本次范围

- 工位列表、步骤树与分类详情，关联调用树按父调用编号匹配。
- 步骤页导航：逻辑轴映射、轴手动卡片、DI/DO 映射行、设备变量行。
- 内存示例的复位、并行 DI 等待、轴组搬运、共享区域占用、暂停、恢复与关闭。
- Host 组合和独立 `--task-simulation` 启动入口。

所有运动、信号和变量运行值都来自 `SimulatedTaskDevice`。未连接 ZDevelop、真实控制卡或 IO。

## 自动检查

- `tools/MotionControl.TaskEngine.Checks`：定义/存储 22 组、执行器 53 组、整机调度 12 组通过；未连接控制器。
- `tools/MotionControl.TaskMonitor.Checks`：示例生命周期、重复子任务关联、并行活动步骤/暂停和 WPF 控件构造共 4 组通过。
- `src/MotionControl.Host.Wpf/MotionControl.Host.Wpf.csproj`：Debug 构建通过。
- `MotionControlFramework.sln`：聚合构建未通过；`MotionControl.Equipment.Template.csproj` 的 Verify 临时路径超过 Windows 文件名长度限制（MSB3541）。其余项目输出包含 Host 和两个任务监控项目。单独的 Host 与监控检查构建通过。
- Windows 界面自动读取工具本轮不可用；离屏 WPF 构造和布局计算不作为实际屏幕目视验收。已打开独立内存示例窗口供人工查看。

## 人工查看

运行 `src/MotionControl.Host.Wpf/bin/Debug/net48/MotionControl.Host.Wpf.exe --task-simulation` 后，窗口标题为“任务运行 — 内存仿真”。可在窗口底部的“仿真信号与参考”中将“上料机构安全位”切为有效，再复位并启动以观察并行等待和工位步骤。该窗口不连接或驱动现场设备。

## 明确边界

- 使用设备目录读取任务、轴/IO 映射、设备变量定义、示教点和清单显式声明的命名资源；详情按钮会定位并选择现有页面对象。
- 设备变量目录提供变量名称和类型；当前任务会话的运行值仍是独立内存值，尚未接入 Host 的生产变量存储。
- 只读页面跳转不会自动使能轴、回零、运动或切换 IO。
- 完整任务编辑 UI 和真实任务执行接入不在本次实现范围。
