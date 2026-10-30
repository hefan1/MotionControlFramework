using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>为一个任务条件提供可选多条设备报警代码的紧凑勾选列表。</summary>
    internal sealed class TaskAlarmSelector : UserControl
    {
        /// <summary>列表中显示的报警代码勾选项。</summary>
        private readonly List<CheckBox> _checks = new List<CheckBox>();

        /// <summary>按目录顺序显示报警代码，并保留已经失效的旧引用。</summary>
        internal TaskAlarmSelector(IEnumerable<string> alarmCodes, IEnumerable<string> selectedCodes,
            Func<string, string> missingCodeLabel, string emptyCatalogLabel)
        {
            var selected = new HashSet<string>(selectedCodes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var catalogCodes = (alarmCodes ?? Enumerable.Empty<string>())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var codes = new List<string>(catalogCodes);
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
            foreach (string code in codes)
            {
                bool isKnownCode = catalogCodes.Contains(code, StringComparer.OrdinalIgnoreCase);
                string label = isKnownCode ? code : missingCodeLabel(code);
                var check = new CheckBox
                {
                    Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                    Tag = code,
                    IsChecked = selected.Contains(code),
                    ToolTip = code,
                    MinHeight = 28,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 1, 3, 1)
                };
                _checks.Add(check);
                items.Children.Add(check);
            }

            Content = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(202, 213, 226)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = new ScrollViewer
                {
                    Content = items,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    MaxHeight = 126,
                    Padding = new Thickness(5, 3, 5, 3)
                }
            };
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
