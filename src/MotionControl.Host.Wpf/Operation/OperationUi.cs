using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Host.Wpf.Operation
{
    /// <summary>两个操作页明确共用的文字与状态颜色，不承担设备控制。</summary>
    internal static class OperationUi
    {
        /// <summary>按中文原文定位本模块资源。</summary>
        internal static string T(FrameworkElement view, string text)
        { return view.TryFindResource("操作页." + text) as string ?? text; }
        /// <summary>只替换本模块字典，不改变设备名称、代码和流程文字。</summary>
        internal static void SetLanguage(FrameworkElement view, string language)
        {
            foreach (var item in view.Resources.MergedDictionaries.Where(item => item.Source != null && item.Source.ToString().Contains("Strings.Operation.")).ToList())
                view.Resources.MergedDictionaries.Remove(item);
            string name = language == "en-US" ? "en-US" : "zh-CN";
            view.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MotionControl.Host.Wpf;component/Operation/Localization/Strings.Operation." + name + ".xaml", UriKind.Relative) });
        }
        /// <summary>状态统一使用文字资源键，未知值不能落入就绪。</summary>
        internal static string StateKey(MachineRunState? state)
        {
            switch (state)
            {
                case MachineRunState.Running: return "设备运行中";
                case MachineRunState.Ready: return "设备已就绪";
                case MachineRunState.Pausing: return "设备暂停中";
                case MachineRunState.Paused: return "设备已暂停";
                case MachineRunState.Alarmed: return "设备有停机报警";
                case MachineRunState.ResetRequired: return "设备等待复位";
                case MachineRunState.Disconnected: return "设备未连接";
                default: return "未读取设备状态";
            }
        }
        /// <summary>颜色只辅助文字；暂停和未知状态不会显示运行绿色。</summary>
        internal static Brush StateBrush(MachineRunState? state)
        {
            if (state == MachineRunState.Alarmed) return new SolidColorBrush(Color.FromRgb(190, 55, 55));
            if (state == MachineRunState.Running) return new SolidColorBrush(Color.FromRgb(27, 139, 74));
            if (state == MachineRunState.Ready) return new SolidColorBrush(Color.FromRgb(35, 117, 209));
            return new SolidColorBrush(Color.FromRgb(130, 101, 37));
        }
    }
}
