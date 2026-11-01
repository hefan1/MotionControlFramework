using System;
using System.Windows;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>提供输出反馈关系的独立编辑入口，不改变现有物理映射。</summary>
    public partial class IoMappingConfigurationView
    {
        /// <summary>关联配置保存成功后通知 Host 刷新 IO 监控显示。</summary>
        public event EventHandler FeedbackAssociationsChanged;

        /// <summary>按已保存的 IO 映射编辑反馈关系，避免引用尚未保存的点位。</summary>
        private void FeedbackAssociationButton_Click(object sender, RoutedEventArgs e)
        {
            bool saved;
            try
            {
                var mapping = _configurationStore.LoadJson<IoMappingConfigurationDocument>(_machineId, IoMappingFilePath);
                ValidateDocumentVersion(mapping);
                var store = new IoFeedbackAssociationStore(_configurationStore, _machineId);
                var selectedOutput = DigitalOutputsGrid.SelectedItem as DigitalIoHardwareDefinition;
                var window = new IoFeedbackAssociationWindow(store, mapping.DigitalInputs, mapping.DigitalOutputs,
                    selectedOutput?.Name)
                {
                    Owner = Window.GetWindow(this)
                };
                saved = window.ShowDialog() == true;
            }
            catch (Exception error)
            {
                MessageBox.Show(string.Format((string)FindResource("IO反馈读取失败"), error.Message),
                    (string)FindResource("IO反馈关联"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!saved) return;
            try { FeedbackAssociationsChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception error)
            {
                MessageBox.Show(string.Format((string)FindResource("IO反馈刷新失败"), error.Message),
                    (string)FindResource("IO反馈关联"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
