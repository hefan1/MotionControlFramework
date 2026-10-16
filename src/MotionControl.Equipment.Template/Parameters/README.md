# Equipment Parameters

这里保存设备工艺动作的默认参数模板，例如命名动作的位置、速度和必要的加减速覆盖。具体轴通道、单位换算、软限位和回零参数放在 `HardwareConfig/`。

产品配方单独管理，不与设备调试参数混在一起。同型号 Equipment 项目共用默认模板；每台物理设备的生效参数由 Runtime 按 `MachineId` 独立保存。
