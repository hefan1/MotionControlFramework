# 本机虚拟控制器

## 13 轴双夹位测试准备

`NewRotaryTestConfiguration.ps1` 只创建不存在的隔离根目录，从源 Machine01 复制硬件配置、参数和任务，不复制或修改源草稿、备份和恢复记录；写入 13 轴专用原点、限位、扫码及工位测试映射，并安装双夹位任务。可以分别创建两种入口：

需要只验证运动、工位和 IO 时，在生成命令后增加 `-MotionOnly`；此选项安装独立的 `RotaryMotionValidation` 样例，不增加扫码映射、扫码模型或逐件记录步骤。C# 入口显式选择 `RotaryMotionValidation`，任务列表使用独立任务和变量；旧版扫码样例和任何已保存的数据不受影响。生成脚本仍要求先安全关闭现有 Host，不接管当前设备会话。

```powershell
& '.\tools\MotionControl.VirtualController\NewRotaryTestConfiguration.ps1' -DestinationRoot '.\tmp\rotary-task-test' -ExecutionOwner TaskEngine -Port 19580
& '.\tools\MotionControl.VirtualController\NewRotaryTestConfiguration.ps1' -DestinationRoot '.\tmp\rotary-code-test' -ExecutionOwner StationFlows -Port 19581
& '.\tools\MotionControl.VirtualController\StartRotaryWelding.ps1' -ConfigurationRoot '.\tmp\rotary-task-test' -LaunchHost
```

启动脚本只接受隔离配置，会启动新的本机控制器并按设备配置创建十三轴原点/限位、三个双到位夹具、扫码、视觉与多路延时反馈；`-LaunchHost` 使用正式 Host 的隔离配置入口，不自动使能、回零、开启供料或生产。先按 Host 的设备准备步骤核对，再使用脚本打印的命令开启供料：只有 Feeder_X、Feeder_Z、Load_X 和 Transfer_Y 都已回零、停稳且位于 0 附近时才累计到料延时。该门禁不知道 Host 的生产暂停及交接，不能替代设备工艺互锁。缓存、合格及不良出口允许信号默认有效，可通过 `SetInput` 设为 false 注入下游阻塞。`FeedbackStuck` 可以分别冻结焊接完成、焊接合格、压合完成/合格、缓存接收、合格出料或剔除完成输入。夹具异常可使用 `ActuatorStuck` 或 `ActuatorConflict`；解除分别使用 `ActuatorRestore`、`ActuatorConflictRestore`。结束时在正式 Host 停止流程并核对轴、输出及输入，关闭本次上位机后仅关闭本次控制器进程。

独立扫码模型使用 `扫码触发`、`扫码完成`、`扫码成功` 和 `扫码数据00`–`扫码数据15`；低位在前。触发高时经过延迟产生一次递增的 1–65535 测试编号，结果及完成状态保持至触发下降，下降会清除反馈并取消尚未完成的操作。失败时完成有效、成功无效且数据为零；耗尽后明确失败。号码仅在本控制器进程内不重复，重启后会从头编号，不能代替现场扫码或产品追溯。故障可在触发关闭后设置：

```powershell
$command = '.\tools\MotionControl.VirtualController\ControllerCommand.ps1'
& $command -Port 19580 -Action ScannerStatus
& $command -Port 19580 -Action ScannerFault -ScannerFaultMode fail
& $command -Port 19580 -Action ScannerFault -ScannerFaultMode noCompletion
& $command -Port 19580 -Action ScannerFault -ScannerFaultMode normal
& '.\tools\MotionControl.VirtualController\ScannerChecks.ps1'
```

以上为软件控制器和测试机构模型，不验证实际机械、制动、布线或安全回路。

这是一个独立进程，通过本机 TCP 接收框架的虚拟卡适配器请求，供正式上位机验证操作界面和设备流程。它不是正运动原厂 SDK，也不会访问实际控制卡。进程关闭后，轴、IO 和测试场景状态均丢失；设备的 Machine01 配置文件不会因此删除或恢复。

## 启动与关闭

在仓库根目录执行：

```powershell
dotnet build tools/MotionControl.VirtualController/MotionControl.VirtualController.csproj -c Release
Start-Process -FilePath .\tools\MotionControl.VirtualController\bin\Release\net48\MotionControl.VirtualController.exe
```

窗口显示监听地址后，在 Machine01 目录下的 `Parameters/ControllerBackend.json` 中明确选择虚拟卡（没有该文件时默认使用正运动）：

```json
{
  "backend": "Virtual",
  "port": 18520
}
```

