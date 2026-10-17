# 设备报警定义与上报

设备项目在 `Alarms` 目录定义设备自己的报警代码和说明，并标记它属于停机报警还是非停机报警。报警条件由设备流程、IO 监控或通讯状态检查得到，再通过 `MachineRuntime.AlarmService` 上报。

```csharp
private static readonly MachineAlarmDefinition VacuumLowAlarm =
    new MachineAlarmDefinition(
        "VACUUM_LOW",
        "真空压力不足",
        MachineAlarmKind.StopMachine);

// 条件成立时上报；重复上报会更新同一条报警，不会重复新增记录。
machineRuntime.AlarmService.ReportCondition(
    VacuumLowAlarm,
    "取料工位",
    conditionActive: !vacuumReady,
    detail: "请检查真空发生器和吸盘。");
```

`StopMachine` 会请求整机暂停。触发条件恢复后，报警记录仍保留；操作人员按复位时，Runtime 才清除已恢复的停机报警。如果条件仍成立，复位会失败。`NonStop` 只在条件成立时显示，条件恢复后自动清除，不暂停工位流程。

报警来源使用清晰的工位名或 `整机`。不要在每个步骤里重复创建不同代码的报警；同一报警代码和来源应始终使用相同类型和说明。急停由独立安全回路处理，不作为普通停机报警上报。
