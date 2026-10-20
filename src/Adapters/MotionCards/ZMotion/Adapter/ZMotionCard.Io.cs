using System;
using System.Collections.Generic;
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
            var point = FindIoPoint(_inputsByName, inputName, "输入");
            return Task.Run(() => ReadIoBit(point, true), cancellationToken);
        }

        /// <summary>按逻辑名称读取数字输出，并转换为配置的有效电平。</summary>
        public Task<bool> ReadOutputAsync(string outputName, CancellationToken cancellationToken)
        {
            var point = FindIoPoint(_outputsByName, outputName, "输出");
            return Task.Run(() => ReadIoBit(point, false), cancellationToken);
        }

        /// <summary>按逻辑名称写入数字输出，并转换为控制卡物理电平。</summary>
        public Task WriteOutputAsync(string outputName, bool value, CancellationToken cancellationToken)
        {
            var point = FindIoPoint(_outputsByName, outputName, "输出");
            return Task.Run(() => WriteIoBit(point, value), cancellationToken);
        }

        /// <summary>读取物理 DI 或 DO，并按逻辑 IO 的有效电平转换结果。</summary>
        private bool ReadIoBit(ZMotionIoPointConfiguration point, bool input)
        {
            lock (_nativeLock)
            {
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

        /// <summary>将逻辑 DO 值转换为物理电平后写入正运动控制卡。</summary>
        private void WriteIoBit(ZMotionIoPointConfiguration point, bool value)
        {
            lock (_nativeLock)
            {
                var handle = GetConnectedHandle();
                var physicalHigh = value == point.ActiveHigh;
                CheckResult(
                    zmcaux.ZAux_Direct_SetOp(handle, point.Channel, physicalHigh ? 1u : 0u),
                    "写入 DO",
                    "逻辑 IO",
                    point.Name);
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
