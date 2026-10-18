using System;
using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>保存单台设备的物理轴映射和轴参数。</summary>
    public sealed class AxisMappingConfigurationDocument
    {
        /// <summary>当前轴映射配置文件格式版本。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>已配置名称的物理轴及其轴参数。</summary>
        public List<AxisMappingEntry> Axes { get; set; }

        /// <summary>创建首版轴映射配置及其默认值。</summary>
        public AxisMappingConfigurationDocument()
        {
            SchemaVersion = 1;
            Axes = new List<AxisMappingEntry>();
        }
    }

    /// <summary>保存一个物理轴通道对应的设备名称和调试参数。</summary>
    public sealed class AxisMappingEntry : INotifyPropertyChanged
    {
        /// <summary>保存设备轴逻辑名称。</summary>
        private string _name;

        /// <summary>保存直线轴或旋转轴类型。</summary>
        private string _axisType;

        /// <summary>保存轴位置显示单位。</summary>
        private string _engineeringUnit;

        /// <summary>保存每工程单位对应的控制器位置单位数。</summary>
        private double _unitsPerEngineeringUnit;

        /// <summary>保存软件限位下限。</summary>
        private double _minimumPosition;

        /// <summary>保存软件限位上限。</summary>
        private double _maximumPosition;

        /// <summary>保存手动调试速度。</summary>
        private double _manualSpeed;

        /// <summary>保存手动定位加速度。</summary>
        private double _manualAcceleration;

        /// <summary>保存手动定位减速度。</summary>
        private double _manualDeceleration;

        /// <summary>保存连续点动加速度。</summary>
        private double _jogAcceleration;

        /// <summary>保存连续点动减速度。</summary>
        private double _jogDeceleration;

        /// <summary>保存控制卡厂商使用的回零模式编号。</summary>
        private int _homeMode;

        /// <summary>保存回零搜索速度。</summary>
        private double _homeSearchSpeed;

        /// <summary>保存回零爬行速度。</summary>
        private double _homeCreepSpeed;

        /// <summary>保存回零加速度。</summary>
        private double _homeAcceleration;

        /// <summary>保存回零减速度。</summary>
        private double _homeDeceleration;

        /// <summary>通知页面中绑定的轴参数已经变化。</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>轴所属的控制卡标识，首版单卡页面使用 MotionCard01。</summary>
        public string ControllerId { get; set; }

        /// <summary>控制器上的物理轴通道号，例如 Axis0 对应 0。</summary>
        public int AxisNumber { get; set; }

        /// <summary>设备流程中使用的逻辑轴中文名称。</summary>
        public string Name
        {
            get { return _name; }
            set
            {
                if (SetValue(ref _name, value, "Name"))
                {
                    RaisePropertyChanged("IsConfigured");
                }
            }
        }

        /// <summary>轴的工艺类型，例如直线轴或旋转轴。</summary>
        public string AxisType
        {
            get { return _axisType; }
            set { SetValue(ref _axisType, value, "AxisType"); }
        }

        /// <summary>轴位置使用的工程单位，例如 mm 或 deg。</summary>
        public string EngineeringUnit
        {
            get { return _engineeringUnit; }
            set { SetValue(ref _engineeringUnit, value, "EngineeringUnit"); }
        }

        /// <summary>每个工程单位对应的控制器脉冲数或位置单位数。</summary>
        public double UnitsPerEngineeringUnit
        {
            get { return _unitsPerEngineeringUnit; }
            set { SetValue(ref _unitsPerEngineeringUnit, value, "UnitsPerEngineeringUnit"); }
        }

        /// <summary>软件限位下限，单位为工程单位。</summary>
        public double MinimumPosition
        {
            get { return _minimumPosition; }
            set { SetValue(ref _minimumPosition, value, "MinimumPosition"); }
        }

        /// <summary>软件限位上限，单位为工程单位。</summary>
        public double MaximumPosition
        {
            get { return _maximumPosition; }
            set { SetValue(ref _maximumPosition, value, "MaximumPosition"); }
        }

        /// <summary>手动调试速度，单位为工程单位每秒。</summary>
        public double ManualSpeed
        {
            get { return _manualSpeed; }
            set { SetValue(ref _manualSpeed, value, "ManualSpeed"); }
        }

        /// <summary>手动定位加速度，单位为工程单位每秒平方。</summary>
        public double ManualAcceleration
        {
            get { return _manualAcceleration; }
            set { SetValue(ref _manualAcceleration, value, "ManualAcceleration"); }
        }

        /// <summary>手动定位减速度，单位为工程单位每秒平方。</summary>
        public double ManualDeceleration
        {
            get { return _manualDeceleration; }
            set { SetValue(ref _manualDeceleration, value, "ManualDeceleration"); }
        }

        /// <summary>连续点动加速度，单位为工程单位每秒平方。</summary>
        public double JogAcceleration
        {
            get { return _jogAcceleration; }
            set { SetValue(ref _jogAcceleration, value, "JogAcceleration"); }
        }

        /// <summary>连续点动减速度，单位为工程单位每秒平方。</summary>
        public double JogDeceleration
        {
            get { return _jogDeceleration; }
            set { SetValue(ref _jogDeceleration, value, "JogDeceleration"); }
        }

        /// <summary>控制卡厂商定义的回零模式编号。</summary>
        public int HomeMode
        {
            get { return _homeMode; }
            set { SetValue(ref _homeMode, value, "HomeMode"); }
        }

        /// <summary>回零搜索速度，单位为工程单位每秒。</summary>
        public double HomeSearchSpeed
        {
            get { return _homeSearchSpeed; }
            set { SetValue(ref _homeSearchSpeed, value, "HomeSearchSpeed"); }
        }

        /// <summary>回零爬行速度，单位为工程单位每秒。</summary>
        public double HomeCreepSpeed
        {
            get { return _homeCreepSpeed; }
            set { SetValue(ref _homeCreepSpeed, value, "HomeCreepSpeed"); }
        }

        /// <summary>回零加速度，单位为工程单位每秒平方。</summary>
        public double HomeAcceleration
        {
            get { return _homeAcceleration; }
            set { SetValue(ref _homeAcceleration, value, "HomeAcceleration"); }
        }

        /// <summary>回零减速度，单位为工程单位每秒平方。</summary>
        public double HomeDeceleration
        {
            get { return _homeDeceleration; }
            set { SetValue(ref _homeDeceleration, value, "HomeDeceleration"); }
        }

        /// <summary>根据逻辑轴名称判断当前物理通道是否已经配置。</summary>
        [JsonIgnore]
        public bool IsConfigured
        {
            get { return !string.IsNullOrWhiteSpace(Name); }
        }

        /// <summary>创建一条带有易读默认轴参数的物理通道记录。</summary>
        public AxisMappingEntry()
        {
            ControllerId = "MotionCard01";
            Name = string.Empty;
            AxisType = "直线轴";
            EngineeringUnit = "mm";
            UnitsPerEngineeringUnit = 1000;
            MinimumPosition = -100;
            MaximumPosition = 100;
            ManualSpeed = 50;
            ManualAcceleration = 10;
            ManualDeceleration = 10;
            JogAcceleration = 10;
            JogDeceleration = 10;
            HomeMode = 3;
            HomeSearchSpeed = 5;
            HomeCreepSpeed = 1;
            HomeAcceleration = 10;
            HomeDeceleration = 10;
        }

        /// <summary>设置轴参数并通知 WPF 刷新对应的画面字段。</summary>
        private bool SetValue<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            RaisePropertyChanged(propertyName);
            return true;
        }

        /// <summary>向 WPF 页面发送指定属性已变化的通知。</summary>
        private void RaisePropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
