using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace MotionControl.Framework.Wpf.ManualDebug
{
    /// <summary>保存设备手动调试页中人工示教的轴位置。</summary>
    public sealed class ManualAxisPositionConfigurationDocument
    {
        /// <summary>创建空的位置配置；位置只有在调试人员添加后才会出现。</summary>
        public ManualAxisPositionConfigurationDocument()
        {
            SchemaVersion = 1;
            Axes = new List<ManualAxisPositionGroup>();
        }

        /// <summary>位置配置文件格式版本；当前版本为 1。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>按逻辑轴名称保存的位置列表。</summary>
        public List<ManualAxisPositionGroup> Axes { get; set; }
    }

    /// <summary>某一个逻辑轴的示教位置和下一个稳定位置编号。</summary>
    public sealed class ManualAxisPositionGroup
    {
        /// <summary>创建一个没有示教位置的轴位置组。</summary>
        public ManualAxisPositionGroup()
        {
            NextPositionNumber = 1;
            Positions = new ObservableCollection<ManualAxisPosition>();
        }

        /// <summary>稳定的控制卡和物理轴标识，不随设备轴显示名称修改。</summary>
        public string AxisKey { get; set; }

        /// <summary>下次添加位置时使用的编号；删除位置不会回收旧编号。</summary>
        public int NextPositionNumber { get; set; }

        /// <summary>当前轴由调试人员添加的位置列表。</summary>
        public ObservableCollection<ManualAxisPosition> Positions { get; set; }
    }

    /// <summary>一个可命名、可示教并可用于手动定位的轴位置。</summary>
    public sealed class ManualAxisPosition : INotifyPropertyChanged
    {
        /// <summary>稳定位置编号，例如 Position1；供后续流程代码引用。</summary>
        private string _id;

        /// <summary>调试人员在界面中看到并可编辑的位置名称。</summary>
        private string _name;

        /// <summary>手动输入或示教保存的轴工程位置；尚未设置时为空。</summary>
        private double? _position;

        /// <summary>通知位置列表中绑定的名称或位置值已经变化。</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>唯一且不随显示名称改变的位置编号。</summary>
        public string Id
        {
            get { return _id; }
            set { SetValue(ref _id, value, "Id"); }
        }

        /// <summary>调试人员可编辑的位置名称，例如“上料位”。</summary>
        public string Name
        {
            get { return _name; }
            set { SetValue(ref _name, value, "Name"); }
        }

        /// <summary>从当前位置示教后保存的工程位置；空值表示还没有写入当前位置。</summary>
        public double? Position
        {
            get { return _position; }
            set { SetValue(ref _position, value, "Position"); }
        }

        /// <summary>更新位置成员并通知 WPF 刷新对应的列表行。</summary>
        private void SetValue<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
