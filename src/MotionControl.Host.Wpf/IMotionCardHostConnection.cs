using System.Collections.Generic;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Wpf.Configuration;

namespace MotionControl.Host.Wpf
{
    /// <summary>供 Host 组合根使用的运动卡连接、逻辑轴和逻辑 IO 入口。</summary>
    public interface IMotionCardHostConnection : IAxisMappingCardConnection
    {
        /// <summary>当前连接的通用运动卡实例；未连接时为空。</summary>
        IMotionCard ConnectedMotionCard { get; }

        /// <summary>当前连接对应的逻辑 IO 服务；未连接时为空。</summary>
        ILogicalIo ConnectedLogicalIo { get; }

        /// <summary>当前卡配置的逻辑轴硬件映射。</summary>
        IReadOnlyList<AxisHardwareDefinition> AxisHardwareDefinitions { get; }

        /// <summary>当前卡配置的逻辑数字输入映射。</summary>
        IReadOnlyList<DigitalIoHardwareDefinition> DigitalInputs { get; }

        /// <summary>当前卡配置的逻辑数字输出映射。</summary>
        IReadOnlyList<DigitalIoHardwareDefinition> DigitalOutputs { get; }
    }
}
