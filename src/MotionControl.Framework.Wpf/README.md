# Framework.Wpf

通用 WPF 页面与控件，不依赖具体厂商的原生 API。

`Variables/DeviceVariableView` 提供单机变量的新增、编辑、删除、当前值监控和现场手动写入。定义保存到当前 MachineId 的 `Parameters/DeviceVariables.json`；运行、暂停或报警期间锁定修改。数据类型列和“无有效数据”提示随 Host 语言切换。

`ManualDebug/ManualMotionDebugView` 只处理已映射逻辑轴，并按物理轴号自动生成紧凑的 `ManualAxisControl` 卡片。每张卡片查看指令位置、编码器反馈位置、伺服、回零、原点输入、硬限位和报警状态，手动页显示期间约每 200 毫秒刷新一次；支持连续点动、按步长寸动、绝对定位、回零、轴报警复位和受控停止。绝对定位和相对定位均按轴映射中的“到位容差”核对实际反馈位置；容差使用轴工程单位，属于软件完成判定，不会写入控制卡。加速度、减速度放在收起的运动曲线设置中；指令位置和原点输入状态由适配器通过 `AxisStatus` 可选数据提供，不支持读取的厂商会显示横线或灰色指示灯。位置列表初始为空，调试人员可以添加并命名位置、直接编辑目标坐标、把轴当前位置示教到该位置，再用定位按钮返回；位置坐标保存在当前设备的 `Parameters/ManualAxisPositions.json`，内部编号如 `Position1` 不随名称编辑或删除而复用。

`ManualDebug/ManualIoDebugView` 单独处理逻辑 DI/DO 状态和手动输出。Host 将它注入“IO 手动”导航，避免在轴手动卡片下方重复放置 IO 面板。

`ManualDebug/IoMonitorView` 用 DI、DO 两个标签页按物理通道显示状态灯、IO 映射名称和“已触发/未触发”或“高电平/低电平”等状态文字；未映射通道也会显示为“未命名”，方便现场人员逐点查线。通道按窗口高度连续分成多列，底部横向滚动条用于浏览后续通道，顶部显示当前可视通道范围；页面只轮询当前页已显示列中的通道。点击 DO 灯会将当前逻辑状态（已映射）或物理高低电平（未映射）切换到相反状态，并读回控制卡状态确认结果。通用页面通过可选的 `IPhysicalDigitalIoMonitor` 访问物理通道，厂商适配器负责实现原始读写、通道编号和电平语义；尚未实现该能力的适配器会明确显示不支持。

`Configuration/AxisMappingConfigurationView` 提供控制卡选择、轴通道数量和物理轴映射配置。厂商、卡类型、适配器和通道数量选项由 Host 注入的连接实现提供；通用页面不维护厂商清单。控制卡选项保存到 `HardwareConfig/MotionCards.json`，设备轴名称和轴参数保存到 `HardwareConfig/AxisParameters.json`；两个文件按 MachineId 独立保存并由 Runtime 配置存储备份。

`Configuration/IoMappingConfigurationView` 编辑 `HardwareConfig/IoMapping.json` 中的逻辑 DI/DO 名称、控制卡标识、物理通道、有效电平和急停输入名称。此页面与轴映射页共用控制卡连接状态；断开时锁定编辑，检查和保存只修改配置文件，不会直接切换物理输出。

回零模式编号由具体适配器提供并在该适配器内校验；通用页面只显示连接实现返回的编号。轴参数用 `HomeModeAdapterType` 记录编号所属的 API 适配器，避免更换适配器后误用相同数字；旧配置中的 `HomeModeVendor` 只用于兼容迁移。需要额外回零方向或特殊参数的卡型，应由对应 Host/适配器配置负责，不能强塞进单一通用模式编号。当前 Host 只注册 ZMotion 总线仿真选项，其他厂商和实体卡适配器尚未接入。

设备项目完成硬件配置校验并创建 `MachineRuntime`、`MotionCardGroup`、`ILogicalIo` 和 `MachineConfigurationStore` 后，由 Host 组合根分别创建两个手动页面并注入 `MainWindow`。轴页面还需传入本设备 `MachineId` 以保存示教位置。页面不负责选择或创建厂商驱动；轴动作和手动 DO 写入前会实时读取 Runtime 的联锁结果，操作执行期间 Runtime 会阻止启动、复位和断卡。急停有效、急停状态不可读、急停报警未复位或自动流程运行时都会拒绝普通手动动作；单轴受控停止走独立入口。阻止原因由中英文资源字典显示。

页面文字使用 Host 当前语言资源字典中的中文键；Host 应同时提供 `Strings.zh-CN.xaml` 和 `Strings.en-US.xaml` 中已列出的手动调试资源。急停状态文字用于软件监控，硬件安全回路仍须独立承担安全停机。
