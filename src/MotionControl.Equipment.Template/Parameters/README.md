# Equipment Parameters

这里保存设备工艺动作的默认参数模板，例如命名动作的位置、速度和必要的加减速覆盖。具体轴通道、单位换算、软限位和回零参数放在 `HardwareConfig/`。

工艺参数 JSON 根节点带有正整数 `schemaVersion`。产品配方单独管理，不与设备调试参数混在一起。同型号 Equipment 项目共用默认模板；每台物理设备的生效参数由 Runtime 按 `MachineId` 独立保存。

通用手动调试页把人工示教位置保存在设备自己的 `Parameters/ManualAxisPositions.json`。每个位置包含稳定编号、可编辑名称和工程位置；未添加的位置不会自动生成默认坐标。
