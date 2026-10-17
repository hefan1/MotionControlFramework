# Runtime Configuration

`MachineConfigurationStore` 为每个 `MachineId` 建立独立目录，首次启动时从设备项目输出的 `EquipmentTemplates/` 复制缺失的 JSON 模板；已有生效文件始终保留。Host 创建 Store 时传入 CommonApplicationData 等安装目录之外的配置根目录。

配置文件按用途分开，例如 `HardwareConfig/MotionCards.json`、`HardwareConfig/AxisParameters.json`、`HardwareConfig/IoMapping.json` 和 `Parameters/ProcessParameters.json`。控制卡清单保存适配器选择，轴参数和 IO 映射各自只有一份权威配置；厂商连接文件只保存该适配器独有的连接参数。每个 JSON 根节点使用正整数 `schemaVersion`。Runtime 提供读取版本和带备份的保存；设备项目按自己的参数类型编写直白的校验与版本转换代码，调用 `SaveJson` 替换旧文件时会先保留时间戳备份。这里不建立任意配置树或反射式迁移引擎。

```csharp
var configurationRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "YourCompany",
    "MotionControlFramework",
    "EquipmentA");

var store = new MachineConfigurationStore(configurationRoot);
store.CopyMissingJsonFilesFromTemplate(
    machineId,
    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EquipmentTemplates"));

var schemaVersion = store.ReadSchemaVersion(machineId, "HardwareConfig/AxisParameters.json");
var axisParameters = store.LoadJson<AxisParameters>(machineId, "HardwareConfig/AxisParameters.json");
```

`AxisParameters` 由实际设备项目定义。设备项目收到旧版本文件时，应读取旧版本并转换到新模型，再通过 Store 保存；Store 会在覆盖前自动保留旧文件。未知的新版本应停止加载并给出文件与设备标识，不能用默认值覆盖现场文件。
