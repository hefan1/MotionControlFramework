# Framework.Wpf

通用 WPF 页面与控件，不依赖具体厂商的原生 API。

`ManualDebug/ManualMotionDebugView` 提供逻辑轴、DI 和 DO 手动调试：查看轴位置/速度/伺服/限位/报警状态，执行伺服开关、回零、轴报警复位、受控停止、按住式点动、软限位范围内的绝对/相对移动，以及 DI 查看和 DO 明确开关。

`Configuration/AxisMappingConfigurationView` 提供控制卡厂商、卡类型、轴通道数量和物理轴映射配置。控制卡选项保存到 `HardwareConfig/MotionCards.json`，设备轴名称和轴参数保存到 `HardwareConfig/AxisParameters.json`；两个文件按 MachineId 独立保存并由 Runtime 配置存储备份。

回零模式编号由控制卡厂商定义，不是跨厂商通用值。轴参数中的 `HomeMode` 要和 `HomeModeVendor` 一起理解；当前页面只为正运动显示并校验 DATUM 模式 3、4、13，选择其他厂商时会清空可见选择并保留原厂商设置，不会把同一个数字自动套用过去。接入新厂商时，应根据该适配器和控制器手册增加它自己的中文选项与参数处理；例如雷赛参考代码把 `home_dir` 和 `mode` 分开传给 API，不能仅凭模式编号推断搜索方向。

设备项目完成硬件配置校验并创建 `MachineRuntime`、`MotionCardGroup` 和 `ILogicalIo` 后，由 Host 组合根创建此页面，并调用 `MainWindow.SetManualMotionDebugPage` 注入。页面不负责选择或创建厂商驱动；设备自动流程运行期间会拒绝手动操作，急停报警未复位时也会拒绝动作。

页面文字使用 Host 当前语言资源字典中的中文键；Host 应同时提供 `Strings.zh-CN.xaml` 和 `Strings.en-US.xaml` 中已列出的手动调试资源。急停状态文字用于软件监控，硬件安全回路仍须独立承担安全停机。
