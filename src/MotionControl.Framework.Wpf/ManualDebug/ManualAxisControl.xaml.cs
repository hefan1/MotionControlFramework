using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Framework.Wpf.ManualDebug
{
    /// <summary>显示一个已映射轴的状态、手动操作和人工示教位置。</summary>
    public partial class ManualAxisControl : UserControl
    {
        /// <summary>当前卡片对应的逻辑轴。</summary>
        private readonly IAxis _axis;

        /// <summary>当前轴的软限位和手动速度配置。</summary>
        private readonly AxisHardwareDefinition _axisConfiguration;

        /// <summary>用于拒绝自动运行期间手动运动的整机运行状态。</summary>
        private readonly MachineRuntime _machineRuntime;

        /// <summary>当前逻辑轴可编辑的示教位置。</summary>
        private readonly ObservableCollection<ManualAxisPosition> _positions;

        /// <summary>为当前轴创建并登记一个新的稳定位置编号。</summary>
        private readonly Func<ManualAxisPosition> _createPosition;

        /// <summary>把当前设备的轴位置配置保存到 JSON 文件。</summary>
        private readonly Action _savePositions;

        /// <summary>记录已经保存的名称，便于拒绝空名称和重复名称后恢复文本。</summary>
        private readonly Dictionary<string, string> _savedPositionNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前按住点动按钮、需要在松开时停止的轴。</summary>
        private IAxis _joggingAxis;

        /// <summary>当前卡片是否正在等待一项轴手动操作完成，避免同轴命令重叠。</summary>
        private bool _axisActionInProgress;

        /// <summary>创建一个逻辑轴卡片，并接入该轴自己的位置列表。</summary>
        public ManualAxisControl(
            IAxis axis,
            AxisHardwareDefinition axisConfiguration,
            MachineRuntime machineRuntime,
            ObservableCollection<ManualAxisPosition> positions,
            Func<ManualAxisPosition> createPosition,
            Action savePositions)
        {
            if (axis == null)
            {
                throw new ArgumentNullException(nameof(axis));
            }

            if (axisConfiguration == null)
            {
                throw new ArgumentNullException(nameof(axisConfiguration));
            }

            if (machineRuntime == null)
            {
                throw new ArgumentNullException(nameof(machineRuntime));
            }

            if (positions == null)
            {
                throw new ArgumentNullException(nameof(positions));
            }

            if (createPosition == null)
            {
                throw new ArgumentNullException(nameof(createPosition));
            }

            if (savePositions == null)
            {
                throw new ArgumentNullException(nameof(savePositions));
            }

            InitializeComponent();
            _axis = axis;
            _axisConfiguration = axisConfiguration;
            _machineRuntime = machineRuntime;
            _positions = positions;
            _createPosition = createPosition;
            _savePositions = savePositions;

            AxisTitleText.Text = string.Format(
                CultureInfo.CurrentCulture,
                GetText("手动轴卡片标题格式"),
                axisConfiguration.AxisNumber + 1,
                axis.Name);
            AxisHardwareText.Text = string.Format(
                CultureInfo.CurrentCulture,
                GetText("轴卡片物理通道格式"),
                axisConfiguration.ControllerId,
                axisConfiguration.AxisNumber);
            PositionUnitText.Text = string.IsNullOrWhiteSpace(axisConfiguration.EngineeringUnit)
                ? GetText("工程单位")
                : axisConfiguration.EngineeringUnit;
            SpeedUnitText.Text = PositionUnitText.Text + "/s";
            SpeedTextBox.Text = axisConfiguration.ManualSpeed.ToString(CultureInfo.CurrentCulture);
            AccelerationTextBox.Text = axisConfiguration.ManualAcceleration > 0
                ? axisConfiguration.ManualAcceleration.ToString(CultureInfo.CurrentCulture)
                : string.Empty;
            DecelerationTextBox.Text = axisConfiguration.ManualDeceleration > 0
                ? axisConfiguration.ManualDeceleration.ToString(CultureInfo.CurrentCulture)
                : string.Empty;

            PositionsList.ItemsSource = _positions;
            _positions.CollectionChanged += Positions_CollectionChanged;
            foreach (ManualAxisPosition position in _positions)
            {
                _savedPositionNames[position.Id] = position.Name;
            }

            UpdateEmptyPositionsText();
        }

        /// <summary>读取并显示指令位置、轴当前位置、伺服、回零、限位和报警状态。</summary>
        public async Task RefreshStatusAsync()
        {
            if (_machineRuntime.State == MachineRunState.Disconnected)
            {
                CommandPositionText.Text = GetText("控制卡未连接");
                FeedbackPositionText.Text = "—";
                UpdateIndicator(AlarmIndicator, false, false);
                UpdateIndicator(HomedIndicator, false, false);
                UpdateIndicator(PositiveLimitIndicator, false, false);
                UpdateIndicator(NegativeLimitIndicator, false, false);
                UpdateIndicator(OriginIndicator, false, false);
                HomedStatusText.Text = GetText("状态未连接");
                OriginStatusText.ToolTip = GetText("控制卡未连接");
                MotionStateText.Text = GetText("状态未连接");
                ServoStatusText.Text = GetText("轴伺服状态未连接");
                ServoToggleButton.Content = GetText("伺服使能");
                return;
            }

            AxisStatus status = await _axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(true);
            CommandPositionText.Text = status.CommandPosition.HasValue
                ? status.CommandPosition.Value.ToString("F3", CultureInfo.CurrentCulture)
                : "—";
            FeedbackPositionText.Text = status.Position.ToString("F3", CultureInfo.CurrentCulture);
            UpdateIndicator(AlarmIndicator, status.AlarmActive, status.AlarmActive);
            UpdateIndicator(HomedIndicator, status.Homed, false);
            HomedStatusText.Text = GetText(status.Homed ? "已回零" : "未回零");
            UpdateIndicator(PositiveLimitIndicator, status.PositiveLimitActive, status.PositiveLimitActive);
            UpdateIndicator(NegativeLimitIndicator, status.NegativeLimitActive, status.NegativeLimitActive);
            UpdateIndicator(OriginIndicator, status.OriginInputActive == true, false);
            OriginStatusText.ToolTip = GetText(status.OriginInputActive.HasValue ? "原点输入状态" : "原点输入未配置");
            MotionStateText.Text = GetText(status.Moving ? "运动中" : "已停止");
            ServoStatusText.Text = GetText(status.ServoEnabled ? "轴伺服状态已使能" : "轴伺服状态未使能");
            ServoToggleButton.Content = GetText(status.ServoEnabled ? "伺服关闭" : "伺服使能");
            AlarmMessageText.Text = status.AlarmActive
                ? (string.IsNullOrWhiteSpace(status.AlarmMessage) ? GetText("轴报警有效") : status.AlarmMessage)
                : status.PositiveSoftLimitActive || status.NegativeSoftLimitActive
                    ? GetText("软件限位当前有效")
                    : string.Empty;
        }

        /// <summary>显示当前轴状态读取失败的具体原因，不影响其他轴卡片刷新。</summary>
        public void ShowStatusReadError(Exception error)
        {
            if (error == null)
            {
                throw new ArgumentNullException(nameof(error));
            }

            CommandPositionText.Text = "—";
            FeedbackPositionText.Text = "—";
            HomedStatusText.Text = "—";
            AlarmMessageText.Text = string.Format(GetText("读取轴状态失败"), error.Message);
        }

        /// <summary>按当前状态设置指示灯颜色，并给有效报警或限位使用醒目颜色。</summary>
        private static void UpdateIndicator(Ellipse indicator, bool active, bool warning)
        {
            indicator.Fill = warning
                ? Brushes.Firebrick
                : active ? Brushes.ForestGreen : Brushes.DarkGray;
        }

        /// <summary>页面离开时停止此卡片可能仍在运行的按住式点动。</summary>
        public async Task StopJogIfActiveAsync()
        {
            await StopJogAsync().ConfigureAwait(true);
        }

        /// <summary>位置列表发生增删时显示或隐藏空列表提示。</summary>
        private void Positions_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateEmptyPositionsText();
        }

        /// <summary>根据当前轴是否已有人工添加的位置显示提示文字。</summary>
        private void UpdateEmptyPositionsText()
        {
            EmptyPositionsText.Visibility = _positions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>添加一个空目标位置；调试人员可立即修改自动生成的名称。</summary>
        private void AddPositionButton_Click(object sender, RoutedEventArgs e)
        {
            ManualAxisPosition position = null;
            try
            {
                position = _createPosition();
                _savedPositionNames[position.Id] = position.Name;
                _savePositions();
                OperationMessageText.Text = string.Format(GetText("位置已添加"), position.Name);
            }
            catch (Exception error)
            {
                if (position != null)
                {
                    _positions.Remove(position);
                    _savedPositionNames.Remove(position.Id);
                }

                OperationMessageText.Text = string.Format(GetText("位置配置保存失败"), error.Message);
            }
        }

        /// <summary>位置名称编辑结束时校验名称并保存当前设备的位置配置。</summary>
        private void PositionNameTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            TextBox nameTextBox = sender as TextBox;
            ManualAxisPosition position = nameTextBox == null ? null : nameTextBox.DataContext as ManualAxisPosition;
            if (nameTextBox == null || position == null)
            {
                return;
            }

            BindingExpression binding = nameTextBox.GetBindingExpression(TextBox.TextProperty);
            if (binding != null)
            {
                binding.UpdateSource();
            }

            string newName = nameTextBox.Text.Trim();
            bool duplicateName = _positions.Any(item => !ReferenceEquals(item, position)
                && string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(newName) || duplicateName)
            {
                position.Name = _savedPositionNames[position.Id];
                nameTextBox.Text = position.Name;
                OperationMessageText.Text = GetText(
                    string.IsNullOrWhiteSpace(newName) ? "位置名称不能为空" : "同一轴的位置名称不能重复");
                return;
            }

            string savedName = _savedPositionNames[position.Id];
            position.Name = newName;
            try
            {
                _savePositions();
                _savedPositionNames[position.Id] = newName;
                OperationMessageText.Text = string.Empty;
            }
            catch (Exception error)
            {
                position.Name = savedName;
                nameTextBox.Text = savedName;
                OperationMessageText.Text = string.Format(GetText("位置配置保存失败"), error.Message);
            }
        }

        /// <summary>位置坐标编辑结束时校验数值、软限位并保存到设备配置。</summary>
        private void PositionValueTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            SavePositionValue(sender as TextBox);
        }

        /// <summary>按回车立即保存位置坐标，方便调试人员连续录入。</summary>
        private void PositionValueTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            SavePositionValue(sender as TextBox);
        }

        /// <summary>保存手动输入的轴坐标；留空会清除坐标，不能定位到空位置。</summary>
        private void SavePositionValue(TextBox positionTextBox)
        {
            ManualAxisPosition position = positionTextBox == null
                ? null
                : positionTextBox.DataContext as ManualAxisPosition;
            if (positionTextBox == null || position == null)
            {
                return;
            }

            double? updatedPosition = null;
            if (!string.IsNullOrWhiteSpace(positionTextBox.Text))
            {
                try
                {
                    updatedPosition = ReadNumber(positionTextBox, GetText("目标位置"));
                    EnsurePositionWithinLimits(updatedPosition.Value);
                }
                catch (Exception error)
                {
                    positionTextBox.Text = FormatPosition(position.Position);
                    OperationMessageText.Text = error.Message;
                    return;
                }
            }

            double? previousPosition = position.Position;
            if (previousPosition == updatedPosition)
            {
                positionTextBox.Text = FormatPosition(previousPosition);
                return;
            }

            position.Position = updatedPosition;
            try
            {
                _savePositions();
                positionTextBox.Text = FormatPosition(updatedPosition);
                OperationMessageText.Text = string.Empty;
            }
            catch (Exception error)
            {
                position.Position = previousPosition;
                positionTextBox.Text = FormatPosition(previousPosition);
                OperationMessageText.Text = string.Format(GetText("位置配置保存失败"), error.Message);
            }
        }

        /// <summary>用可往返解析的当前区域格式显示坐标，避免显示时舍入后误改配置。</summary>
        private static string FormatPosition(double? position)
        {
            return position.HasValue
                ? position.Value.ToString("R", CultureInfo.CurrentCulture)
                : string.Empty;
        }

        /// <summary>读取当前轴位置并保存到指定位置行。</summary>
        private async void TeachPositionButton_Click(object sender, RoutedEventArgs e)
        {
            ManualAxisPosition position = GetPositionFromButton(sender);
            if (position == null)
            {
                OperationMessageText.Text = GetText("无法读取示教位置");
                return;
            }

            await RunAxisActionAsync(GetText("写入当前位置"), async () =>
            {
                EnsurePositionNameIsValid(position);
                AxisStatus status = await _axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(true);
                double? previousPosition = position.Position;
                position.Position = status.Position;
                try
                {
                    _savePositions();
                }
                catch
                {
                    position.Position = previousPosition;
                    throw;
                }

                OperationMessageText.Text = string.Format(
                    GetText("当前位置已写入位置"),
                    position.Name,
                    status.Position.ToString("F3", CultureInfo.CurrentCulture));
            }).ConfigureAwait(true);
        }

        /// <summary>按指定行保存的工程位置执行软限位检查和绝对定位。</summary>
        private async void MoveToPositionButton_Click(object sender, RoutedEventArgs e)
        {
            ManualAxisPosition position = GetPositionFromButton(sender);
            if (position == null)
            {
                OperationMessageText.Text = GetText("无法读取示教位置");
                return;
            }

            await RunAxisActionAsync(GetText("定位到示教位置"), async () =>
            {
                EnsurePositionNameIsValid(position);
                if (!position.Position.HasValue)
                {
                    throw new InvalidOperationException(GetText("目标位置未设置"));
                }

                EnsurePositionWithinLimits(position.Position.Value);
                AxisMotionProfile profile = ReadMotionProfile();
                await _axis.MoveAbsoluteAsync(position.Position.Value, profile, CancellationToken.None)
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);
        }

        /// <summary>删除指定位置并保存位置列表，不会复用该位置的稳定编号。</summary>
        private void DeletePositionButton_Click(object sender, RoutedEventArgs e)
        {
            ManualAxisPosition position = GetPositionFromButton(sender);
            if (position == null)
            {
                OperationMessageText.Text = GetText("无法读取示教位置");
                return;
            }

            int positionIndex = _positions.IndexOf(position);
            _positions.RemoveAt(positionIndex);
            _savedPositionNames.Remove(position.Id);
            try
            {
                _savePositions();
                OperationMessageText.Text = string.Format(GetText("位置已删除"), position.Name);
            }
            catch (Exception error)
            {
                _positions.Insert(positionIndex, position);
                _savedPositionNames[position.Id] = position.Name;
                OperationMessageText.Text = string.Format(GetText("位置配置保存失败"), error.Message);
            }
        }

        /// <summary>按当前伺服状态切换使能或关闭。</summary>
        private async void ServoToggleButton_Click(object sender, RoutedEventArgs e)
        {
            string operationName = ServoToggleButton.Content as string;
            if (string.IsNullOrWhiteSpace(operationName))
            {
                operationName = GetText("伺服状态");
            }

            await RunAxisActionAsync(operationName, async () =>
            {
                AxisStatus status = await _axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(true);
                if (status.ServoEnabled)
                {
                    await _axis.DisableServoAsync(CancellationToken.None).ConfigureAwait(true);
                }
                else
                {
                    await _axis.EnableServoAsync(CancellationToken.None).ConfigureAwait(true);
                }
            }).ConfigureAwait(true);
        }

        /// <summary>使用当前轴映射中保存的回零参数启动回零。</summary>
        private async void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            await RunAxisActionAsync(GetText("回零"), () => _axis.HomeAsync(CancellationToken.None))
                .ConfigureAwait(true);
        }

        /// <summary>清除当前轴的驱动报警。</summary>
        private async void ResetAxisAlarmButton_Click(object sender, RoutedEventArgs e)
        {
            await RunAxisActionAsync(GetText("轴报警复位"), () => _axis.ResetAlarmAsync(CancellationToken.None))
                .ConfigureAwait(true);
        }

        /// <summary>按受控停止方式停止当前轴。</summary>
        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            await RunAxisStopActionAsync(
                GetText("受控停止"),
                () => _axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None)).ConfigureAwait(true);
        }

        /// <summary>校验目标位置和运动曲线后执行绝对定位。</summary>
        private async void MoveAbsoluteButton_Click(object sender, RoutedEventArgs e)
        {
            await RunAxisActionAsync(GetText("定位"), async () =>
            {
                double targetPosition = ReadNumber(TargetPositionTextBox, GetText("定位位置"));
                EnsurePositionWithinLimits(targetPosition);
                AxisMotionProfile profile = ReadMotionProfile();
                await _axis.MoveAbsoluteAsync(targetPosition, profile, CancellationToken.None).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }

        /// <summary>切换按住连续点动和按一次走固定寸动步长。</summary>
        private void JogModeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            JogModeToggleButton.Content = GetText(JogModeToggleButton.IsChecked == true ? "连续点动" : "寸动模式");
        }

        /// <summary>按下负向点动按钮时，按连续或寸动模式执行负向动作。</summary>
        private async void NegativeJogButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            await BeginJogAsync(MotionDirection.Negative, sender as Button).ConfigureAwait(true);
        }

        /// <summary>按下正向点动按钮时，按连续或寸动模式执行正向动作。</summary>
        private async void PositiveJogButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            await BeginJogAsync(MotionDirection.Positive, sender as Button).ConfigureAwait(true);
        }

        /// <summary>点动按钮松开时停止当前轴。</summary>
        private async void JogButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            await StopJogAsync().ConfigureAwait(true);
        }

        /// <summary>点动按钮失去鼠标捕获时停止当前轴，避免轴继续运行。</summary>
        private async void JogButton_LostMouseCapture(object sender, MouseEventArgs e)
        {
            await StopJogAsync().ConfigureAwait(true);
        }

        /// <summary>连续模式启动按住式点动；寸动模式按当前步长执行一次相对定位。</summary>
        private async Task BeginJogAsync(MotionDirection direction, Button jogButton)
        {
            bool continuousJog = JogModeToggleButton.IsChecked == true;
            await RunAxisActionAsync(GetText(continuousJog ? "连续点动" : "寸动模式"), async () =>
            {
                if (jogButton == null)
                {
                    throw new InvalidOperationException(GetText("当前点动按钮不存在"));
                }

                if (_joggingAxis != null)
                {
                    bool previousJogStopped = await StopJogAsync().ConfigureAwait(true);
                    if (!previousJogStopped)
                    {
                        throw new InvalidOperationException(GetText("当前点动停止失败禁止继续点动"));
                    }
                }

                if (continuousJog)
                {
                    double jogSpeed = ReadNumber(SpeedTextBox, GetText("手动调试速度"));
                    await _axis.StartJogAsync(direction, jogSpeed, CancellationToken.None).ConfigureAwait(true);
                    _joggingAxis = _axis;
                    Mouse.Capture(jogButton);
                    return;
                }

                double stepDistance = ReadNumber(JogStepTextBox, GetText("寸动步长"));
                if (stepDistance <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(stepDistance), GetText("寸动步长必须大于零"));
                }

                AxisStatus status = await _axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(true);
                double currentPosition = status.CommandPosition ?? status.Position;
                double distance = direction == MotionDirection.Positive ? stepDistance : -stepDistance;
                EnsurePositionWithinLimits(currentPosition + distance);
                AxisMotionProfile profile = ReadMotionProfile();
                await _axis.MoveRelativeAsync(distance, profile, CancellationToken.None).ConfigureAwait(true);
            }, true).ConfigureAwait(true);
        }

        /// <summary>显示本软件能核对的连接、伺服、运动、报警和限位条件，不替代硬件安全检查。</summary>
        private async void SafetyCheckButton_Click(object sender, RoutedEventArgs e)
        {
            var issues = new List<string>();
            MachineRunState machineState = _machineRuntime.State;
            if (machineState == MachineRunState.Disconnected)
            {
                issues.Add(GetText("运动卡未连接操作被拒绝"));
            }
            else
            {
                ManualOperationBlockReason blockReason = await _machineRuntime
                    .GetManualOperationBlockReasonAsync(CancellationToken.None).ConfigureAwait(true);
                if (blockReason != ManualOperationBlockReason.None)
                {
                    issues.Add(GetManualOperationBlockText(blockReason));
                }

                try
                {
                    AxisStatus status = await _axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(true);
                    if (status.AlarmActive)
                    {
                        issues.Add(GetText("轴报警有效"));
                    }

                    if (!status.ServoEnabled)
                    {
                        issues.Add(GetText("伺服未使能"));
                    }

                    if (status.Moving)
                    {
                        issues.Add(GetText("轴仍在运动"));
                    }

                    if (status.PositiveLimitActive || status.NegativeLimitActive)
                    {
                        issues.Add(GetText("硬限位有效"));
                    }
                }
                catch (Exception error)
                {
                    issues.Add(string.Format(GetText("读取轴状态失败"), error.Message));
                }
            }

            OperationMessageText.Text = issues.Count == 0
                ? GetText("软件手动条件通过")
                : GetText("软件手动条件未通过") + string.Join("；", issues);
        }

        /// <summary>若当前卡片有活动点动，则受控停止并在停止成功后清除活动轴引用。</summary>
        private async Task<bool> StopJogAsync()
        {
            IAxis joggingAxis = _joggingAxis;
            if (joggingAxis == null)
            {
                return true;
            }

            try
            {
                await RunAxisStopOperationAsync(joggingAxis, CancellationToken.None).ConfigureAwait(true);
                if (ReferenceEquals(_joggingAxis, joggingAxis))
                {
                    _joggingAxis = null;
                }

                return true;
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("停止点动失败"), error.Message);
                return false;
            }
        }

        /// <summary>检查联锁并等待轴手动命令完成，避免同一轴上的普通操作重叠。</summary>
        private async Task RunAxisActionAsync(
            string operationName,
            Func<Task> operation,
            bool allowExistingJog = false)
        {
            if (_axisActionInProgress)
            {
                OperationMessageText.Text = GetText("当前轴操作正在执行");
                return;
            }

            if (_joggingAxis != null && !allowExistingJog)
            {
                OperationMessageText.Text = GetText("请先停止当前连续点动");
                return;
            }

            _axisActionInProgress = true;
            OperationMessageText.Text = string.Empty;
            try
            {
                ManualOperationBlockReason blockReason = await _machineRuntime
                    .RunManualOperationAsync(operation, CancellationToken.None).ConfigureAwait(true);
                if (blockReason != ManualOperationBlockReason.None)
                {
                    throw new InvalidOperationException(GetManualOperationBlockText(blockReason));
                }

                if (string.IsNullOrWhiteSpace(OperationMessageText.Text))
                {
                    OperationMessageText.Text = string.Format(GetText("操作完成"), operationName);
                }

                await RefreshStatusAsync().ConfigureAwait(true);
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("操作失败"), operationName, error.Message);
            }
            finally
            {
                _axisActionInProgress = false;
            }
        }

        /// <summary>通过 Runtime 的停止专用入口执行手动受控停止，并显示操作结果。</summary>
        private async Task RunAxisStopActionAsync(string operationName, Func<Task> operation)
        {
            OperationMessageText.Text = string.Empty;
            try
            {
                ManualOperationBlockReason blockReason = await _machineRuntime
                    .RunManualStopAsync(operation, CancellationToken.None).ConfigureAwait(true);
                if (blockReason != ManualOperationBlockReason.None)
                {
                    throw new InvalidOperationException(GetManualOperationBlockText(blockReason));
                }

                OperationMessageText.Text = string.Format(GetText("操作完成"), operationName);
                await RefreshStatusAsync().ConfigureAwait(true);
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("操作失败"), operationName, error.Message);
            }
        }

        /// <summary>通过 Runtime 的停止专用入口停止指定轴，急停有效时仍允许停止。</summary>
        private async Task RunAxisStopOperationAsync(IAxis axis, CancellationToken cancellationToken)
        {
            ManualOperationBlockReason blockReason = await _machineRuntime.RunManualStopAsync(
                () => axis.StopAsync(AxisStopMode.Controlled, cancellationToken),
                cancellationToken).ConfigureAwait(true);
            if (blockReason != ManualOperationBlockReason.None)
            {
                throw new InvalidOperationException(GetManualOperationBlockText(blockReason));
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

        /// <summary>读取轴手动速度、加速度和减速度并创建运动曲线。</summary>
        private AxisMotionProfile ReadMotionProfile()
        {
            return new AxisMotionProfile(
                ReadNumber(SpeedTextBox, GetText("速度")),
                ReadNumber(AccelerationTextBox, GetText("加速度")),
                ReadNumber(DecelerationTextBox, GetText("减速度")));
        }

        /// <summary>读取指定文本框中的有限数字，支持当前区域格式和小数点格式。</summary>
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

        /// <summary>在软限位已启用时拒绝超出轴配置范围的目标位置。</summary>
        private void EnsurePositionWithinLimits(double targetPosition)
        {
            if (_axisConfiguration.SoftLimitEnabled != false
                && (targetPosition < _axisConfiguration.MinimumPosition
                    || targetPosition > _axisConfiguration.MaximumPosition))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetPosition),
                    string.Format(
                        GetText("目标位置超出软限位范围"),
                        _axisConfiguration.MinimumPosition,
                        _axisConfiguration.MaximumPosition));
            }
        }

        /// <summary>从位置行按钮读取对应的示教位置数据。</summary>
        private ManualAxisPosition GetPositionFromButton(object sender)
        {
            Button button = sender as Button;
            return button == null ? null : button.DataContext as ManualAxisPosition;
        }

        /// <summary>检查位置名称非空且不与当前轴的其他位置重名。</summary>
        private void EnsurePositionNameIsValid(ManualAxisPosition position)
        {
            if (string.IsNullOrWhiteSpace(position.Name))
            {
                throw new InvalidOperationException(GetText("位置名称不能为空"));
            }

            if (_positions.Any(item => !ReferenceEquals(item, position)
                && string.Equals(item.Name, position.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(GetText("同一轴的位置名称不能重复"));
            }
        }

        /// <summary>从当前卡片或 Host 语言资源中读取界面文字。</summary>
        private string GetText(string resourceKey)
        {
            object resourceValue = TryFindResource(resourceKey);
            return resourceValue == null ? resourceKey : resourceValue.ToString();
        }

        /// <summary>根据当前语言返回轴状态中布尔值对应的界面文字。</summary>
        private string GetBooleanText(bool value, string trueResourceKey, string falseResourceKey)
        {
            return GetText(value ? trueResourceKey : falseResourceKey);
        }
    }
}
