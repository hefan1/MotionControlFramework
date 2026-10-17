using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Framework.Wpf.ManualDebug
{
    /// <summary>按通用轴和逻辑 IO 契约提供单轴及数字 IO 手动调试页面。</summary>
    public partial class ManualMotionDebugView : UserControl
    {
        private readonly DispatcherTimer _refreshTimer;
        private readonly Dictionary<string, TextBlock> _inputStatusTextByName =
            new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextBlock> _outputStatusTextByName =
            new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AxisHardwareDefinition> _axisConfigurationByName =
            new Dictionary<string, AxisHardwareDefinition>(StringComparer.OrdinalIgnoreCase);

        private readonly MachineRuntime _machineRuntime;
        private readonly MotionCardGroup _motionCards;
        private readonly ILogicalIo _logicalIo;
        private IReadOnlyList<string> _inputNames = new string[0];
        private IReadOnlyList<string> _outputNames = new string[0];
        private IAxis _joggingAxis;
        private bool _refreshInProgress;

        /// <summary>创建通用手动调试页面，并保存本设备 Runtime、轴、IO 与限位配置。</summary>
        public ManualMotionDebugView(
            MachineRuntime machineRuntime,
            MotionCardGroup motionCards,
            ILogicalIo logicalIo,
            IEnumerable<AxisHardwareDefinition> axisConfigurations,
            IEnumerable<string> inputNames,
            IEnumerable<string> outputNames)
        {
            InitializeComponent();

            if (machineRuntime == null)
            {
                throw new ArgumentNullException(nameof(machineRuntime));
            }

            if (motionCards == null)
            {
                throw new ArgumentNullException(nameof(motionCards));
            }

            if (logicalIo == null)
            {
                throw new ArgumentNullException(nameof(logicalIo));
            }

            if (axisConfigurations == null)
            {
                throw new ArgumentNullException(nameof(axisConfigurations));
            }

            if (inputNames == null)
            {
                throw new ArgumentNullException(nameof(inputNames));
            }

            if (outputNames == null)
            {
                throw new ArgumentNullException(nameof(outputNames));
            }

            _machineRuntime = machineRuntime;
            _motionCards = motionCards;
            _logicalIo = logicalIo;
            _inputNames = inputNames.ToList().AsReadOnly();
            _outputNames = outputNames.ToList().AsReadOnly();
            LoadAxisConfigurations(axisConfigurations);
            BuildInputRows();
            BuildOutputRows();

            AxisSelector.ItemsSource = _motionCards.Axes.OrderBy(axis => axis.Name).ToList();
            AxisSelector.DisplayMemberPath = "Name";
            AxisSelector.SelectedIndex = _motionCards.Axes.Count == 0 ? -1 : 0;

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
            Loaded += ManualMotionDebugView_Loaded;
            Unloaded += ManualMotionDebugView_Unloaded;
        }

        /// <summary>把各轴软限位配置按逻辑名称保存，供手动定位前检查目标范围。</summary>
        private void LoadAxisConfigurations(IEnumerable<AxisHardwareDefinition> axisConfigurations)
        {
            foreach (var axisConfiguration in axisConfigurations)
            {
                if (axisConfiguration == null)
                {
                    throw new ArgumentException("手动调试页的轴配置包含空对象。", nameof(axisConfigurations));
                }

                if (_axisConfigurationByName.ContainsKey(axisConfiguration.Name))
                {
                    throw new ArgumentException("手动调试页的逻辑轴配置重复：" + axisConfiguration.Name + "。", nameof(axisConfigurations));
                }

                _axisConfigurationByName.Add(axisConfiguration.Name, axisConfiguration);
            }

            foreach (var axis in _motionCards.Axes)
            {
                if (!_axisConfigurationByName.ContainsKey(axis.Name))
                {
                    throw new ArgumentException("逻辑轴 '" + axis.Name + "' 缺少手动调试所需的软限位配置。", nameof(axisConfigurations));
                }
            }
        }

        /// <summary>为逻辑 DI 建立一行名称和当前状态显示。</summary>
        private void BuildInputRows()
        {
            foreach (var inputName in _inputNames)
            {
                var row = CreateIoRow(inputName);
                var statusText = new TextBlock
                {
                    Text = "—",
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                row.Children.Add(statusText);
                DigitalInputsPanel.Children.Add(row);
                _inputStatusTextByName.Add(inputName, statusText);
            }
        }

        /// <summary>为逻辑 DO 建立状态显示及明确的打开、关闭按钮。</summary>
        private void BuildOutputRows()
        {
            foreach (var outputName in _outputNames)
            {
                var row = CreateIoRow(outputName);
                var stateText = new TextBlock
                {
                    Text = "—",
                    Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0)
                };
                row.Children.Add(stateText);
                _outputStatusTextByName.Add(outputName, stateText);

                var openButton = CreateOutputButton("输出打开", outputName, true);
                var closeButton = CreateOutputButton("输出关闭", outputName, false);
                row.Children.Add(openButton);
                row.Children.Add(closeButton);
                DigitalOutputsPanel.Children.Add(row);
            }
        }

        /// <summary>创建 IO 行中的逻辑点位名称文字。</summary>
        private static StackPanel CreateIoRow(string pointName)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 3)
            };
            row.Children.Add(new TextBlock
            {
                Text = pointName,
                Width = 190,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            return row;
        }

        /// <summary>创建带逻辑输出名称和值的按钮。</summary>
        private Button CreateOutputButton(string resourceKey, string outputName, bool value)
        {
            var button = new Button
            {
                Tag = new OutputCommand(outputName, value),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(2, 0, 2, 0)
            };
            button.SetResourceReference(ContentControl.ContentProperty, resourceKey);
            button.Click += OutputButton_Click;
            return button;
        }

        /// <summary>页面显示时启动状态刷新定时器并立即读取一次设备状态。</summary>
        private async void ManualMotionDebugView_Loaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Start();
            try
            {
                await RefreshAllStatusAsync().ConfigureAwait(true);
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("读取状态失败"), error.Message);
            }
        }

        /// <summary>页面离开视觉树时停止轮询，并停止可能仍按住的点动轴。</summary>
        private async void ManualMotionDebugView_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            await StopJogAsync().ConfigureAwait(true);
        }

        /// <summary>定时刷新所选轴、DI/DO 和整机状态，并把通信错误显示在页面上。</summary>
        private async void RefreshTimer_Tick(object sender, EventArgs e)
        {
            if (_refreshInProgress)
            {
                return;
            }

            _refreshInProgress = true;
            try
            {
                await RefreshAllStatusAsync().ConfigureAwait(true);
                OperationMessageText.Text = string.Empty;
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("读取状态失败"), error.Message);
            }
            finally
            {
                _refreshInProgress = false;
            }
        }

        /// <summary>更新整机状态、当前轴状态和所有配置 DI/DO 状态。</summary>
        private async Task RefreshAllStatusAsync()
        {
            MachineStateText.Text = GetText("整机状态") + GetText("状态分隔符") + GetMachineStateText(_machineRuntime.State);
            if (_machineRuntime.State == MachineRunState.Disconnected)
            {
                AxisStatusText.Text = GetText("控制卡未连接");
                ShowIoDisconnected();
                return;
            }

            await RefreshSelectedAxisStatusAsync().ConfigureAwait(true);
            await RefreshIoStatusAsync().ConfigureAwait(true);
        }

        /// <summary>控制卡断开时把所有 DI/DO 行标记为未连接。</summary>
        private void ShowIoDisconnected()
        {
            foreach (var statusText in _inputStatusTextByName.Values)
            {
                statusText.Text = GetText("控制卡未连接");
                statusText.Foreground = Brushes.Gray;
            }

            foreach (var statusText in _outputStatusTextByName.Values)
            {
                statusText.Text = GetText("控制卡未连接");
                statusText.Foreground = Brushes.Gray;
            }
        }

        /// <summary>读取当前选中轴的位置、伺服、运动、回零、限位和报警状态。</summary>
        private async Task RefreshSelectedAxisStatusAsync()
        {
            var axis = AxisSelector.SelectedItem as IAxis;
            if (axis == null)
            {
                AxisStatusText.Text = GetText("尚未配置轴");
                return;
            }

            if (_machineRuntime.State == MachineRunState.Disconnected)
            {
                AxisStatusText.Text = GetText("控制卡未连接");
                return;
            }

            var status = await axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(true);
            AxisStatusText.Text = GetText("当前位置") + "：" + status.Position.ToString("F3", CultureInfo.CurrentCulture)
                + "    " + GetText("当前速度") + "：" + status.Velocity.ToString("F3", CultureInfo.CurrentCulture)
                + "    " + GetText("伺服状态") + "：" + GetBooleanText(status.ServoEnabled, "已使能", "未使能")
                + "    " + GetText("运动状态") + "：" + GetBooleanText(status.Moving, "运动中", "已停止")
                + "    " + GetText("回零状态") + "：" + GetBooleanText(status.Homed, "已回零", "未回零")
                + "    " + GetText("正限位") + "：" + GetBooleanText(status.PositiveLimitActive, "有效", "无效")
                + "    " + GetText("负限位") + "：" + GetBooleanText(status.NegativeLimitActive, "有效", "无效")
                + "    " + GetText("报警状态") + "：" + GetBooleanText(status.AlarmActive, "有效", "无效")
                + "    " + GetText("报警信息") + "："
                + (string.IsNullOrWhiteSpace(status.AlarmMessage) ? status.AlarmCode : status.AlarmMessage);
        }

        /// <summary>逐个读取已配置的逻辑 DI/DO 状态并更新对应行文字。</summary>
        private async Task RefreshIoStatusAsync()
        {
            foreach (var inputName in _inputNames)
            {
                var active = await _logicalIo.ReadInputAsync(inputName, CancellationToken.None).ConfigureAwait(true);
                _inputStatusTextByName[inputName].Text = GetBooleanText(active, "有效", "无效");
                _inputStatusTextByName[inputName].Foreground = active ? Brushes.ForestGreen : Brushes.Gray;
            }

            foreach (var outputName in _outputNames)
            {
                var active = await _logicalIo.ReadOutputAsync(outputName, CancellationToken.None).ConfigureAwait(true);
                _outputStatusTextByName[outputName].Text = GetBooleanText(active, "输出已开", "输出已关");
                _outputStatusTextByName[outputName].Foreground = active ? Brushes.ForestGreen : Brushes.Gray;
            }
        }

        /// <summary>切换轴选择后立即刷新新轴的状态。</summary>
        private async void AxisSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _refreshInProgress)
            {
                return;
            }

            try
            {
                await RefreshSelectedAxisStatusAsync().ConfigureAwait(true);
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("读取轴状态失败"), error.Message);
            }
        }

        /// <summary>执行伺服使能操作，并把操作结果显示在页面上。</summary>
        private async void ServoEnableButton_Click(object sender, RoutedEventArgs e)
        {
        await RunSelectedAxisActionAsync(GetText("伺服使能"), (axis, token) => axis.EnableServoAsync(token)).ConfigureAwait(true);
        }

        /// <summary>关闭当前选中轴伺服。</summary>
        private async void ServoDisableButton_Click(object sender, RoutedEventArgs e)
        {
        await RunSelectedAxisActionAsync(GetText("伺服关闭"), (axis, token) => axis.DisableServoAsync(token)).ConfigureAwait(true);
        }

        /// <summary>使用轴配置执行回零。</summary>
        private async void HomeButton_Click(object sender, RoutedEventArgs e)
        {
        await RunSelectedAxisActionAsync(GetText("回零"), (axis, token) => axis.HomeAsync(token)).ConfigureAwait(true);
        }

        /// <summary>清除当前选中轴的驱动报警。</summary>
        private async void ResetAxisAlarmButton_Click(object sender, RoutedEventArgs e)
        {
        await RunSelectedAxisActionAsync(GetText("轴报警复位"), (axis, token) => axis.ResetAlarmAsync(token)).ConfigureAwait(true);
        }

        /// <summary>按受控停止方式停止当前选中轴。</summary>
        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            await RunSelectedAxisActionAsync(
                GetText("受控停止"),
                (axis, token) => axis.StopAsync(AxisStopMode.Controlled, token)).ConfigureAwait(true);
        }

        /// <summary>检查目标位置和速度参数后执行轴绝对定位。</summary>
        private async void MoveAbsoluteButton_Click(object sender, RoutedEventArgs e)
        {
            await RunUiActionAsync(GetText("绝对定位"), async () =>
            {
                var axis = GetSelectedAxis();
                var target = ReadNumber(TargetPositionTextBox, GetText("目标位置"));
                var configuration = _axisConfigurationByName[axis.Name];
                EnsurePositionWithinLimits(configuration, target);
                var profile = ReadMotionProfile();
                await axis.MoveAbsoluteAsync(target, profile, CancellationToken.None).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }

        /// <summary>检查相对移动后的目标位置和速度参数后执行轴相对定位。</summary>
        private async void MoveRelativeButton_Click(object sender, RoutedEventArgs e)
        {
            await RunUiActionAsync(GetText("相对移动"), async () =>
            {
                var axis = GetSelectedAxis();
                var distance = ReadNumber(TargetPositionTextBox, GetText("相对距离"));
                var status = await axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(true);
                var target = status.Position + distance;
                EnsurePositionWithinLimits(_axisConfigurationByName[axis.Name], target);
                var profile = ReadMotionProfile();
                await axis.MoveRelativeAsync(distance, profile, CancellationToken.None).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }

        /// <summary>按住负向点动按钮时启动连续低速点动。</summary>
        private async void NegativeJogButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            await BeginJogAsync(MotionDirection.Negative, sender as Button).ConfigureAwait(true);
        }

        /// <summary>按住正向点动按钮时启动连续低速点动。</summary>
        private async void PositiveJogButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            await BeginJogAsync(MotionDirection.Positive, sender as Button).ConfigureAwait(true);
        }

        /// <summary>点动按钮松开时停止轴运动。</summary>
        private async void JogButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            await StopJogAsync().ConfigureAwait(true);
        }

        /// <summary>点动按钮失去鼠标捕获时停止轴，防止界面离开后轴继续点动。</summary>
        private async void JogButton_LostMouseCapture(object sender, MouseEventArgs e)
        {
            await StopJogAsync().ConfigureAwait(true);
        }

        /// <summary>启动按住式点动并捕获鼠标，确保松开时可以发送停止命令。</summary>
        private async Task BeginJogAsync(MotionDirection direction, Button jogButton)
        {
            await RunUiActionAsync(GetText("点动"), async () =>
            {
                if (jogButton == null)
                {
                    throw new InvalidOperationException(GetText("当前点动按钮不存在"));
                }

                if (_joggingAxis != null)
                {
                    await StopJogAsync().ConfigureAwait(true);
                }

                var axis = GetSelectedAxis();
                var profile = ReadMotionProfile();
                await axis.StartJogAsync(direction, profile.Speed, CancellationToken.None).ConfigureAwait(true);
                _joggingAxis = axis;
                Mouse.Capture(jogButton);
            }).ConfigureAwait(true);
        }

        /// <summary>若页面当前有活动点动轴，则发送受控停止并清除活动轴引用。</summary>
        private async Task StopJogAsync()
        {
            var axis = _joggingAxis;
            if (axis == null)
            {
                return;
            }

            _joggingAxis = null;
            try
            {
                await axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("停止点动失败"), error.Message);
            }
        }

        /// <summary>读取逻辑 DO 当前值并写入按钮指定的打开或关闭状态。</summary>
        private async void OutputButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var command = button == null ? null : button.Tag as OutputCommand;
            if (command == null)
            {
                OperationMessageText.Text = GetText("无法读取当前DO操作参数");
                return;
            }

            await RunUiActionAsync(GetText("写入DO") + " " + command.OutputName, async () =>
            {
                await _logicalIo.WriteOutputAsync(command.OutputName, command.Value, CancellationToken.None)
                    .ConfigureAwait(true);
                var actualValue = await _logicalIo.ReadOutputAsync(command.OutputName, CancellationToken.None)
                    .ConfigureAwait(true);
                if (actualValue != command.Value)
                {
                    throw new InvalidOperationException(GetText("DO写入后读回不一致"));
                }

                _outputStatusTextByName[command.OutputName].Text = actualValue
                    ? GetText("输出已开")
                    : GetText("输出已关");
                _outputStatusTextByName[command.OutputName].Foreground = actualValue ? Brushes.ForestGreen : Brushes.Gray;
            }).ConfigureAwait(true);
        }

        /// <summary>在设备非自动运行状态下执行指定的逻辑轴操作。</summary>
        private async Task RunSelectedAxisActionAsync(
            string operationName,
            Func<IAxis, CancellationToken, Task> operation)
        {
            await RunUiActionAsync(operationName, async () =>
            {
                var axis = GetSelectedAxis();
                await operation(axis, CancellationToken.None).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }

        /// <summary>执行手动命令并将成功或完整异常信息写入页面状态栏。</summary>
        private async Task RunUiActionAsync(string operationName, Func<Task> operation)
        {
            try
            {
                EnsureManualOperationAllowed();
                await operation().ConfigureAwait(true);
                OperationMessageText.Text = string.Format(GetText("操作完成"), operationName);
                await RefreshAllStatusAsync().ConfigureAwait(true);
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("操作失败"), operationName, error.Message);
            }
        }

        /// <summary>确认当前 Runtime 不在自动流程中，避免手动命令与工艺流程争用轴。</summary>
        private void EnsureManualOperationAllowed()
        {
            var state = _machineRuntime.State;
            if (state == MachineRunState.Disconnected)
            {
                throw new InvalidOperationException(GetText("运动卡未连接操作被拒绝"));
            }

            if (state == MachineRunState.Running || state == MachineRunState.Pausing)
            {
                throw new InvalidOperationException(GetText("自动流程期间禁止手动操作"));
            }

            if (_machineRuntime.AlarmService.Alarms.Any(
                alarm => string.Equals(alarm.Definition.Code, "EMERGENCY_STOP", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(GetText("急停报警未复位禁止手动动作"));
            }
        }

        /// <summary>返回当前选中逻辑轴；没有选择时报告可理解的错误。</summary>
        private IAxis GetSelectedAxis()
        {
            var axis = AxisSelector.SelectedItem as IAxis;
            if (axis == null)
            {
                throw new InvalidOperationException(GetText("请先选择一个逻辑轴"));
            }

            return axis;
        }

        /// <summary>读取速度、加速度和减速度，并检查均为有效正数。</summary>
        private AxisMotionProfile ReadMotionProfile()
        {
            return new AxisMotionProfile(
                ReadNumber(SpeedTextBox, GetText("速度")),
                ReadNumber(AccelerationTextBox, GetText("加速度")),
                ReadNumber(DecelerationTextBox, GetText("减速度")));
        }

        /// <summary>按当前系统小数格式或小数点格式读取文本框数值。</summary>
        private double ReadNumber(TextBox textBox, string parameterName)
        {
            double value;
            if (!double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                && !double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw new ArgumentException(string.Format(GetText("参数必须为有效数字"), parameterName));
            }

            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentException(string.Format(GetText("参数不能为无穷或NaN"), parameterName));
            }

            return value;
        }

        /// <summary>拒绝超出轴配置软限位范围的手动定位目标。</summary>
        private void EnsurePositionWithinLimits(AxisHardwareDefinition configuration, double targetPosition)
        {
            if (targetPosition < configuration.MinimumPosition || targetPosition > configuration.MaximumPosition)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetPosition),
                    string.Format(
                        GetText("目标位置超出软限位范围"),
                        configuration.MinimumPosition,
                        configuration.MaximumPosition));
            }
        }

        /// <summary>读取当前语言资源；资源缺失时返回中文键名供开发者定位。</summary>
        private string GetText(string resourceKey)
        {
            var resourceValue = TryFindResource(resourceKey);
            return resourceValue == null ? resourceKey : resourceValue.ToString();
        }

        /// <summary>把整机运行状态转换成当前界面语言。</summary>
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

        /// <summary>根据当前语言返回真或假对应的界面文字。</summary>
        private string GetBooleanText(bool value, string trueResourceKey, string falseResourceKey)
        {
            return GetText(value ? trueResourceKey : falseResourceKey);
        }

        /// <summary>读取按钮中的输出命令对象，保存逻辑名称和要写入的值。</summary>
        private sealed class OutputCommand
        {
            /// <summary>创建一个明确的逻辑 DO 写入命令。</summary>
            public OutputCommand(string outputName, bool value)
            {
                OutputName = outputName;
                Value = value;
            }

            public string OutputName { get; private set; }

            public bool Value { get; private set; }
        }
    }
}
