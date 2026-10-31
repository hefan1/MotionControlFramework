using System.ComponentModel;
using System.Windows.Media;

namespace MotionControl.Framework.Wpf.ManualDebug
{
    /// <summary>保存 IO 监控列表中一个物理通道的名称、原始电平和显示状态。</summary>
    internal sealed class IoMonitorChannel : INotifyPropertyChanged
    {
        /// <summary>逻辑映射中的点位名称；未映射时为空。</summary>
        private readonly string _logicalName;

        /// <summary>该通道是否已经配置逻辑映射。</summary>
        private readonly bool _isMapped;

        /// <summary>物理高电平是否表示逻辑有效。</summary>
        private readonly bool _activeHigh;

        /// <summary>该行是否为 DO 输出通道。</summary>
        private readonly bool _isOutput;

        /// <summary>当前读取到的物理高低电平；读取失败或尚未读取时为空。</summary>
        private bool? _physicalHigh;

        /// <summary>当前灯状态对应的文字说明。</summary>
        private string _stateText;

        /// <summary>当前灯的颜色；有效状态为绿色，读取失败为红色。</summary>
        private Brush _indicatorBrush = new SolidColorBrush(Color.FromRgb(160, 168, 174));

        /// <summary>输出写入期间暂存的读回状态。</summary>
        private bool _isWriting;

        /// <summary>当前页面是否允许操作输出通道。</summary>
        private bool _outputControlAvailable;

        /// <summary>最近一次读取失败的原始错误文字。</summary>
        private string _readError;

        /// <summary>创建一个通道行并保存它的物理地址和逻辑映射。</summary>
        public IoMonitorChannel(int channel, string logicalName, bool activeHigh, bool isMapped, bool isOutput)
        {
            Channel = channel;
            _logicalName = logicalName;
            _activeHigh = activeHigh;
            _isMapped = isMapped;
            _isOutput = isOutput;
            Address = string.Empty;
            DisplayName = string.Empty;
            _stateText = string.Empty;
        }

        /// <summary>控制卡上的零起始物理通道号。</summary>
        public int Channel { get; private set; }

        /// <summary>本地化后的 DI 或 DO 物理地址文字。</summary>
        public string Address { get; private set; }

        /// <summary>IO 映射名称；没有映射时显示“未命名”。</summary>
        public string DisplayName { get; private set; }

        /// <summary>当前电平对应的有效、无效或读取错误文字。</summary>
        public string StateText
        {
            get { return _stateText; }
            private set { SetProperty(ref _stateText, value, nameof(StateText)); }
        }

        /// <summary>状态灯当前显示颜色。</summary>
        public Brush IndicatorBrush
        {
            get { return _indicatorBrush; }
            private set { SetProperty(ref _indicatorBrush, value, nameof(IndicatorBrush)); }
        }

        /// <summary>读取到的物理电平；页面据此决定 DO 点击后的反向目标值。</summary>
        public bool? PhysicalHigh
        {
            get { return _physicalHigh; }
            private set
            {
                if (_physicalHigh == value)
                {
                    return;
                }

                _physicalHigh = value;
                OnPropertyChanged(nameof(PhysicalHigh));
                UpdateToggleAvailability();
            }
        }

        /// <summary>最近一次轮询是否读取失败；尚未读取时为 false。</summary>
        public bool HasReadFailure { get; private set; }

        /// <summary>最近一次读取失败的诊断文字；读取成功后为空。</summary>
        public string ReadError { get { return _readError; } }

        /// <summary>映射通道显示逻辑有效状态，未映射通道显示物理高电平状态。</summary>
        public bool IsOn
        {
            get { return _physicalHigh.HasValue && (_isMapped ? _physicalHigh.Value == _activeHigh : _physicalHigh.Value); }
        }

        /// <summary>当前通道是否配置了逻辑名称和有效电平映射。</summary>
        public bool IsMapped
        {
            get { return _isMapped; }
        }