当前电脑的文件位置为 `C:\Users\ASUS\AppData\Local\MotionControlFramework\Configurations\Machine01\Parameters\ControllerBackend.json`；其他电脑请以实际用户目录为准。可用记事本创建或编辑，保存为 UTF-8。先查看已有文件并保留原内容，再按需求切换；恢复正运动时将 `backend` 改为 `ZMotion`。这个文件只选择后端，保留轴、IO 和工位映射；若实际使用其他 MachineId，应修改对应设备目录。

在仓库根目录编译并启动正式 Host：

```powershell
dotnet build .\src\MotionControl.Host.Wpf\MotionControl.Host.Wpf.csproj -c Release
Start-Process -FilePath .\src\MotionControl.Host.Wpf\bin\Release\net48\MotionControl.Host.Wpf.exe
```

先启动虚拟控制器，再启动 Host。控制器只监听 `127.0.0.1:18520`；命令行第一个参数可以指定其他端口，但 Host 和下面脚本的 `-Port` 也必须一致。端口已被占用时进程直接报错退出。连接时必须检查 `ping` 返回的 `controllerType` 和 `protocolVersion`；不要将其他本机服务误认为该控制器，也不要在失败时自动切换其他设备。不同版本的控制器和适配器需要重新构建并使用相同协议版本。

测试结束先通过正式 Host 停止流程、确认各轴停止并关闭相关输出，再关闭本轮 Host 和控制器窗口。关闭控制器窗口会断开连接，不会把 Machine01 测试配置恢复为默认配置。

## 上料定位循环

完成上面的 Release 构建、并在 Machine01 后端配置中选择 Virtual 后，也可以在仓库根目录一键准备上料场景并打开正式上位机：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\MotionControl.VirtualController\StartLoadDemo.ps1
```

脚本按 Machine01 的逻辑 IO 名称“上料到位”“夹具夹紧”读取实际通道，要求同一张控制卡、高有效且映射唯一；不依赖固定编号。脚本核对后端、端口、控制器进程路径和协议身份。已有上位机运行时拒绝重复启动。若控制器尚未启动，脚本启动本机控制器；已有相同上料规则时保留现有延时与缺料状态，不会重置已有规则。不同通道的现有规则、占用中的运动会话或夹具输出开启等情况会报错，脚本不会覆盖配置，也不会自动启动生产或使能伺服。

只想读取当前配置和场景状态、不启动或修改任何内容时，使用：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\MotionControl.VirtualController\StartLoadDemo.ps1 -CheckOnly
```

读取模式要求控制器已经运行。若缺料状态为 True，需要按下面的恢复供料命令明确解除；一键脚本不会代替操作员清除故障。

默认不自动生成输入；需要时由测试人员显式启用上料规则。以下示例假定映射为 `DI4 上料到位`、`DO12 夹具夹紧`，其他配置应按实际映射指定通道：

```powershell
$command = '.\tools\MotionControl.VirtualController\ControllerCommand.ps1'
powershell -NoProfile -ExecutionPolicy Bypass -File $command -Action StartFeeder -InputChannel 4 -OutputChannel 12
powershell -NoProfile -ExecutionPolicy Bypass -File $command -Action Status
```

启动规则前先确认夹具输出处于关闭状态。启用时上料到位输入先变为未到位，默认 1000 ms 后变为到位；检测到夹具输出打开再关闭时，上料输入变为未到位，默认经过 1500 ms 后下一件到位。上料到位是供料规则生成的物料信号，并非夹具到位反馈。通道和延迟可通过 `-InputChannel`、`-OutputChannel`、`-FeedDelayMs`、`-EmptyDelayMs` 指定。若当前输入已由输出反馈规则使用，或夹具输出仍开启，启用会报错。

需要等待上料轴返回安全位时，显式指定该轴的物理轴号、回零后的逻辑安全位置和容差，例如 `-FeederSafeAxis 1 -FeederSafePosition 0 -FeederSafeTolerance 0.01`；三个参数必须同时填写。启用后，供料延时仅在有效运动会话、轴使能并回零、无轴报警和限位、轴停止且位于安全位、夹具输出关闭时计时。条件中断则丢弃已累计时间，条件恢复后重新等待完整延时；缺料故障或停止供料会废止等待，解除缺料后重新检查。`Status` 中可查看 `safeAxis`、`gateSatisfied`、`feedPending` 和 `delayCounting`。已到位的物料不会因为门禁变化自动消失。

这个门禁只反映上料轴和控制器会话，软件控制器不能从运动卡接口判断上位机是否处于生产暂停、下游是否已经接收载具：轴停在安全位时，即使上位机暂停或下游尚未交接，仍可能供料。真实设备还需要由设备工艺明确设计生产许可、在制品及工位交接互锁；该模型不能作为安全功能。未配置安全位时，原有供料规则行为保持不变。

