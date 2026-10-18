using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.Motion
{
    /// <summary>为可查询 I/O 能力的运动卡提供独立的只读查询接口。</summary>
    public interface IMotionCardIoCapabilityProvider
    {
        /// <summary>读取控制卡自带、最大支持和已扫描总线扩展的数字 I/O 数量。</summary>
        Task<MotionCardIoCapabilities> ReadIoCapabilitiesAsync(CancellationToken cancellationToken);
    }

    /// <summary>控制卡自带、最大支持和已扫描总线扩展的数字 I/O 数量。</summary>
    public sealed class MotionCardIoCapabilities
    {
        /// <summary>控制器本体自带的 DI 输入点数；厂商未提供时为空。</summary>
        public int? BuiltInDigitalInputCount { get; set; }

        /// <summary>控制器本体自带的 DO 输出点数；厂商未提供时为空。</summary>
        public int? BuiltInDigitalOutputCount { get; set; }

        /// <summary>控制器规格声明的最大 DI 输入点数；厂商未提供时为空。</summary>
        public int? MaximumDigitalInputCount { get; set; }

        /// <summary>控制器规格声明的最大 DO 输出点数；厂商未提供时为空。</summary>
        public int? MaximumDigitalOutputCount { get; set; }

        /// <summary>成功扫描到的总线从站节点数；扫描失败时为空。</summary>
        public int? ScannedBusNodeCount { get; set; }

        /// <summary>成功扫描到的总线从站 DI 输入点总数；扫描失败时为空。</summary>
        public int? ScannedBusDigitalInputCount { get; set; }

        /// <summary>成功扫描到的总线从站 DO 输出点总数；扫描失败时为空。</summary>
        public int? ScannedBusDigitalOutputCount { get; set; }

        /// <summary>总线节点扫描失败原因；控制器本体能力仍可能已经读取成功。</summary>
        public string BusScanError { get; set; }
    }
}
