using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace MotionControl.Framework.Wpf.ManualDebug
{
    /// <summary>为轴卡片提供关联 IO 配置和实时状态显示。</summary>
    public sealed class ManualAxisIoAssociationViewItem : INotifyPropertyChanged
    {
        /// <summary>当前行保存到设备配置中的 IO 关联。</summary>
        public ManualAxisIoAssociation Association { get; private set; }

        /// <summary>供按钮显示的现场名称。</summary>
        public string DisplayName { get; private set; }

        /// <summary>供操作员核对的逻辑 IO 名称。</summary>
        public string PointName { get; private set; }

        /// <summary>指示当前行是否为可写 DO。</summary>
        public bool IsOutput { get; private set; }

        /// <summary>显示 DI 或 DO 类型缩写。</summary>
        public string TypeText { get; private set; }

        /// <summary>显示逻辑 DO 写入 true 时使用的按钮文字。</summary>
        public string OnActionName { get { return Association.OnActionName; } }

        /// <summary>显示逻辑 DO 写入 false 时使用的按钮文字。</summary>
        public string OffActionName { get { return Association.OffActionName; } }

        /// <summary>显示输入电平或控制卡报告的输出命令状态。</summary>
        public string StateText { get; private set; }

        /// <summary>当前电平状态对应的提示底色。</summary>
        public Brush StateBackground { get; private set; }

        /// <summary>提供状态值的来源或读取失败的具体原因。</summary>
        public string StateToolTip { get; private set; }

        /// <summary>当前映射有效且输出状态读取成功时允许写入。</summary>
        public bool CanWriteOutput { get; private set; }

        /// <summary>配置值或状态变化时通知 WPF 更新当前行。</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>从已保存的关联配置创建一行轴卡片显示项。</summary>
        public ManualAxisIoAssociationViewItem(ManualAxisIoAssociation association)
        {
            UpdateAssociation(association);
            SetStatus("—", Brushes.LightGray, false, string.Empty);
        }

        /// <summary>用编辑后的关联配置更新行文字并等待新状态读取。</summary>
        public void UpdateAssociation(ManualAxisIoAssociation association)
        {
            Association = association;
            DisplayName = association.DisplayName;
            PointName = association.PointName;
            IsOutput = association.PointType == ManualAxisIoPointType.DigitalOutput;
            TypeText = IsOutput ? "DO" : "DI";
            SetStatus("—", Brushes.LightGray, false, string.Empty);
            NotifyChanged(nameof(Association));
            NotifyChanged(nameof(DisplayName));
            NotifyChanged(nameof(PointName));
            NotifyChanged(nameof(IsOutput));
            NotifyChanged(nameof(TypeText));
            NotifyChanged(nameof(OnActionName));
            NotifyChanged(nameof(OffActionName));
        }

        /// <summary>更新状态文字、底色和 DO 写入可用状态。</summary>
        public void SetStatus(string stateText, Brush background, bool canWriteOutput, string stateToolTip)
        {
            StateText = stateText;
            StateBackground = background;
            CanWriteOutput = canWriteOutput;
            StateToolTip = stateToolTip;
            NotifyChanged(nameof(StateText));
            NotifyChanged(nameof(StateBackground));
            NotifyChanged(nameof(CanWriteOutput));
            NotifyChanged(nameof(StateToolTip));
        }

        /// <summary>通知绑定界面刷新指定属性，空名称表示刷新整行。</summary>
        private void NotifyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
