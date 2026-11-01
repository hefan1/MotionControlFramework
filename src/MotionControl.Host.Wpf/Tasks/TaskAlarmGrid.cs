using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MotionControl.Framework.Contracts.Alarms;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>任务运行页的只读报警表格，只负责显示快照，不控制报警和设备。</summary>
    internal sealed class TaskAlarmGrid : DataGrid
    {
        /// <summary>从 Host 当前语言资源读取表头和报警级别文字。</summary>
        private readonly Func<string, string> _getText;
        /// <summary>保存上次显示的行，内容未变化时不重新绑定表格。</summary>
        private List<TaskAlarmDisplayRow> _rows = new List<TaskAlarmDisplayRow>();

        /// <summary>创建只读报警表格，保持任务页原有列和样式。</summary>
        internal TaskAlarmGrid(Func<string, string> getText)
        {
            _getText = getText ?? throw new ArgumentNullException(nameof(getText));
            IsReadOnly = true;
            AutoGenerateColumns = false;
            CanUserAddRows = false;
            HeadersVisibility = DataGridHeadersVisibility.Column;
            MinHeight = 64;
            MaxHeight = 150;
            BorderBrush = Brush("#D9E0E8");
            BorderThickness = new Thickness(1);
            Background = Brush("#FFFFFF");
            RowHeight = 28;
            ColumnHeaderHeight = 30;
            Columns.Add(CreateColumn("报警级别", "Kind", new DataGridLength(100)));
            Columns.Add(CreateColumn("报警代码", "Code", new DataGridLength(150)));
            Columns.Add(CreateColumn("报警来源", "Source", new DataGridLength(130)));
            Columns.Add(CreateColumn("报警内容", "Message", new DataGridLength(1, DataGridLengthUnitType.Star)));
            Columns.Add(CreateColumn("当前条件", "Detail", new DataGridLength(1.2, DataGridLengthUnitType.Star)));
        }

        /// <summary>把当前报警快照转换为表格行；内容相同时保留现有绑定。</summary>
        internal void ShowAlarms(IEnumerable<MachineAlarmRecord> alarms)
        {
            var rows = alarms.Select(alarm => new TaskAlarmDisplayRow(alarm,
                _getText(alarm.Definition.Kind == MachineAlarmKind.StopMachine ? "停机报警" : "非停机报警"))).ToList();
            if (_rows.Count == rows.Count && _rows.Zip(rows, (left, right) => left.Signature == right.Signature).All(equal => equal))
                return;
            _rows = rows;
            ItemsSource = _rows;
        }

        /// <summary>设备断开或任务会话关闭后清空已显示报警。</summary>
        internal void ClearAlarms()
        {
            ItemsSource = null;
            _rows.Clear();
        }

        /// <summary>切换语言后更新报警表格表头。</summary>
        internal void UpdateHeaders()
        {
            string[] keys = { "报警级别", "报警代码", "报警来源", "报警内容", "当前条件" };
            for (int index = 0; index < keys.Length && index < Columns.Count; index++)
                Columns[index].Header = _getText(keys[index]);
        }

        /// <summary>创建绑定报警字段的表格列。</summary>
        private DataGridTextColumn CreateColumn(string headerKey, string path, DataGridLength width)
        {
            return new DataGridTextColumn { Header = _getText(headerKey), Binding = new Binding(path), Width = width };
        }

        /// <summary>创建任务页使用的明确颜色画刷。</summary>
        private static Brush Brush(string color)
        {
            return (Brush)new BrushConverter().ConvertFromString(color);
        }

        /// <summary>一条只用于显示的报警行和内容比较键。</summary>
        private sealed class TaskAlarmDisplayRow
        {
            /// <summary>显示停机或非停机类型。</summary>
            public string Kind { get; }
            /// <summary>稳定的报警代码。</summary>
            public string Code { get; }
            /// <summary>报告该报警的来源。</summary>
            public string Source { get; }
            /// <summary>报警库说明。</summary>
            public string Message { get; }
            /// <summary>当前报警条件补充信息。</summary>
            public string Detail { get; }
            /// <summary>用于判断表格内容是否发生变化。</summary>
            public string Signature { get; }

            /// <summary>从 Runtime 报警记录生成界面行。</summary>
            public TaskAlarmDisplayRow(MachineAlarmRecord alarm, string kind)
            {
                Kind = kind; Code = alarm.Definition.Code; Source = alarm.SourceName;
                Message = alarm.Definition.Message; Detail = alarm.Detail;
                Signature = string.Join("|", Kind, Code, Source, Message, Detail, alarm.ConditionActive);
            }
        }
    }
}
