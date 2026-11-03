# 夹具焊接多工位并行样例

本样例参考 BCW HIVE-BZ 夹具焊接项目的扫码、焊接、压合、复检及载具流转，目的是测试正式 Host 的任务列表与四工位并行，不复制参考设备的机械动作或工艺参数。四个工位独立循环，工位间按“载具可接收、数据复制、接收确认、请求复归、确认复归”交接；每个工位保留一个载具状态，所以下游堵住时上游会逐段等待。

扫码步骤交替写入 SAMPLE-A/CARRIER-A 和 SAMPLE-B/CARRIER-B，附带预设 OK、NG 结果；复检按结果记录 OK 缓存或 NG 缓存去向。**标识循环复用，不是唯一条码或追溯数据；没有连接扫码枪、焊机或视觉，也没有实际执行两条独立的物理分拣。**

每次建立新会话时，初始化任务会清空工位占用、交接状态和最近完成的测试记录，只适用于已经确认设备无在制品的测试；不能用于实际设备的断电后恢复。所有轴、IO 及反馈均为软件控制器测试，不代表机构安全、真实定位或焊接、压合质量。

## 配置及安装

先关闭正式 Host。安装器要求 `Machine01` 已选择 Virtual 后端及任务列表入口，已有 `Load_X`、`Press_Z`、`Camera_X`、`Unload_X` 四轴与以下现有映射，且已存在 `DEMO_SENSOR_TIMEOUT`、`FOUR_HANDSHAKE_TIMEOUT` 停机报警。请先构建 `MotionControl.TaskEngine.Checks` 的 Release，再运行：

```powershell
dotnet build .\tools\MotionControl.TaskEngine.Checks\MotionControl.TaskEngine.Checks.csproj -c Release
& '.\src\MotionControl.TaskEngine\Samples\ClipWelding\InstallSample.ps1' -ConfigurationRoot "$env:LOCALAPPDATA\MotionControlFramework\Configurations"
```

安装器备份运行清单、设备变量、报警文件，新增单独命名的任务文件；已有同名变量或任务时拒绝覆盖。任务草稿不修改，若草稿属于旧运行版本，请勿直接应用旧草稿覆盖新运行清单。此操作会切换当前任务运行清单，需按项目规则保留测试配置；不要对含在制品的设备使用。

| 样例工位 | 现有测试轴 | 测试输出 → 输入 |
| --- | --- | --- |
| 扫码 | Load_X | DO12 夹具夹紧；DI4 上料到位由供料规则产生 |
| 焊接 | Press_Z | DO5 压头动作；DI5 压装完成 |
| 压合 | Camera_X | DO6 相机触发；DI6 检测完成 |
| 复检 | Unload_X | DO7 下料电磁阀；DI7 下料到位 |

这是本测试配置的通道约定：DO0–DO4 留空，DI4 是上料有料信号，不是夹具夹紧反馈；夹具没有独立到位输入，DI12 留空。DI 与 DO 为独立端子，同号仅用于便于查找，输入要由设备或软件控制器规则产生。上述逻辑 IO 不是实际焊接设备接线，也不表示已驱动焊机、压机或相机。软件控制器使用 0–1 mm、5 mm/s，具体现场行程和速度须另行设计；使用不同配置时以 IO 映射为准。

焊接、压合和复检在轴运动前及发出对应测试输出前各确认一次反馈复位；反馈卡在有效时超时报停，不会将旧反馈当成本次动作完成。此检查仅是样例流程前置条件，不能替代真实设备的时序和安全联锁。

## 软件控制器与 Host 操作

本样例不适用只接受旧四工位清单的 `StartFourStation.ps1`。启动本项目软件控制器，确认测试输出均为关闭且没有其他控制器会话后，在仓库根目录配置：

```powershell
$clipCommand = '.\tools\MotionControl.VirtualController\ControllerCommand.ps1'
& $clipCommand -Action StartFeeder -OutputChannel 12 -InputChannel 4
& $clipCommand -Action ConfigureFeedback -OutputChannel 5 -InputChannel 5
& $clipCommand -Action ConfigureFeedback -OutputChannel 6 -InputChannel 6
& $clipCommand -Action ConfigureFeedback -OutputChannel 7 -InputChannel 7
```

