# 参考工程中的运动卡 API

本轮从参考代码中抽出了厂商原生 API 声明，分别放入独立项目。API 文件只负责 P/Invoke 声明，不承担设备流程、逻辑轴映射、运行状态管理或 WPF 页面。

| 厂商 | API 项目 | 参考来源 | 原生依赖及观察结果 |
| --- | --- | --- | --- |
| 雷赛 | `MotionControl.VendorApi.Leadshine` | `Leadshine.CNC.POL.../Leadshine.CNC.POL.Motion/LTDMC.cs` | 导入 `LTDMC.dll`。参考目录中同时存在 x86、x64 两种 DLL，不能混用。 |
| 正运动 | `MotionControl.VendorApi.ZMotion` | `HLAMotion_X8222/HLAMotion.Motion/Zmotion/Zmcaux.cs` | 导入 `zauxdll.dll`、`zmotion.dll`。参考运行目录中的版本为 x64。 |
| ADLINK | `MotionControl.VendorApi.ADLink` | `版本库.../Lead.Detect.PrimMotionADLink/ADLink/APS168.cs` | 声明导入 `APS168x64.dll`。参考目录找到的 x64 文件名为 `APS168.dll`，名称和声明不一致，不能直接当成可部署依赖。 |

从这些文件及其调用工程中能看到，三类 API 都涉及卡初始化、轴运动、回零、停止和 IO；雷赛与 ADLINK 还展示了较多插补/总线相关声明，正运动 API 还涉及网口或 PCI 连接。具体卡型是否支持某个函数，仍以对应型号的厂商 SDK 手册为准。

## 接入边界

```text
MotionControl.Framework.Runtime
             │ 只依赖厂商无关契约
             ▼
MotionControl.Adapter.MotionCard.*  ──>  MotionControl.VendorApi.*  ──> 厂商原生 DLL
```

当前只接入低层 API 声明，没有将任何厂商 SDK 类型放进 `Framework.Contracts`，也没有在本机调用原生 DLL。下一步确认公共轴/IO 契约后，再由独立的 `MotionControl.Adapter.MotionCard.*` 项目封装具体卡型差异。

参考工程中的 `IMC100API.cs` 位于机器人/PLC 辅助模块，现有代码不足以确认它是通用运动控制卡 SDK，因此本轮没有把它列为运动卡 API。若后续有该厂商控制卡 SDK 或实际调用代码，可以再按同一目录边界增加。
