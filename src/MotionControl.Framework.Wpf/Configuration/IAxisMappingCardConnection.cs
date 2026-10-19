using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>供轴映射页面连接控制卡并读取当前物理轴参数。</summary>
    public interface IAxisMappingCardConnection
    {
        /// <summary>返回当前 Host 已接入且轴映射页允许选择的控制卡选项。</summary>
        IReadOnlyList<MotionCardSelectionOption> SupportedMotionCards { get; }

        /// <summary>当前控制卡是否已经连接成功。</summary>
        bool IsConnected { get; }

        /// <summary>连接后从控制器读取的实际型号或仿真器身份。</summary>
        string ActualControllerType { get; }

        /// <summary>连接后从控制器读取的固件或仿真器版本。</summary>
        string ActualControllerVersion { get; }

        /// <summary>按页面选择的厂商和卡类型连接控制卡，并保存成功的连接配置。</summary>
        Task ConnectAsync(MotionCardSettingsDocument settings, CancellationToken cancellationToken);

        /// <summary>断开当前控制卡连接并释放厂商连接句柄。</summary>
        Task DisconnectAsync(CancellationToken cancellationToken);

        /// <summary>停止状态下重新加载当前设备保存的轴映射并应用轴参数。</summary>
        Task ReconnectAsync(MotionCardSettingsDocument settings, CancellationToken cancellationToken);

        /// <summary>读取指定物理轴的单位换算、软限位和当前运动参数。</summary>
        Task<AxisMappingCardParameters> ReadAxisParametersAsync(int axisNumber, CancellationToken cancellationToken);

        /// <summary>按当前卡型和物理轴返回回零编号及由适配器提供的显示文字资源键。</summary>
        IReadOnlyList<AxisHomeModeOption> GetSupportedHomeModes(
            MotionCardSelectionOption motionCard,
            int axisNumber);
    }

    /// <summary>轴映射页面从控制卡读取的一组轴参数。</summary>
    public sealed class AxisMappingCardParameters
    {
        /// <summary>控制器上的物理轴通道号，从零开始。</summary>
        public int AxisNumber { get; set; }

        /// <summary>每工程单位对应的控制器位置单位数。</summary>
        public double UnitsPerEngineeringUnit { get; set; }

        /// <summary>控制卡当前负向软限位位置。</summary>
        public double MinimumPosition { get; set; }

        /// <summary>控制卡当前正向软限位位置。</summary>
        public double MaximumPosition { get; set; }

        /// <summary>整体正负软限位是否启用；读卡状态不一致或未知时为空。</summary>
        public bool? SoftLimitEnabled { get; set; }

        /// <summary>控制卡正向软限位当前是否生效；空值表示当前厂商接口无法可靠判断。</summary>
        public bool? PositiveSoftLimitEffective { get; set; }

        /// <summary>控制卡负向软限位当前是否生效；空值表示当前厂商接口无法可靠判断。</summary>
        public bool? NegativeSoftLimitEffective { get; set; }

        /// <summary>软限位状态无法判断时提供的厂商读回诊断信息。</summary>
        public string SoftLimitStatusDiagnostic { get; set; }

        /// <summary>控制卡当前读取到的速度参数。</summary>
        public double Speed { get; set; }

        /// <summary>控制卡当前读取到的爬行速度参数。</summary>
        public double CreepSpeed { get; set; }

        /// <summary>控制卡当前读取到的加速度参数。</summary>
        public double Acceleration { get; set; }

        /// <summary>控制卡当前读取到的减速度参数。</summary>
        public double Deceleration { get; set; }
    }
}
