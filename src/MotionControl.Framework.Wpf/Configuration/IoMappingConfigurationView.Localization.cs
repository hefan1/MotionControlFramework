using System.Globalization;
using System.Windows.Controls;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>刷新 IO 映射页面在语言切换后由代码生成的文字。</summary>
    public partial class IoMappingConfigurationView
    {
        /// <summary>当前底部状态使用的中文资源键。</summary>
        private string _configurationStatusResourceKey;

        /// <summary>当前底部状态格式化所用的参数。</summary>
        private object[] _configurationStatusArguments;

        /// <summary>当前底部状态为校验错误时保留的已格式化文字。</summary>
        private string _configurationStatusText;

        /// <summary>标记底部文字来自校验结果，语言变化时需要重新执行只读校验。</summary>
        private bool _configurationStatusIsValidationResult;

        /// <summary>按当前语言更新两个数据表格的列标题。</summary>
        private void RefreshIoMappingGridHeaders()
        {
            DigitalInputsGrid.Columns[0].Header = FindResource("逻辑名称");
            DigitalInputsGrid.Columns[1].Header = FindResource("控制卡编号");
            DigitalInputsGrid.Columns[2].Header = FindResource("物理通道");
            DigitalInputsGrid.Columns[3].Header = FindResource("有效电平");

            DigitalOutputsGrid.Columns[0].Header = FindResource("逻辑名称");
            DigitalOutputsGrid.Columns[1].Header = FindResource("控制卡编号");
            DigitalOutputsGrid.Columns[2].Header = FindResource("物理通道");
            DigitalOutputsGrid.Columns[3].Header = FindResource("有效电平");
        }

        /// <summary>按资源键保存并显示底部状态，使语言切换后可重新读取文字。</summary>
        private void SetConfigurationStatus(string resourceKey, params object[] arguments)
        {
            _configurationStatusResourceKey = resourceKey;
            _configurationStatusArguments = arguments;
            _configurationStatusText = null;
            _configurationStatusIsValidationResult = false;
            UpdateConfigurationStatusText();
        }

        /// <summary>保存并显示已格式化的校验结果，语言变化时会重新校验当前配置。</summary>
        private void SetConfigurationStatusText(string statusText, bool isValidationResult)
        {
            _configurationStatusResourceKey = null;
            _configurationStatusArguments = null;
            _configurationStatusText = statusText;
            _configurationStatusIsValidationResult = isValidationResult;
            ConfigurationStatusText.Text = statusText;
        }

        /// <summary>根据当前语言资源更新底部状态文字。</summary>
        private void RefreshConfigurationStatusText()
        {
            if (_configurationStatusIsValidationResult)
            {
                string errorMessage;
                if (!TryValidateConfiguration(out errorMessage))
                {
                    SetConfigurationStatusText(errorMessage, true);
                    return;
                }

                SetConfigurationStatus("IO映射检查通过");
                return;
            }

            UpdateConfigurationStatusText();
        }

        /// <summary>用当前语言格式重新生成最近一次资源状态文字。</summary>
        private void UpdateConfigurationStatusText()
        {
            if (string.IsNullOrWhiteSpace(_configurationStatusResourceKey))
            {
                if (_configurationStatusText != null)
                {
                    ConfigurationStatusText.Text = _configurationStatusText;
                }

                return;
            }

            string statusFormat = (string)FindResource(_configurationStatusResourceKey);
            if (_configurationStatusArguments == null || _configurationStatusArguments.Length == 0)
            {
                ConfigurationStatusText.Text = statusFormat;
                return;
            }

            ConfigurationStatusText.Text = string.Format(
                CultureInfo.CurrentCulture,
                statusFormat,
                _configurationStatusArguments);
        }
    }
}
