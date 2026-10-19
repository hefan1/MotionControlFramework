# Framework.Wpf

通用 WPF 页面与控件，不依赖具体厂商的原生 API。

`ManualDebug/ManualMotionDebugView` 只处理已映射逻辑轴，并按物理轴号自动生成紧凑的 `ManualAxisControl` 卡片。每张卡片查看指令位置、反馈位置、伺服、回零、原点输入、硬限位和报警状态，支持连续点动、按步长寸动、绝对定位、回零、轴报警复位和受控停止。加速度、减速度放在收起的运动曲线设置中；指令位置和原点输入状态由适配器通过 `AxisStatus` 可选数据提供，不支持读取的厂商会显示横线或灰色指示灯。位置列表初始为空，调试人员可以添加并命名位置、把轴当前位置示教到该位置，再用定位按钮返回；位置坐标保存在当前设备的 `Parameters/ManualAxisPositions.json`，内部编号如 `Position1` 不随名称编辑或删除而复用。

`ManualDebug/ManualIoDebugView` 单独处理逻辑 DI/DO 状态和手动输出。Host 将它注入“IO 手动”导航，避免在轴手动卡片下方重复放置 IO 面板。

`Configuration/AxisMappingConfigurationView` 提供控制卡厂商、卡类型、轴通道数量和物理轴映射配置。控制卡选项保存到 `HardwareConfig/MotionCards.json`，设备轴名称和轴参数保存到 `HardwareConfig/AxisParameters.json`；两个文件按 MachineId 独立保存并由 Runtime 配置存储备份。

`Configuration/IoMappingConfigurationView` 编辑 `HardwareConfig/IoMapping.json` 中的逻辑 DI/DO 名称、控制卡标识、物理通道、有效电平和急停输入名称。此页面与轴映射页共用控制卡连接状态；断开时锁定编辑，检查和保存只修改配置文件，不会直接切换物理输出。

回零模式编号由控制卡厂商定义，不是跨厂商通用值。轴参数中的 `HomeMode` 要和 `HomeModeVendor` 一起理解；当前页面只为正运动显示并校验 DATUM 模式 3、4、13，选择其他厂商时会清空可见选择并保留原厂商设置，不会把同一个数字自动套用过去。接入新厂商时，应根据该适配器和控制器手册增加它自己的中文选项与参数处理；例如雷赛参考代码把 `home_dir` 和 `mode` 分开传给 API，不能仅凭模式编号推断搜索方向。

设备项目完成硬件配置校验并创建 `MachineRuntime`、`MotionCardGroup`、`ILogicalIo` 和 `MachineConfigurationStore` 后，由 Host 组合根分别创建两个手动页面并注入 `MainWindow`。轴页面还需传入本设备 `MachineId` 以保存示教位置。页面不负责选择或创建厂商驱动；设备自动流程运行期间会拒绝手动操作，急停报警未复位时也会拒绝动作。

页面文字使用 Host 当前语言资源字典中的中文键；Host 应同时提供 `Strings.zh-CN.xaml` 和 `Strings.en-US.xaml` 中已列出的手动调试资源。急停状态文字用于软件监控，硬件安全回路仍须独立承担安全停机。
