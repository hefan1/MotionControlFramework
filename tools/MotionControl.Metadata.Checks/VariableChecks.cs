using System;
using System.Linq;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.Framework.Wpf.Variables;
using Newtonsoft.Json;

namespace MotionControl.Metadata.Checks
{
    internal static partial class Program
    {
        /// <summary>提供具有明确物理含义的数值变量，所有值均为隔离检查数据。</summary>
        private static DeviceVariableDefinition LengthVariable()
        {
            return new DeviceVariableDefinition
            {
                Name = "Offset", DisplayName = "补偿距离", DataType = "Number", InitialValue = "2.5", Description = "长度补偿",
                EngineeringUnit = "mm", Dimension = "Length", MinimumValue = -10, MaximumValue = 10, DecimalPlaces = 2,
                SourceId = "Vision.Calibration.Offset", SourceDescription = "隔离视觉结果", AllowOperatorWrite = true
            };
        }

        /// <summary>旧格式缺元数据仍可读取；零和未知分开，配置往返和定义副本保留全部新字段。</summary>
        private static void LegacyAndMetadataRoundTrip()
        {
            var legacy = JsonConvert.DeserializeObject<DeviceVariableConfigurationDocument>(
                "{\"schemaVersion\":1,\"variables\":[{\"name\":\"Old\",\"dataType\":\"Number\"}]}");
            legacy.Validate();
            var oldStore = new DeviceVariableStore(legacy.Variables);
            DeviceVariableSnapshot unknown = oldStore.GetSnapshot().Single();
            Check(!unknown.HasValue && unknown.EngineeringUnit == null && unknown.Dimension == null, "旧配置不能被补造单位或零值。");
            Reject(() => oldStore.GetNumber("Old"), "没有有效值");
            var row = new DeviceVariableRow(unknown);
            Check(row.CurrentValueDisplay == "无有效数据" && row.EngineeringUnitDisplay == "未定义", "未知值或单位显示错误。");
            oldStore.SetNumber("Old", 0);
            row.Update(oldStore.GetSnapshot().Single());
            Check(row.HasValue && row.CurrentValueDisplay == "0", "有效零被显示成未知。");

            using (var files = new TestFiles())
            {
                DeviceVariableDefinition definition = LengthVariable();
                files.Store.SaveJson("M", "Parameters/DeviceVariables.json", new DeviceVariableConfigurationDocument { Variables = { definition } });
                var loaded = files.Store.LoadJson<DeviceVariableConfigurationDocument>("M", "Parameters/DeviceVariables.json");
                loaded.Validate();
                DeviceVariableDefinition copy = loaded.Variables.Single().Copy();
                Check(copy.Name == "Offset" && copy.DisplayName == "补偿距离" && copy.EngineeringUnit == "mm" && copy.Dimension == "Length"
                    && copy.MinimumValue == -10 && copy.MaximumValue == 10 && copy.DecimalPlaces == 2
                    && copy.SourceId == "Vision.Calibration.Offset" && copy.SourceDescription == "隔离视觉结果", "元数据配置往返或复制丢字段。");
                var store = new DeviceVariableStore(loaded.Variables);
                loaded.Variables[0].EngineeringUnit = "deg";
                store.GetDefinitions()[0].SourceId = "changed-outside";
                DeviceVariableSnapshot snapshot = store.GetSnapshot().Single();
                Check(snapshot.EngineeringUnit == "mm" && snapshot.SourceId == "Vision.Calibration.Offset", "运行时定义被外部对象改写。");
                row.Update(snapshot);
                row.UpdateValueDisplay("No data", "Undefined", "Not applicable");
                Check(row.CurrentValueDisplay == "2.50" && row.CurrentValue == "2.5", "显示精度修改了原始值或未生效。");
                Check(row.Name == "Offset" && row.NameDisplay.Contains("补偿距离") && row.NameDisplay.Contains("Offset"), "显示名称改变了稳定键。");
                row.UpdateSearchMatch("补偿距离");
                Check(row.IsSearchMatch, "显示名称未进入只读搜索。");
            }

            DeviceVariableDefinition bad = LengthVariable();
            bad.MinimumValue = 20;
            Reject(() => DeviceVariableStore.ValidateDefinitions(new[] { bad }), "上下限");
            bad = LengthVariable(); bad.DecimalPlaces = 16;
            Reject(() => DeviceVariableStore.ValidateDefinitions(new[] { bad }), "0 到 15");
            bad = LengthVariable(); bad.Dimension = "Angle";
            Reject(() => DeviceVariableStore.ValidateDefinitions(new[] { bad }), "单位与量纲不一致");
            bad = LengthVariable(); bad.DataType = "Boolean";
            Reject(() => DeviceVariableStore.ValidateDefinitions(new[] { bad }), "非数值变量");
        }

