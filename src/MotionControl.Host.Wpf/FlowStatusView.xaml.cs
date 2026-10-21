using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Host.Wpf
{
    /// <summary>显示 Runtime 的工位步骤、整机状态和当前报警，供现场核对流程进度。</summary>
    public partial class FlowStatusView : UserControl
    {
        /// <summary>本页观察的整机运行时，与手动轴和 IO 页面共享同一个实例。</summary>
        private readonly MachineRuntime _runtime;

        /// <summary>按工位保存当前步骤文字，避免定时刷新时重建列表。</summary>
        private readonly Dictionary<IStationFlow, TextBlock> _stepTextByFlow =
            new Dictionary<IStationFlow, TextBlock>();

        /// <summary>页面显示期间刷新可能在后台推进的当前工位步骤。</summary>
        private readonly DispatcherTimer _refreshTimer;

        /// <summary>创建工位进度页，并建立当前 Runtime 的每个工位行。</summary>
        public FlowStatusView(MachineRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            InitializeComponent();
            foreach (IStationFlow flow in _runtime.StationFlows)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                row.Children.Add(new TextBlock
                {
                    Text = flow.StationName + "：",
                    Width = 140,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(64, 83, 106))
                });
                var stepText = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(38, 56, 77)) };
                row.Children.Add(stepText);
                StationRowsPanel.Children.Add(row);
                _stepTextByFlow.Add(flow, stepText);
            }

            if (_stepTextByFlow.Count == 0)
            {
                var emptyText = new TextBlock
                {
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(89, 107, 125))
                };
                emptyText.SetResourceReference(TextBlock.TextProperty, "未配置自动工位");
                StationRowsPanel.Children.Add(emptyText);
            }

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _refreshTimer.Tick += RefreshTimer_Tick;
            Loaded += FlowStatusView_Loaded;
            Unloaded += FlowStatusView_Unloaded;
            RefreshDisplay();
        }

        /// <summary>页面进入可见区域时订阅 Runtime 事件并启动步骤刷新。</summary>
        private void FlowStatusView_Loaded(object sender, RoutedEventArgs e)
        {
            _runtime.StatusChanged += Runtime_Changed;
            _runtime.AlarmsChanged += Runtime_Changed;
            _refreshTimer.Start();
            RefreshDisplay();
        }

        /// <summary>离开页面时停止轮询并解除事件订阅。</summary>
        private void FlowStatusView_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            _runtime.StatusChanged -= Runtime_Changed;
            _runtime.AlarmsChanged -= Runtime_Changed;
        }

        /// <summary>Runtime 可能从后台线程通知，统一切回界面线程读取状态。</summary>
        private void Runtime_Changed(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshDisplay));
                return;
            }

            RefreshDisplay();
        }

        /// <summary>定时读取工位步骤，使轴动作和传感器等待进度实时可见。</summary>
        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshDisplay();
        }

        /// <summary>语言切换后重新读取代码生成的状态和报警文字。</summary>
        public void RefreshLocalizedText()
        {
            RefreshDisplay();
        }

        /// <summary>刷新当前状态、步骤、报警及最近故障文字。</summary>
        private void RefreshDisplay()
        {
            MachineStateText.Text = ((string)FindResource("整机当前状态"))
                + (string)FindResource(GetMachineStateResourceKey(_runtime.State));
            foreach (KeyValuePair<IStationFlow, TextBlock> row in _stepTextByFlow)
            {
                row.Value.Text = row.Key.CurrentStepName;
            }

            MachineAlarmRecord[] alarms = _runtime.AlarmService.Alarms.ToArray();
            AlarmText.Text = alarms.Length == 0
                ? (string)FindResource("无当前报警")
                : string.Join("\n", alarms.Select(alarm =>
                    alarm.SourceName + " / " + alarm.Definition.Code + " / " + alarm.Definition.Message
                    + "：" + alarm.Detail
                    + (alarm.ConditionActive ? string.Empty : (string)FindResource("报警待复位"))));

            LastErrorText.Text = _runtime.LastError == null
                ? string.Empty
                : ((string)FindResource("最近故障")) + _runtime.LastError.GetBaseException().Message;
        }

        /// <summary>把 Runtime 状态映射到与主窗口一致的多语言资源键。</summary>
        private static string GetMachineStateResourceKey(MachineRunState state)
        {
            switch (state)
            {
                case MachineRunState.ResetRequired: return "状态等待复位";
                case MachineRunState.Ready: return "状态就绪";
                case MachineRunState.Running: return "状态运行中";
                case MachineRunState.Pausing: return "状态暂停中";
                case MachineRunState.Paused: return "状态已暂停";
                case MachineRunState.Alarmed: return "状态报警";
                default: return "状态未连接";
            }
        }
    }
}