检查缺料、恢复供料和停止设备规则：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File $command -Action MissingMaterial
powershell -NoProfile -ExecutionPolicy Bypass -File $command -Action Status
powershell -NoProfile -ExecutionPolicy Bypass -File $command -Action RestoreFeeder
powershell -NoProfile -ExecutionPolicy Bypass -File $command -Action StopFeeder
```

缺料故障阻止**后续**供料，不会抹掉已经到位的物料。当前物料取走后输入变为未到位，不会再自动出现下一件。恢复供料后，如果当前无料，会按配置延时产生下一件；停止规则不会改写当前输入电平。`Status` 显示规则是否启用、通道、输入输出当前电平、延时、缺料故障及夹具动作状态。

若需要单独设置未被设备规则驱动的输入，可以在 PowerShell 中执行：

```powershell
& $command -Action SetInput -InputChannel 4 -Value $true
& $command -Action SetInput -InputChannel 4 -Value $false
```

脚本每次先核对控制器身份，失败或命令被拒绝会打印原因并返回非零退出码。`AxisAlarm -Axis 1` 可注入轴报警，清除报警应走正式上位机的复位流程。手动设置受自动场景控制的输入可能在下一次设备规则触发时被覆盖，应优先启用或停用相应规则。

## 实现范围

- 提供 64 轴、4096 路 DI 和 4096 路 DO，通道均从零开始；支持伺服、回零、绝对定位、点动、停止、状态读取及轴报警注入。
- 轴使用工程单位、恒速位置插值；停止是即时停止，没有模拟加减速曲线、惯量、编码器误差、总线和现场时序。回零为软件零位，不代表真实原点信号或回零工艺。
- 默认轴没有启用行程或正负限位传感器模型；可按下文显式配置独立的软件原点与限位。无论是否配置，状态都只属于软件模型，不能用于证明实际设备限位、安全回路或运动参数正确。
- `configureFeedback` 可显式建立 DO 到 DI 的延迟规则，一个输出可关联多个不同输入；`setFeedbackFault` 可保持指定输入原状态，解除故障后恢复跟随。上料规则和普通反馈规则不能同时控制同一输入。
- 为避免 Host 意外退出后虚拟轴继续移动，控制器只允许一个 Host 获取运动会话。只有该会话的心跳能续期；心跳超时或主动释放会在控制器进程内停止所有轴，不更改 DO。这个看门狗仅保护虚拟轴，不是实际设备的安全回路。
- 网络请求是本机 UTF-8 JSON 单行协议，返回 `ok` 与出错原因；不支持的命令明确失败。接口设计用于本机开发测试，没有远程认证，不应暴露到现场网络。

## 软件检查

构建后运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\MotionControl.VirtualController\SmokeChecks.ps1`。检查独立进程与协议身份、轴移动和停止、会话过期与主动释放、延迟反馈、故障及上料规则；脚本使用独立端口，端口已占用时直接退出。还可以运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\MotionControl.VirtualController\ScenarioIoMappingChecks.ps1`，检查旧、新通道及无效映射拒绝。它们不能代替正式 Host 的屏幕验证和实际设备验收。

## 可选轴原点与限位模型

默认轴保持旧的软件回零行为；只有显式配置模型后，轴才按独立物理坐标计算原点与正负限位。输入状态会随软件轴位置更新，直接读取 DI 也会推进模拟位置。回零必须实际检测原点：离开原点时搜索感应区，在边沿停下并把当前位置设为逻辑零；已处于有效感应区可直接确认。原点未触发时搜索到限位或搜索边界后报警并保持未回零，限位触发也会停轴并要求重新回零。感应区、限位、速度和处理逻辑只是软件测试约定，不代表实物轴、厂商回零方式或安全功能。

下面仅给出空闲控制器的独立通道示例；运行前应按本次测试配置核对轴号、输入通道及运动范围：

```powershell
$command = '.\tools\MotionControl.VirtualController\ControllerCommand.ps1'
& $command -Action ConfigureAxisSensors -Axis 1 -AxisOriginInput 101 -AxisNegativeInput 102 -AxisPositiveInput 103 -AxisOriginPosition 2 -AxisOriginHalfWidth 0.1 -AxisNegativeLimit -1 -AxisPositiveLimit 4
& $command -Action AxisSensorsStatus -Axis 1
& $command -Action AxisSensorFault -Axis 1 -AxisSensor origin -AxisSensorMode stuckOff
& $command -Action AxisSensorFault -Axis 1 -AxisSensor origin -AxisSensorMode normal
```

故障对象可以是 `origin`、`negative` 或 `positive`，状态可以是 `normal`、`stuckOff`（始终未触发）或 `stuckOn`（始终触发）。恢复信号不会自动复位报警或重新发起回零；应先确认状态，再通过上位机处理。规则使用的 DI 不可再分给相机、夹具、供料或普通反馈，也不能直接设置输入。静止轴可以用 `RemoveAxisSensors -Axis <轴号>` 移除规则，移除时保留输入电平并撤销该轴已回零状态。独立检查：`powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\MotionControl.VirtualController\AxisSensorChecks.ps1`，不连接正式 Host。

## 普通输出与输入反馈

设备有多个气缸时，可以分别建立反馈。例如本示例配置的 DO5 压头动作对应 DI5 压装完成，高电平跟随，300 ms 后到位（其他配置以实际映射为准）：

```powershell
$command = '.\tools\MotionControl.VirtualController\ControllerCommand.ps1'
& $command -Action ConfigureFeedback -OutputChannel 5 -InputChannel 5 -FeedbackDelayMs 300
& $command -Action FeedbackStatus -InputChannel 5
& $command -Action FeedbackStuck -InputChannel 5
& $command -Action FeedbackRestore -InputChannel 5
& $command -Action RemoveFeedback -InputChannel 5
```

`FeedbackStuck` 表示输入卡在当前电平，不跟随输出，用于测试超时；`FeedbackRestore` 解除故障后按当前输出及延迟恢复反馈。`RemoveFeedback` 删除该规则并保持当前输入电平。反向反馈可增加 `-FeedbackActiveHigh $false`。同一输入不能同时被上料规则和普通反馈规则控制；请先停用或删除原规则。

## 相机触发和独立的检测完成

软件控制器可选配一台相机测试模型。它默认关闭，不修改 Machine01 配置，通道必须由测试人员明确指定。例如测试项目可以约定 `DO6` 触发、`DI6` 触发已接受、`DI14` 检测完成、`DI18` OK、`DI19` NG、`DO15` 清除完成；这只是示例，不代表实体相机的实际协议或接线。先核对这些通道未被其他场景占用，触发及复位输出、相机输入均为低电平，再显式启用：

```powershell
$command = '.\tools\MotionControl.VirtualController\ControllerCommand.ps1'
& $command -Action ConfigureCamera -CameraTriggerOutput 6 -CameraAcknowledgeInput 6 -CameraCompleteInput 14 -CameraOkInput 18 -CameraNgInput 19 -CameraResult OK -CameraResetOutput 15 -CameraProcessingDelayMs 500
& $command -Action CameraStatus
```

触发输出上升沿被接受后，触发反馈变为有效；触发输出下降时反馈复位。即使触发只是短脉冲，处理仍继续；延迟到期后检测完成置位并锁存，不能把触发输出变化当作已完成。忙碌、完成尚未复位或复位输出有效时，新的触发不被接受。确认触发输出无效后，复位输出的上升沿会取消正在处理的任务并清除完成和结果；触发仍有效时的复位输出上升沿不会生效，触发恢复无效后须重新产生复位上升沿。复位输出必须关闭后才可接受下一次触发。也可不指定 `-CameraResetOutput`，通过下面的调试命令复位；未配置复位输出的规则不能由 Host 自动复位。

如果配置 OK 和 NG 输入，必须同时配置两个不同通道并明确指定测试结果 `OK`、`NG` 或 `Invalid`。完成时 OK 和 NG 至多一个有效；`Invalid` 表示完成但没有有效结果，两者均无效。完成和结果一直保留到复位；只有相机空闲并且结果已复位时，才能用 `-Action CameraResult -CameraResult NG` 选择下一周期的结果。也可完全不配置 OK/NG 输入，此时不提供结果信号。

如果测试中的本次结果是 `Invalid` 且检测已完成，可以由测试人员在确认触发和复位输出都已关闭后显式执行 `-Action CameraResolve -CameraResult OK` 或 `-CameraResult NG`，模拟外部设备人工复核后提供结果；该命令只允许填补尚未产生的有效结果，不能覆盖既有 OK/NG，不能用于处理尚未完成的检测。它只是软件测试命令，不包含现场人工复核的权限、记录或追溯机制。

```powershell
& $command -Action CameraNoCompletion  # 注入处理不完成
& $command -Action CameraStatus
& $command -Action CameraRestore       # 解除故障；旧处理不会自动补发完成
& $command -Action CameraResume        # 显式继续故障中断的已接受作业
& $command -Action CameraReset         # 或取消旧处理并清除完成，之后需重新触发
& $command -Action RemoveCamera        # 触发及复位输出均无效时移除规则
```

故障解除后可显式继续同一作业，或复位后重新触发；继续会重新经历处理延迟并保留原定测试结果，不会伪造新的触发。复位使旧处理失效；移除规则保留现有输入电平。相机输入不能与其他设备规则共用，重复配置会被拒绝。模型不提供真实图像和相机协议，OK/NG 是测试人员指定的结果，并非实际图像检测；任务是否使用这些信号由设备任务配置决定。真实相机故障后可能必须重拍，须以设备协议为准。可用 `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\MotionControl.VirtualController\CameraChecks.ps1` 在独立端口检查短脉冲、结果、忙碌、复位和故障。

相机处理和夹具动作模拟独立设备：Host 暂停或租约过期不会自动取消已接受的检测、停止气缸运动或改写输出。重新控制前必须读取完成、结果和到位状态；不能把等待时长归零或同一会话复位当作外部设备也被复位。实际设备行为由设备本身和现场设计决定。

## 单电磁阀夹具的双到位反馈

夹具规则同样默认关闭。以下仅是软件测试示例：高电平 DO12 夹紧，低电平松开；DI12 夹紧到位、DI13 松开到位。确认输出和两个输入均为低电平，且输入未被其他规则使用后：

```powershell
& $command -Action ConfigureActuator -OutputChannel 12 -ActuatorClampedInput 12 -ActuatorReleasedInput 13 -ActuatorDelayMs 300
& $command -Action ActuatorStatus -OutputChannel 12
& $command -Action ActuatorStuck -OutputChannel 12
& $command -Action ActuatorRestore -OutputChannel 12
& $command -Action RemoveActuator -OutputChannel 12
```

安装后先经过动作延迟到达松开位。输出改变时旧到位立即无效，两个输入在运动途中均无效，到位后只允许一个输入有效。卡滞时不产生新的到位；解除后继续等待完整动作延迟。移除规则前输出必须无效，移除时保留输入电平。该模型不能替代现场气路、电磁阀、传感器或安全回路。可运行 `ActuatorChecks.ps1` 检查双到位、快速反向、卡滞和占用冲突。

## 控制命令故障注入

以下命令只作用于这个独立软件控制器，先在正式 Host 核对目标设备及轴、IO 状态。每次故障只影响指定物理通道或轴号的下一次匹配请求；未触发时可查询和撤销，故障触发后自动移除。

```powershell
$command = '.\tools\MotionControl.VirtualController\ControllerCommand.ps1'
# 示例配置中 DO5 的下一次写入已改变电平，但调用者收到失败。
& $command -Action SetCommandFault -TargetCommand writeOutput -TargetIndex 5 -FaultMode errorAfter
& $command -Action CommandFaultStatus -TargetCommand writeOutput -TargetIndex 5
# 轴 2 的下一次停轴请求直接失败，轴按原状态继续运动。
& $command -Action SetCommandFault -TargetCommand stop -TargetIndex 2 -FaultMode reject
# DI5 的下一次读取延迟应答 1500 毫秒，不阻塞其他连接。
& $command -Action SetCommandFault -TargetCommand readInput -TargetIndex 5 -FaultMode delayReply -FaultDelayMs 1500
# DO5 下一次写入延迟执行；等待期间会话失效时，旧请求不会写入。
& $command -Action SetCommandFault -TargetCommand writeOutput -TargetIndex 5 -FaultMode delayBefore -FaultDelayMs 1500
& $command -Action ClearCommandFault -TargetCommand stop -TargetIndex 2
```

可注入的命令为 `readInput`、`readOutput`、`writeOutput`、`readAxis` 和 `stop`，分别按通道号或轴号匹配；批量 IO 读取不受单通道故障影响。`reject` 在设备状态改变前返回失败；`errorAfter` 仅支持 `writeOutput` 和 `stop`，设备状态已经改变，但返回失败；`delayBefore` 在执行前等待，执行前再次核对原控制会话；`delayReply` 在执行后等待再应答。延迟范围为 1–30000 ms。故障清除不会回滚已经执行的动作，也不会猜测安全输出，测试结束应通过正式 Host 确认轴停止和 IO 状态。

软件控制器适配器读取应答超时为 3 秒。较长延迟可能导致 Host 判断通信失败并停止心跳，控制器看门狗会停止轴但保留输出；这一行为用于测试本软件模型，不能代表某个实体控制卡或机械设备的超时、停轴、急停或断电行为。
