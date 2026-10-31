using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>负责保存 IO 映射、热更新当前连接并通知 Host 刷新页面。</summary>
    public partial class IoMappingConfigurationView
    {
        /// <summary>检查通过后保存 DI/DO 映射，并在当前控制卡连接内应用新配置。</summary>
        private async void SaveConfigurationButton_Click(object sender, RoutedEventArgs e)
        {
            if (_ioMappingCardConnection == null)
            {
                SetConfigurationStatus("IO映射控制卡不支持热更新");
                return;
            }

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

            IoMappingConfigurationDocument previousConfiguration;
            string mappingPath = GetConfigurationPath(IoMappingFilePath);
            bool previousConfigurationExists;
            try
            {
                previousConfigurationExists = File.Exists(mappingPath);
                if (previousConfigurationExists)
                {
                    previousConfiguration = _configurationStore.LoadJson<IoMappingConfigurationDocument>(
                        _machineId,
                        IoMappingFilePath);
                    ValidateDocumentVersion(previousConfiguration);
                }
                else
                {
                    previousConfiguration = new IoMappingConfigurationDocument();
                }
            }
            catch (Exception error)
            {
                SetConfigurationStatus("IO映射读取失败", error.Message);
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

            bool configurationSaveAttempted = false;
            bool configurationSaved = false;
            bool mappingsApplied = false;
            bool operationCompleted = false;
            _configurationApplyInProgress = true;
            UpdateConnectionState();
            try
            {
                async Task SaveAndApplyAsync()
                {
                    configurationSaveAttempted = true;
                    _configurationStore.SaveJson(_machineId, IoMappingFilePath, configuration);
                    configurationSaved = true;
                    await _ioMappingCardConnection.ApplyIoMappingsAsync(
                        configuration.DigitalInputs,
                        configuration.DigitalOutputs,
                        CancellationToken.None);
                    mappingsApplied = true;
                }
                if (ApplyConnectedConfigurationAsync != null)
                    await ApplyConnectedConfigurationAsync(SaveAndApplyAsync, false, CancellationToken.None);
                else
                    await SaveAndApplyAsync();
                operationCompleted = true;
            }
            catch (Exception error)
            {
                if (configurationSaveAttempted && !mappingsApplied)
                {
                    try
                    {
                        if (previousConfigurationExists)
                        {
                            _configurationStore.SaveJson(_machineId, IoMappingFilePath, previousConfiguration);
                        }
                        else
                        {
                            File.Delete(mappingPath);
                        }
                    }
                    catch (Exception restoreError)
                    {
                        var restoreFailure = new AggregateException(
                            "IO 映射保存或应用失败，恢复原配置文件时也发生错误。",
                            error,
                            restoreError);
                        SetConfigurationStatus("IO映射恢复失败格式", restoreFailure.Message);
                        return;
                    }

                    string failureMessage = configurationSaved
                        ? "IO 映射未能应用，已恢复原配置。"
                        : "IO 映射保存失败，已恢复原配置。";
                    SetConfigurationStatus("IO映射保存失败", failureMessage + error.Message);
                }
                else
                {
                    SetConfigurationStatus("IO映射保存失败", error.Message);
                }
            }
            finally
            {
                _configurationApplyInProgress = false;
                UpdateConnectionState();
            }

            if (mappingsApplied)
            {
                if (operationCompleted)
                    SetConfigurationStatus("IO映射保存成功");
                NotifyIoMappingApplied();
            }
        }

        /// <summary>通知 Host 刷新当前映射相关的手动调试和安全配置页面。</summary>
        private void NotifyIoMappingApplied()
        {
            EventHandler handler = IoMappingApplied;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
