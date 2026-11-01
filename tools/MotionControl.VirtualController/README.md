# 本机虚拟控制器

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

脚本检查 Machine01 的后端、端口、控制卡和高有效“上料到位 DI4 / 夹具夹紧 DO0”映射，核对本项目控制器的进程路径和协议身份。已有上位机运行时拒绝重复启动。若控制器尚未启动，脚本启动本机控制器；已有相同上料规则时保留现有延时与缺料状态，不会重置已有规则。不同通道的现有规则、占用中的运动会话或夹具输出开启等情况会报错，脚本不会覆盖配置，也不会自动启动生产或使能伺服。

只想读取当前配置和场景状态、不启动或修改任何内容时，使用：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\MotionControl.VirtualController\StartLoadDemo.ps1 -CheckOnly
```

读取模式要求控制器已经运行。若缺料状态为 True，需要按下面的恢复供料命令明确解除；一键脚本不会代替操作员清除故障。

默认不自动生成输入；需要时由测试人员显式启用上料规则。下面示例使用当前 Machine01 的 `DI4 上料到位`、`DO0 夹具夹紧`：

```powershell
$command = '.\tools\MotionControl.VirtualController\ControllerCommand.ps1'
powershell -NoProfile -ExecutionPolicy Bypass -File $command -Action StartFeeder
powershell -NoProfile -ExecutionPolicy Bypass -File $command -Action Status
```

启动规则前先确认夹具输出处于关闭状态。启用时 DI4 先变为未到位，默认 1000 ms 后变为到位；检测到 DO0 打开再关闭时，DI4 变为未到位，默认经过 1500 ms 后下一件到位。通道和延迟可通过 `-InputChannel`、`-OutputChannel`、`-FeedDelayMs`、`-EmptyDelayMs` 指定。若当前输入已由输出反馈规则使用，或夹具输出仍开启，启用会报错。

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
- 本版本没有模型化行程或正负限位传感器，返回的未触发状态仅属于该软件模型，不能用于证明实际设备限位、安全回路或运动参数正确。
- `configureFeedback` 可显式建立 DO 到 DI 的延迟规则，一个输出可关联多个不同输入；`setFeedbackFault` 可保持指定输入原状态，解除故障后恢复跟随。上料规则和普通反馈规则不能同时控制同一输入。
- 为避免 Host 意外退出后虚拟轴继续移动，控制器只允许一个 Host 获取运动会话。只有该会话的心跳能续期；心跳超时或主动释放会在控制器进程内停止所有轴，不更改 DO。这个看门狗仅保护虚拟轴，不是实际设备的安全回路。
- 网络请求是本机 UTF-8 JSON 单行协议，返回 `ok` 与出错原因；不支持的命令明确失败。接口设计用于本机开发测试，没有远程认证，不应暴露到现场网络。

## 软件检查

构建后运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\MotionControl.VirtualController\SmokeChecks.ps1`。检查独立进程与协议身份、轴移动和停止、会话过期与主动释放、延迟反馈、故障及上料规则；脚本使用独立端口，端口已占用时直接退出。它不能代替正式 Host 的屏幕验证和实际设备验收。

## 普通输出与输入反馈

设备有多个气缸时，可以分别建立反馈。例如 DO1 对应 DI5，高电平跟随，300 ms 后到位：

```powershell
$command = '.\tools\MotionControl.VirtualController\ControllerCommand.ps1'
& $command -Action ConfigureFeedback -OutputChannel 1 -InputChannel 5 -FeedbackDelayMs 300
& $command -Action FeedbackStatus -InputChannel 5
& $command -Action FeedbackStuck -InputChannel 5
& $command -Action FeedbackRestore -InputChannel 5
& $command -Action RemoveFeedback -InputChannel 5
```

`FeedbackStuck` 表示输入卡在当前电平，不跟随输出，用于测试超时；`FeedbackRestore` 解除故障后按当前输出及延迟恢复反馈。`RemoveFeedback` 删除该规则并保持当前输入电平。反向反馈可增加 `-FeedbackActiveHigh $false`。同一输入不能同时被上料规则和普通反馈规则控制；请先停用或删除原规则。