        /// <summary>映射通道物理高电平表示逻辑有效时为 true。</summary>
        public bool ActiveHigh
        {
            get { return _activeHigh; }
        }

        /// <summary>当前是否可以点击 DO 状态灯切换输出。</summary>
        public bool CanToggleOutput { get; private set; }

        /// <summary>列表绑定属性变化时通知 WPF 刷新对应行。</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>按当前页面语言更新物理地址、未映射提示和状态文字。</summary>
        public void RefreshLocalizedText(
            string addressPrefix,
            string unmappedText,
            string activeText,
            string inactiveText,
            string highText,
            string lowText,
            string readingText,
            string readFailedFormat)
        {
            Address = addressPrefix + Channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(Address));
            DisplayName = _isMapped && !string.IsNullOrWhiteSpace(_logicalName) ? _logicalName : unmappedText;
            OnPropertyChanged(nameof(DisplayName));

            if (!string.IsNullOrEmpty(_readError))
            {
                StateText = string.Format(readFailedFormat, _readError);
                return;
            }

            if (!_physicalHigh.HasValue)
            {
                StateText = readingText;
                return;
            }

            UpdateStatePresentation(activeText, inactiveText, highText, lowText);
        }

        /// <summary>保存原始电平并依据映射极性刷新状态灯和文字。</summary>
        public void SetReadSuccess(bool physicalHigh, string activeText, string inactiveText, string highText, string lowText)
        {
            _readError = null;
            HasReadFailure = false;
            PhysicalHigh = physicalHigh;
            UpdateStatePresentation(activeText, inactiveText, highText, lowText);
        }

        /// <summary>清除失效电平，显示读取错误并禁止基于旧状态切换 DO。</summary>
        public void SetReadFailure(string error, string readFailedFormat)
        {
            _readError = error;
            HasReadFailure = true;
            PhysicalHigh = null;
            IndicatorBrush = new SolidColorBrush(Color.FromRgb(184, 58, 50));
            StateText = string.Format(readFailedFormat, error);
        }

        /// <summary>设置通道是否处于写入中，并更新 DO 灯的可操作状态。</summary>
        public void SetWriting(bool isWriting)
        {
            _isWriting = isWriting;
            UpdateToggleAvailability();
        }

        /// <summary>按连接和自动流程状态更新 DO 灯是否允许点击。</summary>
        public void SetOutputControlAvailable(bool available)
        {
            _outputControlAvailable = available;
            UpdateToggleAvailability();
        }

        /// <summary>按映射有效电平或未映射物理电平更新状态文字和指示灯颜色。</summary>
        private void UpdateStatePresentation(string activeText, string inactiveText, string highText, string lowText)
        {
            bool isOn = IsOn;
            StateText = _isMapped
                ? (isOn ? activeText : inactiveText)
                : (_physicalHigh.Value ? highText : lowText);
            IndicatorBrush = isOn
                ? new SolidColorBrush(Color.FromRgb(46, 160, 91))
                : new SolidColorBrush(Color.FromRgb(160, 168, 174));
        }

        /// <summary>根据当前值、输出类型和写入状态计算状态灯是否可点击。</summary>
        private void UpdateToggleAvailability()
        {
            bool canToggle = _isOutput && _physicalHigh.HasValue && _outputControlAvailable && !_isWriting;
            if (CanToggleOutput == canToggle)
            {
                return;
            }

            CanToggleOutput = canToggle;
            OnPropertyChanged(nameof(CanToggleOutput));
        }

        /// <summary>更新一个可绑定属性并通知界面。</summary>
        private void SetProperty<T>(ref T field, T value, string propertyName)
        {
            if (Equals(field, value))
            {
                return;
            }

            field = value;
            OnPropertyChanged(propertyName);
        }

        /// <summary>向 WPF 绑定通知指定属性已经改变。</summary>
        private void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
