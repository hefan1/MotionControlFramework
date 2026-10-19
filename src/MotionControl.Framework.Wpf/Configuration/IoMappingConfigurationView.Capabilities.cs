using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>显示控制卡可查询的 DI/DO 通道数量与扫描诊断。</summary>
    public partial class IoMappingConfigurationView
    {
        /// <summary>当前适配器提供的可选 I/O 能力查询接口。</summary>
        private readonly IMotionCardIoCapabilityProvider _ioCapabilityProvider;

        /// <summary>当前连接卡最近一次读取到的能力，用于显示范围和校验物理通道。</summary>
        private MotionCardIoCapabilities _lastIoCapabilities;

        /// <summary>阻止 I/O 能力查询期间重复提交适配器请求。</summary>
        private bool _isReadingIoCapabilities;

        /// <summary>当前状态文字对应的中文资源键，用于切换语言后重新显示。</summary>
        private string _ioCapabilityStatusResourceKey;

        /// <summary>最近一次 I/O 能力读取异常，用于切换语言后重新显示本地化提示。</summary>
        private string _ioCapabilityFailureMessage;

        /// <summary>每次打开页面都刷新控制卡清单，取得轴映射页刚保存的卡标识。</summary>
        private async void IoMappingConfigurationView_Loaded(object sender, RoutedEventArgs e)
        {
            LoadControllerIds();
            UpdateConnectionState();
            await RefreshIoCapabilitiesAsync(false);
        }

        /// <summary>页面按钮触发一次控制卡 I/O 数量读取。</summary>
        private async void RefreshIoCapabilitiesButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshIoCapabilitiesAsync(true);
        }

        /// <summary>语言切换后用当前资源重新生成 I/O 能力摘要，不重复读取控制卡。</summary>
        public void RefreshLocalizedText()
        {
            UpdateIoCapabilityStatusText();
            RefreshIoMappingGridHeaders();
            RefreshConfigurationStatusText();
        }

        /// <summary>显示连接后缓存的 I/O 能力，或按用户要求重新读取控制器 API。</summary>
        private async Task RefreshIoCapabilitiesAsync(bool forceRefresh)
        {
            _lastIoCapabilities = null;
            _ioCapabilityFailureMessage = null;
            if (!_cardConnection.IsConnected)
            {
                _ioCapabilityStatusResourceKey = "控制卡Io数量未连接";
                UpdateIoCapabilityStatusText();
                UpdateConnectionState();
                return;
            }

            if (_ioCapabilityProvider == null)
            {
                _ioCapabilityStatusResourceKey = "控制卡Io数量能力不支持";
                UpdateIoCapabilityStatusText();
                UpdateConnectionState();
                return;
            }

            _isReadingIoCapabilities = true;
            _ioCapabilityStatusResourceKey = "控制卡Io数量读取中";
            UpdateConnectionState();
            UpdateIoCapabilityStatusText();

            try
            {
                MotionCardIoCapabilities capabilities = forceRefresh
                    ? await _ioCapabilityProvider.RefreshIoCapabilitiesAsync(CancellationToken.None)
                    : await _ioCapabilityProvider.ReadIoCapabilitiesAsync(CancellationToken.None);
                if (capabilities == null)
                {
                    throw new InvalidOperationException("控制卡适配器没有返回 I/O 能力信息。");
                }

                _lastIoCapabilities = capabilities;
                _ioCapabilityStatusResourceKey = null;
                UpdateIoCapabilityStatusText();
            }
            catch (Exception error)
            {
                _lastIoCapabilities = null;
                _ioCapabilityStatusResourceKey = "控制卡Io数量查询失败格式";
                _ioCapabilityFailureMessage = error.Message;
                UpdateIoCapabilityStatusText();
            }
            finally
            {
                _isReadingIoCapabilities = false;
                UpdateConnectionState();
            }
        }

        /// <summary>按缓存的能力结果或当前状态资源重新生成页面摘要文字。</summary>
        private void UpdateIoCapabilityStatusText()
        {
            if (_lastIoCapabilities != null)
            {
                MotionCardIoCapabilities capabilities = _lastIoCapabilities;
                string statusText = string.Format(
                    CultureInfo.CurrentCulture,
                    (string)FindResource("控制卡Io数量读取结果格式"),
                    FormatIoCount(capabilities.BuiltInDigitalInputCount),
                    FormatIoCount(capabilities.BuiltInDigitalOutputCount),
                    FormatIoCount(capabilities.MaximumDigitalInputCount),
                    FormatIoCount(capabilities.MaximumDigitalOutputCount),
                    FormatMappableIoRange(capabilities.DigitalInputChannelRange),
                    FormatMappableIoRange(capabilities.DigitalOutputChannelRange),
                    FormatIoCount(capabilities.ScannedBusNodeCount),
                    FormatIoCount(capabilities.ScannedBusDigitalInputCount),
                    FormatIoCount(capabilities.ScannedBusDigitalOutputCount));

                if (!string.IsNullOrWhiteSpace(capabilities.BusScanError))
                {
                    statusText += " " + string.Format(
                        CultureInfo.CurrentCulture,
                        (string)FindResource("控制卡Io总线扫描失败格式"),
                        capabilities.BusScanError);
                }

                IoCapabilityStatusText.Text = statusText;
                return;
            }

            if (string.Equals(
                _ioCapabilityStatusResourceKey,
                "控制卡Io数量查询失败格式",
                StringComparison.Ordinal))
            {
                IoCapabilityStatusText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    (string)FindResource(_ioCapabilityStatusResourceKey),
                    _ioCapabilityFailureMessage);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_ioCapabilityStatusResourceKey))
            {
                IoCapabilityStatusText.Text = (string)FindResource(_ioCapabilityStatusResourceKey);
            }
        }

        /// <summary>把尚未读取的可选数量转换成当前语言的提示文字。</summary>
        private string FormatIoCount(int? count)
        {
            return count.HasValue
                ? count.Value.ToString(CultureInfo.CurrentCulture)
                : (string)FindResource("控制卡Io数量未读取");
        }

        /// <summary>把连续可读通道数、首个失败地址和适配器说明整理成页面提示。</summary>
        private string FormatMappableIoRange(MotionCardIoChannelRange range)
        {
            if (range == null || !range.MappableChannelCount.HasValue)
            {
                return (string)FindResource("控制卡Io数量未读取");
            }

            string countFormatKey = range.ReachedProbeLimit
                ? "控制卡Io通道数量下限格式"
                : "控制卡Io通道数量格式";
            string countText = string.Format(
                CultureInfo.CurrentCulture,
                (string)FindResource(countFormatKey),
                range.MappableChannelCount.Value);

            if (!range.FirstUnavailableChannel.HasValue)
            {
                return countText;
            }

            string failureMessage = string.IsNullOrWhiteSpace(range.FirstUnavailableMessage)
                ? (string)FindResource("控制卡Io数量未读取")
                : range.FirstUnavailableMessage;

            return string.Format(
                CultureInfo.CurrentCulture,
                (string)FindResource("控制卡Io通道探测边界格式"),
                countText,
                range.FirstUnavailableChannel.Value,
                failureMessage);
        }

        /// <summary>只对当前能力查询对应的控制卡返回 DI 或 DO API 确认通道数。</summary>
        private int? GetMappableChannelCount(string controllerId, bool input)
        {
            if (_lastIoCapabilities == null
                || !string.Equals(
                    _lastIoCapabilities.ControllerId,
                    controllerId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            MotionCardIoChannelRange range = input
                ? _lastIoCapabilities.DigitalInputChannelRange
                : _lastIoCapabilities.DigitalOutputChannelRange;
            return range == null ? (int?)null : range.MappableChannelCount;
        }
    }
}
