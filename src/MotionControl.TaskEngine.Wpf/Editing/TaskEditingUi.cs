using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>任务编辑页少量明确控件与语言方法，不承担工艺执行。</summary>
    internal static class TaskEditingUi
    {
        /// <summary>替换本页语言字典，保持工程名称、对象 ID 与数值不变。</summary>
        internal static void SetLanguage(FrameworkElement view, string language)
        {
            foreach (var dictionary in view.Resources.MergedDictionaries.Where(item => item.Source != null && item.Source.ToString().Contains("Strings.Editor.")).ToList())
                view.Resources.MergedDictionaries.Remove(dictionary);
            string file = language == "en-US" ? "en-US" : "zh-CN";
            view.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MotionControl.TaskEngine.Wpf;component/Localization/Strings.Editor." + file + ".xaml", UriKind.Relative) });
        }

        /// <summary>按中文原文查语言资源，缺少资源时仍保留可读原文。</summary>
        internal static string T(FrameworkElement view, string text)
        { return view.TryFindResource("任务编辑." + text) as string ?? text; }

        /// <summary>创建保持边距的字段文字。</summary>
        internal static TextBlock Label(FrameworkElement view, string text)
        { return new TextBlock { Text = T(view, text), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) }; }

        /// <summary>创建普通输入框，单位由邻近标签明确说明。</summary>
        internal static TextBox Text(string value)
        { return new TextBox { Text = value ?? string.Empty, MinHeight = 30, Padding = new Thickness(7, 4, 7, 4) }; }

        /// <summary>创建对象下拉，保留已经失效的旧引用，交由检查定位，不静默换成第一项。</summary>
        internal static ComboBox Names(IEnumerable<string> names, string value)
        {
            var values = new List<string> { string.Empty };
            values.AddRange(names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(value) && !values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
            return new ComboBox { ItemsSource = values, SelectedItem = value ?? string.Empty, MinHeight = 30, Padding = new Thickness(5) };
        }

        /// <summary>创建少量固定选项，内部值与显示语言分开。</summary>
        internal static ComboBox Choices(FrameworkElement view, object[] values, string[] labels, object selected)
        {
            var box = new ComboBox { MinHeight = 30, Padding = new Thickness(5), SelectedValuePath = "Tag" };
            for (int i = 0; i < values.Length; i++) box.Items.Add(new ComboBoxItem { Tag = values[i], Content = T(view, labels[i]) });
            box.SelectedValue = selected;
            return box;
        }

        /// <summary>创建明确的单次按钮事件入口。</summary>
        internal static Button Button(FrameworkElement view, string text, RoutedEventHandler handler)
        { var button = new Button { Content = T(view, text), Margin = new Thickness(2), Padding = new Thickness(9, 5, 9, 5) }; button.Click += handler; return button; }

        /// <summary>解析工程数值，不受界面语言和系统小数分隔符影响。</summary>
        internal static double Number(TextBox box, string field)
        {
            double value;
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidOperationException(field + "：请输入有效数字，小数使用点号。");
            return value;
        }

        /// <summary>解析步时限等整数字段，拒绝小数和溢出。</summary>
        internal static int Integer(TextBox box, string field)
        {
            int value;
            if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) throw new InvalidOperationException(field + "：请输入有效整数。");
            return value;
        }
    }
}
