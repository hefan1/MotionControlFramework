using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.TaskEngine.Catalog;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>为一个任务条件提供可选多条设备报警代码的紧凑勾选列表。</summary>
    internal sealed class TaskAlarmSelector : UserControl
    {
        /// <summary>列表中显示的报警代码勾选项。</summary>
        private readonly List<CheckBox> _checks = new List<CheckBox>();
        /// <summary>按报警代码保存等级，用于让关联数量摘要同时显示停机与非停机条数。</summary>
        private readonly Dictionary<string, MachineAlarmKind> _alarmKindsByCode =
            new Dictionary<string, MachineAlarmKind>(StringComparer.OrdinalIgnoreCase);
        /// <summary>显示当前关联数量，让操作人员确认多选结果。</summary>
        private readonly TextBlock _selectionSummary;
        /// <summary>关联数量的本地化格式。</summary>
        private readonly string _selectionSummaryFormat;

        /// <summary>按目录顺序展示报警说明和停机级别，并保留已经失效的旧引用。</summary>
        internal TaskAlarmSelector(IEnumerable<TaskAlarmInfo> alarmDefinitions, IEnumerable<string> selectedCodes,
            Func<string, string> missingCodeLabel, string emptyCatalogLabel, string selectionSummaryFormat,
            string stopAlarmLabel, string nonStopAlarmLabel, string checkGuidanceLabel, string recoveryGuidanceLabel)
        {
            _selectionSummaryFormat = selectionSummaryFormat;
            var selected = new HashSet<string>(selectedCodes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var catalogAlarms = (alarmDefinitions ?? Enumerable.Empty<TaskAlarmInfo>())
                .Where(alarm => alarm != null && !string.IsNullOrWhiteSpace(alarm.Code))
                .GroupBy(alarm => alarm.Code, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            foreach (TaskAlarmInfo alarm in catalogAlarms)
            {
                _alarmKindsByCode[alarm.Code] = alarm.Kind;
            }

            var codes = catalogAlarms.Select(alarm => alarm.Code).ToList();
            foreach (string code in selected)
            {
                if (!codes.Contains(code, StringComparer.OrdinalIgnoreCase))
                {
                    codes.Add(code);
                }
            }

            if (codes.Count == 0)
            {
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(247, 249, 252)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(202, 213, 226)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8),
                    Child = new TextBlock
                    {
                        Text = emptyCatalogLabel,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromRgb(89, 107, 125))
                    }
                };
                return;
            }

            var items = new StackPanel();
            _selectionSummary = new TextBlock
            {
                Margin = new Thickness(2, 0, 2, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(64, 83, 106)),
                FontWeight = FontWeights.SemiBold
            };
            foreach (string code in codes)
            {
                TaskAlarmInfo alarm = catalogAlarms.FirstOrDefault(item =>
                    string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase));
                bool isKnownCode = alarm != null;
                string label = isKnownCode ? code : missingCodeLabel(code);
                var description = new StackPanel { Margin = new Thickness(4, 2, 4, 2) };
                var title = new DockPanel();
                var codeText = new TextBlock
                {
                    Text = label,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = isKnownCode
                        ? new SolidColorBrush(Color.FromRgb(38, 56, 77))
                        : new SolidColorBrush(Color.FromRgb(177, 80, 32))
                };
                title.Children.Add(codeText);
                if (isKnownCode)
                {
                    var kindText = new TextBlock
                    {
                        Text = alarm.Kind == MachineAlarmKind.StopMachine ? stopAlarmLabel : nonStopAlarmLabel,
                        Foreground = alarm.Kind == MachineAlarmKind.StopMachine
                            ? new SolidColorBrush(Color.FromRgb(176, 55, 55))
                            : new SolidColorBrush(Color.FromRgb(36, 111, 79)),
                        Margin = new Thickness(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    DockPanel.SetDock(kindText, Dock.Right);
                    title.Children.Add(kindText);
                    description.Children.Add(title);
                    description.Children.Add(new TextBlock
                    {
                        Text = alarm.Message,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromRgb(89, 107, 125)),
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }
                else
                {
                    description.Children.Add(codeText);
                }
                var check = new CheckBox
                {
                    Content = description,
                    Tag = code,
                    IsChecked = selected.Contains(code),
                    ToolTip = isKnownCode
                        ? BuildGuidanceTooltip(alarm, checkGuidanceLabel, recoveryGuidanceLabel)
                        : code,
                    MinHeight = 38,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 2, 3, 2)
                };
                check.Checked += SelectionChanged;
                check.Unchecked += SelectionChanged;
                _checks.Add(check);
                items.Children.Add(check);
            }

            UpdateSelectionSummary();
            var content = new StackPanel();
            content.Children.Add(_selectionSummary);
            content.Children.Add(new ScrollViewer
            {
                Content = items,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 150,
                Padding = new Thickness(5, 3, 5, 3)
            });
            Content = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(202, 213, 226)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = content
            };
        }

        /// <summary>更新多选数量；一条事件可以同时引用多条独立报警。</summary>
        private void SelectionChanged(object sender, RoutedEventArgs e)
        {
            UpdateSelectionSummary();
        }

        /// <summary>把关联总数、停机数、非停机数和失效引用数显示在报警列表上方。</summary>
        private void UpdateSelectionSummary()
        {
            if (_selectionSummary != null)
            {
                IReadOnlyList<string> selectedCodes = SelectedCodes;
                int stopCount = 0;
                int nonStopCount = 0;
                foreach (string code in selectedCodes)
                {
                    MachineAlarmKind kind;
                    if (!_alarmKindsByCode.TryGetValue(code, out kind))
                    {
                        continue;
                    }

                    if (kind == MachineAlarmKind.StopMachine)
                    {
                        stopCount++;
                    }
                    else
                    {
                        nonStopCount++;
                    }
                }

                int missingCount = selectedCodes.Count - stopCount - nonStopCount;
                _selectionSummary.Text = string.Format(
                    _selectionSummaryFormat,
                    selectedCodes.Count,
                    stopCount,
                    nonStopCount,
                    missingCount);
            }
        }

        /// <summary>把报警的现场检查和恢复指导放入悬停说明。</summary>
        private static string BuildGuidanceTooltip(TaskAlarmInfo alarm, string checkGuidanceLabel,
            string recoveryGuidanceLabel)
        {
            var lines = new List<string> { alarm.Code, alarm.Message };
            if (!string.IsNullOrWhiteSpace(alarm.CheckGuidance)) lines.Add(checkGuidanceLabel + alarm.CheckGuidance);
            if (!string.IsNullOrWhiteSpace(alarm.RecoveryGuidance)) lines.Add(recoveryGuidanceLabel + alarm.RecoveryGuidance);
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>返回当前勾选项的原始代码，不包含给操作员看的失效说明。</summary>
        internal IReadOnlyList<string> SelectedCodes
        {
            get
            {
                return _checks.Where(check => check.IsChecked == true)
                    .Select(check => (string)check.Tag)
                    .ToList();
            }
        }
    }
}
