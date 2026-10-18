using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>供轴映射页面连接控制卡并读写当前物理轴参数。</summary>
    public interface IAxisMappingCardConnection
    {
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

        /// <summary>读取指定物理轴的单位换算、软限位和当前运动参数。</summary>
        Task<AxisMappingCardParameters> ReadAxisParametersAsync(int axisNumber, CancellationToken cancellationToken);

        /// <summary>把页面指定的物理轴参数显式写入控制卡当前运行参数。</summary>
        Task WriteAxisParametersAsync(AxisMappingCardParameters parameters, CancellationToken cancellationToken);
    }

    /// <summary>轴映射页面读取或写入的一组控制卡轴参数。</summary>
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
