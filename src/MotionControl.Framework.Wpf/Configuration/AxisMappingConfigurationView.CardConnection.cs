using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>负责轴映射页面的控制卡连接、状态刷新和轴参数读回。</summary>
    public partial class AxisMappingConfigurationView
    {
        /// <summary>根据物理轴号选择当前编辑行。</summary>
        private async void AxisMappingGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectAxis(AxisMappingGrid.SelectedItem as AxisMappingEntry);
            ResetCardReadbackStatus();
            if (!_loadingConfiguration && _cardConnection.IsConnected)
            {
                await ReadSelectedAxisParametersAsync(false);
            }
        }

        /// <summary>首次配置时按用户选择连接控制卡，已保存配置则由页面启动流程调用。</summary>
        private async void ConnectCardButton_Click(object sender, RoutedEventArgs e)
        {
            await ConnectCardAsync(false);
        }

        /// <summary>按用户操作断开控制卡，并锁定轴映射编辑区。</summary>
        private async void DisconnectCardButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_cardConnection.IsConnected)
            {
                UpdateCardConnectionUiState();
                return;
            }

            _connectionInProgress = true;
            UpdateCardConnectionUiState();
            try
            {
                if (PrepareCardDisconnectAsync != null)
                    await PrepareCardDisconnectAsync(CancellationToken.None);
                await _cardConnection.DisconnectAsync(CancellationToken.None);
                SetCardConnectionStatus("控制卡连接已断开");
                ResetCardReadbackStatus();
            }
            catch (Exception error)
            {
                SetCardConnectionStatus("控制卡断开失败格式", error.Message);
            }
            finally
            {
                _connectionInProgress = false;
                UpdateCardConnectionUiState();
                // 拒绝断开时原会话仍在使用；不能通知 Host 按新连接重建 Runtime。
                if (!_cardConnection.IsConnected)
                    NotifyCardConnectionChanged();
            }
        }

        /// <summary>首次连接或按已保存配置自动连接，并在成功后读取当前选中物理轴参数。</summary>
        private async Task ConnectCardAsync(bool automatic)
        {
            if (_cardConnection.IsConnected)
            {
                UpdateCardConnectionUiState();
                await ReadSelectedAxisParametersAsync(false);
                NotifyCardConnectionChanged();
                return;
            }

            if (!_configurationCanBeSaved)
            {
                SetCardConnectionStatus("控制卡连接失败格式", (string)FindResource("轴映射读取失败"));
                return;
            }

            if (GetSelectedMotionCardOption() == null)
            {
                SetCardConnectionStatus("控制卡选项未接入");
                UpdateCardConnectionUiState();
                return;
            }

            _connectionInProgress = true;
            SetCardConnectionStatus("正在连接控制卡");
            UpdateCardConnectionUiState();
            try
            {
                await _cardConnection.ConnectAsync(_cardConfiguration, CancellationToken.None);
                SetCardConnectionStatus(
                    "控制卡连接成功格式",
                    _cardConnection.ActualControllerType,
                    _cardConnection.ActualControllerVersion);
                await ReadSelectedAxisParametersAsync(false);
            }
            catch (Exception error)
            {
                SetCardConnectionStatus("控制卡连接失败格式", error.Message);
                if (!automatic)
                {
                    MessageBox.Show(
                        error.Message,
                        (string)FindResource("连接运动控制卡"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                _connectionInProgress = false;
                UpdateCardConnectionUiState();
                NotifyCardConnectionChanged();
            }
        }

        /// <summary>依据真实连接状态启用参数编辑、轴映射保存和控制卡操作按钮。</summary>
        private void UpdateCardConnectionUiState()
        {
            if (CardConnectionStatusText == null)
            {
                return;
            }

            bool connected = _cardConnection.IsConnected;
            bool hasSupportedCard = GetSelectedMotionCardOption() != null;
            ConnectionIndicator.Fill = connected ? new SolidColorBrush(Color.FromRgb(35, 153, 85)) : new SolidColorBrush(Color.FromRgb(135, 151, 168));
            CardConnectionStatusBadge.Background = connected ? new SolidColorBrush(Color.FromRgb(232, 247, 237)) : new SolidColorBrush(Color.FromRgb(239, 244, 249));
            CardConnectionStatusText.Foreground = connected ? new SolidColorBrush(Color.FromRgb(28, 126, 70)) : new SolidColorBrush(Color.FromRgb(89, 107, 125));
            CardVendorComboBox.IsEnabled = !connected && !_connectionInProgress;
            CardTypeComboBox.IsEnabled = !connected && !_connectionInProgress;
            AxisCountComboBox.IsEnabled = !connected && !_connectionInProgress;
            ConnectCardButton.IsEnabled = !connected && !_connectionInProgress && hasSupportedCard;
            DisconnectCardButton.IsEnabled = connected && !_connectionInProgress;
            AxisMappingGrid.IsEnabled = !_connectionInProgress;
            AxisDetailsGrid.IsEnabled = !_connectionInProgress;
            ClearAxisMappingButton.IsEnabled = !_connectionInProgress;
            CheckMappingButton.IsEnabled = !_connectionInProgress;
            SaveConfigurationButton.IsEnabled = !_connectionInProgress && _configurationCanBeSaved;
            ReadAxisParametersButton.IsEnabled = connected && !_connectionInProgress;
        }

        /// <summary>读取当前物理轴的控制器参数，并在右侧以只读文本显示卡上实时值。</summary>
        private async Task<AxisMappingCardParameters> ReadSelectedAxisParametersAsync(bool showError)
        {
            if (!_cardConnection.IsConnected || SelectedAxis == null)
            {
                return null;
            }

            int axisNumber = SelectedAxis.AxisNumber;
            try
            {
                AxisMappingCardParameters parameters = await _cardConnection.ReadAxisParametersAsync(
                    axisNumber,
                    CancellationToken.None);
                if (parameters == null)
                {
                    throw new InvalidOperationException("控制卡适配器没有返回轴参数。");
                }

                _cardReadbackAttempted = true;
                _lastReadbackParameters = parameters;
                _lastReadbackError = null;
                _lastReadbackAxisNumber = axisNumber;
                RefreshCardReadbackText();

                return parameters;
            }
            catch (Exception error)
            {
                _cardReadbackAttempted = true;
                _lastReadbackParameters = null;
                _lastReadbackError = error.Message;
                _lastReadbackAxisNumber = axisNumber;
                RefreshCardReadbackText();
                if (showError)
                {
                    MessageBox.Show(
                        error.Message,
                        (string)FindResource("读取控制卡参数"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return null;
            }
        }

        /// <summary>把卡上软限位状态转换成当前界面的本地化文字。</summary>
        private string GetSoftLimitStateText(bool? isEnabled)
        {
            if (!isEnabled.HasValue)
            {
                return (string)FindResource("软限位状态无法判断");
            }

            return isEnabled.Value
                ? (string)FindResource("软限位已启用")
                : (string)FindResource("软限位未启用");
        }

        /// <summary>按用户操作读取卡上参数并回填当前轴编辑区，不自动保存配置或写卡。</summary>
        private async void ReadAxisParametersButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_cardConnection.IsConnected || SelectedAxis == null)
            {
                return;
            }

            AxisMappingEntry axis = SelectedAxis;
            _connectionInProgress = true;
            UpdateCardConnectionUiState();
            try
            {
                AxisMappingCardParameters parameters = await ReadSelectedAxisParametersAsync(true);
                if (parameters == null)
                {
                    return;
                }

                var invalidValues = new List<string>();
                CopyPositiveValue(
                    parameters.UnitsPerEngineeringUnit,
                    value => axis.UnitsPerEngineeringUnit = value,
                    (string)FindResource("单位换算"),
                    invalidValues);

                bool? controllerSoftLimitEnabled = GetCombinedSoftLimitState(parameters);
                if (controllerSoftLimitEnabled.HasValue)
                {
                    axis.SoftLimitEnabled = controllerSoftLimitEnabled;
                    if (controllerSoftLimitEnabled.Value)
                    {
                        if (IsValidSoftLimitRange(parameters.MinimumPosition, parameters.MaximumPosition))
                        {
                            axis.MinimumPosition = parameters.MinimumPosition;
                            axis.MaximumPosition = parameters.MaximumPosition;
                        }
                        else
                        {
                            invalidValues.Add(
                                (string)FindResource("软限位下限") + " / "
                                + (string)FindResource("软限位上限"));
                        }
                    }
                }
                else
                {
                    invalidValues.Add("软限位状态");
                }
                UpdateSoftLimitEnabledEditor(axis);

                CopyPositiveValue(
                    parameters.Speed,
                    value => axis.HomeSearchSpeed = value,
                    (string)FindResource("回零搜索速度"),
                    invalidValues);
                CopyPositiveValue(
                    parameters.CreepSpeed,
                    value => axis.HomeCreepSpeed = value,
                    (string)FindResource("回零爬行速度"),
                    invalidValues);
                CopyPositiveValue(
                    parameters.Acceleration,
                    value => axis.HomeAcceleration = value,
                    (string)FindResource("回零加速度"),
                    invalidValues);
                CopyPositiveValue(
                    parameters.Deceleration,
                    value => axis.HomeDeceleration = value,
                    (string)FindResource("回零减速度"),
                    invalidValues);

                if (invalidValues.Count == 0)
                {
                    SetConfigurationStatus("轴参数读卡回填完成格式", axis.AxisNumber);
                }
                else
                {
                    SetConfigurationStatus(
                        "轴参数读卡回填部分格式",
                        axis.AxisNumber,
                        string.Join("、", invalidValues.Distinct()));
                }

                if (controllerSoftLimitEnabled == false)
                {
                    _configurationStatusShowsSoftLimitNotice = true;
                    RefreshConfigurationStatusText();
                }
            }
            finally
            {
                _connectionInProgress = false;
                UpdateCardConnectionUiState();
            }
        }


    }
}
