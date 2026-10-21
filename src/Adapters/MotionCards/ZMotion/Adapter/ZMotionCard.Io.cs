using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.VendorApi.ZMotion.Native;

namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>正运动适配器中负责逻辑 DI/DO 与控制卡 IO 能力读取的实现部分。</summary>
    public sealed partial class ZMotionCard
    {
        /// <summary>读取正运动控制器的板载点数、最大可用编号范围和总线扩展点数。</summary>
        public Task<MotionCardIoCapabilities> ReadIoCapabilitiesAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (_nativeLock)
                {
                    IntPtr handle = GetConnectedHandle();
                    var response = new StringBuilder(4096);
                    CheckResult(
                        zmcaux.ZAux_Execute(handle, "?*MAX", response, 4096),
                        "读取控制器最大规格",
                        "控制卡",
                        ControllerId);

                    int builtInInputs;
                    int maximumInputs;
                    int builtInOutputs;
                    int maximumOutputs;
                    int busSlotCount;
                    if (!TryReadMaximumPair(response.ToString(), "max_in", out builtInInputs, out maximumInputs)
                        || !TryReadMaximumPair(response.ToString(), "max_out", out builtInOutputs, out maximumOutputs)
                        || !TryReadMaximumValue(response.ToString(), "max_slot", out busSlotCount))
                    {
                        throw new InvalidOperationException(
                            "正运动控制器的 ?*MAX 响应中缺少 max_in、max_out 或 max_slot，无法读取完整 I/O 能力。返回内容："
                            + response.ToString());
                    }

                    var capabilities = new MotionCardIoCapabilities
                    {
                        ControllerId = ControllerId,
                        BuiltInDigitalInputCount = builtInInputs,
                        BuiltInDigitalOutputCount = builtInOutputs,
                        MaximumDigitalInputCount = maximumInputs,
                        MaximumDigitalOutputCount = maximumOutputs
                    };

                    capabilities.DigitalInputChannelRange = ProbeIoChannelRange(
                        handle,
                        maximumInputs,
                        true,
                        cancellationToken);
                    capabilities.DigitalOutputChannelRange = ProbeIoChannelRange(
                        handle,
                        maximumOutputs,
                        false,
                        cancellationToken);
                    ReadScannedBusIoCapabilities(handle, busSlotCount, capabilities, cancellationToken);
                    return capabilities;
                }
            }, cancellationToken);
        }

        /// <summary>重新调用正运动只读 API，刷新控制器规格、DI/DO 通道范围和总线能力。</summary>
        public Task<MotionCardIoCapabilities> RefreshIoCapabilitiesAsync(CancellationToken cancellationToken)
        {
            return ReadIoCapabilitiesAsync(cancellationToken);
        }

        /// <summary>从通道 0 开始逐路读取 DI 或 DO，遇到首个失败通道时结束连续地址探测。</summary>
        private static MotionCardIoChannelRange ProbeIoChannelRange(
            IntPtr handle,
            int maximumChannelCount,
            bool input,
            CancellationToken cancellationToken)
        {
            var range = new MotionCardIoChannelRange();
            for (int channel = 0; channel < maximumChannelCount; channel++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint value = 0;
                int result = input
                    ? zmcaux.ZAux_Direct_GetIn(handle, channel, ref value)
                    : zmcaux.ZAux_Direct_GetOp(handle, channel, ref value);

                if (result == 0)
                {
                    continue;
                }

                range.MappableChannelCount = channel;
                range.FirstUnavailableChannel = channel;
                range.FirstUnavailableMessage = (input ? "ZAux_Direct_GetIn" : "ZAux_Direct_GetOp")
                    + " 返回码："
                    + result.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return range;
            }

            range.MappableChannelCount = maximumChannelCount;
            range.ReachedProbeLimit = maximumChannelCount > 0;
            return range;
        }

        /// <summary>按逻辑名称读取数字输入，并转换为配置的有效电平。</summary>
        public Task<bool> ReadInputAsync(string inputName, CancellationToken cancellationToken)
        {
            return Task.Run(() => ReadIoBit(inputName, true), cancellationToken);
        }

        /// <summary>按逻辑名称读取数字输出，并转换为配置的有效电平。</summary>
        public Task<bool> ReadOutputAsync(string outputName, CancellationToken cancellationToken)
        {
            return Task.Run(() => ReadIoBit(outputName, false), cancellationToken);
        }

        /// <summary>按逻辑名称写入数字输出，并转换为控制卡物理电平。</summary>
        public Task WriteOutputAsync(string outputName, bool value, CancellationToken cancellationToken)
        {
            return Task.Run(() => WriteIoBit(outputName, value), cancellationToken);
        }

        /// <summary>读取指定物理 DI 通道的原始电平，不依赖逻辑名称映射。</summary>
        public Task<bool> ReadDigitalInputChannelAsync(int channel, CancellationToken cancellationToken)
        {
            return Task.Run(() => ReadPhysicalIoBit(channel, true), cancellationToken);
        }

        /// <summary>读取指定物理 DO 通道的原始电平，不依赖逻辑名称映射。</summary>
        public Task<bool> ReadDigitalOutputChannelAsync(int channel, CancellationToken cancellationToken)
        {
            return Task.Run(() => ReadPhysicalIoBit(channel, false), cancellationToken);
        }

        /// <summary>通过正运动多路输入 API 批量读取物理 DI，按通道号还原各路电平。</summary>
        public Task<IReadOnlyDictionary<int, bool>> ReadDigitalInputChannelsAsync(
            IReadOnlyCollection<int> channels,
            CancellationToken cancellationToken)
        {
            return ReadPhysicalIoChannelsAsync(channels, true, cancellationToken);
        }

        /// <summary>通过正运动多路输出 API 批量读取物理 DO，按通道号还原各路电平。</summary>
        public Task<IReadOnlyDictionary<int, bool>> ReadDigitalOutputChannelsAsync(
            IReadOnlyCollection<int> channels,
            CancellationToken cancellationToken)
        {
            return ReadPhysicalIoChannelsAsync(channels, false, cancellationToken);
        }

        /// <summary>按物理高低电平写入 DO；通道编号由控制卡能力扫描结果限定。</summary>
        public Task WriteDigitalOutputChannelAsync(
            int channel,
            bool physicalHigh,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => WritePhysicalOutputBit(channel, physicalHigh), cancellationToken);
        }

        /// <summary>在原生锁内读取当前逻辑名称对应的物理 DI 或 DO。</summary>
        private bool ReadIoBit(string pointName, bool input)
        {
            lock (_nativeLock)
            {
                var points = input ? _inputsByName : _outputsByName;
                var point = FindIoPoint(points, pointName, input ? "输入" : "输出");
                var handle = GetConnectedHandle();
                uint physicalValue = 0;
                var result = input
                    ? zmcaux.ZAux_Direct_GetIn(handle, point.Channel, ref physicalValue)
                    : zmcaux.ZAux_Direct_GetOp(handle, point.Channel, ref physicalValue);
                CheckIoResult(result, input ? "读取 DI" : "读取 DO", point.Name);

                var physicalHigh = physicalValue != 0;
                return physicalHigh == point.ActiveHigh;
            }
        }

        /// <summary>在原生锁内读取未映射或已映射的物理 DI/DO 通道电平。</summary>
        private bool ReadPhysicalIoBit(int channel, bool input)
        {
            if (channel < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channel), "物理 IO 通道号不能小于零。");
            }

            lock (_nativeLock)
            {
                IntPtr handle = GetConnectedHandle();
                uint physicalValue = 0;
                int result = input
                    ? zmcaux.ZAux_Direct_GetIn(handle, channel, ref physicalValue)
                    : zmcaux.ZAux_Direct_GetOp(handle, channel, ref physicalValue);
                string channelName = (input ? "DI" : "DO")
                    + channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
                CheckResult(result, input ? "读取物理 DI" : "读取物理 DO", "物理 IO", channelName);
                return physicalValue != 0;
            }
        }

        /// <summary>从通道 0 起批量读取到最高请求通道，减少 IO 监控页的单点通讯次数。</summary>
        private Task<IReadOnlyDictionary<int, bool>> ReadPhysicalIoChannelsAsync(
            IReadOnlyCollection<int> channels,
            bool input,
            CancellationToken cancellationToken)
        {
            if (channels == null)
            {
                throw new ArgumentNullException(nameof(channels));
            }

            int[] requestedChannels = channels.Distinct().OrderBy(channel => channel).ToArray();
            if (requestedChannels.Any(channel => channel < 0))
            {
                throw new ArgumentOutOfRangeException(nameof(channels), "物理 IO 通道号不能小于零。");
            }

            if (requestedChannels.Length == 0)
            {
                return Task.FromResult<IReadOnlyDictionary<int, bool>>(
                    new Dictionary<int, bool>());
            }

            return Task.Run<IReadOnlyDictionary<int, bool>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                int lastChannelExclusive = checked(requestedChannels[requestedChannels.Length - 1] + 1);
                int wordCount = checked((lastChannelExclusive + 31) / 32);
                var states = new Dictionary<int, bool>(requestedChannels.Length);

                lock (_nativeLock)
                {
                    IntPtr handle = GetConnectedHandle();
                    if (input)
                    {
                        var packedValues = new int[wordCount];
                        CheckResult(
                            zmcaux.ZAux_Direct_GetInMulti(handle, 0, lastChannelExclusive, packedValues),
                            "批量读取物理 DI",
                            "物理 IO",
                            "DI0-DI" + (lastChannelExclusive - 1).ToString(CultureInfo.InvariantCulture));

                        foreach (int channel in requestedChannels)
                        {
                            uint packedWord = unchecked((uint)packedValues[channel / 32]);
                            states.Add(channel, (packedWord & (1u << (channel % 32))) != 0);
                        }
                    }
                    else
                    {
                        if (lastChannelExclusive > ushort.MaxValue)
                        {
                            throw new ArgumentOutOfRangeException(
                                nameof(channels),
                                "正运动多路 DO 读取接口的结束通道超出 UInt16 范围。");
                        }

                        var packedValues = new uint[wordCount];
                        CheckResult(
                            zmcaux.ZAux_Direct_GetOutMulti(
                                handle,
                                0,
                                (ushort)lastChannelExclusive,
                                packedValues),
                            "批量读取物理 DO",
                            "物理 IO",
                            "DO0-DO" + (lastChannelExclusive - 1).ToString(CultureInfo.InvariantCulture));

                        foreach (int channel in requestedChannels)
                        {
                            states.Add(
                                channel,
                                (packedValues[channel / 32] & (1u << (channel % 32))) != 0);
                        }
                    }
                }

                return (IReadOnlyDictionary<int, bool>)states;
            }, cancellationToken);
        }

        /// <summary>在原生锁内写入指定物理 DO 通道的高低电平。</summary>
        private void WritePhysicalOutputBit(int channel, bool physicalHigh)
        {
            if (channel < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channel), "物理 IO 通道号不能小于零。");
            }

            lock (_nativeLock)
            {
                IntPtr handle = GetConnectedHandle();
                string channelName = "DO" + channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
                CheckResult(
                    zmcaux.ZAux_Direct_SetOp(handle, channel, physicalHigh ? 1u : 0u),
                    "写入物理 DO",
                    "物理 IO",
                    channelName);
            }
        }

        /// <summary>在原生锁内查找当前 DO 映射并将逻辑值写入正运动控制卡。</summary>
        private void WriteIoBit(string outputName, bool value)
        {
            lock (_nativeLock)
            {
                var point = FindIoPoint(_outputsByName, outputName, "输出");
                var handle = GetConnectedHandle();
                var physicalHigh = value == point.ActiveHigh;
                CheckResult(
                    zmcaux.ZAux_Direct_SetOp(handle, point.Channel, physicalHigh ? 1u : 0u),
                    "写入 DO",
                    "逻辑 IO",
                    point.Name);
            }
        }

        /// <summary>在当前连接内校验并整体替换逻辑 DI/DO 名称映射，不重新创建控制卡或轴对象。</summary>
        public void UpdateIoMappings(
            IReadOnlyCollection<ZMotionIoPointConfiguration> digitalInputs,
            IReadOnlyCollection<ZMotionIoPointConfiguration> digitalOutputs)
        {
            if (digitalInputs == null)
            {
                throw new ArgumentNullException(nameof(digitalInputs));
            }

            if (digitalOutputs == null)
            {
                throw new ArgumentNullException(nameof(digitalOutputs));
            }

            var newInputsByName = CreateIoPointMap(digitalInputs.ToList(), "输入", _controllerId);
            var newOutputsByName = CreateIoPointMap(digitalOutputs.ToList(), "输出", _controllerId);

            lock (_nativeLock)
            {
                GetConnectedHandle();
                _inputsByName = newInputsByName;
                _outputsByName = newOutputsByName;
            }
        }

        /// <summary>读取每个已扫描总线节点的 DI/DO 数量，并在扫描失败时保留板卡本体能力。</summary>
        private void ReadScannedBusIoCapabilities(
            IntPtr handle,
            int busSlotCount,
            MotionCardIoCapabilities capabilities,
            CancellationToken cancellationToken)
        {
            if (busSlotCount < 0 || busSlotCount > 64)
            {
                capabilities.BusScanError = "控制器返回的总线数量无效：" + busSlotCount + "。";
                return;
            }

            int totalNodeCount = 0;
            int totalInputCount = 0;
            int totalOutputCount = 0;

            try
            {
                for (int slot = 0; slot < busSlotCount; slot++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int nodeCount = 0;
                    CheckResult(
                        zmcaux.ZAux_BusCmd_GetNodeNum(handle, slot, ref nodeCount),
                        "读取总线节点数量",
                        "总线槽位",
                        slot.ToString(System.Globalization.CultureInfo.InvariantCulture));

                    if (nodeCount < 0 || nodeCount > 4096)
                    {
                        throw new InvalidOperationException(
                            "控制器返回了无效的总线节点数量：槽位 " + slot + "，节点数 " + nodeCount + "。");
                    }

                    totalNodeCount += nodeCount;
                    for (int node = 0; node < nodeCount; node++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int inputCount = 0;
                        int outputCount = 0;
                        string nodeName = "槽位 "
                            + slot.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + "，节点 "
                            + node.ToString(System.Globalization.CultureInfo.InvariantCulture);

                        CheckResult(
                            zmcaux.ZAux_BusCmd_GetNodeInfo(
                                handle,
                                (uint)slot,
                                (uint)node,
                                10,
                                ref inputCount),
                            "读取总线节点 DI 数量",
                            "总线节点",
                            nodeName);
                        CheckResult(
                            zmcaux.ZAux_BusCmd_GetNodeInfo(
                                handle,
                                (uint)slot,
                                (uint)node,
                                11,
                                ref outputCount),
                            "读取总线节点 DO 数量",
                            "总线节点",
                            nodeName);

                        if (inputCount < 0 || outputCount < 0)
                        {
                            throw new InvalidOperationException("控制器返回了负数的总线 DI/DO 数量：" + nodeName + "。");
                        }

                        totalInputCount += inputCount;
                        totalOutputCount += outputCount;
                    }
                }
            }
            catch (InvalidOperationException error)
            {
                capabilities.BusScanError = error.Message;
                return;
            }

            capabilities.ScannedBusNodeCount = totalNodeCount;
            capabilities.ScannedBusDigitalInputCount = totalInputCount;
            capabilities.ScannedBusDigitalOutputCount = totalOutputCount;
        }

        /// <summary>从 ?*MAX 的返回文本读取指定参数的两个逗号分隔整数。</summary>
        private static bool TryReadMaximumPair(
            string response,
            string parameterName,
            out int firstValue,
            out int secondValue)
        {
            firstValue = 0;
            secondValue = 0;
            string value = FindMaximumSpecificationValue(response, parameterName);
            if (value == null)
            {
                return false;
            }

            string[] values = value.Split(',');
            return values.Length == 2
                && int.TryParse(
                    values[0].Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out firstValue)
                && int.TryParse(
                    values[1].Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out secondValue)
                && firstValue >= 0
                && secondValue >= firstValue;
        }

        /// <summary>从 ?*MAX 的返回文本读取指定参数的单个整数。</summary>
        private static bool TryReadMaximumValue(string response, string parameterName, out int value)
        {
            value = 0;
            string text = FindMaximumSpecificationValue(response, parameterName);
            return text != null
                && int.TryParse(
                    text,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value)
                && value >= 0;
        }

        /// <summary>按参数名查找 ?*MAX 输出中的冒号后文本。</summary>
        private static string FindMaximumSpecificationValue(string response, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return null;
            }

            string expectedPrefix = parameterName + ":";
            string[] lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (!line.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return line.Substring(expectedPrefix.Length).Trim();
            }

            return null;
        }

        /// <summary>按逻辑名称查找已配置 IO 点位，找不到时列出当前可用名称。</summary>
        private ZMotionIoPointConfiguration FindIoPoint(
            IDictionary<string, ZMotionIoPointConfiguration> points,
            string pointName,
            string direction)
        {
            if (string.IsNullOrWhiteSpace(pointName))
            {
                throw new ArgumentException("逻辑" + direction + "名称不能为空。", nameof(pointName));
            }

            ZMotionIoPointConfiguration point;
            if (points.TryGetValue(pointName, out point))
            {
                return point;
            }

            var names = string.Join(", ", points.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            throw new KeyNotFoundException(
                "正运动逻辑" + direction + " '" + pointName + "' 未配置。已配置名称：" + names + "。");
        }

        /// <summary>校验一组逻辑 IO 配置，并按逻辑名称建立查找表。</summary>
        private static Dictionary<string, ZMotionIoPointConfiguration> CreateIoPointMap(
            IReadOnlyCollection<ZMotionIoPointConfiguration> points,
            string direction,
            string controllerId)
        {
            var pointsByName = new Dictionary<string, ZMotionIoPointConfiguration>(StringComparer.OrdinalIgnoreCase);
            foreach (var point in points)
            {
                if (point == null)
                {
                    throw new ArgumentException("正运动逻辑" + direction + "配置中包含空对象。", nameof(points));
                }

                point.Validate(direction);
                if (!string.Equals(point.ControllerId, controllerId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "逻辑" + direction + " '" + point.Name + "' 属于控制卡 '"
                        + point.ControllerId + "'，不能交给控制卡 '" + controllerId + "'。",
                        nameof(points));
                }

                if (pointsByName.ContainsKey(point.Name))
                {
                    throw new ArgumentException(
                        "正运动逻辑" + direction + "名称重复：" + point.Name + "。",
                        nameof(points));
                }

                pointsByName.Add(point.Name, point);
            }

            return pointsByName;
        }
    }
}
