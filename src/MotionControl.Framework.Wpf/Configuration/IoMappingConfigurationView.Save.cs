using System;
using System.Linq;
using System.Threading;
using System.Windows;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>负责 IO 映射配置保存、控制卡重新连接和 Host 通知。</summary>
    public partial class IoMappingConfigurationView
    {
        /// <summary>检查通过后保存 DI/DO 映射，重新连接控制卡并应用新配置。</summary>
        private async void SaveConfigurationButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CommitGridEdits())
            {
                SetConfigurationStatus("IO映射表格输入无效");
                return;
            }

            if (!_configurationCanBeSaved)
            {
                SetConfigurationStatus("IO映射禁止保存");
                return;
            }

            string errorMessage;
            if (!TryValidateConfiguration(out errorMessage))
            {
                SetConfigurationStatusText(errorMessage, true);
                return;
            }

            MotionCardSettingsDocument cardSettings;
            try
            {
                cardSettings = _configurationStore.LoadJson<MotionCardSettingsDocument>(
                    _machineId,
                    MotionCardsFilePath);
                if (cardSettings == null || cardSettings.SchemaVersion != 1 || cardSettings.MotionCards == null)
                {
                    throw new InvalidOperationException("MotionCards.json 格式无效，不能应用新的 IO 映射。");
                }
            }
            catch (Exception error)
            {
                SetConfigurationStatus("IO映射控制卡读取失败", error.Message);
                return;
            }

            if (MessageBox.Show(
                (string)FindResource("IO映射保存并应用确认内容"),
                (string)FindResource("IO映射保存并应用确认标题"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                SetConfigurationStatus("IO映射尚未保存");
                return;
            }

            var configuration = new IoMappingConfigurationDocument
            {
                DigitalInputs = DigitalInputs
                    .Where(point => !string.IsNullOrWhiteSpace(point.Name))
                    .Select(CopyPoint)
                    .ToList(),
                DigitalOutputs = DigitalOutputs
                    .Where(point => !string.IsNullOrWhiteSpace(point.Name))
                    .Select(CopyPoint)
                    .ToList()
            };

            bool configurationSaved = false;
            bool reconnectSucceeded = false;
            bool reconnectLeftCardDisconnected = false;
            _configurationApplyInProgress = true;
            UpdateConnectionState();
            try
            {
                _configurationStore.SaveJson(_machineId, IoMappingFilePath, configuration);
                configurationSaved = true;

                await _cardConnection.ReconnectAsync(cardSettings, CancellationToken.None);
                reconnectSucceeded = true;
                SetConfigurationStatus("IO映射保存成功");
            }
            catch (Exception error)
            {
                reconnectLeftCardDisconnected = !_cardConnection.IsConnected;
                SetConfigurationStatus(
                    configurationSaved ? "IO映射保存并应用失败格式" : "IO映射保存失败",
                    error.Message);
            }
            finally
            {
                _configurationApplyInProgress = false;
                UpdateConnectionState();

                if (reconnectSucceeded || reconnectLeftCardDisconnected)
                {
                    NotifyCardConnectionChanged();
                }
            }
        }

        /// <summary>控制卡重新连接后通知 Host 按新映射重建运行时和手动调试页。</summary>
        private void NotifyCardConnectionChanged()
        {
            EventHandler handler = CardConnectionChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
