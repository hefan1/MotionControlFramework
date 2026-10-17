# Framework.Wpf

通用 WPF 页面与控件，不依赖具体厂商的原生 API。

`ManualDebug/ManualMotionDebugView` 提供逻辑轴、DI 和 DO 手动调试：查看轴位置/速度/伺服/限位/报警状态，执行伺服开关、回零、轴报警复位、受控停止、按住式点动、软限位范围内的绝对/相对移动，以及 DI 查看和 DO 明确开关。

设备项目完成硬件配置校验并创建 `MachineRuntime`、`MotionCardGroup` 和 `ILogicalIo` 后，由 Host 组合根创建此页面，并调用 `MainWindow.SetManualMotionDebugPage` 注入。页面不负责选择或创建厂商驱动；设备自动流程运行期间会拒绝手动操作，急停报警未复位时也会拒绝动作。

页面文字使用 Host 当前语言资源字典中的中文键；Host 应同时提供 `Strings.zh-CN.xaml` 和 `Strings.en-US.xaml` 中已列出的手动调试资源。急停状态文字用于软件监控，硬件安全回路仍须独立承担安全停机。
