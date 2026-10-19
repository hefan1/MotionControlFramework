using System;
using System.Globalization;
using System.Windows.Controls;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>刷新轴映射页中由代码生成的多语言文字。</summary>
    public partial class AxisMappingConfigurationView
    {
        /// <summary>当前配置状态对应的资源键，语言切换时用它重新显示。</summary>
        private string _configurationStatusResourceKey;

        /// <summary>当前配置状态格式化时使用的参数。</summary>
        private object[] _configurationStatusArguments = new object[0];

        /// <summary>当前配置状态是否显示最近一次检查发现的校验错误。</summary>
        private bool _configurationStatusShowsValidationError;

        /// <summary>读卡回填状态是否需要附加软限位停用提示。</summary>
        private bool _configurationStatusShowsSoftLimitNotice;

        /// <summary>当前控制卡连接状态对应的资源键。</summary>
        private string _cardConnectionStatusResourceKey = "控制卡连接已断开";

        /// <summary>当前控制卡连接状态格式化时使用的参数。</summary>
        private object[] _cardConnectionStatusArguments = new object[0];

        /// <summary>最近一次从控制卡读到的轴参数，语言切换时不重新访问控制卡。</summary>
        private AxisMappingCardParameters _lastReadbackParameters;

        /// <summary>最近一次读取轴参数失败时的错误文字。</summary>
        private string _lastReadbackError;

        /// <summary>最近一次读取的物理轴号，确保延迟返回结果仍显示正确轴号。</summary>
        private int _lastReadbackAxisNumber = -1;

        /// <summary>当前轴是否已经执行过一次参数读取。</summary>
        private bool _cardReadbackAttempted;

        /// <summary>语言切换后刷新轴映射页生成的文字，不执行运动或控制卡读写。</summary>
        public void RefreshLocalizedText()
        {
            if (AxisMappingGrid == null)
            {
                return;
            }

            RefreshAxisMappingGridHeaders();
            UpdateConfiguredAxisCountText();
            UpdateSelectedAxisText();
            UpdateConfigurationFileText();

            bool wasLoadingConfiguration = _loadingConfiguration;
            _loadingConfiguration = true;
            try
            {
                InitializeSupportedCardVendorItems();
                SelectComboBoxItem(CardVendorComboBox, _cardConfiguration.MotionCardVendor);
                PopulateCardTypeItems(_cardConfiguration.MotionCardVendor, _cardConfiguration.MotionCardType);
                UpdateHomeModeEditor(SelectedAxis);
            }
            finally
            {
                _loadingConfiguration = wasLoadingConfiguration;
            }

            RefreshCardConnectionStatusText();
            RefreshCardReadbackText();
            RefreshConfigurationStatusText();
        }

        /// <summary>按当前语言重设轴表格列标题。</summary>
        private void RefreshAxisMappingGridHeaders()
        {
            string[] resourceKeys =
            {
                "物理轴号", "设备轴名称", "轴类型", "工程单位", "单位换算",
                "软限位下限", "软限位上限", "配置状态"
            };

            int headerCount = Math.Min(AxisMappingGrid.Columns.Count, resourceKeys.Length);
            for (int columnIndex = 0; columnIndex < headerCount; columnIndex++)
            {
                AxisMappingGrid.Columns[columnIndex].Header = FindResource(resourceKeys[columnIndex]);
            }
        }

        /// <summary>按当前语言重新显示配置文件所在路径。</summary>
        private void UpdateConfigurationFileText()
        {
            if (ConfigurationFileText == null || _configurationStore == null)
            {
                return;
            }

            string machineDirectory = _configurationStore.GetMachineDirectory(_machineId);
            string axisConfigurationPath = System.IO.Path.Combine(
                machineDirectory,
                AxisParametersFilePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            string cardConfigurationPath = System.IO.Path.Combine(
                machineDirectory,
                MotionCardsFilePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            ConfigurationFileText.Text = string.Format(
                CultureInfo.CurrentCulture,
                (string)FindResource("轴映射保存路径格式"),
                cardConfigurationPath,
                axisConfigurationPath);
        }

        /// <summary>按当前选择的物理轴重新显示右侧标题。</summary>
        private void UpdateSelectedAxisText()
        {
            if (SelectedAxisText == null)
            {
                return;
            }

            SelectedAxisText.Text = SelectedAxis == null
                ? string.Empty
                : string.Format(
                    CultureInfo.CurrentCulture,
                    (string)FindResource("当前选择物理轴格式"),
                    SelectedAxis.AxisNumber);
        }

        /// <summary>保存并立即显示一个可在语言切换后重建的配置状态。</summary>
        private void SetConfigurationStatus(string resourceKey, params object[] arguments)
        {
            _configurationStatusResourceKey = resourceKey;
            _configurationStatusArguments = arguments ?? new object[0];
            _configurationStatusShowsValidationError = false;
            _configurationStatusShowsSoftLimitNotice = false;
            RefreshConfigurationStatusText();
        }

        /// <summary>记录当前显示的是映射检查错误，并按当前语言重新验证错误文字。</summary>
        private void SetConfigurationValidationStatus()
        {
            _configurationStatusShowsValidationError = true;
            _configurationStatusShowsSoftLimitNotice = false;
            RefreshConfigurationStatusText();
        }

        /// <summary>保存并立即显示一个可在语言切换后重建的控制卡连接状态。</summary>
        private void SetCardConnectionStatus(string resourceKey, params object[] arguments)
        {
            _cardConnectionStatusResourceKey = resourceKey;
            _cardConnectionStatusArguments = arguments ?? new object[0];
            RefreshCardConnectionStatusText();
        }

        /// <summary>清除当前轴的旧读卡结果，并显示尚未读取状态。</summary>
        private void ResetCardReadbackStatus()
        {
            _cardReadbackAttempted = false;
            _lastReadbackParameters = null;
            _lastReadbackError = null;
            _lastReadbackAxisNumber = -1;
            RefreshCardReadbackText();
        }

        /// <summary>使用当前语言重新显示控制卡连接状态及原有诊断参数。</summary>
        private void RefreshCardConnectionStatusText()
        {
            if (CardConnectionStatusText == null || string.IsNullOrWhiteSpace(_cardConnectionStatusResourceKey))
            {
                return;
            }

            CardConnectionStatusText.Text = FormatResourceText(
                _cardConnectionStatusResourceKey,
                _cardConnectionStatusArguments);
        }

        /// <summary>使用当前语言重新显示配置状态，不重新保存配置或操作控制卡。</summary>
        private void RefreshConfigurationStatusText()
        {
            if (ConfigurationStatusText == null)
            {
                return;
            }

            if (_configurationStatusShowsValidationError)
            {
                var errors = ValidateConfiguredAxes();
                ConfigurationStatusText.Text = errors.Count > 0
                    ? errors[0]
                    : (string)FindResource("轴映射尚未保存");
                return;
            }

            if (string.IsNullOrWhiteSpace(_configurationStatusResourceKey))
            {
                ConfigurationStatusText.Text = (string)FindResource("轴映射尚未保存");
                return;
            }

            ConfigurationStatusText.Text = FormatResourceText(
                _configurationStatusResourceKey,
                _configurationStatusArguments);
            if (_configurationStatusShowsSoftLimitNotice)
            {
                ConfigurationStatusText.Text += " " + (string)FindResource("软限位停用时未回填范围");
            }
        }

        /// <summary>使用当前语言重画最近一次读卡结果，不向控制卡再次发起读取。</summary>
        private void RefreshCardReadbackText()
        {
            if (CardReadbackText == null || CardSoftLimitStatusText == null)
            {
                return;
            }

            if (!_cardReadbackAttempted)
            {
                CardReadbackText.Text = (string)FindResource("控制器参数未读取");
                CardSoftLimitStatusText.Text = (string)FindResource("控制器软限位状态未读取");
                return;
            }

            if (!string.IsNullOrWhiteSpace(_lastReadbackError))
            {
                CardReadbackText.Text = FormatResourceText(
                    "控制器参数读取失败格式",
                    _lastReadbackAxisNumber,
                    _lastReadbackError);
                CardSoftLimitStatusText.Text = (string)FindResource("控制器软限位状态读取失败");
                return;
            }

            if (_lastReadbackParameters == null)
            {
                return;
            }

            AxisMappingCardParameters parameters = _lastReadbackParameters;
            CardReadbackText.Text = FormatResourceText(
                "控制器参数读回格式",
                parameters.UnitsPerEngineeringUnit,
                parameters.MinimumPosition,
                parameters.MaximumPosition,
                parameters.Speed,
                parameters.CreepSpeed,
                parameters.Acceleration,
                parameters.Deceleration);
            CardSoftLimitStatusText.Text = FormatResourceText(
                "控制器软限位状态格式",
                GetSoftLimitStateText(parameters.PositiveSoftLimitEffective),
                GetSoftLimitStateText(parameters.NegativeSoftLimitEffective));
            if (!string.IsNullOrWhiteSpace(parameters.SoftLimitStatusDiagnostic))
            {
                CardSoftLimitStatusText.Text += " " + parameters.SoftLimitStatusDiagnostic;
            }
        }

        /// <summary>用当前语言资源格式化一条状态文字，并保留数字格式规则。</summary>
        private string FormatResourceText(string resourceKey, params object[] arguments)
        {
            string format = (string)FindResource(resourceKey);
            if (arguments == null || arguments.Length == 0)
            {
                return format;
            }

            return string.Format(CultureInfo.CurrentCulture, format, arguments);
        }
    }
}
