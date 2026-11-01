using System;
using System.Linq;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Host.Wpf.Tasks;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>检查任务页报警表格只读取快照，并正确保留和清理显示行。</summary>
    internal static class TaskAlarmGridChecks
    {
        /// <summary>覆盖停机与非停机、未变化快照、状态变化、语言更新及清空。</summary>
        internal static void Run()
        {
            bool english = false;
            var grid = new TaskAlarmGrid(key => english ? "EN:" + key : key);
            var stop = Record("S1", MachineAlarmKind.StopMachine, true);
            var warning = Record("W1", MachineAlarmKind.NonStop, true);
            grid.ShowAlarms(new[] { stop, warning });
            var firstRows = grid.ItemsSource;
            Ensure(grid.Items.Count == 2, "两条报警应同时显示");
            Ensure(grid.Columns.Count == 5 && (string)grid.Columns[0].Header == "报警级别", "报警列或表头不正确");
            grid.ShowAlarms(new[] { stop, warning });
            Ensure(ReferenceEquals(firstRows, grid.ItemsSource), "相同快照不应重新绑定");
            grid.ShowAlarms(new[] { Record("S1", MachineAlarmKind.StopMachine, false), warning });
            Ensure(!ReferenceEquals(firstRows, grid.ItemsSource), "报警条件变化应刷新显示快照");
            english = true;
            grid.UpdateHeaders();
            grid.ShowAlarms(new[] { stop, warning });
            Ensure((string)grid.Columns[0].Header == "EN:报警级别", "语言切换应更新表头");
            object firstRow = grid.Items.Cast<object>().First();
            Ensure((string)firstRow.GetType().GetProperty("Kind").GetValue(firstRow) == "EN:停机报警", "刷新后应使用当前语言显示级别");
            grid.ClearAlarms();
            Ensure(grid.ItemsSource == null && grid.Items.Count == 0, "清空后不得保留旧报警");
        }

        /// <summary>创建给表格展示的固定报警快照，不控制设备。</summary>
        private static MachineAlarmRecord Record(string code, MachineAlarmKind kind, bool active)
        {
            return new MachineAlarmRecord(new MachineAlarmDefinition(code, "测试报警", kind), "上料工位", "检查输入", active,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        /// <summary>失败时报告具体显示条件。</summary>
        private static void Ensure(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
