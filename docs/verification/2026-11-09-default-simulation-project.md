# 最近沙盒仿真项目的本机默认启动

## 目的与实现

此前正式 Host 默认读取原设备配置，重新打开时不能直接使用最近一轮沙盒中的十三轴和 IO 映射。本次通过本机默认项目设置，在无参数启动时打开独立的软件仿真项目；用户没有设置默认项目时仍保持原来的配置入口。`--use-production-config` 明确选择原设备配置，`--simulation-config-root` 可明确选择其他隔离配置。默认仿真项目只允许 `Virtual` 后端；设置损坏、后端变化或端口被其他程序占用时不回退到原设备，也不接管其他服务。

`SimulationConfigurationRoot.Resolve` 负责解析入口并拒绝项目内部的目录联接或符号链接，`HostMotionCardBackendSelection.CreateVirtualOnly` 再次限定后端。项目独有的 `ProjectSession.id` 与项目路径共同标识软件会话，`DefaultSimulationController` 只启动或复用属于该项目、且位于本程序构建目录中的控制器。默认项目的每条控制器请求使用本机管道，`VerifiedLocalControllerPipe` 在发送前读取 Windows 提供的实际管道服务进程号并核对程序路径，再在同一连接上核对项目会话；管道通过 Windows 的远程客户端拒绝标志和访问规则限制为当前用户本机访问。后端的原有 TCP 测试接口仍保留。控制器在 Host 正常关闭后保持运行，重开可沿用该软件会话；控制器进程退出或电脑重启后，其运行时轴和 IO 状态丢失。代码不因此自动运行设备流程。

安装脚本 `tools/Install-RecentSimulationProject.ps1` 从最近一次保存的沙盒配置中创建新项目，检查源配置应用标记及复制前后的清单和哈希，在临时目录完成验证后以不覆盖目标的目录移动落位，再设置本机默认入口。它与 Host 共用单实例锁，但不能阻止其他不遵守该锁的程序同时修改源文件，因此源应为停止使用的快照。脚本复制硬件配置、参数、任务和草稿 JSON；为全新、独立的软件仿真会话建立初始交接记录，不复制旧 Runtime，也不修改或清除源项目中的报警、恢复和资源证据。既有设备项目的迁移、未知报警解除和现场核查仍按 [报警交接待完成项](2026-11-09-alarm-continuity-sandbox.md) 处理。

本机已将默认项目设置为 `%LOCALAPPDATA%/MotionControlFramework/SimulationProjects/RecentSandboxProject`，并在桌面建立“运动控制上位机（最近仿真项目）”快捷方式，指向主工作区 Release Host。原 `%LOCALAPPDATA%/MotionControlFramework/Configurations/Machine01` 未被覆盖。源为主工作区 `tmp/alarm-recovery-20261109/results/preserved-AlarmContinuityEarlyConfig`；源 `AlarmContinuity.pending` 仍保留。安装后逐项对照源文件，35 个 JSON 的 SHA-256 均一致，包括 13 轴、88 个数字输入、14 个数字输出及任务配置。

## 沙盒验证

- 在独立 Windows Sandbox 中安装配置，以无参数方式正常打开正式 Host；软件控制器自动启动并连接。最终构建的轴映射页和 IO 映射页显示所复制的配置（`20-final-default-axis.png`、`21-final-default-io.png`）。正常退出再打开仍加载同一项目，复用同一软件控制器进程及项目会话，配置哈希未变。
- 重复安装同名项目被拒绝；端口已占用时新安装被拒绝；切换到另一个新项目时旧默认设置保留备份。模拟陌生程序占用端口时，Host 仅发送身份查询并拒绝接管（`05-foreign-port-refused.png`）。进一步在断开后替换 TCP 和管道服务，使其伪称另一个真实控制器的 PID；最终版拒绝连接，两个替换服务均未收到命令（`14-release-replacement-rejected.png`），恢复本程序控制器后可重新连接。测试过程中发现沙盒监听表对正常端口报告不可靠，修复为独占绑定探测；同时修复更新默认设置时 PowerShell 的空备份路径问题。
- 同一程序路径、同一端口但会话属于另一项目的软件控制器被拒绝（`15-other-project-refused.png`）；正常会话重新打开会复用原进程。管道权限测试确认当前用户可访问、网络用户被拒绝；同一 Sandbox 中使用 `127.0.0.1` 的远程管道访问对照：普通测试管道可以连接，受限管道拒绝连接。Windows 标志和安全描述符含义见 [Microsoft 命名管道说明](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea) 和 [访问权限说明](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)。
- 在默认项目中通过软件控制器 API 将急停 DI3 置高，正式 Host 拒绝报警复位；将 DI3 恢复原值后未复位，断开并重连，界面仍显示旧报警交接未确认且拒绝复位（`22-final-reconnect-alarm.png`）。正常关闭后，以最终版 Host 和软件控制器打开同一项目，交接未知仍存在且复位被拒绝（`23-final-restart-alarm.png`）。此测试结束时十三轴停止、十四个映射输出不活动；待交接记录和配置已保存，软件控制器正常退出。
- 源任务或工程应用未完成、轴或 IO 映射缺失时拒绝安装；草稿文件被复制，不同新项目有独立会话编号，安装中出现同名目标时保留既有内容，源文件发生可观察变化时拒绝切换。默认项目内部的目录联接被拒绝。以相互冲突的后端配置和无效 IO 映射分别复现未连接却遗留待交接记录，修复后同一场景保留干净记录；原有未知记录不会因这一分支被清除。
- 主工作区 Release 解决方案及隔离配置检查器构建均为 0 警告、0 错误；沙盒内隔离配置检查全部通过、操作页面内部检查 63 组且失败 0、Runtime 回归全部通过；安装检查 8 项和软件控制器协议、身份与运动烟测通过。正式 Host SHA-256 为 `2880636A76CC09BAC1B715CC85BFBC481227C8DD5D38E29AE8E79279AD4C8565`，软件控制器 SHA-256 为 `548FE22372F7EE97A5761456DD403086300744C11C71C382BD119CA4865275F1`，均与沙盒最终测试文件一致。

截图、测试输出和测试配置留在 `tmp/default-simulation-20261109/results/`。上述均为独立软件控制器及沙盒结果，不代表其他控制卡、真实设备或安全回路验收；报警交接仍有既有项目迁移、可信解除入口和并发顺序等未完成问题。
