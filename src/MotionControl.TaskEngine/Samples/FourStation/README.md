# 四工位单件交接测试样例

这是用于正式 Host 和本机软件控制器联调的独立测试配置。四个工位各有独立的轴：`Load_X`、`Press_Z`、`Camera_X`、`Unload_X`。上料工位依次请求压装、检测和下料；每次交接遵循请求、完成、撤销请求、完成复归四个阶段。这个样例是**单件串联交接**，不能代表多件并行、转盘分度、共享区域防碰撞或整机安全验收。

`FourInit` 在每次创建新任务会话后的首次启动运行，清除六个请求和完成变量，然后放行工位；同一会话暂停或故障复位不重复初始化。此清除策略仅用于确定没有在制品的测试现场，不适合未经工艺确认的实际断电恢复。任务文件等待 DI 的超时为 30 秒，等待下游交接为 180 秒。样例使用 0 和 1 mm 两个绝对目标、5 mm/s，具体值不是现场设备推荐参数。

## 安装

先关闭正式 Host，确认 Machine01 使用软件控制器、任务列表入口，轴和 IO 映射符合下表。安装程序不连接控制器或运动轴；它不会修改或覆盖任务草稿。现有任务清单、变量和报警文件会备份，原有任务文件保留，新任务使用独立标识。

在仓库根目录运行：

```powershell
dotnet build .\tools\MotionControl.TaskEngine.Checks\MotionControl.TaskEngine.Checks.csproj -c Release
& '.\src\MotionControl.TaskEngine\Samples\FourStation\InstallSample.ps1' -ConfigurationRoot "$env:LOCALAPPDATA\MotionControlFramework\Configurations"
```

脚本验证既有轴与 IO、变量和报警类型、任务标识以及设备清单，再在写入后用框架的正式任务配置校验器检查；中途失败会尝试回滚，备份路径会在输出中显示。已有同名任务或冲突变量、报警时直接拒绝覆盖。安装后的任务清单是当前运行文件；如果任务配置页的草稿来自旧版本，页面会提示来源变化，不能把该旧草稿当成新任务安排。请按实际需要在页面中处理，**不要直接应用旧草稿覆盖新运行文件**。

| 用途 | 逻辑名 | 控制器通道 |
| --- | --- | --- |
| 上料输入 / 夹具输出 | 上料到位 / 夹具夹紧 | DI4 / DO0 |
| 压装反馈 / 压头输出 | 压装完成 / 压头动作 | DI5 / DO1 |
| 检测反馈 / 检测输出 | 检测完成 / 相机触发 | DI6 / DO2 |
| 下料反馈 / 下料输出 | 下料到位 / 下料电磁阀 | DI7 / DO3 |

脚本会追加 `Press.Request`、`Press.Complete`、`Inspect.Request`、`Inspect.Complete`、`Unload.Request`、`Unload.Complete` 六个 bool 变量和两个样例报警，保留原变量、报警及旧任务。此样例也使用现有的 `DEMO_SENSOR_TIMEOUT` 停机报警和 `LOAD_MATERIAL_MISSING` 非停机报警。

## 软件控制器测试

安装完成后，先关闭已有的上位机和占用所配端口的控制器；然后在仓库根目录一键启动：

```powershell
& '.\tools\MotionControl.VirtualController\StartFourStation.ps1'
```

脚本会核对 Machine01 任务清单与 IO 映射，检查正式配置可读取，在配置端口空闲时启动本项目软件控制器并设置四组规则，再打开正式 Host。已有 Host、端口占用、规则或输出冲突时拒绝接管；它不自动使能、回零、复位或启动任务。脚本输出本次控制器和 Host 的进程号，供测试结束时辨认。启动失败时只会尝试关闭它自己新建、且尚未交给 Host 的软件控制器。

若要手动配置，启动本项目软件控制器并确认输出处于关闭状态后，在仓库根目录运行：

```powershell
$taskCommand = '.\tools\MotionControl.VirtualController\ControllerCommand.ps1'
& $taskCommand -Action StartFeeder
& $taskCommand -Action ConfigureFeedback -OutputChannel 1 -InputChannel 5
& $taskCommand -Action ConfigureFeedback -OutputChannel 2 -InputChannel 6
& $taskCommand -Action ConfigureFeedback -OutputChannel 3 -InputChannel 7
```

启动正式 Host 后，核对连接、轴使能及回零，在任务运行页读取任务、检查现场、复位并启动。压装反馈异常可通过 `& $taskCommand -Action FeedbackStuck -InputChannel 5` 保持当前反馈；该输入下一次应变为有效时将等待超时。解除故障使用 `& $taskCommand -Action FeedbackRestore -InputChannel 5`，在 Host 确认报警原因、机构和输出后复位并启动。压装故障时 DO1 可能仍保持有效，不能把暂停视为输出已关闭。

测试结束，通过 Host 停止任务并确认四轴停止、相关 DO 均关闭，停止上料规则并移除反馈规则，确认 DI 恢复安全值。软件控制器不模拟真实惯量、限位、机械碰撞、安全回路、相机或压装工艺。

## 检查

只读核对现有 Machine01 目录与样例兼容性，并在内存中检查旧握手信号初始化、压装故障与恢复：

```powershell
& '.\tools\MotionControl.TaskEngine.Checks\bin\Release\net48\MotionControl.TaskEngine.Checks.exe' --four-station-demo "$env:LOCALAPPDATA\MotionControlFramework\Configurations" Machine01
```

此命令不修改 Machine01、不启动正式 Host，也不连接软件控制器，不能代替正式窗口里的操作与运行验证。
