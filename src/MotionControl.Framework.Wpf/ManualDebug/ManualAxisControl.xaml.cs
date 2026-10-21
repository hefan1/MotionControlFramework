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
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Framework.Wpf.ManualDebug
{
    /// <summary>显示一个已映射轴的状态、手动操作和人工示教位置。</summary>
    public partial class ManualAxisControl : UserControl
    {
        /// <summary>控制点动、回零、定位、伺服使能和 DO 写入等普通动作；默认等待 Runtime 完成联锁检查。</summary>
        public static readonly DependencyProperty CanRunManualOperationsProperty =
            DependencyProperty.Register(
                nameof(CanRunManualOperations),
                typeof(bool),
                typeof(ManualAxisControl),
                new PropertyMetadata(false));

        /// <summary>向操作员说明当前禁用轴动作的联锁原因。</summary>
        public static readonly DependencyProperty ManualOperationBlockTextProperty =
            DependencyProperty.Register(
                nameof(ManualOperationBlockText),
                typeof(string),
                typeof(ManualAxisControl),
                new PropertyMetadata(string.Empty));

        /// <summary>控制关闭伺服或示教当前位置等恢复操作是否可用。</summary>
        public static readonly DependencyProperty CanRunManualRecoveryOperationsProperty =
            DependencyProperty.Register(
                nameof(CanRunManualRecoveryOperations),
                typeof(bool),
                typeof(ManualAxisControl),
                new PropertyMetadata(false));

        /// <summary>按当前伺服状态和联锁条件控制伺服按钮是否可用。</summary>
        public static readonly DependencyProperty CanToggleServoProperty =
            DependencyProperty.Register(
                nameof(CanToggleServo),
                typeof(bool),
                typeof(ManualAxisControl),
                new PropertyMetadata(false));

        /// <summary>控制轴报警复位是否可用；急停输入恢复后可单独复位轴报警。</summary>
        public static readonly DependencyProperty CanResetAxisAlarmProperty =
            DependencyProperty.Register(
                nameof(CanResetAxisAlarm),
                typeof(bool),
                typeof(ManualAxisControl),
                new PropertyMetadata(false));

        /// <summary>说明当前位置示教按钮当前可用的条件或被阻止的原因。</summary>
        public static readonly DependencyProperty RecoveryOperationBlockTextProperty =
            DependencyProperty.Register(
                nameof(RecoveryOperationBlockText),
                typeof(string),
                typeof(ManualAxisControl),
                new PropertyMetadata(string.Empty));

        /// <summary>当前卡片对应的逻辑轴。</summary>
        private readonly IAxis _axis;

        /// <summary>当前轴的软限位和手动速度配置。</summary>
        private readonly AxisHardwareDefinition _axisConfiguration;

        /// <summary>用于拒绝自动运行期间手动运动的整机运行状态。</summary>
        private readonly MachineRuntime _machineRuntime;

        /// <summary>当前逻辑轴可编辑的示教位置。</summary>
        private readonly ObservableCollection<ManualAxisPosition> _positions;

        /// <summary>当前物理轴保存的关联 IO 配置。</summary>
        private readonly ManualAxisIoAssociationGroup _ioAssociationGroup;

        /// <summary>当前轴卡片显示的逻辑 IO 状态行。</summary>
        private readonly ObservableCollection<ManualAxisIoAssociationViewItem> _ioAssociationItems =
            new ObservableCollection<ManualAxisIoAssociationViewItem>();

        /// <summary>当前设备按逻辑名称读取和写入 IO 的服务。</summary>
        private readonly ILogicalIo _logicalIo;

        /// <summary>当前设备已命名的逻辑 DI 点。</summary>
        private readonly IReadOnlyList<DigitalIoHardwareDefinition> _digitalInputs;

        /// <summary>当前设备已命名的逻辑 DO 点。</summary>
        private readonly IReadOnlyList<DigitalIoHardwareDefinition> _digitalOutputs;

        /// <summary>为当前轴创建并登记一个新的稳定位置编号。</summary>
        private readonly Func<ManualAxisPosition> _createPosition;

        /// <summary>把当前设备的轴位置配置保存到 JSON 文件。</summary>
        private readonly Action _savePositions;

        /// <summary>保存当前设备全部轴卡片的关联 IO 配置。</summary>
        private readonly Action _saveIoAssociations;

        /// <summary>关联配置读取失败时禁止编辑，防止覆盖原文件。</summary>
        private readonly bool _canEditIoAssociations;

        /// <summary>记录已经保存的名称，便于拒绝空名称和重复名称后恢复文本。</summary>
        private readonly Dictionary<string, string> _savedPositionNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前按住点动按钮、需要在松开时停止的轴。</summary>
        private IAxis _joggingAxis;

        /// <summary>记录当前按住的点动按钮，覆盖启动命令尚未返回时的按压状态。</summary>
        private Button _pressedJogButton;

        /// <summary>按下或松开点动按钮时递增，用于识别过期的异步启动请求。</summary>
        private long _jogButtonPressVersion;

        /// <summary>当前连续点动捕获鼠标的按钮；停止后释放，保证其它页面控件仍可操作。</summary>
        private Button _joggingButton;

        /// <summary>当前卡片是否正在等待一项轴手动操作完成，避免同轴命令重叠。</summary>
        private bool _axisActionInProgress;

        /// <summary>当前卡片是否正在执行一项手动 DO 写入。</summary>
        private bool _ioOperationInProgress;

        /// <summary>最近一次 Runtime 检查是否允许执行普通手动轴和 DO 操作。</summary>
        private bool _manualOperationAllowed;

        /// <summary>最近一次 Runtime 检查返回的手动操作联锁原因。</summary>
        private ManualOperationBlockReason _manualOperationBlockReason;

        /// <summary>最近一次轴状态读取是否成功。</summary>
        private bool _axisStatusAvailable;

        /// <summary>最近一次读取到的伺服使能状态。</summary>
        private bool _axisServoEnabled;

        /// <summary>最近一次读取到的轴运动状态。</summary>
        private bool _axisMoving;

        /// <summary>创建一个逻辑轴卡片，并接入该轴自己的位置列表。</summary>
        public ManualAxisControl(
            IAxis axis,
            AxisHardwareDefinition axisConfiguration,
            MachineRuntime machineRuntime,
            ObservableCollection<ManualAxisPosition> positions,
            ManualAxisIoAssociationGroup ioAssociationGroup,
            ILogicalIo logicalIo,
            IReadOnlyList<DigitalIoHardwareDefinition> digitalInputs,
            IReadOnlyList<DigitalIoHardwareDefinition> digitalOutputs,
            Func<ManualAxisPosition> createPosition,
            Action savePositions,
            Action saveIoAssociations,
            bool canEditIoAssociations)
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

            if (ioAssociationGroup == null)
            {
                throw new ArgumentNullException(nameof(ioAssociationGroup));
            }

            if (logicalIo == null)
            {
                throw new ArgumentNullException(nameof(logicalIo));
            }

            if (digitalInputs == null)
            {
                throw new ArgumentNullException(nameof(digitalInputs));
            }

            if (digitalOutputs == null)
            {
                throw new ArgumentNullException(nameof(digitalOutputs));
            }

            if (createPosition == null)
            {
                throw new ArgumentNullException(nameof(createPosition));
            }

            if (savePositions == null)
            {
                throw new ArgumentNullException(nameof(savePositions));
            }

            if (saveIoAssociations == null)
            {
                throw new ArgumentNullException(nameof(saveIoAssociations));
            }

            InitializeComponent();
            _axis = axis;
            _axisConfiguration = axisConfiguration;
            _machineRuntime = machineRuntime;
            _positions = positions;
            _ioAssociationGroup = ioAssociationGroup;
            _logicalIo = logicalIo;
            _digitalInputs = digitalInputs;
            _digitalOutputs = digitalOutputs;
            _createPosition = createPosition;
            _savePositions = savePositions;
            _saveIoAssociations = saveIoAssociations;
            _canEditIoAssociations = canEditIoAssociations;

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
            AxisIoAssociationList.ItemsSource = _ioAssociationItems;
            _positions.CollectionChanged += Positions_CollectionChanged;
            foreach (ManualAxisPosition position in _positions)
            {
                _savedPositionNames[position.Id] = position.Name;
            }

            foreach (ManualAxisIoAssociation association in _ioAssociationGroup.Points)
            {
                _ioAssociationItems.Add(new ManualAxisIoAssociationViewItem(association));
            }

            UpdateEmptyAxisIoText();
            UpdateAddAssociatedIoButton();

            UpdateEmptyPositionsText();
        }

        /// <summary>供 XAML 绑定普通轴动作按钮的联锁可用状态。</summary>
        public bool CanRunManualOperations
        {
            get { return (bool)GetValue(CanRunManualOperationsProperty); }
            private set { SetValue(CanRunManualOperationsProperty, value); }
        }

        /// <summary>供禁用按钮提示当前手动联锁未通过的原因。</summary>
        public string ManualOperationBlockText
        {
            get { return (string)GetValue(ManualOperationBlockTextProperty); }
            private set { SetValue(ManualOperationBlockTextProperty, value); }
        }

        /// <summary>指示控制卡已连接、自动流程停止且轴状态可读时可执行恢复或示教操作。</summary>
        public bool CanRunManualRecoveryOperations
        {
            get { return (bool)GetValue(CanRunManualRecoveryOperationsProperty); }
            private set { SetValue(CanRunManualRecoveryOperationsProperty, value); }
        }

        /// <summary>伺服已使能时允许安全关闭；伺服未使能时仅在普通手动联锁通过后允许使能。</summary>
        public bool CanToggleServo
        {
            get { return (bool)GetValue(CanToggleServoProperty); }
            private set { SetValue(CanToggleServoProperty, value); }
        }

        /// <summary>指示轴停止且急停输入未有效、未发生状态读取故障时可复位轴报警。</summary>
        public bool CanResetAxisAlarm
        {
            get { return (bool)GetValue(CanResetAxisAlarmProperty); }
            private set { SetValue(CanResetAxisAlarmProperty, value); }
        }

        /// <summary>为示教位置按钮显示可用提示或当前阻止原因。</summary>
        public string RecoveryOperationBlockText
        {
            get { return (string)GetValue(RecoveryOperationBlockTextProperty); }
            private set { SetValue(RecoveryOperationBlockTextProperty, value); }
        }

        /// <summary>按整机 Runtime 返回的联锁结果更新轴卡动作按钮；安全检查和允许的停止入口保持可用。</summary>
        public void SetManualOperationAvailability(ManualOperationBlockReason blockReason)
        {
            _manualOperationBlockReason = blockReason;
            _manualOperationAllowed = blockReason == ManualOperationBlockReason.None;
            UpdateAxisActionAvailability();

            PositiveJogButton.IsEnabled = CanRunManualOperations;
            NegativeJogButton.IsEnabled = CanRunManualOperations;
            HomeButton.IsEnabled = CanRunManualOperations;
            MoveAbsoluteButton.IsEnabled = CanRunManualOperations;
            StopButton.IsEnabled = blockReason != ManualOperationBlockReason.MotionCardDisconnected
                && blockReason != ManualOperationBlockReason.AutomaticFlowActive;

            PositiveJogButton.ToolTip = ManualOperationBlockText;
            NegativeJogButton.ToolTip = ManualOperationBlockText;
            HomeButton.ToolTip = ManualOperationBlockText;
            MoveAbsoluteButton.ToolTip = ManualOperationBlockText;
            StopButton.ToolTip = StopButton.IsEnabled ? null : ManualOperationBlockText;

            foreach (ManualAxisIoAssociationViewItem viewItem in _ioAssociationItems)
            {
                if (!viewItem.IsOutput)
                {
                    continue;
                }

                bool isMapped = IsIoAssociationMapped(viewItem.Association);
                bool canWriteOutput = _manualOperationAllowed && isMapped && !_ioOperationInProgress;
                string stateToolTip = _manualOperationAllowed
                    ? viewItem.StateToolTip
                    : ManualOperationBlockText;
                viewItem.SetStatus(
                    viewItem.StateText,
                    viewItem.StateBackground,
                    canWriteOutput,
                    stateToolTip);
            }
        }

        /// <summary>按轴状态和整机联锁分别更新普通运动、伺服恢复、位置示教和轴报警复位入口。</summary>
        private void UpdateAxisActionAvailability()
        {
            bool controllerAvailableForRecovery = _manualOperationBlockReason != ManualOperationBlockReason.MotionCardDisconnected
                && _manualOperationBlockReason != ManualOperationBlockReason.AutomaticFlowActive;
            bool axisStoppedAndReadable = _axisStatusAvailable && !_axisMoving;

            CanRunManualOperations = _manualOperationAllowed && _axisStatusAvailable;
            ManualOperationBlockText = !_manualOperationAllowed
                ? GetManualOperationBlockText(_manualOperationBlockReason)
                : !_axisStatusAvailable
                    ? GetText("轴状态读取失败禁止手动操作")
                    : string.Empty;
            CanRunManualRecoveryOperations = controllerAvailableForRecovery && axisStoppedAndReadable;
            CanToggleServo = axisStoppedAndReadable
                && (_axisServoEnabled
                    ? controllerAvailableForRecovery
                    : _manualOperationAllowed);
            CanResetAxisAlarm = axisStoppedAndReadable
                && (_manualOperationAllowed
                    || _manualOperationBlockReason == ManualOperationBlockReason.EmergencyStopAlarmNotReset);

            if (!_axisStatusAvailable)
            {
                RecoveryOperationBlockText = GetText("轴状态不可读禁止恢复操作");
            }
            else if (_axisMoving)
            {
                RecoveryOperationBlockText = GetText("轴运动中禁止恢复操作");
            }
            else if (!controllerAvailableForRecovery)
            {
                RecoveryOperationBlockText = GetManualOperationBlockText(_manualOperationBlockReason);
            }
            else
            {
                RecoveryOperationBlockText = GetText("写入当前位置");
            }

            ServoToggleButton.ToolTip = !CanToggleServo
                ? (!_axisStatusAvailable
                    ? GetText("轴状态不可读禁止恢复操作")
                    : _axisMoving
                        ? GetText("轴运动中禁止切换伺服")
                        : ManualOperationBlockText)
                : _axisServoEnabled
                    ? GetText("关闭伺服按钮提示")
                    : GetText("使能伺服按钮提示");

            ResetAxisAlarmButton.ToolTip = !CanResetAxisAlarm
                ? (!_axisStatusAvailable
                    ? GetText("轴状态不可读禁止恢复操作")
                    : _axisMoving
                        ? GetText("轴运动中禁止恢复操作")
                        : ManualOperationBlockText)
                : _manualOperationBlockReason == ManualOperationBlockReason.EmergencyStopAlarmNotReset
                    ? GetText("急停恢复后可复位轴报警")
                    : GetText("轴报警复位按钮提示");
        }

        /// <summary>读取并显示指令位置、编码器反馈位置、伺服、回零、限位和报警状态。</summary>
        public async Task RefreshStatusAsync()
        {
            if (_machineRuntime.State == MachineRunState.Disconnected)
            {
                _axisStatusAvailable = false;
                _axisServoEnabled = false;
                _axisMoving = false;
                SetManualOperationAvailability(ManualOperationBlockReason.MotionCardDisconnected);
                CommandPositionText.Text = GetText("控制卡未连接");
                FeedbackPositionText.Text = "—";
                UpdateLimitIndicator(AlarmIndicator, false, false);
                UpdateIndicator(HomedIndicator, false, false);
                UpdateLimitIndicator(PositiveLimitIndicator, false, false);
                UpdateLimitIndicator(NegativeLimitIndicator, false, false);
                PositiveLimitIndicator.ToolTip = GetText("控制卡未连接");
                NegativeLimitIndicator.ToolTip = GetText("控制卡未连接");
                UpdateIndicator(OriginIndicator, false, false);
                HomedStatusText.Text = GetText("状态未连接");
                OriginStatusText.ToolTip = GetText("控制卡未连接");
                MotionStateText.Text = GetText("状态未连接");
                ServoStatusText.Text = GetText("轴伺服状态未连接");
                ServoToggleButton.Content = GetText("伺服使能");
                AlarmMessageText.Text = string.Empty;
                AlarmMessageText.Foreground = Brushes.Firebrick;
                SetAssociatedIoDisconnected();
                return;
            }

            AxisStatus status = await ReadAxisStatusForManualActionAsync().ConfigureAwait(true);
            CommandPositionText.Text = status.CommandPosition.HasValue
                ? status.CommandPosition.Value.ToString("F3", CultureInfo.CurrentCulture)
                : "—";
            FeedbackPositionText.Text = status.Position.ToString("F3", CultureInfo.CurrentCulture);
            UpdateIndicator(AlarmIndicator, status.AlarmActive, status.AlarmActive);
            UpdateIndicator(HomedIndicator, status.Homed, false);
            HomedStatusText.Text = GetText(status.Homed ? "已回零" : "未回零");
            bool positiveLimitActive = status.PositiveLimitActive == true || status.PositiveSoftLimitActive == true;
            bool negativeLimitActive = status.NegativeLimitActive == true || status.NegativeSoftLimitActive == true;
            bool positiveLimitKnown = status.PositiveLimitActive.HasValue && status.PositiveSoftLimitActive.HasValue;
            bool negativeLimitKnown = status.NegativeLimitActive.HasValue && status.NegativeSoftLimitActive.HasValue;
            UpdateLimitIndicator(PositiveLimitIndicator, positiveLimitActive, positiveLimitKnown);
            UpdateLimitIndicator(NegativeLimitIndicator, negativeLimitActive, negativeLimitKnown);
            PositiveLimitIndicator.ToolTip = BuildLimitStatusToolTip(
                "正限位",
                status.PositiveLimitActive,
                status.PositiveSoftLimitActive,
                status.LimitStatusSource);
            NegativeLimitIndicator.ToolTip = BuildLimitStatusToolTip(
                "负限位",
                status.NegativeLimitActive,
                status.NegativeSoftLimitActive,
                status.LimitStatusSource);
            UpdateIndicator(OriginIndicator, status.OriginInputActive == true, false);
            OriginStatusText.ToolTip = GetText(status.OriginInputActive.HasValue ? "原点输入状态" : "原点输入未配置");
            MotionStateText.Text = GetText(status.Moving ? "运动中" : "已停止");
            ServoStatusText.Text = GetText(status.ServoEnabled ? "轴伺服状态已使能" : "轴伺服状态未使能");
            ServoToggleButton.Content = GetText(status.ServoEnabled ? "伺服关闭" : "伺服使能");
            var statusMessages = new List<string>();
            if (status.AlarmActive)
            {
                statusMessages.Add(string.IsNullOrWhiteSpace(status.AlarmMessage)
                    ? GetText("轴报警有效")
                    : status.AlarmMessage);
            }

            string limitStatusMessage = BuildLimitStatusMessage(status);
            if (!string.IsNullOrWhiteSpace(limitStatusMessage))
            {
                statusMessages.Add(limitStatusMessage);
            }

            bool hasUnknownLimitStatus = !positiveLimitKnown || !negativeLimitKnown;
            if (hasUnknownLimitStatus)
            {
                statusMessages.Add(GetText("限位状态无法确认"));
            }

            AlarmMessageText.Text = string.Join("；", statusMessages);
            AlarmMessageText.Foreground = status.AlarmActive || positiveLimitActive || negativeLimitActive
                ? Brushes.Firebrick
                : hasUnknownLimitStatus ? Brushes.DarkGoldenrod : Brushes.Firebrick;

            // 先完成轴反馈显示，再读取关联 IO，避免 IO 查询延迟编码器位置刷新。
            await RefreshAssociatedIoStatusAsync().ConfigureAwait(true);
        }

        /// <summary>显示当前轴状态读取失败的具体原因，不影响其他轴卡片刷新。</summary>
        public void ShowStatusReadError(Exception error)
        {
            if (error == null)
            {
                throw new ArgumentNullException(nameof(error));
            }

            _axisStatusAvailable = false;
            UpdateAxisActionAvailability();
            CommandPositionText.Text = "—";
            FeedbackPositionText.Text = "—";
            HomedStatusText.Text = "—";
            UpdateLimitIndicator(AlarmIndicator, false, false);
            UpdateLimitIndicator(PositiveLimitIndicator, false, false);
            UpdateLimitIndicator(NegativeLimitIndicator, false, false);
            PositiveLimitIndicator.ToolTip = string.Format(GetText("读取轴状态失败"), error.Message);
            NegativeLimitIndicator.ToolTip = string.Format(GetText("读取轴状态失败"), error.Message);
            AlarmMessageText.Text = string.Format(GetText("读取轴状态失败"), error.Message);
            AlarmMessageText.Foreground = Brushes.DarkGoldenrod;
        }

        /// <summary>读取轴状态并更新手动操作可用性；读取失败时立即锁住依赖状态的操作。</summary>
        private async Task<AxisStatus> ReadAxisStatusForManualActionAsync()
        {
            try
            {
                AxisStatus status = await _axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(true);
                _axisStatusAvailable = true;
                _axisServoEnabled = status.ServoEnabled;
                _axisMoving = status.Moving;
                UpdateAxisActionAvailability();
                return status;
            }
            catch (Exception error)
            {
                ShowStatusReadError(error);
                throw;
            }
        }

        /// <summary>按当前状态设置指示灯颜色，并给有效报警或限位使用醒目颜色。</summary>
        private static void UpdateIndicator(Ellipse indicator, bool active, bool warning)
        {
            indicator.Fill = warning
                ? Brushes.Firebrick
                : active ? Brushes.ForestGreen : Brushes.DarkGray;
        }

        /// <summary>显示方向限位灯；有效为红色，未知为橙色，已确认未触发为灰色。</summary>
        private static void UpdateLimitIndicator(Ellipse indicator, bool active, bool known)
        {
            indicator.Fill = active
                ? Brushes.Firebrick
                : known ? Brushes.DarkGray : Brushes.DarkOrange;
        }

        /// <summary>按当前硬限位、软限位状态和来源生成轴卡片中的具体限位说明。</summary>
        private string BuildLimitStatusMessage(AxisStatus status)
        {
            var messages = new List<string>();
            if (status.PositiveLimitActive == true)
            {
                messages.Add(GetText("正向硬限位有效"));
            }

            if (status.NegativeLimitActive == true)
            {
                messages.Add(GetText("负向硬限位有效"));
            }

            if (status.PositiveSoftLimitActive == true)
            {
                messages.Add(GetText("正向软限位有效"));
            }

            if (status.NegativeSoftLimitActive == true)
            {
                messages.Add(GetText("负向软限位有效"));
            }

            if (messages.Count > 0 && !string.IsNullOrWhiteSpace(status.LimitStatusSource))
            {
                messages.Add(string.Format(GetText("限位状态来源格式"), status.LimitStatusSource));
            }

            return string.Join("；", messages);
        }

        /// <summary>为一个方向的限位灯显示硬限位、软限位和适配器状态来源。</summary>
        private string BuildLimitStatusToolTip(
            string directionResourceKey,
            bool? hardLimitActive,
            bool? softLimitActive,
            string limitStatusSource)
        {
            var messages = new List<string>
            {
                hardLimitActive.HasValue
                    ? GetText(hardLimitActive.Value ? "硬限位有效" : "硬限位未触发")
                    : GetText("硬限位状态未知"),
                softLimitActive.HasValue
                    ? GetText(softLimitActive.Value ? "软件限位当前有效" : "软件限位未触发")
                    : GetText("软件限位状态未知")
            };

            if (!string.IsNullOrWhiteSpace(limitStatusSource))
            {
                messages.Add(string.Format(GetText("限位状态来源格式"), limitStatusSource));
            }

            return GetText(directionResourceKey)
                + GetText("状态分隔符")
                + string.Join("；", messages);
        }

        /// <summary>页面离开时停止此卡片可能仍在运行的按住式点动。</summary>
        public async Task StopJogIfActiveAsync()
        {
            CancelJogButtonPress(_pressedJogButton);
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

        /// <summary>打开 IO 关联设置，并将新选择的映射保存到当前轴卡片。</summary>
        private void AddAssociatedIoButton_Click(object sender, RoutedEventArgs e)
        {
            ManualAxisIoAssociationDialog dialog = CreateIoAssociationDialog(null);
            if (dialog.ShowDialog() != true || dialog.Result == null)
            {
                return;
            }

            ManualAxisIoAssociation association = dialog.Result;
            var viewItem = new ManualAxisIoAssociationViewItem(association);
            _ioAssociationGroup.Points.Add(association);
            _ioAssociationItems.Add(viewItem);
            try
            {
                _saveIoAssociations();
                UpdateEmptyAxisIoText();
                UpdateAddAssociatedIoButton();
                OperationMessageText.Text = string.Format(GetText("轴关联IO已添加"), association.DisplayName);
            }
            catch (Exception error)
            {
                _ioAssociationGroup.Points.Remove(association);
                _ioAssociationItems.Remove(viewItem);
                UpdateEmptyAxisIoText();
                UpdateAddAssociatedIoButton();
                OperationMessageText.Text = string.Format(GetText("轴关联IO保存失败"), error.Message);
            }
        }

        /// <summary>打开设置窗口并更新指定关联项，保存失败时恢复原配置。</summary>
        private void EditAssociatedIoButton_Click(object sender, RoutedEventArgs e)
        {
            ManualAxisIoAssociationViewItem viewItem = GetIoAssociationFromButton(sender);
            if (viewItem == null)
            {
                return;
            }

            ManualAxisIoAssociation oldAssociation = viewItem.Association;
            int index = _ioAssociationGroup.Points.IndexOf(oldAssociation);
            if (index < 0)
            {
                OperationMessageText.Text = GetText("无法读取轴关联IO");
                return;
            }

            ManualAxisIoAssociationDialog dialog = CreateIoAssociationDialog(oldAssociation);
            if (dialog.ShowDialog() != true || dialog.Result == null)
            {
                return;
            }

            ManualAxisIoAssociation updatedAssociation = dialog.Result;
            _ioAssociationGroup.Points[index] = updatedAssociation;
            viewItem.UpdateAssociation(updatedAssociation);
            try
            {
                _saveIoAssociations();
                UpdateAddAssociatedIoButton();
                OperationMessageText.Text = string.Format(GetText("轴关联IO已更新"), updatedAssociation.DisplayName);
            }
            catch (Exception error)
            {
                _ioAssociationGroup.Points[index] = oldAssociation;
                viewItem.UpdateAssociation(oldAssociation);
                UpdateAddAssociatedIoButton();
                OperationMessageText.Text = string.Format(GetText("轴关联IO保存失败"), error.Message);
            }
        }

        /// <summary>删除当前轴卡片中的 IO 关联，保存失败时恢复该行。</summary>
        private void DeleteAssociatedIoButton_Click(object sender, RoutedEventArgs e)
        {
            ManualAxisIoAssociationViewItem viewItem = GetIoAssociationFromButton(sender);
            if (viewItem == null)
            {
                return;
            }

            ManualAxisIoAssociation association = viewItem.Association;
            int index = _ioAssociationGroup.Points.IndexOf(association);
            if (index < 0)
            {
                OperationMessageText.Text = GetText("无法读取轴关联IO");
                return;
            }

            _ioAssociationGroup.Points.RemoveAt(index);
            _ioAssociationItems.Remove(viewItem);
            try
            {
                _saveIoAssociations();
                UpdateEmptyAxisIoText();
                UpdateAddAssociatedIoButton();
                OperationMessageText.Text = string.Format(GetText("轴关联IO已删除"), association.DisplayName);
            }
            catch (Exception error)
            {
                _ioAssociationGroup.Points.Insert(index, association);
                _ioAssociationItems.Insert(index, viewItem);
                UpdateEmptyAxisIoText();
                UpdateAddAssociatedIoButton();
                OperationMessageText.Text = string.Format(GetText("轴关联IO保存失败"), error.Message);
            }
        }

        /// <summary>读取指定逻辑 DO 的目标值并通过运行时手动联锁执行写入。</summary>
        private async void SetAssociatedOutputButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            ManualAxisIoAssociationViewItem viewItem = GetIoAssociationFromButton(sender);
            bool outputValue;
            if (button == null || viewItem == null || !viewItem.IsOutput
                || !bool.TryParse(Convert.ToString(button.Tag), out outputValue))
            {
                OperationMessageText.Text = GetText("无法读取轴关联IO");
                return;
            }

            if (_ioOperationInProgress)
            {
                OperationMessageText.Text = GetText("DO操作正在执行");
                return;
            }

            ManualAxisIoAssociation association = viewItem.Association;
            if (!IsIoAssociationMapped(association))
            {
                OperationMessageText.Text = GetText("轴关联IO映射已失效");
                await RefreshAssociatedIoStatusAsync().ConfigureAwait(true);
                return;
            }

            string operationName = outputValue ? association.OnActionName : association.OffActionName;
            _ioOperationInProgress = true;
            AxisIoAssociationList.IsEnabled = false;
            OperationMessageText.Text = string.Empty;
            try
            {
                ManualOperationBlockReason blockReason = await _machineRuntime.RunManualOperationAsync(
                    async () =>
                    {
                        await _logicalIo.WriteOutputAsync(
                            association.PointName,
                            outputValue,
                            CancellationToken.None).ConfigureAwait(false);
                        bool actualValue = await _logicalIo.ReadOutputAsync(
                            association.PointName,
                            CancellationToken.None).ConfigureAwait(false);
                        if (actualValue != outputValue)
                        {
                            throw new InvalidOperationException(GetText("DO写入后读回不一致"));
                        }
                    },
                    CancellationToken.None).ConfigureAwait(true);

                if (blockReason != ManualOperationBlockReason.None)
                {
                    SetManualOperationAvailability(blockReason);
                    throw new InvalidOperationException(GetManualOperationBlockText(blockReason));
                }

                OperationMessageText.Text = string.Format(GetText("操作完成"), operationName);
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("操作失败"), operationName, error.Message);
            }
            finally
            {
                _ioOperationInProgress = false;
                AxisIoAssociationList.IsEnabled = true;
                await RefreshAssociatedIoStatusAsync().ConfigureAwait(true);
            }
        }

        /// <summary>构造只允许选择未重复逻辑名称的关联 IO 设置窗口。</summary>
        private ManualAxisIoAssociationDialog CreateIoAssociationDialog(ManualAxisIoAssociation association)
        {
            var dialog = new ManualAxisIoAssociationDialog(
                _digitalInputs,
                _digitalOutputs,
                _ioAssociationGroup.Points,
                association)
            {
                Owner = Window.GetWindow(this)
            };
            return dialog;
        }

        /// <summary>从按钮数据上下文取得对应的逻辑 IO 行。</summary>
        private static ManualAxisIoAssociationViewItem GetIoAssociationFromButton(object sender)
        {
            Button button = sender as Button;
            return button == null ? null : button.DataContext as ManualAxisIoAssociationViewItem;
        }

        /// <summary>根据当前 IO 行数显示紧凑的空关联提示。</summary>
        private void UpdateEmptyAxisIoText()
        {
            EmptyAxisIoText.Visibility = _ioAssociationItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_ioAssociationItems.Count == 0)
            {
                bool hasMappedIo = _digitalInputs.Count > 0 || _digitalOutputs.Count > 0;
                EmptyAxisIoText.Text = GetText(hasMappedIo ? "暂无轴关联IO" : "暂无已命名IO映射");
            }
        }

        /// <summary>没有可添加的命名逻辑点或配置不可保存时禁用关联按钮。</summary>
        private void UpdateAddAssociatedIoButton()
        {
            bool hasUnusedInput = _digitalInputs.Any(point => !_ioAssociationGroup.Points.Any(association =>
                association.PointType == ManualAxisIoPointType.DigitalInput
                && string.Equals(association.PointName, point.Name, StringComparison.OrdinalIgnoreCase)));
            bool hasUnusedOutput = _digitalOutputs.Any(point => !_ioAssociationGroup.Points.Any(association =>
                association.PointType == ManualAxisIoPointType.DigitalOutput
                && string.Equals(association.PointName, point.Name, StringComparison.OrdinalIgnoreCase)));
            bool hasUnusedPoint = hasUnusedInput || hasUnusedOutput;

            AddAssociatedIoButton.IsEnabled = _canEditIoAssociations && hasUnusedPoint;
            string toolTipKey = !_canEditIoAssociations
                ? "轴关联IO配置禁止覆盖"
                : !hasUnusedPoint
                    ? (_digitalInputs.Count == 0 && _digitalOutputs.Count == 0
                        ? "请先配置逻辑IO映射"
                        : "轴已关联全部逻辑IO")
                    : "添加轴关联IO";
            AddAssociatedIoButton.ToolTip = GetText(toolTipKey);
        }

        /// <summary>按关联类型和逻辑名称检查映射是否仍存在。</summary>
        private bool IsIoAssociationMapped(ManualAxisIoAssociation association)
        {
            IReadOnlyList<DigitalIoHardwareDefinition> points = association.PointType == ManualAxisIoPointType.DigitalInput
                ? _digitalInputs
                : _digitalOutputs;
            return points.Any(point => string.Equals(
                point.Name,
                association.PointName,
                StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>轮询所有关联 DI/DO 状态，读取失败时显示错误而不伪装为关闭。</summary>
        private async Task RefreshAssociatedIoStatusAsync()
        {
            foreach (ManualAxisIoAssociationViewItem viewItem in _ioAssociationItems)
            {
                ManualAxisIoAssociation association = viewItem.Association;
                if (!IsIoAssociationMapped(association))
                {
                    viewItem.SetStatus(
                        GetText("轴关联IO映射已失效"),
                        Brushes.MistyRose,
                        false,
                        GetText("轴关联IO映射已失效"));
                    continue;
                }

                try
                {
                    bool isActive = association.PointType == ManualAxisIoPointType.DigitalInput
                        ? await _logicalIo.ReadInputAsync(association.PointName, CancellationToken.None).ConfigureAwait(true)
                        : await _logicalIo.ReadOutputAsync(association.PointName, CancellationToken.None).ConfigureAwait(true);
                    string stateText;
                    string stateToolTip;
                    if (association.PointType == ManualAxisIoPointType.DigitalInput)
                    {
                        stateText = GetText(isActive ? "IO有效" : "IO无效");
                        stateToolTip = GetText("轴关联DI状态说明");
                    }
                    else
                    {
                        stateText = GetText(isActive ? "逻辑ON" : "逻辑OFF");
                        stateToolTip = _manualOperationAllowed
                            ? GetText("轴关联DO状态说明")
                            : ManualOperationBlockText;
                    }

                    viewItem.SetStatus(
                        stateText,
                        isActive ? Brushes.Honeydew : Brushes.Gainsboro,
                        association.PointType == ManualAxisIoPointType.DigitalOutput
                            && _manualOperationAllowed
                            && !_ioOperationInProgress,
                        stateToolTip);
                }
                catch (Exception error)
                {
                    viewItem.SetStatus(
                        GetText("IO读取失败简短"),
                        Brushes.MistyRose,
                        association.PointType == ManualAxisIoPointType.DigitalOutput
                            && _manualOperationAllowed
                            && !_ioOperationInProgress,
                        _manualOperationAllowed
                            ? error.Message
                            : ManualOperationBlockText + Environment.NewLine + error.Message);
                }
            }
        }

        /// <summary>控制卡断开时清除关联 IO 的旧状态并禁用 DO 操作。</summary>
        private void SetAssociatedIoDisconnected()
        {
            foreach (ManualAxisIoAssociationViewItem viewItem in _ioAssociationItems)
            {
                viewItem.SetStatus(GetText("状态未连接"), Brushes.LightGray, false, GetText("控制卡未连接"));
            }
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

            await RunManualRecoveryActionAsync(GetText("写入当前位置"), async () =>
            {
                EnsurePositionNameIsValid(position);
                AxisStatus status = await ReadAxisStatusForManualActionAsync().ConfigureAwait(true);
                if (status.Moving)
                {
                    throw new InvalidOperationException(GetText("轴运动中禁止写入当前位置"));
                }

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

                double targetPosition = position.Position.Value;
                EnsurePositionWithinLimits(targetPosition);
                AxisMotionProfile profile = ReadMotionProfile();
                await _axis.MoveAbsoluteAsync(targetPosition, profile, CancellationToken.None)
                    .ConfigureAwait(true);

                AxisStatus finalStatus;
                try
                {
                    finalStatus = await ReadAxisStatusForManualActionAsync().ConfigureAwait(true);
                }
                catch (Exception error)
                {
                    OperationMessageText.Text = string.Format(
                        GetText("定位完成但反馈读取失败"),
                        error.Message);
                    return;
                }

                string feedbackPosition = finalStatus.Position.ToString("F3", CultureInfo.CurrentCulture);
                string displayedTargetPosition = targetPosition.ToString("F3", CultureInfo.CurrentCulture);
                if (!string.Equals(feedbackPosition, displayedTargetPosition, StringComparison.Ordinal))
                {
                    OperationMessageText.Text = string.Format(
                        GetText("定位反馈与目标不一致"),
                        feedbackPosition,
                        displayedTargetPosition,
                        PositionUnitText.Text);
                }
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
            await ToggleServoAsync().ConfigureAwait(true);
        }

        /// <summary>伺服关闭走恢复入口，伺服使能继续走完整手动联锁入口。</summary>
        private async Task ToggleServoAsync()
        {
            if (_axisActionInProgress)
            {
                OperationMessageText.Text = GetText("当前轴操作正在执行");
                return;
            }

            if (_joggingAxis != null)
            {
                OperationMessageText.Text = GetText("请先停止当前连续点动");
                return;
            }

            _axisActionInProgress = true;
            string operationName = GetText("伺服状态");
            OperationMessageText.Text = string.Empty;
            try
            {
                AxisStatus status = await ReadAxisStatusForManualActionAsync().ConfigureAwait(true);
                ServoToggleButton.Content = GetText(status.ServoEnabled ? "伺服关闭" : "伺服使能");

                if (status.Moving)
                {
                    throw new InvalidOperationException(GetText("轴运动中禁止切换伺服"));
                }

                ManualOperationBlockReason blockReason;
                if (status.ServoEnabled)
                {
                    operationName = GetText("伺服关闭");
                    blockReason = await _machineRuntime.RunManualRecoveryOperationAsync(
                        () => _axis.DisableServoAsync(CancellationToken.None),
                        CancellationToken.None).ConfigureAwait(true);
                }
                else
                {
                    operationName = GetText("伺服使能");
                    blockReason = await _machineRuntime.RunManualOperationAsync(
                        () => _axis.EnableServoAsync(CancellationToken.None),
                        CancellationToken.None).ConfigureAwait(true);
                }

                if (blockReason != ManualOperationBlockReason.None)
                {
                    SetManualOperationAvailability(blockReason);
                    throw new InvalidOperationException(GetManualOperationBlockText(blockReason));
                }

                OperationMessageText.Text = string.Format(GetText("操作完成"), operationName);
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

        /// <summary>使用当前轴映射中保存的回零参数启动回零。</summary>
        private async void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            await RunAxisActionAsync(GetText("回零"), () => _axis.HomeAsync(CancellationToken.None))
                .ConfigureAwait(true);
        }

        /// <summary>清除当前轴的驱动报警。</summary>
        private async void ResetAxisAlarmButton_Click(object sender, RoutedEventArgs e)
        {
            await ResetAxisAlarmAsync().ConfigureAwait(true);
        }

        /// <summary>轴停止且急停输入已恢复后复位轴报警；急停仍有效或状态不可读时 Runtime 会拒绝。</summary>
        private async Task ResetAxisAlarmAsync()
        {
            if (_axisActionInProgress)
            {
                OperationMessageText.Text = GetText("当前轴操作正在执行");
                return;
            }

            if (_joggingAxis != null)
            {
                OperationMessageText.Text = GetText("请先停止当前连续点动");
                return;
            }

            _axisActionInProgress = true;
            string operationName = GetText("轴报警复位");
            OperationMessageText.Text = string.Empty;
            try
            {
                ManualOperationBlockReason blockReason = await _machineRuntime.RunManualAxisAlarmResetAsync(
                    async () =>
                    {
                        AxisStatus status = await ReadAxisStatusForManualActionAsync().ConfigureAwait(true);
                        if (status.Moving)
                        {
                            throw new InvalidOperationException(GetText("轴运动中禁止恢复操作"));
                        }

                        await _axis.ResetAlarmAsync(CancellationToken.None).ConfigureAwait(false);
                    },
                    CancellationToken.None).ConfigureAwait(true);

                if (blockReason != ManualOperationBlockReason.None)
                {
                    SetManualOperationAvailability(blockReason);
                    throw new InvalidOperationException(GetManualOperationBlockText(blockReason));
                }

                OperationMessageText.Text = string.Format(GetText("操作完成"), operationName);
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

        /// <summary>按受控停止方式停止当前轴。</summary>
        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            CancelJogButtonPress(_pressedJogButton);
            if (_joggingAxis != null)
            {
                bool stopped = await StopJogAsync().ConfigureAwait(true);
                if (stopped)
                {
                    OperationMessageText.Text = string.Format(GetText("操作完成"), GetText("受控停止"));
                    await RefreshStatusAsync().ConfigureAwait(true);
                }

                return;
            }

            await RunAxisStopActionAsync(
                GetText("受控停止"),
                () => _axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None)).ConfigureAwait(true);
        }

        /// <summary>校验目标位置和运动曲线后定位，并核对定位后的反馈位置。</summary>
        private async void MoveAbsoluteButton_Click(object sender, RoutedEventArgs e)
        {
            await RunAxisActionAsync(GetText("定位"), async () =>
            {
                double targetPosition = ReadNumber(TargetPositionTextBox, GetText("定位位置"));
                EnsurePositionWithinLimits(targetPosition);
                AxisMotionProfile profile = ReadMotionProfile();
                await _axis.MoveAbsoluteAsync(targetPosition, profile, CancellationToken.None).ConfigureAwait(true);

                AxisStatus finalStatus;
                try
                {
                    finalStatus = await ReadAxisStatusForManualActionAsync().ConfigureAwait(true);
                }
                catch (Exception error)
                {
                    OperationMessageText.Text = string.Format(
                        GetText("定位完成但反馈读取失败"),
                        error.Message);
                    return;
                }

                string feedbackPosition = finalStatus.Position.ToString("F3", CultureInfo.CurrentCulture);
                string displayedTargetPosition = targetPosition.ToString("F3", CultureInfo.CurrentCulture);
                if (!string.Equals(feedbackPosition, displayedTargetPosition, StringComparison.Ordinal))
                {
                    OperationMessageText.Text = string.Format(
                        GetText("定位反馈与目标不一致"),
                        feedbackPosition,
                        displayedTargetPosition,
                        PositionUnitText.Text);
                }
            }).ConfigureAwait(true);
        }

        /// <summary>切换按住连续点动和按一次走固定寸动步长。</summary>
        private void JogModeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            JogModeToggleButton.Content = GetText(JogModeToggleButton.IsChecked == true ? "连续点动" : "寸动模式");
        }

        /// <summary>记录负向按钮本次按压，再按连续或寸动模式执行动作。</summary>
        private async void NegativeJogButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Button jogButton = sender as Button;
            long pressVersion = RegisterJogButtonPress(jogButton);
            await BeginJogAsync(MotionDirection.Negative, jogButton, pressVersion).ConfigureAwait(true);
        }

        /// <summary>记录正向按钮本次按压，再按连续或寸动模式执行动作。</summary>
        private async void PositiveJogButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Button jogButton = sender as Button;
            long pressVersion = RegisterJogButtonPress(jogButton);
            await BeginJogAsync(MotionDirection.Positive, jogButton, pressVersion).ConfigureAwait(true);
        }

        /// <summary>点动按钮松开时使对应按压请求失效，并停止已经启动的点动。</summary>
        private async void JogButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CancelJogButtonPress(sender as Button);
            await StopJogAsync().ConfigureAwait(true);
        }

        /// <summary>点动按钮失去鼠标捕获时使按压请求失效并停止当前轴。</summary>
        private async void JogButton_LostMouseCapture(object sender, MouseEventArgs e)
        {
            Button lostButton = sender as Button;
            if (ReferenceEquals(lostButton, _joggingButton)
                || ReferenceEquals(lostButton, _pressedJogButton))
            {
                CancelJogButtonPress(lostButton);
                await StopJogAsync().ConfigureAwait(true);
            }
        }

        /// <summary>连续模式启动按住式点动；寸动模式按当前步长执行一次相对定位。</summary>
        private async Task BeginJogAsync(MotionDirection direction, Button jogButton, long pressVersion)
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
                    AxisMotionProfile jogProfile = ReadMotionProfile();
                    await _axis.StartJogAsync(direction, jogProfile, CancellationToken.None).ConfigureAwait(true);
                    _joggingAxis = _axis;
                    _joggingButton = jogButton;
                    if (!IsJogButtonPressActive(jogButton, pressVersion))
                    {
                        bool stoppedAfterRelease = await StopJogAsync().ConfigureAwait(true);
                        if (!stoppedAfterRelease)
                        {
                            throw new InvalidOperationException(GetText("当前点动停止失败禁止继续点动"));
                        }

                        return;
                    }

                    if (!Mouse.Capture(jogButton))
                    {
                        await StopJogAsync().ConfigureAwait(true);
                        throw new InvalidOperationException(GetText("连续点动按钮无法捕获鼠标，已请求停止"));
                    }

                    return;
                }

                double stepDistance = ReadNumber(JogStepTextBox, GetText("寸动步长"));
                if (stepDistance <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(stepDistance), GetText("寸动步长必须大于零"));
                }

                AxisStatus status = await ReadAxisStatusForManualActionAsync().ConfigureAwait(true);
                double currentPosition = status.CommandPosition ?? status.Position;
                double distance = direction == MotionDirection.Positive ? stepDistance : -stepDistance;
                EnsurePositionWithinLimits(currentPosition + distance);
                AxisMotionProfile profile = ReadMotionProfile();
                await _axis.MoveRelativeAsync(distance, profile, CancellationToken.None).ConfigureAwait(true);
            }, true).ConfigureAwait(true);
        }

        /// <summary>登记点动按钮本次按下操作，并返回后续异步启动所用的版本号。</summary>
        private long RegisterJogButtonPress(Button jogButton)
        {
            _jogButtonPressVersion++;
            _pressedJogButton = jogButton;
            return _jogButtonPressVersion;
        }

        /// <summary>松开当前按钮时递增版本号，使尚未完成的旧启动请求不能继续运行。</summary>
        private void CancelJogButtonPress(Button jogButton)
        {
            if (jogButton == null || !ReferenceEquals(_pressedJogButton, jogButton))
            {
                return;
            }

            _pressedJogButton = null;
            _jogButtonPressVersion++;
        }

        /// <summary>确认启动请求仍对应当前按压，并检查鼠标左键仍处于按下状态。</summary>
        private bool IsJogButtonPressActive(Button jogButton, long pressVersion)
        {
            return ReferenceEquals(_pressedJogButton, jogButton)
                && _jogButtonPressVersion == pressVersion
                && Mouse.LeftButton == MouseButtonState.Pressed;
        }

        /// <summary>显示本软件能核对的连接、伺服、运动、报警和限位条件，不替代硬件安全检查。</summary>
        private async void SafetyCheckButton_Click(object sender, RoutedEventArgs e)
        {
            var issues = new List<string>();
            MachineRunState machineState = _machineRuntime.State;
            if (machineState == MachineRunState.Disconnected)
            {
                SetManualOperationAvailability(ManualOperationBlockReason.MotionCardDisconnected);
                issues.Add(GetText("运动卡未连接操作被拒绝"));
            }
            else
            {
                ManualOperationBlockReason blockReason = await _machineRuntime
                    .GetManualOperationBlockReasonAsync(CancellationToken.None).ConfigureAwait(true);
                SetManualOperationAvailability(blockReason);
                if (blockReason != ManualOperationBlockReason.None)
                {
                    issues.Add(GetManualOperationBlockText(blockReason));
                }

                try
                {
                    AxisStatus status = await ReadAxisStatusForManualActionAsync().ConfigureAwait(true);
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

                    string limitStatusMessage = BuildLimitStatusMessage(status);
                    if (!string.IsNullOrWhiteSpace(limitStatusMessage))
                    {
                        issues.Add(limitStatusMessage);
                    }

                    if (!status.PositiveLimitActive.HasValue
                        || !status.NegativeLimitActive.HasValue
                        || !status.PositiveSoftLimitActive.HasValue
                        || !status.NegativeSoftLimitActive.HasValue)
                    {
                        issues.Add(GetText("限位状态无法确认"));
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
            bool stopped = true;

            try
            {
                if (joggingAxis != null)
                {
                    await RunAxisStopOperationAsync(joggingAxis, CancellationToken.None).ConfigureAwait(true);
                    if (ReferenceEquals(_joggingAxis, joggingAxis))
                    {
                        _joggingAxis = null;
                    }
                }
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("停止点动失败"), error.Message);
                stopped = false;
            }
            finally
            {
                Button joggingButton = _joggingButton;
                _joggingButton = null;
                if (joggingButton != null && ReferenceEquals(Mouse.Captured, joggingButton))
                {
                    Mouse.Capture(null);
                }
            }

            return stopped;
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

            if (!_axisStatusAvailable)
            {
                OperationMessageText.Text = GetText("轴状态读取失败禁止手动操作");
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
                    SetManualOperationAvailability(blockReason);
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

        /// <summary>执行允许急停锁存期间使用的恢复或示教操作，仍拒绝断卡和自动流程状态。</summary>
        private async Task RunManualRecoveryActionAsync(string operationName, Func<Task> operation)
        {
            if (_axisActionInProgress)
            {
                OperationMessageText.Text = GetText("当前轴操作正在执行");
                return;
            }

            if (!_axisStatusAvailable)
            {
                OperationMessageText.Text = GetText("轴状态不可读禁止恢复操作");
                return;
            }

            if (_joggingAxis != null)
            {
                OperationMessageText.Text = GetText("请先停止当前连续点动");
                return;
            }

            _axisActionInProgress = true;
            OperationMessageText.Text = string.Empty;
            try
            {
                ManualOperationBlockReason blockReason = await _machineRuntime
                    .RunManualRecoveryOperationAsync(operation, CancellationToken.None).ConfigureAwait(true);
                if (blockReason != ManualOperationBlockReason.None)
                {
                    SetManualOperationAvailability(blockReason);
                    throw new InvalidOperationException(GetManualOperationBlockText(blockReason));
                }

                OperationMessageText.Text = string.Format(GetText("操作完成"), operationName);
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