        /// <summary>校验最终写入范围、显示改名兼容、来源变化隔离及旧确认框拒绝。</summary>
        private static void NumericRangeAndValueIdentity()
        {
            DeviceVariableDefinition definition = LengthVariable();
            var store = new DeviceVariableStore(new[] { definition });
            string error;
            Check(store.TrySetOperatorValue("Offset", "10", out error), "允许范围上边界写入失败。");
            Check(!store.TrySetOperatorValue("Offset", "10.1", out error) && store.GetNumber("Offset") == 10, "越界人工写入覆盖了旧值。");
            Reject(() => store.SetNumber("Offset", -11), "低于配置下限");
            Check(store.GetNumber("Offset") == 10, "失败流程写入破坏原值。");

            DeviceVariableDefinition confirmed = store.GetDefinitions().Single();
            definition.DisplayName = "新显示名";
            definition.SourceDescription = "更新文字说明";
            store.ReplaceDefinitions(new[] { definition });
            Check(store.GetNumber("Offset") == 10, "纯显示改动丢失运行值。");
            Check(store.TrySetOperatorValue("Offset", "8", out error, confirmed), "纯显示文字变化错误阻止稳定来源写入。");
            DeviceVariableSnapshot beforeSourceChange = store.GetSnapshot().Single();
            definition.SourceId = "Other.Source";
            store.ReplaceDefinitions(new[] { definition });
            Check(store.GetNumber("Offset") == 2.5 && !string.IsNullOrWhiteSpace(store.GetSnapshot().Single().ValueNotice), "来源变化未隔离旧值或没有原因。");
            Check(!store.TrySetOperatorValue("Offset", "9", out error, confirmed) && error.Contains("已变化"), "旧确认框可以写入新来源。");
            Check(store.RestoreCurrentValues(new[] { beforeSourceChange }).Any() && store.GetNumber("Offset") == 2.5, "旧来源恢复值被错误接受。");

            DeviceVariableSnapshot matching = store.GetSnapshot().Single();
            store.SetNumber("Offset", 6);
            Check(store.RestoreCurrentValues(new[] { matching }).Count == 0 && store.GetNumber("Offset") == 2.5, "兼容来源恢复失败。");
            definition.EngineeringUnit = "deg"; definition.Dimension = "Angle"; definition.InitialValue = null;
            store.ReplaceDefinitions(new[] { definition });
            Check(!store.GetSnapshot().Single().HasValue, "单位和量纲变化后仍使用旧位置值。");
            Check(store.RestoreCurrentValues(new[] { matching }).Any(), "长度快照被当作角度恢复。");

            var legacySnapshot = new DeviceVariableSnapshot("Offset", "Number", "0", true, DateTime.Now, "", false);
            Check(store.RestoreCurrentValues(new[] { legacySnapshot }).Any() && !store.GetSnapshot().Single().HasValue,
                "缺来源证据的历史值被贴上当前单位。");

            var pressure = new DeviceVariableDefinition { Name = "Pressure", DataType = "Number", InitialValue = "1",
                EngineeringUnit = "mPa", Dimension = "Pressure", AllowOperatorWrite = true };
            var pressureStore = new DeviceVariableStore(new[] { pressure });
            pressureStore.SetNumber("Pressure", 9);
            DeviceVariableSnapshot oldPressure = pressureStore.GetSnapshot().Single();
            pressure.EngineeringUnit = "MPa";
            pressureStore.ReplaceDefinitions(new[] { pressure });
            Check(pressureStore.GetNumber("Pressure") == 1 && !string.IsNullOrWhiteSpace(pressureStore.GetSnapshot().Single().ValueNotice),
                "mPa 改为 MPa 后错误沿用原运行值。");
            Check(pressureStore.RestoreCurrentValues(new[] { oldPressure }).Any() && pressureStore.GetNumber("Pressure") == 1,
                "mPa 历史值被错误作为 MPa 恢复。");
        }
    }
}
