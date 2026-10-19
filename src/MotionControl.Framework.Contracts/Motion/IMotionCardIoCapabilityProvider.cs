using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.Motion
{
    /// <summary>为可查询 I/O 能力的运动卡提供独立的只读查询接口。</summary>
    public interface IMotionCardIoCapabilityProvider
    {
        /// <summary>读取控制卡 I/O 能力；各厂商适配器按自己的 API 填写可查询字段。</summary>
        Task<MotionCardIoCapabilities> ReadIoCapabilitiesAsync(CancellationToken cancellationToken);

        /// <summary>忽略已有缓存，按当前厂商 API 重新查询控制卡 I/O 能力。</summary>
        Task<MotionCardIoCapabilities> RefreshIoCapabilitiesAsync(CancellationToken cancellationToken);
    }

    /// <summary>保存当前已连接控制卡的数字 I/O 能力查询结果。</summary>
    public sealed class MotionCardIoCapabilities
    {
        /// <summary>提供这组 I/O 能力结果的控制卡配置编号。</summary>
        public string ControllerId { get; set; }

        /// <summary>控制器本体自带的 DI 输入点数；厂商未提供时为空。</summary>
        public int? BuiltInDigitalInputCount { get; set; }

        /// <summary>控制器本体自带的 DO 输出点数；厂商未提供时为空。</summary>
        public int? BuiltInDigitalOutputCount { get; set; }

        /// <summary>控制器规格声明的最大 DI 输入点数；厂商未提供时为空。</summary>
        public int? MaximumDigitalInputCount { get; set; }

        /// <summary>控制器规格声明的最大 DO 输出点数；厂商未提供时为空。</summary>
        public int? MaximumDigitalOutputCount { get; set; }

        /// <summary>从通道 0 起按厂商 API 确认的连续 DI 实际通道范围；仿真器可能返回虚拟地址范围。</summary>
        public MotionCardIoChannelRange DigitalInputChannelRange { get; set; }

        /// <summary>从通道 0 起按厂商 API 确认的连续 DO 实际通道范围；仿真器可能返回虚拟地址范围。</summary>
        public MotionCardIoChannelRange DigitalOutputChannelRange { get; set; }

        /// <summary>成功扫描到的总线从站节点数；扫描失败时为空。</summary>
        public int? ScannedBusNodeCount { get; set; }

        /// <summary>成功扫描到的总线从站 DI 输入点总数；扫描失败时为空。</summary>
        public int? ScannedBusDigitalInputCount { get; set; }

        /// <summary>成功扫描到的总线从站 DO 输出点总数；扫描失败时为空。</summary>
        public int? ScannedBusDigitalOutputCount { get; set; }

        /// <summary>总线节点扫描失败原因；控制器本体能力仍可能已经读取成功。</summary>
        public string BusScanError { get; set; }
    }

    /// <summary>记录从通道 0 起按 API 连续读取成功的实际数字 I/O 通道范围及探测边界。</summary>
    public sealed class MotionCardIoChannelRange
    {
        /// <summary>从通道 0 起连续读取成功的通道数；真实控制卡用作实际通道清单，仿真器可能返回虚拟范围。</summary>
        public int? MappableChannelCount { get; set; }

        /// <summary>首个读取失败的通道号；厂商直接返回数量时为空。</summary>
        public int? FirstUnavailableChannel { get; set; }

        /// <summary>首个读取失败通道对应的厂商 API 返回码；没有逐路探测时为空。</summary>
        public int? FirstUnavailableReturnCode { get; set; }

        /// <summary>是否因安全探测上限而停止；为真时数量表示至少可映射这么多通道。</summary>
        public bool ReachedProbeLimit { get; set; }
    }
}
