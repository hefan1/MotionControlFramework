using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Framework.Wpf.ManualDebug
{
    /// <summary>按物理 DI/DO 通道显示状态，并允许调试人员点击 DO 灯切换输出。</summary>
    public partial class IoMonitorView : UserControl
    {
        /// <summary>通道行的固定显示高度，用于计算每列可容纳的通道数。</summary>
        private const double ChannelRowHeight = 30.0;

        /// <summary>每列外框、内边距和上下留白占用的高度。</summary>
        private const double ChannelColumnChromeHeight = 12.0;

        /// <summary>提供连接卡的可读取 DI/DO 通道范围。</summary>
        private readonly IMotionCardIoCapabilityProvider _capabilityProvider;

        /// <summary>提供厂商适配器实现的原始物理通道读写。</summary>
        private readonly IPhysicalDigitalIoMonitor _physicalIoMonitor;

        /// <summary>执行 DO 手动写入时检查连接、自动流程和急停联锁。</summary>
        private readonly MachineRuntime _machineRuntime;

        /// <summary>当前设备配置的 DI 映射，用于给物理通道显示逻辑名称和极性。</summary>
        private readonly IReadOnlyList<DigitalIoHardwareDefinition> _digitalInputs;

        /// <summary>当前设备配置的 DO 映射，用于显示名称并按有效电平切换输出。</summary>
        private readonly IReadOnlyList<DigitalIoHardwareDefinition> _digitalOutputs;

        /// <summary>按物理通道顺序保存完整 DI 列表，供列布局和状态轮询使用。</summary>
        private readonly ObservableCollection<IoMonitorChannel> _inputChannels =
            new ObservableCollection<IoMonitorChannel>();

        /// <summary>按物理通道顺序保存完整 DO 列表，供列布局和状态轮询使用。</summary>
        private readonly ObservableCollection<IoMonitorChannel> _outputChannels =
            new ObservableCollection<IoMonitorChannel>();

        /// <summary>按页面高度分组后的 DI 列表；每组显示为一列并可横向浏览。</summary>
        private readonly ObservableCollection<ObservableCollection<IoMonitorChannel>> _inputColumns =
            new ObservableCollection<ObservableCollection<IoMonitorChannel>>();

        /// <summary>按页面高度分组后的 DO 列表；每组显示为一列并可横向浏览。</summary>
        private readonly ObservableCollection<ObservableCollection<IoMonitorChannel>> _outputColumns =
            new ObservableCollection<ObservableCollection<IoMonitorChannel>>();

        /// <summary>每 500 毫秒刷新当前标签页中已经显示的物理通道。</summary>
        private readonly DispatcherTimer _refreshTimer;

        /// <summary>标记卡连接状态；页面不连接或断开控制卡。</summary>
        private readonly bool _isCardConnected;

        /// <summary>是否已根据适配器报告的可读范围建立通道列表。</summary>
        private bool _channelsInitialized;

        /// <summary>标记当前是否正在执行通道范围加载或状态刷新。</summary>
        private bool _refreshInProgress;

        /// <summary>缓存最近一次通道范围读取错误，用于页面状态栏说明。</summary>
        private string _capabilityError;

        /// <summary>当前适配器报告的 DI 可读通道数。</summary>
        private int _inputChannelCount;

        /// <summary>当前适配器报告的 DO 可读通道数。</summary>
        private int _outputChannelCount;

        /// <summary>每列显示的通道行数；页面高度变化时重新计算。</summary>
        private int _rowsPerColumn;

        /// <summary>创建物理 IO 监控页；此页面只使用现有连接，不创建或重连控制卡。</summary>
        public IoMonitorView(
            bool isCardConnected,
            MachineRuntime machineRuntime,
            IMotionCardIoCapabilityProvider capabilityProvider,
            IPhysicalDigitalIoMonitor physicalIoMonitor,
            IEnumerable<DigitalIoHardwareDefinition> digitalInputs,
            IEnumerable<DigitalIoHardwareDefinition> digitalOutputs)
        {
            InitializeComponent();

            _isCardConnected = isCardConnected;
            _machineRuntime = machineRuntime;
            _capabilityProvider = capabilityProvider;
            _physicalIoMonitor = physicalIoMonitor;
            _digitalInputs = digitalInputs == null
                ? new List<DigitalIoHardwareDefinition>().AsReadOnly()
                : digitalInputs.ToList().AsReadOnly();
            _digitalOutputs = digitalOutputs == null
                ? new List<DigitalIoHardwareDefinition>().AsReadOnly()
                : digitalOutputs.ToList().AsReadOnly();

            RefreshLocalizedText();
            UpdateOutputControlAvailability();

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += RefreshTimer_Tick;
            Loaded += IoMonitorView_Loaded;
            Unloaded += IoMonitorView_Unloaded;
        }

        /// <summary>重新应用当前语言资源，更新通道地址、状态和页签数量。</summary>
        public void RefreshLocalizedText()
        {
            foreach (IoMonitorChannel channel in _inputChannels)
            {
                RefreshChannelLocalizedText(channel, true);
            }

            foreach (IoMonitorChannel channel in _outputChannels)
            {
                RefreshChannelLocalizedText(channel, false);
            }

            DigitalInputTab.Header = string.Format(
                GetText("IO监控分页标题格式"),
                GetText("数字输入"),
                _inputChannelCount);
            DigitalOutputTab.Header = string.Format(
                GetText("IO监控分页标题格式"),
                GetText("数字输出"),
                _outputChannelCount);
            UpdateMonitorStatusText();
            UpdateDisplayedRangeText();
        }

        /// <summary>页面首次显示时读取通道范围并启动可见行状态刷新。</summary>
        private async void IoMonitorView_Loaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Start();
            if (!_channelsInitialized)
            {
                await LoadChannelRangesAsync().ConfigureAwait(true);
            }

            await RefreshVisibleChannelsAsync().ConfigureAwait(true);
        }

        /// <summary>离开监控页后暂停轮询，减少对控制卡通信的占用。</summary>
        private void IoMonitorView_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
        }

        /// <summary>按当前 DI/DO 标签页定时刷新屏幕中可见的通道行。</summary>
        private async void RefreshTimer_Tick(object sender, EventArgs e)
        {
            await RefreshVisibleChannelsAsync().ConfigureAwait(true);
        }

        /// <summary>用户点击刷新时重新获取范围，或轮询当前可见通道状态。</summary>
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            OperationMessageText.Text = string.Empty;
            if (!_channelsInitialized)
            {
                await LoadChannelRangesAsync().ConfigureAwait(true);
            }

            await RefreshVisibleChannelsAsync().ConfigureAwait(true);
        }

        /// <summary>切换 DI/DO 标签页后立即读取新页面中可见的通道。</summary>
        private async void MonitorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || !ReferenceEquals(sender, MonitorTabs))
            {
                return;
            }

            UpdateDisplayedRangeText();
            await RefreshVisibleChannelsAsync().ConfigureAwait(true);
        }

        /// <summary>窗口高度变化时重新分组通道，保证每列在可视区域内完整显示。</summary>
        private void ChannelViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_channelsInitialized || e.NewSize.Height <= 0)
            {
                return;
            }

            int rowsPerColumn = GetRowsPerColumn(e.NewSize.Height);
            if (rowsPerColumn == _rowsPerColumn)
            {
                return;
            }

            _rowsPerColumn = rowsPerColumn;
            RebuildChannelColumns(_inputChannels, _inputColumns, _rowsPerColumn);
            RebuildChannelColumns(_outputChannels, _outputColumns, _rowsPerColumn);
            UpdateDisplayedRangeText();
        }

        /// <summary>水平滚动后更新顶部显示的物理通道号范围。</summary>
        private void ChannelColumns_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.HorizontalChange != 0)
            {
                UpdateDisplayedRangeText();
            }
        }

        /// <summary>读取适配器报告的连续可读通道数，并建立 DI/DO 物理通道列表。</summary>
        private async Task LoadChannelRangesAsync()
        {
            if (_refreshInProgress)
            {
                return;
            }

            if (!_isCardConnected)
            {
                _capabilityError = null;
                UpdateMonitorStatusText();
                return;
            }

            if (_capabilityProvider == null || _physicalIoMonitor == null)
            {
                _capabilityError = GetText("IO监控适配器不支持");
                UpdateMonitorStatusText();
                return;
            }

            _refreshInProgress = true;
            MonitorStatusText.Text = GetText("IO监控读取通道中");
            try
            {
                MotionCardIoCapabilities capabilities = await _capabilityProvider
                    .ReadIoCapabilitiesAsync(CancellationToken.None)
                    .ConfigureAwait(true);
                if (capabilities == null)
                {
                    throw new InvalidOperationException(GetText("IO监控能力为空"));
                }

                _inputChannelCount = GetReadableChannelCount(capabilities.DigitalInputChannelRange, "DI");
                _outputChannelCount = GetReadableChannelCount(capabilities.DigitalOutputChannelRange, "DO");
                BuildChannelRows(_inputChannels, _digitalInputs, _inputChannelCount, true);
                BuildChannelRows(_outputChannels, _digitalOutputs, _outputChannelCount, false);
                _rowsPerColumn = GetRowsPerColumn(DigitalInputViewport.ActualHeight);
                RebuildChannelColumns(_inputChannels, _inputColumns, _rowsPerColumn);
                RebuildChannelColumns(_outputChannels, _outputColumns, _rowsPerColumn);
                DigitalInputList.ItemsSource = _inputColumns;
                DigitalOutputList.ItemsSource = _outputColumns;
                NoDigitalInputsText.Visibility = _inputChannelCount == 0 ? Visibility.Visible : Visibility.Collapsed;
                NoDigitalOutputsText.Visibility = _outputChannelCount == 0 ? Visibility.Visible : Visibility.Collapsed;
                _channelsInitialized = true;
                _capabilityError = null;
                RefreshLocalizedText();
                await Dispatcher.InvokeAsync(
                    UpdateDisplayedRangeText,
                    DispatcherPriority.Loaded);
            }
            catch (Exception error)
            {
                _capabilityError = error.Message;
                UpdateMonitorStatusText();
            }
            finally
            {
                _refreshInProgress = false;
            }
        }

        /// <summary>检查适配器报告的可读通道数，缺少范围时给出明确错误。</summary>
        private int GetReadableChannelCount(MotionCardIoChannelRange range, string ioType)
        {
            if (range == null || !range.MappableChannelCount.HasValue)
            {
                throw new InvalidOperationException(string.Format(
                    GetText("IO监控通道范围未提供"),
                    ioType));
            }

            if (range.MappableChannelCount.Value < 0)
            {
                throw new InvalidOperationException(string.Format(
                    GetText("IO监控通道范围无效"),
                    ioType,
                    range.MappableChannelCount.Value));
            }

            return range.MappableChannelCount.Value;
        }

        /// <summary>为适配器可读范围内的每路通道匹配 IO 映射名称和有效电平。</summary>
        private void BuildChannelRows(
            ObservableCollection<IoMonitorChannel> destination,
            IReadOnlyList<DigitalIoHardwareDefinition> mappings,
            int channelCount,
            bool isInput)
        {
            destination.Clear();
            Dictionary<int, DigitalIoHardwareDefinition> mappingByChannel = mappings
                .Where(point => point != null && point.Channel >= 0 && point.Channel < channelCount)
                .GroupBy(point => point.Channel)
                .ToDictionary(group => group.Key, group => group.First());

            for (int channel = 0; channel < channelCount; channel++)
            {
                DigitalIoHardwareDefinition mapping;
                bool isMapped = mappingByChannel.TryGetValue(channel, out mapping)
                    && !string.IsNullOrWhiteSpace(mapping.Name);
                var row = new IoMonitorChannel(
                    channel,
                    isMapped ? mapping.Name : string.Empty,
                    isMapped && mapping.ActiveHigh,
                    isMapped,
                    !isInput);
                RefreshChannelLocalizedText(row, isInput);
                row.SetOutputControlAvailable(!isInput && _isCardConnected && _physicalIoMonitor != null
                    && _machineRuntime != null);
                destination.Add(row);
            }
        }

        /// <summary>刷新当前标签页已显示列中的通道，避免轮询未显示的 IO 点。</summary>
        private async Task RefreshVisibleChannelsAsync()
        {
            UpdateOutputControlAvailability();
            if (_refreshInProgress || !_isCardConnected || _physicalIoMonitor == null || !_channelsInitialized)
            {
                UpdateMonitorStatusText();
                return;
            }

            _refreshInProgress = true;
            try
            {
                bool isInput = MonitorTabs.SelectedIndex == 0;
                ListBox channelList = isInput ? DigitalInputList : DigitalOutputList;
                List<IoMonitorChannel> visibleChannels = GetVisibleChannels(channelList);
                foreach (IoMonitorChannel channel in visibleChannels)
                {
                    try
                    {
                        bool physicalHigh = isInput
                            ? await _physicalIoMonitor.ReadDigitalInputChannelAsync(channel.Channel, CancellationToken.None)
                                .ConfigureAwait(true)
                            : await _physicalIoMonitor.ReadDigitalOutputChannelAsync(channel.Channel, CancellationToken.None)
                                .ConfigureAwait(true);
                        SetChannelReadSuccess(channel, physicalHigh);
                    }
                    catch (Exception error)
                    {
                        channel.SetReadFailure(error.Message, GetText("IO读取失败"));
                    }
                }

                UpdateMonitorStatusText();
            }
            finally
            {
                _refreshInProgress = false;
            }
        }

        /// <summary>只返回当前 ListBox 已创建并显示的列内通道，不读取屏外的通道。</summary>
        private static List<IoMonitorChannel> GetVisibleChannels(ListBox channelList)
        {
            var visibleChannels = new List<IoMonitorChannel>();
            for (int index = 0; index < channelList.Items.Count; index++)
            {
                var container = channelList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
                if (container == null || !container.IsVisible)
                {
                    continue;
                }

                var columnChannels = container.DataContext as ObservableCollection<IoMonitorChannel>;
                if (columnChannels != null)
                {
                    visibleChannels.AddRange(columnChannels);
                }
            }

            return visibleChannels;
        }

        /// <summary>按页面可用高度计算每列行数，并预留水平滚动条和列边框空间。</summary>
        private static int GetRowsPerColumn(double viewportHeight)
        {
            double availableHeight = viewportHeight
                - SystemParameters.HorizontalScrollBarHeight
                - ChannelColumnChromeHeight;
            return Math.Max(1, (int)Math.Floor(availableHeight / ChannelRowHeight));
        }

        /// <summary>把连续物理通道按每列行数分组，通道号按列从上到下递增。</summary>
        private static void RebuildChannelColumns(
            IReadOnlyList<IoMonitorChannel> channels,
            ObservableCollection<ObservableCollection<IoMonitorChannel>> columns,
            int rowsPerColumn)
        {
            columns.Clear();
            if (rowsPerColumn <= 0)
            {
                return;
            }

            for (int firstChannel = 0; firstChannel < channels.Count; firstChannel += rowsPerColumn)
            {
                var column = new ObservableCollection<IoMonitorChannel>();
                int lastChannelExclusive = Math.Min(firstChannel + rowsPerColumn, channels.Count);
                for (int index = firstChannel; index < lastChannelExclusive; index++)
                {
                    column.Add(channels[index]);
                }

                columns.Add(column);
            }
        }

        /// <summary>显示当前标签页已呈现的首末通道号，便于操作员横向查找 IO 点。</summary>
        private void UpdateDisplayedRangeText()
        {
            if (MonitorRangeText == null || MonitorTabs == null)
            {
                return;
            }

            bool isInput = MonitorTabs.SelectedIndex == 0;
            ListBox channelList = isInput ? DigitalInputList : DigitalOutputList;
            List<IoMonitorChannel> visibleChannels = GetVisibleChannels(channelList);
            if (visibleChannels.Count == 0)
            {
                MonitorRangeText.Text = string.Empty;
                return;
            }

            int firstChannel = visibleChannels.Min(channel => channel.Channel);
            int lastChannel = visibleChannels.Max(channel => channel.Channel);
            string addressPrefix = GetText(isInput ? "DI通道地址前缀" : "DO通道地址前缀");
            MonitorRangeText.Text = string.Format(
                GetText("IO监控当前显示范围格式"),
                addressPrefix,
                firstChannel,
                lastChannel);
        }

        /// <summary>把原始物理电平转换为映射有效状态或未映射的高低电平显示。</summary>
        private void SetChannelReadSuccess(IoMonitorChannel channel, bool physicalHigh)
        {
            channel.SetReadSuccess(
                physicalHigh,
                GetText("IO有效"),
                GetText("IO无效"),
                GetText("IO物理高电平"),
                GetText("IO物理低电平"));
        }

        /// <summary>按当前 Runtime 状态设置各个 DO 灯能否操作。</summary>
        private void UpdateOutputControlAvailability()
        {
            bool available = _isCardConnected
                && _physicalIoMonitor != null
                && _machineRuntime != null
                && _machineRuntime.State != MachineRunState.Running
                && _machineRuntime.State != MachineRunState.Pausing;
            foreach (IoMonitorChannel channel in _outputChannels)
            {
                channel.SetOutputControlAvailable(available);
            }
        }

        /// <summary>点击 DO 状态灯后按当前逻辑状态反向写入，并立即回读物理电平。</summary>
        private async void OutputIndicator_Click(object sender, RoutedEventArgs e)
        {
            Button indicatorButton = sender as Button;
            IoMonitorChannel channel = indicatorButton == null
                ? null
                : indicatorButton.Tag as IoMonitorChannel;
            if (_refreshInProgress || channel == null || !channel.PhysicalHigh.HasValue || _machineRuntime == null
                || _physicalIoMonitor == null)
            {
                return;
            }

            bool nextState = !channel.IsOn;
            bool targetPhysicalHigh = channel.IsMapped
                ? nextState == channel.ActiveHigh
                : nextState;
            bool actualPhysicalHigh = false;
            bool readbackAvailable = false;
            bool writeStarted = false;
            string readbackMismatchText = GetText("DO写入后读回不一致");
            _refreshInProgress = true;
            channel.SetWriting(true);
            OperationMessageText.Text = string.Empty;
            try
            {
                ManualOperationBlockReason blockReason = await _machineRuntime.RunManualOperationAsync(async () =>
                {
                    writeStarted = true;
                    await _physicalIoMonitor.WriteDigitalOutputChannelAsync(
                        channel.Channel,
                        targetPhysicalHigh,
                        CancellationToken.None).ConfigureAwait(false);
                    actualPhysicalHigh = await _physicalIoMonitor
                        .ReadDigitalOutputChannelAsync(channel.Channel, CancellationToken.None)
                        .ConfigureAwait(false);
                    readbackAvailable = true;
                    if (actualPhysicalHigh != targetPhysicalHigh)
                    {
                        throw new InvalidOperationException(readbackMismatchText);
                    }
                }, CancellationToken.None).ConfigureAwait(true);

                if (blockReason != ManualOperationBlockReason.None)
                {
                    throw new InvalidOperationException(GetManualOperationBlockText(blockReason));
                }

                SetChannelReadSuccess(channel, actualPhysicalHigh);
                OperationMessageText.Text = string.Format(
                    GetText("IO监控DO切换成功"),
                    channel.Address,
                    GetText(targetPhysicalHigh ? "IO物理高电平" : "IO物理低电平"));
            }
            catch (Exception error)
            {
                if (readbackAvailable)
                {
                    SetChannelReadSuccess(channel, actualPhysicalHigh);
                }
                else if (writeStarted)
                {
                    channel.SetReadFailure(error.Message, GetText("IO读取失败"));
                }

                OperationMessageText.Text = string.Format(
                    GetText("操作失败"),
                    channel.Address,
                    error.Message);
            }
            finally
            {
                channel.SetWriting(false);
                _refreshInProgress = false;
                UpdateOutputControlAvailability();
            }
        }

        /// <summary>把 Runtime 手动操作拦截原因转换成操作员能读懂的提示。</summary>
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

        /// <summary>更新单个通道行的本地化物理地址、名称和状态。</summary>
        private void RefreshChannelLocalizedText(IoMonitorChannel channel, bool isInput)
        {
            channel.RefreshLocalizedText(
                GetText(isInput ? "DI通道地址前缀" : "DO通道地址前缀"),
                GetText("IO未命名"),
                GetText("IO有效"),
                GetText("IO无效"),
                GetText("IO物理高电平"),
                GetText("IO物理低电平"),
                GetText("IO等待读取"),
                GetText("IO读取失败"));
        }

        /// <summary>更新顶部连接状态及当前已探测的 DI/DO 路数。</summary>
        private void UpdateMonitorStatusText()
        {
            if (!_isCardConnected)
            {
                MonitorStatusText.Text = GetText("控制卡未连接");
                return;
            }

            if (_capabilityProvider == null || _physicalIoMonitor == null)
            {
                MonitorStatusText.Text = GetText("IO监控适配器不支持");
                return;
            }

            if (!string.IsNullOrWhiteSpace(_capabilityError))
            {
                MonitorStatusText.Text = string.Format(GetText("IO监控能力读取失败"), _capabilityError);
                return;
            }

            if (!_channelsInitialized)
            {
                MonitorStatusText.Text = GetText("IO监控读取通道中");
                return;
            }

            MonitorStatusText.Text = string.Format(
                GetText("IO监控通道数量格式"),
                _inputChannelCount,
                _outputChannelCount);
        }

        /// <summary>读取当前页面语言资源，资源缺失时返回资源键以便定位。</summary>
        private string GetText(string resourceKey)
        {
            object resourceValue = TryFindResource(resourceKey);
            return resourceValue == null ? resourceKey : resourceValue.ToString();
        }
    }
}