在正式 Host 核对软件控制器连接、四轴使能回零和 IO，再读取任务、复位、启动；任务运行页查看各工位当前步骤、等待条件和完成次数。设备变量页可查看 `Clip.Scan/Weld/Press/Inspect.Occupied`、各工位 `Product/Carrier/Result`，及 `Clip.LastProduct/LastCarrier/LastResult/LastDestination`。本地变量只是测试观察值；工位不占用时保留上次数据，应结合 `Occupied` 判断。

`Clip.ReceiveEnabled` 默认 `true`，允许人工写入；设为 `false` 可阻塞复检后的测试下料。设备变量页限制运行中写入，建议在待复位或就绪状态写入 `false`，启动后观察两个以上工位有料；暂停、复位回就绪并确认 UI 允许写入后设回 `true`，再启动。实际窗口是否允许在相应阶段写入以正式 Host 实测为准，不要绕过运行中写入限制。超过等待时限会产生停机报警。

结束时在 Host 停止任务，确认所有轴停止、DO5–DO7 及 DO12 关闭；停止供料并移除三组测试反馈，确认 DI4–DI7 恢复安全值。故障暂停可能保持某些输出，不能假定停止即关闭夹具或气缸。

## 焊接与压合工位准备

`ClipWeldPrepare.json` 和 `ClipPressPrepare.json` 是独立准备任务，生产任务正文和 `ClipInit` 不变。
`StationPreparation.json` 安装为 `Machine01/Parameters/StationPreparation.json`，只引用现有任务、轴、IO 与占料变量。
先关闭正式 Host，再对已经安装本样例的设备执行：

```powershell
& '.\src\MotionControl.TaskEngine\Samples\ClipWelding\InstallPreparation.ps1' -ConfigurationRoot "$env:LOCALAPPDATA\MotionControlFramework\Configurations"
```

安装只增加两个准备任务及独立准备清单，不改变设备入口、不清空变量或载具、不应用旧草稿；同名文件内容不同时拒绝覆盖。
准备清单字段：`id/name/taskId` 表示工位和所用任务；`dependencies` 指定必须先准备且仍在规定姿态的工位；
`axes` 指定回零后待机位置和允许误差（沿用轴工程单位）；`inputs/outputs` 指定期望逻辑状态；
`emptyVariables` 指定必须明确为 false 的占料变量，仅检查，不写入。

焊接使用现有 `Press_Z`、`压头动作` 和 `压装完成`，先确认动作输出关闭、等待完成反馈复位，再回零并停到 0 mm；
压合使用 `Camera_X`、`相机触发` 和 `检测完成`，依赖焊接准备成功且仍在让位位置，再执行同类准备。
这是两个测试工位的回零与让位演示，名称沿用既有映射，不代表真实压合机构或焊接工艺。
`Load_X` 和 `Unload_X` 仍需按已有轴手动入口准备，不能把这两个工位准备通过理解为全设备已经可以生产。
任务准备不负责伺服使能；读取任务之前先在轴手动页确认相关轴已使能且停稳。若已读取任务后发现未使能，先结束任务会话，再使能并重新读取任务。

正式 Host 读取当前任务后，在设备准备页执行整机或单工位准备；完成后停留在准备结果，由操作员另按启动。
准备过程中停止会取消当前准备任务并等待轴退出；重试从准备任务头重新检查，生产步骤和载具记录保持不变。
本会话已经开始生产后禁止重新回零，必须先按设备工艺处理在制品并结束生产会话。旧完成记录不会跨连接自动当作当前准备成功。
在制品检查失败时不可通过清变量来绕过；本样例不实现实体设备的排空或断电带料恢复。

内部检查使用仓库任务文件与独立内存设备，不操作现场配置：

```powershell
dotnet run --project tools/MotionControl.TaskPreparation.Checks -c Release
```

## 多工位生产检查

将样例安装到隔离的 Machine01 配置副本后，可运行：

```powershell
& '.\tools\MotionControl.TaskEngine.Checks\bin\Release\net48\MotionControl.TaskEngine.Checks.exe' --clip-welding-demo '<隔离配置根目录>' Machine01
```

该检查使用独立内存设备：先堵住下游，观察 A/B 同时处于不同工位，暂停并放行后逐件检查 A/B/A/B 的产品、载具、测试结果和去向；再注入焊接反馈超时，检查故障定位和恢复续跑。它不连接 Host 或控制器，不能代替正式上位机屏幕操作验证，更不能证明实物设备或唯一产品追溯。
