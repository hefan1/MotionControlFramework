using System;
using System.Windows.Data;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Framework.Wpf.Monitoring
{
    /// <summary>只读显示当前设备已映射逻辑轴的运行状态，不提供运动控制操作。</summary>
    public partial class AxisMonitorView : UserControl
    {
        /// <summary>按轴映射顺序保存当前设备的逻辑轴状态行。</summary>
        private readonly ObservableCollection<AxisMonitorRow> _axisRows = new ObservableCollection<AxisMonitorRow>();

        /// <summary>当前已连接运动卡提供的逻辑轴集合；离线时为空。</summary>
        private readonly Dictionary<string, IAxis> _axesByName = new Dictionary<string, IAxis>(StringComparer.OrdinalIgnoreCase);

        /// <summary>标记创建页面时是否已有控制卡连接。</summary>
        private readonly bool _initialConnectionAvailable;
        /// <summary>观察宿主已有卡组的当前连接，不创建连接。</summary>
        private readonly MotionCardGroup _motionCards;
        /// <summary>正式 Host 的 Runtime 连接会话，由已有连接的拥有者提供。</summary>
        private readonly Func<Guid?> _getConnectionSession;
        /// <summary>同时检查初始资格与当前连接状态。</summary>
        private bool _isCardConnected { get { return _initialConnectionAvailable && _motionCards != null && _motionCards.IsConnected && (_getConnectionSession == null || _getConnectionSession().HasValue); } }

        /// <summary>页面可见时每 500 毫秒读取一次轴状态。</summary>
        private readonly DispatcherTimer _refreshTimer;

        /// <summary>防止上一次状态读取未结束时启动下一轮刷新。</summary>
        private bool _refreshInProgress;
        /// <summary>当前显示行所属的连接；用于在下一轮驱动读数开始前清除旧反馈。</summary>
        private Guid? _displaySession;
        /// <summary>离页后拒绝上一可见周期的迟到结果。</summary>
        private int _visibilityGeneration;

        /// <summary>保存界面读取轮次完成时间；不是设备采样或最后成功采样时间。</summary>
        private DateTime? _lastRefreshTime;

        /// <summary>使用当前连接卡与轴映射创建只读状态页；不连接控制卡或发送运动命令。</summary>
        public AxisMonitorView(
            bool isCardConnected,
            MotionCardGroup motionCards,
            IEnumerable<AxisHardwareDefinition> axisConfigurations)
            : this(isCardConnected, motionCards, axisConfigurations, null)
        {
        }

        /// <summary>按宿主提供的连接会话拒绝重连前的旧轴读数。</summary>
        public AxisMonitorView(bool isCardConnected, MotionCardGroup motionCards,
            IEnumerable<AxisHardwareDefinition> axisConfigurations, Func<Guid?> getConnectionSession)
        {
            InitializeComponent();
            _getConnectionSession = getConnectionSession;

            _motionCards = motionCards;
            _initialConnectionAvailable = isCardConnected && motionCards != null && motionCards.IsConnected;
            if (motionCards != null)
            {
                foreach (IAxis axis in motionCards.Axes)
                {
                    if (axis != null && !string.IsNullOrWhiteSpace(axis.Name))
                    {
                        _axesByName[axis.Name] = axis;
                    }
                }
            }

            BuildAxisRows(axisConfigurations);
            // 将同一个受控视图直接交给表格，避免过滤默认视图却刷新另一层ItemCollection。
            var axisView = CollectionViewSource.GetDefaultView(_axisRows);
            axisView.Filter = value => MatchesAxisFilter((AxisMonitorRow)value);
            AxisStatusGrid.ItemsSource = axisView;
            RefreshLocalizedText();

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
            Loaded += AxisMonitorView_Loaded;
            Unloaded += AxisMonitorView_Unloaded;
        }

        /// <summary>切换界面语言后重新生成列标题并重绘最近一次轴状态。</summary>
        public void RefreshLocalizedText()
        {
            BuildColumns();
            foreach (AxisMonitorRow row in _axisRows)
            {
                ApplyCachedStatus(row);
            }

            UpdateMonitorStatusText(_axisRows.Count(row => row.HasReadError));
            RefreshFilteredRows();
        }

        /// <summary>页面显示时立即读取轴状态，并启动页面可见期间的定时刷新。</summary>
        private async void AxisMonitorView_Loaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Start();
            await RefreshAxisStatusesAsync().ConfigureAwait(true);
        }

        /// <summary>离开轴监控页后暂停状态轮询，避免后台占用控制卡通信。</summary>
        private void AxisMonitorView_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            _visibilityGeneration++;
            foreach (AxisMonitorRow row in _axisRows) SetReadError(row, GetText("轴监控状态未知"));
            RefreshFilteredRows();
        }

        /// <summary>按固定周期刷新当前设备已配置轴的状态。</summary>
        private async void RefreshTimer_Tick(object sender, EventArgs e)
        {
            await RefreshAxisStatusesAsync().ConfigureAwait(true);
        }

        /// <summary>按硬件映射建立显示行，并把逻辑名称关联到当前连接卡的轴对象。</summary>
        private void BuildAxisRows(IEnumerable<AxisHardwareDefinition> axisConfigurations)
        {
            if (axisConfigurations != null)
            {
                foreach (AxisHardwareDefinition definition in axisConfigurations)
                {
                    if (definition == null || string.IsNullOrWhiteSpace(definition.Name))
                    {
                        continue;
                    }

                    IAxis axis;
                    _axesByName.TryGetValue(definition.Name, out axis);
                    _axisRows.Add(new AxisMonitorRow
                    {
                        Axis = axis,
                        LogicalAxisName = definition.Name,
                        PhysicalAxisText = FormatPhysicalAxis(definition.ControllerId, definition.AxisNumber),
                        EngineeringUnit = string.IsNullOrWhiteSpace(definition.EngineeringUnit)
                            ? string.Empty
                            : definition.EngineeringUnit.Trim()
                    });
                }
            }

            if (_axisRows.Count == 0 && _axesByName.Count > 0)
            {
                foreach (IAxis axis in _axesByName.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    _axisRows.Add(new AxisMonitorRow
                    {
                        Axis = axis,
                        LogicalAxisName = axis.Name,
                        PhysicalAxisText = GetText("轴监控未配置物理轴"),
                        EngineeringUnit = string.Empty
                    });
                }
            }

            foreach (AxisMonitorRow row in _axisRows)
            {
                if (!_isCardConnected)
                {
                    SetDisconnectedState(row);
                }
                else if (row.Axis == null)
                {
                    SetReadError(row, GetText("轴监控轴未连接到控制卡"));
                }
            }
        }

        /// <summary>创建紧凑且居中对齐的状态列，方便操作人员快速横向核对。</summary>
        private void BuildColumns()
        {
            AxisStatusGrid.Columns.Clear();
            AddTextColumn("轴监控逻辑轴列", "LogicalAxisName", 120);
            AddTextColumn("轴监控物理轴列", "PhysicalAxisText", 108);
            AddTextColumn("轴监控位置列", "PositionText", 160);
            AddTextColumn("轴监控当前速度列", "VelocityText", 78);
            AddTextColumn("轴监控伺服状态列", "ServoStateText", 70);
            AddTextColumn("轴监控运动状态列", "MotionStateText", 70);
            AddTextColumn("轴监控回零状态列", "HomedStateText", 70);
            AddTextColumn("轴监控原点输入列", "OriginInputStateText", 120);
            AddTextColumn("轴监控正限位列", "PositiveLimitText", 135);
            AddTextColumn("轴监控负限位列", "NegativeLimitText", 135);
            AddAlarmColumn();
            AddStatusColumn();
        }

        /// <summary>添加只读文本列，并将单元格文字水平居中显示。</summary>
        private void AddTextColumn(string headerKey, string bindingPath, double width)
        {
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            AxisStatusGrid.Columns.Add(new DataGridTextColumn
            {
                Header = GetText(headerKey),
                Binding = new System.Windows.Data.Binding(bindingPath),
                Width = new DataGridLength(width),
                IsReadOnly = true,
                ElementStyle = textStyle
            });
        }

        /// <summary>添加异常报警使用红色提示的轴报警列。</summary>
        private void AddAlarmColumn()
        {
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.DarkGreen));

            var disconnectedTrigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding("IsConnected"),
                Value = false
            };
            disconnectedTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Gray));
            textStyle.Triggers.Add(disconnectedTrigger);

            var alarmTrigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding("HasAlarm"),
                Value = true
            };
            alarmTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Firebrick));
            textStyle.Triggers.Add(alarmTrigger);

            var errorTrigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding("HasReadError"),
                Value = true
            };
            errorTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Firebrick));
            textStyle.Triggers.Add(errorTrigger);

            AxisStatusGrid.Columns.Add(new DataGridTextColumn
            {
                Header = GetText("轴监控报警状态"),
                Binding = new System.Windows.Data.Binding("AlarmStateText"),
                Width = new DataGridLength(130),
                IsReadOnly = true,
                ElementStyle = textStyle
            });
        }

        /// <summary>添加带状态颜色的轴读取结果列，区分正常、未连接和读取失败。</summary>
        private void AddStatusColumn()
        {
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.DarkGreen));
            var disconnectedTrigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding("IsConnected"),
                Value = false
            };
            disconnectedTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Gray));
            textStyle.Triggers.Add(disconnectedTrigger);
            var uncertainTrigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding(nameof(AxisMonitorRow.HasUncertainFeedback)),
                Value = true
            };
            uncertainTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.DarkGoldenrod));
            textStyle.Triggers.Add(uncertainTrigger);
            var errorTrigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding("HasReadError"),
                Value = true
            };
            errorTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Firebrick));
            textStyle.Triggers.Add(errorTrigger);

            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("ReadStateText"));
            textFactory.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("ReadErrorMessage"));
            textFactory.SetValue(FrameworkElement.StyleProperty, textStyle);
            AxisStatusGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = GetText("轴监控读取状态"),
                Width = new DataGridLength(88),
                CellTemplate = new DataTemplate { VisualTree = textFactory }
            });
        }

        /// <summary>串行读取各轴当前状态，并在单轴读取失败时保留其他轴的监控结果。</summary>
        private async Task RefreshAxisStatusesAsync()
        {
            if (!IsLoaded) return;
            Guid? session = _getConnectionSession?.Invoke();
            if (_displaySession != session)
            {
                IsReadSessionCurrent(_displaySession);
                _displaySession = session;
            }
            if (!IsReadSessionCurrent(session) || _refreshInProgress) return;
            _refreshInProgress = true;
            int failedAxisCount = 0;
            int generation = _visibilityGeneration;
            try
            {
                foreach (AxisMonitorRow row in _axisRows)
                {
                    if (!IsReadSessionCurrent(session)) return;
                    if (row.Axis == null)
                    {
                        failedAxisCount++;
                        continue;
                    }

                    try
                    {
                        AxisStatus status = await row.Axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(true);
                        if (status == null)
                        {
                            throw new InvalidOperationException("控制卡没有返回轴状态数据。");
                        }

                        if (!IsLoaded || generation != _visibilityGeneration) return;
                        if (!IsReadSessionCurrent(session)) return;
                        row.LastStatus = status;
                        row.ReadErrorMessage = string.Empty;
                        row.HasReadError = false;
                        row.IsConnected = true;
                        ApplyAxisStatus(row, row.LastStatus);
                    }
                    catch (Exception error)
                    {
                        if (!IsLoaded || generation != _visibilityGeneration) return;
                        if (!IsReadSessionCurrent(session)) return;
                        Trace.TraceWarning("读取逻辑轴 '" + row.LogicalAxisName + "' 状态失败：" + error);
                        SetReadError(row, error.Message);
                        failedAxisCount++;
                    }
                }

                _lastRefreshTime = DateTime.Now;
                RefreshFilteredRows();
                UpdateMonitorStatusText(failedAxisCount);
            }
            finally
            {
                _refreshInProgress = false;
            }
        }

        /// <summary>丢弃读取期间断连或更换会话的整轮状态；已重连时显示未知，等待新轮次。</summary>
        private bool IsReadSessionCurrent(Guid? session)
        {
            bool connected = _isCardConnected;
            if (connected && session == _getConnectionSession?.Invoke()) return true;
            _lastRefreshTime = null;
            foreach (AxisMonitorRow row in _axisRows)
            {
                if (connected) SetReadError(row, GetText("轴监控状态未知"));
                else SetDisconnectedState(row);
            }
            RefreshFilteredRows();
            UpdateMonitorStatusText(connected ? _axisRows.Count : 0);
            return false;
        }

        /// <summary>将通用轴状态转换成带工程单位的现场显示文字。</summary>
        private void ApplyAxisStatus(AxisMonitorRow row, AxisStatus status)
        {
            if (row == null || status == null)
            {
                return;
            }

            string feedbackPosition = FormatPosition(status.Position, row.EngineeringUnit);
            string commandPosition = FormatPosition(status.CommandPosition, row.EngineeringUnit);
            row.PositionText = string.Format(
                GetText("轴监控反馈指令位置格式"),
                feedbackPosition,
                commandPosition);
            row.VelocityText = IsFinite(status.Velocity)
                ? FormatPosition(status.Velocity, row.EngineeringUnit) + "/s"
                : GetText("轴监控状态未知");
            row.ServoStateText = status.ServoEnabled ? GetText("轴监控伺服已使能") : GetText("轴监控伺服未使能");
            bool stopped = AxisStopFeedback.IsConfirmedStopped(status);
            row.MotionStateText = GetText(status.Moving ? "轴监控运动中" : stopped ? "轴监控已停止" : "轴监控停稳未知");
            row.HomedStateText = status.Homed ? GetText("轴监控已回零") : GetText("轴监控未回零");
            row.OriginInputStateText = !status.OriginInputActive.HasValue
                ? GetText("轴监控原点输入未配置或不可读")
                : GetText(status.OriginInputActive.Value ? "轴监控原点输入已触发" : "轴监控原点输入未触发");
            row.PositiveLimitText = FormatLimitState(status.PositiveLimitActive, status.PositiveSoftLimitActive);
            row.NegativeLimitText = FormatLimitState(status.NegativeLimitActive, status.NegativeSoftLimitActive);
            row.AlarmStateText = status.AlarmActive
                ? string.IsNullOrWhiteSpace(status.AlarmMessage)
                    ? GetText("轴监控已报警")
                    : string.Format(GetText("轴监控报警格式"), status.AlarmMessage)
                : GetText("轴监控无报警");
            row.HasAlarm = status.AlarmActive;
            row.HasUncertainFeedback = !IsFinite(status.Position) || !IsFinite(status.Velocity)
                || (status.CommandPosition.HasValue && !IsFinite(status.CommandPosition.Value))
                || (!status.Moving && !stopped);
            row.ReadStateText = GetText(row.HasUncertainFeedback ? "轴监控部分反馈待确认" : "轴监控读取正常");
            row.ReadErrorMessage = string.IsNullOrWhiteSpace(status.LimitStatusSource)
                ? string.Empty
                : GetText("轴监控限位来源格式").Replace("{0}", status.LimitStatusSource);
        }

        /// <summary>使用缓存状态刷新页面文字，语言切换时不额外访问控制卡。</summary>
        private void ApplyCachedStatus(AxisMonitorRow row)
        {
            if (!_isCardConnected)
            {
                SetDisconnectedState(row);
                return;
            }

            if (row.HasReadError)
            {
                SetReadError(row, row.ReadErrorMessage);
                return;
            }

            if (row.LastStatus != null)
            {
                ApplyAxisStatus(row, row.LastStatus);
            }
        }

        /// <summary>将断开连接或尚未连接时的状态全部显示为未知，不伪装成轴空闲。</summary>
        private void SetDisconnectedState(AxisMonitorRow row)
        {
            row.HasUncertainFeedback = false;
            row.LastStatus = null;
            row.HasReadError = false;
            row.IsConnected = false;
            row.ReadErrorMessage = string.Empty;
            row.PositionText = GetText("轴监控无数据");
            row.VelocityText = GetText("轴监控无数据");
            row.ServoStateText = GetText("轴监控状态未知");
            row.MotionStateText = GetText("轴监控状态未知");
            row.HomedStateText = GetText("轴监控状态未知");
            row.OriginInputStateText = GetText("轴监控状态未知");
            row.PositiveLimitText = GetText("轴监控状态未知");
            row.NegativeLimitText = GetText("轴监控状态未知");
            row.AlarmStateText = GetText("轴监控状态未知");
            row.HasAlarm = false;
            row.ReadStateText = GetText("轴监控未连接");
        }

        /// <summary>清除过期的轴数值并显示读取错误，避免旧状态看起来仍是实时数据。</summary>
        private void SetReadError(AxisMonitorRow row, string errorMessage)
        {
            row.HasUncertainFeedback = false;
            row.LastStatus = null;
            row.HasReadError = true;
            row.IsConnected = true;
            row.ReadErrorMessage = string.IsNullOrWhiteSpace(errorMessage)
                ? GetText("轴监控状态未知")
                : errorMessage;
            row.PositionText = GetText("轴监控读取失败");
            row.VelocityText = GetText("轴监控读取失败");
            row.ServoStateText = GetText("轴监控状态未知");
            row.MotionStateText = GetText("轴监控状态未知");
            row.HomedStateText = GetText("轴监控状态未知");
            row.OriginInputStateText = GetText("轴监控状态未知");
            row.PositiveLimitText = GetText("轴监控状态未知");
            row.NegativeLimitText = GetText("轴监控状态未知");
            row.AlarmStateText = GetText("轴监控状态未知");
            row.HasAlarm = false;
            row.ReadStateText = GetText("轴监控读取失败");
        }

        /// <summary>把正负硬限位和软限位分别显示，未知状态明确标记为未知。</summary>
        private string FormatLimitState(bool? hardLimitActive, bool? softLimitActive)
        {
            return GetText("轴监控硬限位简写") + FormatBooleanState(hardLimitActive)
                + " / " + GetText("轴监控软限位简写") + FormatBooleanState(softLimitActive);
        }

        /// <summary>将可空限位状态转为触发、未触发或未知文字。</summary>
        private string FormatBooleanState(bool? isActive)
        {
            if (!isActive.HasValue)
            {
                return GetText("轴监控状态未知");
            }

            return isActive.Value ? GetText("轴监控限位触发") : GetText("轴监控限位未触发");
        }

        /// <summary>非有限数值不能作为有效的轴反馈显示。</summary>
        private static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        /// <summary>按轴工程单位格式化位置、速度或指令位置数值。</summary>
        private string FormatPosition(double? value, string engineeringUnit)
        {
            if (!value.HasValue)
            {
                return GetText("轴监控无数据");
            }

            if (!IsFinite(value.Value)) return GetText("轴监控状态未知");
            string number = value.Value.ToString("0.###", CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(engineeringUnit)
                ? number
                : number + " " + engineeringUnit;
        }

        /// <summary>将控制卡标识和物理轴号组合成现场可核对的轴地址。</summary>
        private string FormatPhysicalAxis(string controllerId, int axisNumber)
        {
            string controllerName = string.IsNullOrWhiteSpace(controllerId)
                ? GetText("轴监控控制卡未命名")
                : controllerId.Trim();
            return controllerName + " / " + axisNumber.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>筛选隐藏时仍保留真实行对象，不把第一条匹配轴变成选择目标。</summary>
        private AxisMonitorRow _filterSelection;
        /// <summary>仅按逻辑名与真实未知/异常字段筛选，采集仍遍历全量轴。</summary>
        private bool MatchesAxisFilter(AxisMonitorRow row)
        {
            string query = AxisSearchBox.Text.Trim();
            var status = row.LastStatus;
            // 限位null明确表示无法确认；原点null同时可能未配置，不能据此推断故障。
            bool uncertainOrActiveLimit = status != null && (status.PositiveLimitActive != false || status.NegativeLimitActive != false
                || status.PositiveSoftLimitActive != false || status.NegativeSoftLimitActive != false);
            bool attention = !row.IsConnected || status == null || row.HasReadError || row.HasUncertainFeedback || row.HasAlarm || uncertainOrActiveLimit;
            return ((row.LogicalAxisName ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                && (AttentionOnlyBox.IsChecked != true || attention);
        }
        /// <summary>刷新显示视图与匹配数，不读硬件或修改全量行集合。</summary>
        private void RefreshFilteredRows()
        {
            if (AxisStatusGrid == null || AxisStatusGrid.ItemsSource == null) return;
            _filterSelection = AxisStatusGrid.SelectedItem as AxisMonitorRow ?? _filterSelection;
            ((System.ComponentModel.ICollectionView)AxisStatusGrid.ItemsSource).Refresh();
            if (_filterSelection != null && _axisRows.Contains(_filterSelection) && MatchesAxisFilter(_filterSelection))
                AxisStatusGrid.SelectedItem = _filterSelection;
            int count = _axisRows.Count(MatchesAxisFilter);
            AxisMatchText.Text = string.Format(GetText("轴筛选数量"), count, _axisRows.Count)
                + (_axisRows.Count == 0 ? " · " + GetText("轴监控没有映射轴") : count == 0 ? " · " + GetText("轴筛选无匹配") : string.Empty);
            RefreshAxisDetail();
            ClearAxisFilterButton.IsEnabled = AxisSearchBox.Text.Length > 0 || AttentionOnlyBox.IsChecked == true;
        }
        /// <summary>选择轴仅重绘缓存详情，不读取硬件或发送动作。</summary>
        private void AxisSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshAxisDetail();
        }

        /// <summary>展示完整映射、缓存反馈与原始错误；无状态时明确未知。</summary>
        private void RefreshAxisDetail()
        {
            if (AxisDetailText == null) return;
            var row = AxisStatusGrid.SelectedItem as AxisMonitorRow;
            if (row == null) { AxisDetailText.Text = GetText("轴详情未选择"); return; }
            string unknown = GetText("轴详情未知");
            bool valid = row.LastStatus != null && !row.HasReadError && row.IsConnected;
            // XAML 字符串会折叠普通换行；先展开模板分隔符，避免改变设备名称或原始错误。
            string template = GetText("轴详情格式").Replace("|", Environment.NewLine);
            AxisDetailText.Text = string.Format(template, row.LogicalAxisName,
                row.PhysicalAxisText, valid ? row.PositionText : unknown, valid ? row.VelocityText : unknown,
                valid ? row.PositiveLimitText : unknown, valid ? row.NegativeLimitText : unknown,
                row.ReadStateText, string.IsNullOrWhiteSpace(row.ReadErrorMessage) ? GetText("轴详情无错误") : row.ReadErrorMessage)
                .Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }

        /// <summary>编辑查询只刷新视图，不改变采样集合。</summary>
        private void AxisFilterChanged(object sender, TextChangedEventArgs e) { RefreshFilteredRows(); }
        /// <summary>切换异常条件只刷新视图，不发送命令。</summary>
        private void AxisFilterToggled(object sender, RoutedEventArgs e) { RefreshFilteredRows(); }
        /// <summary>清除名称及异常条件，不清除设备报警或未知标志。</summary>
        private void ClearAxisFilter_Click(object sender, RoutedEventArgs e) { AxisSearchBox.Clear(); AttentionOnlyBox.IsChecked = false; }

        /// <summary>根据连接、轴数量、刷新时间和失败数更新页面顶部状态提示。</summary>
        private void UpdateMonitorStatusText(int failedAxisCount)
        {
            if (_axisRows.Count == 0)
            {
                MonitorStatusText.Text = GetText("轴监控没有映射轴");
                return;
            }

            if (!_isCardConnected)
            {
                MonitorStatusText.Text = string.Format(
                    GetText("轴监控断开状态格式"),
                    _axisRows.Count);
                return;
            }

            string refreshTime = _lastRefreshTime.HasValue
                ? _lastRefreshTime.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : GetText("轴监控等待刷新");
            MonitorStatusText.Text = string.Format(
                GetText("轴监控连接状态格式"),
                _axisRows.Count,
                failedAxisCount,
                refreshTime) + (GetText("轴监控读取失败") == "读取失败" ? " · 界面轮次完成时间；设备采样时间/逐字段质量未提供" : " · UI round completion; device sample time / field quality not provided");
        }

        /// <summary>按中文资源键读取当前语言对应的文字。</summary>
        private string GetText(string resourceKey)
        {
            object value = FindResource(resourceKey);
            return value == null ? resourceKey : value.ToString();
        }
    }
}
