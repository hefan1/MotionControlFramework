using System;
using System.Collections.Generic;
using System.ComponentModel;
using MotionControl.Framework.Contracts.Configuration;
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

        /// <summary>创建首版轴映射配置文档。</summary>
        public AxisMappingConfigurationDocument()
        {
            SchemaVersion = 1;
            Axes = new List<AxisMappingEntry>();
        }
    }

    /// <summary>保存一个物理轴通道对应的设备名称和调试参数。</summary>
    public sealed class AxisMappingEntry : INotifyPropertyChanged
    {
        /// <summary>保存设备轴逻辑名称；物理轴未映射时为空。</summary>
        private string _name;

        /// <summary>保存直线轴或旋转轴类型；未选择时为空。</summary>
        private string _axisType;

        /// <summary>保存轴位置显示单位；未选择时为空。</summary>
        private string _engineeringUnit;

        /// <summary>保存每工程单位对应的控制器位置单位数；未配置时为空。</summary>
        private double? _unitsPerEngineeringUnit;

        /// <summary>保存定位到位允许的最大反馈误差；未配置时为空。</summary>
        private double? _positionTolerance;

        /// <summary>保存软件限位下限；未配置时为空。</summary>
        private double? _minimumPosition;

        /// <summary>保存软件限位上限；未配置时为空。</summary>
        private double? _maximumPosition;

        /// <summary>保存是否要求控制卡启用正负软件限位；未选择时为空。</summary>
        private bool? _softLimitEnabled;

        /// <summary>保存手动调试速度；未配置时为空。</summary>
        private double? _manualSpeed;

        /// <summary>保存当前驱动适配器使用的回零模式编号；未配置时为空。</summary>
        private int? _homeMode;

        /// <summary>记录回零模式编号所属的驱动适配器，防止更换 API 家族后误用相同数字。</summary>
        private string _homeModeAdapterType;

        /// <summary>保留早期配置使用的回零模式厂商标记。</summary>
        private string _homeModeVendor;

        /// <summary>保存回零搜索速度；未配置时为空。</summary>
        private double? _homeSearchSpeed;

        /// <summary>保存回零爬行速度；未配置时为空。</summary>
        private double? _homeCreepSpeed;

        /// <summary>保存回零加速度；未配置时为空。</summary>
        private double? _homeAcceleration;

        /// <summary>保存回零减速度；未配置时为空。</summary>
        private double? _homeDeceleration;

        /// <summary>通知页面中绑定的轴参数已经变化。</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>轴所属的控制卡标识，首版单卡页面使用 MotionCard01。</summary>
        public string ControllerId { get; set; }

        /// <summary>控制器上的物理轴通道号，例如 Axis0 对应 0。</summary>
        public int AxisNumber { get; set; }

        /// <summary>设备流程中使用的逻辑轴中文名称；通道未映射时为空。</summary>
        public string Name
        {
            get { return _name; }
            set
            {
                string normalizedName = string.IsNullOrWhiteSpace(value) ? null : value;
                if (SetValue(ref _name, normalizedName, "Name"))
                {
                    if (!string.IsNullOrWhiteSpace(normalizedName) && !PositionTolerance.HasValue)
                    {
                        PositionTolerance = AxisHardwareDefinition.DefaultPositionTolerance;
                    }

                    RaisePropertyChanged("IsConfigured");
                }
            }
        }

        /// <summary>轴的工艺类型，例如直线轴或旋转轴；未选择时为空。</summary>
        public string AxisType
        {
            get { return _axisType; }
            set { SetValue(ref _axisType, value, "AxisType"); }
        }

        /// <summary>轴位置使用的工程单位，例如 mm 或 deg；未选择时为空。</summary>
        public string EngineeringUnit
        {
            get { return _engineeringUnit; }
            set { SetValue(ref _engineeringUnit, value, "EngineeringUnit"); }
        }

        /// <summary>每个工程单位对应的控制器脉冲数或位置单位数；未配置时为空。</summary>
        public double? UnitsPerEngineeringUnit
        {
            get { return _unitsPerEngineeringUnit; }
            set { SetValue(ref _unitsPerEngineeringUnit, value, "UnitsPerEngineeringUnit"); }
        }

        /// <summary>定位到位允许的最大实际反馈误差，单位为工程单位；新映射默认 0.003。</summary>
        public double? PositionTolerance
        {
            get { return _positionTolerance; }
            set { SetValue(ref _positionTolerance, value, "PositionTolerance"); }
        }

        /// <summary>软件限位下限，单位为工程单位；未配置时为空。</summary>
        public double? MinimumPosition
        {
            get { return _minimumPosition; }
            set { SetValue(ref _minimumPosition, value, "MinimumPosition"); }
        }

        /// <summary>软件限位上限，单位为工程单位；未配置时为空。</summary>
        public double? MaximumPosition
        {
            get { return _maximumPosition; }
            set { SetValue(ref _maximumPosition, value, "MaximumPosition"); }
        }

        /// <summary>是否启用控制卡正负软件限位；空值表示尚未选择。</summary>
        public bool? SoftLimitEnabled
        {
            get { return _softLimitEnabled; }
            set { SetValue(ref _softLimitEnabled, value, "SoftLimitEnabled"); }
        }

        /// <summary>手动调试速度，单位为工程单位每秒；未配置时为空。</summary>
        public double? ManualSpeed
        {
            get { return _manualSpeed; }
            set { SetValue(ref _manualSpeed, value, "ManualSpeed"); }
        }

        /// <summary>控制卡厂商定义的回零模式编号；未配置时为空。</summary>
        public int? HomeMode
        {
            get { return _homeMode; }
            set { SetValue(ref _homeMode, value, "HomeMode"); }
        }

        /// <summary>回零模式编号所属的驱动适配器稳定标识。</summary>
        public string HomeModeAdapterType
        {
            get { return _homeModeAdapterType; }
            set
            {
                string normalizedAdapterType = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                SetValue(ref _homeModeAdapterType, normalizedAdapterType, "HomeModeAdapterType");
            }
        }

        /// <summary>旧版 JSON 使用的厂商标记；新配置以 HomeModeAdapterType 判断编号归属。</summary>
        public string HomeModeVendor
        {
            get { return _homeModeVendor; }
            set
            {
                string normalizedVendor = string.IsNullOrWhiteSpace(value) ? null : value;
                SetValue(ref _homeModeVendor, normalizedVendor, "HomeModeVendor");
            }
        }

        /// <summary>回零搜索速度，单位为工程单位每秒；未配置时为空。</summary>
        public double? HomeSearchSpeed
        {
            get { return _homeSearchSpeed; }
            set { SetValue(ref _homeSearchSpeed, value, "HomeSearchSpeed"); }
        }

        /// <summary>回零爬行速度，单位为工程单位每秒；未配置时为空。</summary>
        public double? HomeCreepSpeed
        {
            get { return _homeCreepSpeed; }
            set { SetValue(ref _homeCreepSpeed, value, "HomeCreepSpeed"); }
        }

        /// <summary>回零加速度，单位为工程单位每秒平方；未配置时为空。</summary>
        public double? HomeAcceleration
        {
            get { return _homeAcceleration; }
            set { SetValue(ref _homeAcceleration, value, "HomeAcceleration"); }
        }

        /// <summary>回零减速度，单位为工程单位每秒平方；未配置时为空。</summary>
        public double? HomeDeceleration
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

        /// <summary>创建一条未配置的物理轴通道记录，不预填运动参数。</summary>
        public AxisMappingEntry()
        {
            ControllerId = "MotionCard01";
            Name = null;
            AxisType = null;
            EngineeringUnit = null;
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
