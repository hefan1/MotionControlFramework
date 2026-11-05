using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>单独配置软件急停联锁使用的逻辑 DI，不影响 DI/DO 映射保存。</summary>
    public partial class SafetyInterlockConfigurationView : UserControl, INotifyPropertyChanged
    {
        /// <summary>安全联锁文件在设备配置目录中的固定位置。</summary>
        private const string SafetyInterlockFilePath = "HardwareConfig/SafetyInterlock.json";

        /// <summary>DI/DO 映射文件在设备配置目录中的固定位置。</summary>
        private const string IoMappingFilePath = "HardwareConfig/IoMapping.json";

        /// <summary>当前设备的 JSON 配置存储。</summary>
        private readonly MachineConfigurationStore _configurationStore;

        /// <summary>当前页面编辑的设备编号。</summary>
        private readonly string _machineId;

        /// <summary>校验安全联锁配置所用的已映射 DI 名称。</summary>
        private readonly HashSet<string> _mappedInputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>按逻辑名称记录已保存 DI 的控制卡与物理通道，便于核对急停实际来源。</summary>
        private readonly Dictionary<string, string> _mappedInputAddresses =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前页面是否可以保存配置文件。</summary>
        private bool _configurationCanBeSaved = true;

        /// <summary>当前配置状态使用的本地化资源键。</summary>
        private string _statusResourceKey;

        /// <summary>当前状态资源的格式化参数。</summary>
        private object[] _statusArguments;

        /// <summary>当前选择的急停 DI 逻辑名称。</summary>
        private string _emergencyStopInputName = string.Empty;

        /// <summary>当前选择的现场启动按钮 DI 逻辑名称。</summary>
        private string _startInputName = string.Empty;

        /// <summary>当前选择的现场停止按钮 DI 逻辑名称；运行时按暂停处理。</summary>
        private string _pauseInputName = string.Empty;

        /// <summary>当前选择的现场复位按钮 DI 逻辑名称。</summary>
        private string _resetInputName = string.Empty;

        /// <summary>页面文字或配置状态变化时通知 WPF 更新绑定。</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>正式 Host 在工程与设备命令互斥范围内保存并应用同一快照。</summary>
        public Func<SafetyInterlockConfigurationDocument, Task<string>> SaveConfigurationAsync { get; set; }

        /// <summary>配置保存并完成应用后通知其他页面。</summary>
        public event EventHandler ConfigurationSaved;

        /// <summary>从 IO 映射文件读取出的 DI 逻辑名称候选项。</summary>
        public ObservableCollection<string> DigitalInputNames { get; private set; }

        /// <summary>获取或设置软件急停状态对应的逻辑 DI 名称。</summary>
        public string EmergencyStopInputName
        {
            get { return _emergencyStopInputName; }
            set
            {
                string newValue = value ?? string.Empty;
                if (string.Equals(_emergencyStopInputName, newValue, StringComparison.Ordinal))
                {
                    return;
                }

                _emergencyStopInputName = newValue;
                OnPropertyChanged();
                UpdateLoadedConfigurationStatus("安全联锁读取完成");
            }
        }

        /// <summary>获取或设置现场启动按钮对应的逻辑 DI 名称。</summary>
        public string StartInputName
        {
            get { return _startInputName; }
            set { SetInputName(ref _startInputName, value, nameof(StartInputName)); }
        }

        /// <summary>获取或设置现场停止按钮对应的逻辑 DI 名称。</summary>
        public string PauseInputName
        {
            get { return _pauseInputName; }
            set { SetInputName(ref _pauseInputName, value, nameof(PauseInputName)); }
        }

        /// <summary>获取或设置现场复位按钮对应的逻辑 DI 名称。</summary>
        public string ResetInputName
        {
            get { return _resetInputName; }
            set { SetInputName(ref _resetInputName, value, nameof(ResetInputName)); }
        }

        /// <summary>现场启动绑定对应的已保存逻辑映射，不表示实时输入反馈。</summary>
        public string StartMappingSummary => DescribeMapping(StartInputName);
        /// <summary>现场暂停绑定对应的已保存逻辑映射。</summary>
        public string PauseMappingSummary => DescribeMapping(PauseInputName);
        /// <summary>现场复位绑定对应的已保存逻辑映射。</summary>
        public string ResetMappingSummary => DescribeMapping(ResetInputName);
        /// <summary>软件安全联锁对应的已保存逻辑映射。</summary>
        public string EmergencyStopMappingSummary => DescribeMapping(EmergencyStopInputName);
        /// <summary>仅解释已保存的地址；未绑定、失效引用和读取失败分别说明。</summary>
        private string DescribeMapping(string name)
        {
            string logicalName = (name ?? string.Empty).Trim();
            if (!_configurationCanBeSaved) return (string)FindResource("安全联锁映射未知");
            if (logicalName.Length == 0) return (string)FindResource("安全联锁绑定为空");
            string address;
            return _mappedInputAddresses.TryGetValue(logicalName, out address)
                ? string.Format((string)FindResource("安全联锁绑定地址"), address)
                : string.Format((string)FindResource("安全联锁绑定失效"), logicalName);
        }
        /// <summary>刷新四行只读映射说明，不改变绑定或保存资格。</summary>
        private void RefreshMappingSummaries()
        {
            OnPropertyChanged(nameof(StartMappingSummary));
            OnPropertyChanged(nameof(PauseMappingSummary));
            OnPropertyChanged(nameof(ResetMappingSummary));
            OnPropertyChanged(nameof(EmergencyStopMappingSummary));
        }

        /// <summary>读取现有按钮和安全设置、DI 候选项，并显示配置文件位置。</summary>
        public SafetyInterlockConfigurationView(MachineConfigurationStore configurationStore, string machineId)
        {
            if (configurationStore == null)
            {
                throw new ArgumentNullException(nameof(configurationStore));
            }

            if (string.IsNullOrWhiteSpace(machineId))
            {
                throw new ArgumentException("设备编号不能为空。", nameof(machineId));
            }

            InitializeComponent();
            _configurationStore = configurationStore;
            _machineId = machineId;
            DigitalInputNames = new ObservableCollection<string>();
            DataContext = this;
            ConfigurationFileText.Text = GetConfigurationPath(SafetyInterlockFilePath);
            LoadConfiguration();
        }

        /// <summary>读取整机按钮与安全联锁设置；缺少设置文件表示尚未配置。</summary>
        private void LoadConfiguration()
        {
            _configurationCanBeSaved = true;
            EmergencyStopInputName = string.Empty;
            StartInputName = string.Empty;
            PauseInputName = string.Empty;
            ResetInputName = string.Empty;

            try
            {
                LoadDigitalInputNames();
                string filePath = GetConfigurationPath(SafetyInterlockFilePath);
                if (File.Exists(filePath))
                {
                    SafetyInterlockConfigurationDocument configuration = _configurationStore
                        .LoadJson<SafetyInterlockConfigurationDocument>(_machineId, SafetyInterlockFilePath);
                    if (configuration == null || configuration.SchemaVersion != 1)
                    {
                        throw new InvalidOperationException(
                            "只支持 schemaVersion 为 1 的 SafetyInterlock.json。未覆盖原配置。");
                    }

                    EmergencyStopInputName = configuration.EmergencyStopInputName ?? string.Empty;
                    StartInputName = configuration.StartInputName ?? string.Empty;
                    PauseInputName = configuration.PauseInputName ?? string.Empty;
                    ResetInputName = configuration.ResetInputName ?? string.Empty;
                }

                UpdateLoadedConfigurationStatus("安全联锁读取完成");
            }
            catch (Exception error)
            {
                _configurationCanBeSaved = false;
                SetStatus("安全联锁读取失败", error.Message);
            }
        }

        /// <summary>从 IoMapping.json 刷新可选 DI 名称并记录有效映射清单。</summary>
        private void LoadDigitalInputNames()
        {
            DigitalInputNames.Clear();
            _mappedInputNames.Clear();
            _mappedInputAddresses.Clear();

            string filePath = GetConfigurationPath(IoMappingFilePath);
            if (!File.Exists(filePath))
            {
                return;
            }

            IoMappingConfigurationDocument ioConfiguration = _configurationStore
                .LoadJson<IoMappingConfigurationDocument>(_machineId, IoMappingFilePath);
            if (ioConfiguration == null || ioConfiguration.SchemaVersion != 1
                || ioConfiguration.DigitalInputs == null)
            {
                throw new InvalidOperationException("IoMapping.json 格式无效，无法读取急停 DI 候选项。");
            }

            foreach (var input in ioConfiguration.DigitalInputs)
            {
                if (input == null || string.IsNullOrWhiteSpace(input.Name))
                {
                    continue;
                }

                string inputName = input.Name.Trim();
                if (_mappedInputNames.Add(inputName))
                {
                    DigitalInputNames.Add(inputName);
                    _mappedInputAddresses.Add(
                        inputName,
                        string.Format("{0} DI{1}", input.ControllerId, input.Channel));
                }
            }
        }

        /// <summary>按操作人员的刷新请求重新读取 DI 列表，保留当前编辑值。</summary>
        private void RefreshInputNamesButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshDigitalInputNames();
        }

        /// <summary>重新读取 IO 映射中的 DI 名称，并保留当前按钮和急停输入编辑值。</summary>
        public void RefreshDigitalInputNames()
        {
            string emergencyStopName = EmergencyStopInputName;
            string startName = StartInputName;
            string pauseName = PauseInputName;
            string resetName = ResetInputName;
            try
            {
                _configurationCanBeSaved = true;
                LoadDigitalInputNames();
                EmergencyStopInputName = emergencyStopName;
                StartInputName = startName;
                PauseInputName = pauseName;
                ResetInputName = resetName;
                UpdateLoadedConfigurationStatus("安全联锁DI刷新完成");
            }
            catch (Exception error)
            {
                _configurationCanBeSaved = false;
                SetStatus("安全联锁读取失败", error.Message);
            }
        }

        /// <summary>提示配置读取状态，并检查各按钮或急停 DI 是否仍有映射。</summary>
        private void UpdateLoadedConfigurationStatus(string successResourceKey)
        {
            string[] configuredNames =
            {
                StartInputName,
                PauseInputName,
                ResetInputName,
                EmergencyStopInputName
            };

            foreach (string configuredName in configuredNames)
            {
                string inputName = (configuredName ?? string.Empty).Trim();
                if (inputName.Length > 0 && !_mappedInputNames.Contains(inputName))
                {
                    SetStatus("安全联锁输入未映射", inputName);
                    return;
                }
            }

            if (configuredNames.All(string.IsNullOrWhiteSpace))
            {
                SetStatus("安全联锁未配置");
                return;
            }

            string emergencyStopName = (EmergencyStopInputName ?? string.Empty).Trim();
            string inputAddress;
            if (emergencyStopName.Length > 0 && _mappedInputAddresses.TryGetValue(emergencyStopName, out inputAddress))
            {
                SetStatus("安全联锁输入映射有效", emergencyStopName, inputAddress);
                return;
            }

            SetStatus(successResourceKey);
        }

        /// <summary>校验已选择的按钮和急停名称必须来自当前 DI 映射且互不重复。</summary>
        private bool TryValidateConfiguration(out string errorMessage)
        {
            if (!_configurationCanBeSaved)
            {
                errorMessage = (string)FindResource("安全联锁配置不可保存");
                return false;
            }

            var selectedNames = new[]
            {
                StartInputName,
                PauseInputName,
                ResetInputName,
                EmergencyStopInputName
            }.Select(name => (name ?? string.Empty).Trim()).Where(name => name.Length > 0).ToList();

            foreach (string inputName in selectedNames)
            {
                if (!_mappedInputNames.Contains(inputName))
                {
                    errorMessage = (string)FindResource("安全联锁输入必须来自DI映射");
                    return false;
                }
            }

            if (selectedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selectedNames.Count)
            {
                errorMessage = (string)FindResource("设备按钮DI不能重复");
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }

        /// <summary>验证后保存设备按钮和安全联锁配置，不重连控制卡或改写 IO 映射。</summary>
        private async void SaveConfigurationButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsEnabled) return;
            string errorMessage;
            if (!TryValidateConfiguration(out errorMessage))
            {
                SetStatusText(errorMessage);
                return;
            }

            var configuration = new SafetyInterlockConfigurationDocument
            {
                EmergencyStopInputName = (EmergencyStopInputName ?? string.Empty).Trim(),
                StartInputName = (StartInputName ?? string.Empty).Trim(),
                PauseInputName = (PauseInputName ?? string.Empty).Trim(),
                ResetInputName = (ResetInputName ?? string.Empty).Trim()
            };

            SetCurrentValue(IsEnabledProperty, false);
            try
            {
                string monitorError = null;
                if (SaveConfigurationAsync == null)
                    _configurationStore.SaveJson(_machineId, SafetyInterlockFilePath, configuration);
                else monitorError = await SaveConfigurationAsync(configuration);
                if (monitorError != null) SetStatus("安全联锁已保存但监控失败", monitorError);
                else SetStatus(string.IsNullOrWhiteSpace(configuration.EmergencyStopInputName)
                    ? "安全联锁未配置"
                    : "安全联锁保存成功");
            }
            catch (Exception error)
            {
                SetStatus("安全联锁保存失败", error.Message);
                return;
            }
            finally { SetCurrentValue(IsEnabledProperty, true); }

            try { ConfigurationSaved?.Invoke(this, EventArgs.Empty); }
            catch (Exception error)
            {
                SetStatus("安全联锁已保存但通知失败", error.Message);
            }
        }

        /// <summary>放弃当前编辑并从本地配置文件重新加载按钮、安全设置与 DI 名称。</summary>
        private void CancelChangesButton_Click(object sender, RoutedEventArgs e)
        {
            LoadConfiguration();
        }

        /// <summary>根据当前语言资源键刷新页面状态文字。</summary>
        public void RefreshLocalizedText()
        {
            UpdateStatusText();
            RefreshMappingSummaries();
        }

        /// <summary>设置一个本地化状态，并保留格式化参数供语言切换时重绘。</summary>
        private void SetStatus(string resourceKey, params object[] arguments)
        {
            _statusResourceKey = resourceKey;
            _statusArguments = arguments;
            UpdateStatusText();
            RefreshMappingSummaries();
        }

        /// <summary>显示已格式化的校验错误，避免错误文字丢失本地化信息。</summary>
        private void SetStatusText(string message)
        {
            _statusResourceKey = null;
            _statusArguments = null;
            ConfigurationStatusText.Text = message;
            RefreshMappingSummaries();
        }

        /// <summary>按当前语言将最近一次状态资源格式化后显示。</summary>
        private void UpdateStatusText()
        {
            if (string.IsNullOrWhiteSpace(_statusResourceKey))
            {
                return;
            }

            string statusFormat = (string)FindResource(_statusResourceKey);
            ConfigurationStatusText.Text = _statusArguments == null || _statusArguments.Length == 0
                ? statusFormat
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, statusFormat, _statusArguments);
        }

        /// <summary>按设备配置根目录和相对路径生成绝对配置文件路径。</summary>
        private string GetConfigurationPath(string relativePath)
        {
            return Path.Combine(
                _configurationStore.GetMachineDirectory(_machineId),
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>通知 WPF 更新指定绑定属性。</summary>
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        /// <summary>更新一个 DI 名称属性并在变化时刷新页面绑定和配置状态。</summary>
        private void SetInputName(ref string currentValue, string newValue, string propertyName)
        {
            string normalizedValue = newValue ?? string.Empty;
            if (string.Equals(currentValue, normalizedValue, StringComparison.Ordinal))
            {
                return;
            }

            currentValue = normalizedValue;
            OnPropertyChanged(propertyName);
            UpdateLoadedConfigurationStatus("安全联锁读取完成");
        }
    }
}
