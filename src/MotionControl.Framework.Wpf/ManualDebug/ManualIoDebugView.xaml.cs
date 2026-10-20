using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Framework.Wpf.ManualDebug
{
    /// <summary>集中显示已映射 DI 状态，并提供受控的逻辑 DO 手动操作。</summary>
    public partial class ManualIoDebugView : UserControl
    {
        /// <summary>提供设备连接、自动运行和急停报警状态。</summary>
        private readonly MachineRuntime _machineRuntime;

        /// <summary>通过设备逻辑名称读取 DI、读写 DO。</summary>
        private readonly ILogicalIo _logicalIo;

        /// <summary>当前设备映射的全部逻辑 DI 名称。</summary>
        private readonly IReadOnlyList<string> _inputNames;

        /// <summary>当前设备映射的全部逻辑 DO 名称。</summary>
        private readonly IReadOnlyList<string> _outputNames;

        /// <summary>按逻辑 DI 名称保存状态文字。</summary>
        private readonly Dictionary<string, TextBlock> _inputStatusTextByName =
            new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);

        /// <summary>按逻辑 DO 名称保存状态文字。</summary>
        private readonly Dictionary<string, TextBlock> _outputStatusTextByName =
            new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);

        /// <summary>按逻辑 DO 名称保存两枚操作按钮，便于按 Runtime 联锁统一启用或禁用。</summary>
        private readonly Dictionary<string, List<Button>> _outputActionButtonsByName =
            new Dictionary<string, List<Button>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>定时刷新 DI 和 DO 状态。</summary>
        private readonly DispatcherTimer _refreshTimer;

        /// <summary>防止上一次 IO 刷新未完成时重复启动下一轮。</summary>
        private bool _refreshInProgress;

        /// <summary>创建单独的 IO 手动页，只操作已映射的逻辑点位。</summary>
        public ManualIoDebugView(
            MachineRuntime machineRuntime,
            ILogicalIo logicalIo,
            IEnumerable<string> inputNames,
            IEnumerable<string> outputNames)
        {
            if (machineRuntime == null)
            {
                throw new ArgumentNullException(nameof(machineRuntime));
            }

            if (logicalIo == null)
            {
                throw new ArgumentNullException(nameof(logicalIo));
            }

            if (inputNames == null)
            {
                throw new ArgumentNullException(nameof(inputNames));
            }

            if (outputNames == null)
            {
                throw new ArgumentNullException(nameof(outputNames));
            }

            InitializeComponent();
            _machineRuntime = machineRuntime;
            _logicalIo = logicalIo;
            _inputNames = inputNames.ToList().AsReadOnly();
            _outputNames = outputNames.ToList().AsReadOnly();

            BuildInputRows();
            BuildOutputRows();
            NoInputsText.Visibility = _inputNames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoOutputsText.Visibility = _outputNames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += RefreshTimer_Tick;
            Loaded += ManualIoDebugView_Loaded;
            Unloaded += ManualIoDebugView_Unloaded;
        }

        /// <summary>按已映射名称建立只读 DI 状态行。</summary>
        private void BuildInputRows()
        {
            foreach (string inputName in _inputNames)
            {
                var row = CreatePointRow(inputName);
                var indicator = new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = Brushes.Gray,
                    Margin = new Thickness(8, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var stateText = new TextBlock
                {
                    Text = "—",
                    Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(indicator);
                row.Children.Add(stateText);
                DigitalInputsPanel.Children.Add(row);
                _inputStatusTextByName.Add(inputName, stateText);
            }
        }

        /// <summary>按已映射名称建立带开、关按钮的逻辑 DO 行。</summary>
        private void BuildOutputRows()
        {
            foreach (string outputName in _outputNames)
            {
                var row = CreatePointRow(outputName);
                var stateText = new TextBlock
                {
                    Text = "—",
                    Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 6, 0)
                };
                row.Children.Add(stateText);
                Button openButton = CreateOutputButton("输出打开", outputName, true);
                Button closeButton = CreateOutputButton("输出关闭", outputName, false);
                row.Children.Add(openButton);
                row.Children.Add(closeButton);
                DigitalOutputsPanel.Children.Add(row);
                _outputStatusTextByName.Add(outputName, stateText);
                _outputActionButtonsByName.Add(outputName, new List<Button> { openButton, closeButton });
            }
        }

        /// <summary>创建显示逻辑 IO 名称的统一行容器。</summary>
        private static StackPanel CreatePointRow(string pointName)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 3),
                Background = new SolidColorBrush(Color.FromRgb(246, 248, 250))
            };
            row.Children.Add(new TextBlock
            {
                Text = pointName,
                Width = 190,
                Margin = new Thickness(6, 5, 0, 5),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            return row;
        }

        /// <summary>创建写入指定逻辑 DO 值的按钮。</summary>
        private Button CreateOutputButton(string resourceKey, string outputName, bool value)
        {
            var button = new Button
            {
                Tag = new OutputCommand(outputName, value),
                IsEnabled = false,
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(2, 3, 2, 3)
            };
            button.SetResourceReference(ContentControl.ContentProperty, resourceKey);
            button.Click += OutputButton_Click;
            return button;
        }

        /// <summary>IO 页面显示时启动刷新，并立即读取一次当前 DI/DO 状态。</summary>
        private async void ManualIoDebugView_Loaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Start();
            await RefreshIoStatusAsync().ConfigureAwait(true);
        }

        /// <summary>离开 IO 页面后停止状态轮询。</summary>
        private void ManualIoDebugView_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
        }

        /// <summary>定时读取逻辑 DI 和 DO 状态。</summary>
        private async void RefreshTimer_Tick(object sender, EventArgs e)
        {
            if (_refreshInProgress)
            {
                return;
            }

            _refreshInProgress = true;
            try
            {
                await RefreshIoStatusAsync().ConfigureAwait(true);
            }
            finally
            {
                _refreshInProgress = false;
            }
        }

        /// <summary>读取已映射的 DI 和 DO；单点读取失败时把错误显示在对应点位上。</summary>
        private async Task RefreshIoStatusAsync()
        {
            ManualOperationBlockReason blockReason;
            try
            {
                blockReason = await _machineRuntime
                    .GetManualOperationBlockReasonAsync(CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception)
            {
                // 联锁状态读取异常时按禁止手动写入处理。
                blockReason = ManualOperationBlockReason.EmergencyStopStatusUnavailable;
            }

            UpdateOutputButtonAvailability(blockReason);
            MachineStateText.Text = GetText("整机状态") + GetText("状态分隔符")
                + GetMachineStateText(_machineRuntime.State);

            if (_machineRuntime.State == MachineRunState.Disconnected)
            {
                SetAllIoDisconnected();
                return;
            }

            foreach (string inputName in _inputNames)
            {
                try
                {
                    bool active = await _logicalIo.ReadInputAsync(inputName, CancellationToken.None).ConfigureAwait(true);
                    TextBlock stateText = _inputStatusTextByName[inputName];
                    stateText.Text = GetText(active ? "有效" : "无效");
                    stateText.Foreground = active ? Brushes.ForestGreen : Brushes.Gray;
                }
                catch (Exception error)
                {
                    _inputStatusTextByName[inputName].Text = string.Format(GetText("IO读取失败"), error.Message);
                    _inputStatusTextByName[inputName].Foreground = Brushes.Firebrick;
                }
            }

            foreach (string outputName in _outputNames)
            {
                try
                {
                    bool active = await _logicalIo.ReadOutputAsync(outputName, CancellationToken.None).ConfigureAwait(true);
                    TextBlock stateText = _outputStatusTextByName[outputName];
                    stateText.Text = GetText(active ? "输出已开" : "输出已关");
                    stateText.Foreground = active ? Brushes.ForestGreen : Brushes.Gray;
                }
                catch (Exception error)
                {
                    _outputStatusTextByName[outputName].Text = string.Format(GetText("IO读取失败"), error.Message);
                    _outputStatusTextByName[outputName].Foreground = Brushes.Firebrick;
                }
            }
        }

        /// <summary>按当前 Runtime 联锁结果设置所有 DO 按钮，并为禁用状态提供原因提示。</summary>
        private void UpdateOutputButtonAvailability(ManualOperationBlockReason blockReason)
        {
            bool canWriteOutput = blockReason == ManualOperationBlockReason.None;
            string toolTip = canWriteOutput
                ? null
                : GetManualOperationBlockText(blockReason);

            foreach (List<Button> buttons in _outputActionButtonsByName.Values)
            {
                foreach (Button button in buttons)
                {
                    button.IsEnabled = canWriteOutput;
                    button.ToolTip = toolTip;
                    button.SetValue(ToolTipService.ShowOnDisabledProperty, true);
                }
            }
        }

        /// <summary>控制卡断开时清楚标记 IO 状态不可读取。</summary>
        private void SetAllIoDisconnected()
        {
            foreach (TextBlock stateText in _inputStatusTextByName.Values)
            {
                stateText.Text = GetText("控制卡未连接");
                stateText.Foreground = Brushes.Gray;
            }

            foreach (TextBlock stateText in _outputStatusTextByName.Values)
            {
                stateText.Text = GetText("控制卡未连接");
                stateText.Foreground = Brushes.Gray;
            }
        }

        /// <summary>通过 Runtime 实时检查手动联锁后写入 DO，并立即读回确认结果。</summary>
        private async void OutputButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            OutputCommand command = button == null ? null : button.Tag as OutputCommand;
            if (command == null)
            {
                OperationMessageText.Text = GetText("无法读取当前DO操作参数");
                return;
            }

            OperationMessageText.Text = string.Empty;
            try
            {
                bool actualValue = false;
                ManualOperationBlockReason blockReason = await _machineRuntime.RunManualOperationAsync(async () =>
                {
                    await _logicalIo.WriteOutputAsync(command.OutputName, command.Value, CancellationToken.None)
                        .ConfigureAwait(true);
                    actualValue = await _logicalIo.ReadOutputAsync(command.OutputName, CancellationToken.None)
                        .ConfigureAwait(true);
                    if (actualValue != command.Value)
                    {
                        throw new InvalidOperationException(GetText("DO写入后读回不一致"));
                    }
                }, CancellationToken.None).ConfigureAwait(true);
                if (blockReason != ManualOperationBlockReason.None)
                {
                    UpdateOutputButtonAvailability(blockReason);
                    throw new InvalidOperationException(GetManualOperationBlockText(blockReason));
                }

                TextBlock stateText = _outputStatusTextByName[command.OutputName];
                stateText.Text = GetText(actualValue ? "输出已开" : "输出已关");
                stateText.Foreground = actualValue ? Brushes.ForestGreen : Brushes.Gray;
                OperationMessageText.Text = string.Format(GetText("操作完成"), GetText("写入DO") + " " + command.OutputName);
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("操作失败"), command.OutputName, error.Message);
            }
        }

        /// <summary>把 Runtime 的手动联锁原因转换为当前语言的界面提示。</summary>
        private string GetManualOperationBlockText(ManualOperationBlockReason reason)
        {
            switch (reason)
            {
                case ManualOperationBlockReason.MotionCardDisconnected:
                    return GetText("运动卡未连接操作被拒绝");
                case ManualOperationBlockReason.AutomaticFlowActive:
                    return GetText("自动流程期间禁止手动操作");
                case ManualOperationBlockReason.EmergencyStopInputActive:
                    return GetText("急停输入有效禁止手动操作");
                case ManualOperationBlockReason.EmergencyStopAlarmNotReset:
                    return GetText("急停报警未复位禁止手动动作");
                case ManualOperationBlockReason.EmergencyStopStatusUnavailable:
                    return GetText("急停状态读取失败禁止手动操作");
                default:
                    return GetText("软件手动条件未通过");
            }
        }

        /// <summary>从页面或 Host 资源中读取当前语言对应的文字。</summary>
        private string GetText(string resourceKey)
        {
            object resourceValue = TryFindResource(resourceKey);
            return resourceValue == null ? resourceKey : resourceValue.ToString();
        }

        /// <summary>按运行时状态显示简单中文或英文状态。</summary>
        private string GetMachineStateText(MachineRunState state)
        {
            switch (state)
            {
                case MachineRunState.Disconnected:
                    return GetText("状态未连接");
                case MachineRunState.ResetRequired:
                    return GetText("状态等待复位");
                case MachineRunState.Ready:
                    return GetText("状态就绪");
                case MachineRunState.Running:
                    return GetText("状态运行中");
                case MachineRunState.Pausing:
                    return GetText("状态暂停中");
                case MachineRunState.Paused:
                    return GetText("状态已暂停");
                case MachineRunState.Alarmed:
                    return GetText("状态报警");
                default:
                    return state.ToString();
            }
        }

        /// <summary>记录一次 DO 写入对应的逻辑名称和值，不依赖物理地址。</summary>
        private sealed class OutputCommand
        {
            /// <summary>创建一个逻辑 DO 写入命令。</summary>
            public OutputCommand(string outputName, bool value)
            {
                OutputName = outputName;
                Value = value;
            }

            /// <summary>设备流程使用的逻辑 DO 名称。</summary>
            public string OutputName { get; private set; }

            /// <summary>要写入的逻辑电平；物理极性由运动卡适配器处理。</summary>
            public bool Value { get; private set; }
        }
    }
}
