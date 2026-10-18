# 参考工程中的运动卡 API

本轮从参考代码中抽出了厂商原生 API 声明，分别放入独立项目。API 文件只负责 P/Invoke 声明，不承担设备流程、逻辑轴映射、运行状态管理或 WPF 页面。

| 厂商 | API 项目 | 参考来源 | 原生依赖及观察结果 |
| --- | --- | --- | --- |
| 雷赛 | `MotionControl.VendorApi.Leadshine` | `Leadshine.CNC.POL.../Leadshine.CNC.POL.Motion/LTDMC.cs` | 导入 `LTDMC.dll`。参考目录中同时存在 x86、x64 两种 DLL，不能混用。 |
| 正运动 | `MotionControl.VendorApi.ZMotion` | `HLAMotion_X8222/HLAMotion.Motion/Zmotion/Zmcaux.cs` | 导入 `zauxdll.dll`、`zmotion.dll`。参考运行目录中的版本为 x64。 |
| ADLINK | `MotionControl.VendorApi.ADLink` | `版本库.../Lead.Detect.PrimMotionADLink/ADLink/APS168.cs` | 声明导入 `APS168x64.dll`。参考目录找到的 x64 文件名为 `APS168.dll`，名称和声明不一致，不能直接当成可部署依赖。 |

从这些文件及其调用工程中能看到，三类 API 都涉及卡初始化、轴运动、回零、停止和 IO；雷赛与 ADLINK 还展示了较多插补/总线相关声明，正运动 API 还涉及网口或 PCI 连接。具体卡型是否支持某个函数，仍以对应型号的厂商 SDK 手册为准。

## 当前适配器进度

正运动已有独立的 `MotionControl.Adapter.MotionCard.ZMotion` 适配器，仅支持 ZDevelop 离线仿真器：连接地址固定为 `127.0.0.1`，并且连接后必须确认身份为 `VPLC5xx-Simu`。轴参数需显式应用；仿真回零只清零指令坐标。Leadshine 与 ADLINK 仍只有 API 声明，没有可运行的卡适配器。

正运动适配器的逻辑 DI/DO 映射使用单个物理 IO 点号，读写调用单点 `ZAux_Direct_GetIn`、`ZAux_Direct_GetOp` 和 `ZAux_Direct_SetOp`。不同参考项目对端口分组的封装方式不完全一致，首期适配器依照厂商 API 单点调用及当前参考实现，具体硬件点号仍需按控制器 IO 表核对。

## 接入边界

```text
MotionControl.Framework.Runtime
             │ 只依赖厂商无关契约
             ▼
MotionControl.Adapter.MotionCard.*  ──>  MotionControl.VendorApi.*  ──> 厂商原生 DLL
```

适配器通过独立项目引用低层 API 声明，没有将任何厂商 SDK 类型放进 `Framework.Contracts`。已通过正运动 x64 原生 DLL 从上位机进程连接本机 `127.0.0.1` 仿真器，并读到控制器身份 `VPLC5xx-Simu`。厂商 DLL 不提交到 Git；开发机应按 SDK 许可将匹配位数的 `zauxdll.dll` 和 `zmotion.dll` 放入 `src/Adapters/MotionCards/ZMotion/Adapter/Runtime/x64/`。Host 默认以 x64 运行，构建时会从此目录自动复制 DLL 到应用输出目录。若使用 x86 SDK，需将对应文件放入 `Runtime/x86/` 并显式以 x86 构建。此前只把 DLL 手动放入旧的 `bin/x64` 输出目录会在重建或切换输出目录后丢失，因此不能作为部署方式。本次未执行运动和 IO 写入。

参考工程中的 `IMC100API.cs` 位于机器人/PLC 辅助模块，现有代码不足以确认它是通用运动控制卡 SDK，因此本轮没有把它列为运动卡 API。若后续有该厂商控制卡 SDK 或实际调用代码，可以再按同一目录边界增加。
