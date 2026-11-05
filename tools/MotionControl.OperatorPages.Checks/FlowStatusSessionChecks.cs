using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>使用内存连接核对工位步骤、关联输入与报警结论属于当前页面会话。</summary>
    internal static class FlowStatusSessionChecks
    {
        /// <summary>分别用中文和英文资源检查有效、读取失败、断连及同对象重连。</summary>
        internal static void Run()
        {
            foreach (string language in new[] { "zh-CN", "en-US" })
            {
                var card = new GuardCard { IsConnected = true };
                var alarms = new ReadOnlyAlarms();
                var flow = new ReadOnlyInputFlow();
                var runtime = new MachineRuntime(new MotionCardGroup(new[] { card }), new IStationFlow[] { flow }, alarms, new GuardSafety());
                runtime.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                var page = new FlowStatusView(runtime);
                page.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(
                    "/MotionControl.Host.Wpf;component/Localization/Strings." + language + ".xaml", UriKind.Relative) });
                var input = (Button)page.FindName("StationInputButton");
                var detail = (TextBlock)page.FindName("StationDetailText");
                var alarm = (TextBlock)page.FindName("AlarmText");
                int navigations = 0;
                page.NavigationRequested += (sender, args) => navigations++;
                string message(string key) { return (string)page.FindResource(key); }
                page.RefreshLocalizedText();
                Require(input.IsEnabled && detail.Text.Contains(flow.WaitingInputName)
                    && alarm.Text == message("无当前报警"), "有效连接未显示真实输入和空报警。");
                alarms.Read = () => throw new InvalidOperationException("alarm read failed");
                page.RefreshLocalizedText();
                Require(alarm.Text == message("流程页.报警状态未知") && Equals(alarm.ToolTip, "alarm read failed"),
                    "报警服务失败后未保留未知及原因。");
                alarms.Read = null;
                page.RefreshLocalizedText();
                Require(alarm.Text == message("无当前报警") && alarm.ToolTip == null, "成功重读后仍沿用旧错误。");

                // 步骤已读完、报警仍在读取时掉线，这一轮的输入也必须失效。
                alarms.Read = () => card.IsConnected = false;
                page.RefreshLocalizedText();
                Require(!input.IsEnabled && !detail.Text.Contains(flow.WaitingInputName)
                    && detail.Text.Contains(message("流程页.断连未知")) && alarm.Text.Contains(message("流程页.报警状态未知")),
                    "断连的同一轮继续显示旧步骤、输入或报警。");
                input.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(navigations == 0 && !page.FocusStationByName(flow.StationName), "失效会话仍可定向定位。");
                runtime.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                card.IsConnected = true;
                runtime.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                alarms.Read = null;
                page.RefreshLocalizedText();
                Require(!input.IsEnabled && !detail.Text.Contains(flow.WaitingInputName)
                    && !page.FocusStationByName(flow.StationName), "同一 Runtime 重连后旧页面重新接受了输入。");
                var fresh = new FlowStatusView(runtime);
                Require(((Button)fresh.FindName("StationInputButton")).IsEnabled && fresh.FocusStationByName(flow.StationName)
                    && card.Calls == 0, "新会话未恢复有效定位，或只读导航访问了卡。");
            }
        }

        /// <summary>记录复核失败的具体原因。</summary>
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        /// <summary>空报警内存源；观测失败可控，禁止修改状态。</summary>
        private sealed class ReadOnlyAlarms : IMachineAlarmService
        {
            internal Action Read;
            public IReadOnlyCollection<MachineAlarmRecord> Alarms { get { Read?.Invoke(); return Array.Empty<MachineAlarmRecord>(); } }
            public bool HasLatchedStopAlarm => false;
            public event EventHandler AlarmsChanged { add { } remove { } }
            public event EventHandler<MachineAlarmRaisedEventArgs> StopAlarmRaised { add { } remove { } }
            public void ReportCondition(MachineAlarmDefinition definition, string sourceName, bool conditionActive, string detail)
            { throw new InvalidOperationException("只读页面禁止更新报警。"); }
            public void ResetStopAlarms() { throw new InvalidOperationException("只读页面禁止复位。"); }
            public void EnsureStopAlarmConditionsRecovered() { throw new InvalidOperationException("只读页面禁止执行动作检查。"); }
        }
    }
}
