using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Configuration;

namespace MotionControl.Framework.Runtime.Configuration
{
    /// <summary>在 Host 创建运动卡之前检查设备控制卡、轴、IO 和急停配置之间的关系。</summary>
    public static class MachineHardwareConfigurationValidator
    {
        /// <summary>校验设备硬件映射；发现错误时汇总上下文并拒绝继续启动。</summary>
        public static void Validate(
            IEnumerable<MotionCardHardwareDefinition> motionCards,
            IEnumerable<AxisHardwareDefinition> axes,
            IEnumerable<DigitalIoHardwareDefinition> digitalInputs,
            IEnumerable<DigitalIoHardwareDefinition> digitalOutputs,
            string emergencyStopInputName)
        {
            if (motionCards == null)
            {
                throw new ArgumentNullException(nameof(motionCards));
            }

            if (axes == null)
            {
                throw new ArgumentNullException(nameof(axes));
            }

            if (digitalInputs == null)
            {
                throw new ArgumentNullException(nameof(digitalInputs));
            }

            if (digitalOutputs == null)
            {
                throw new ArgumentNullException(nameof(digitalOutputs));
            }

            var cardList = motionCards.ToList();
            var axisList = axes.ToList();
            var inputList = digitalInputs.ToList();
            var outputList = digitalOutputs.ToList();
            var errors = new List<string>();

            ValidateMotionCards(cardList, errors);
            ValidateAxes(axisList, cardList, errors);
            ValidateIoPoints(inputList, cardList, "数字输入", errors);
            ValidateIoPoints(outputList, cardList, "数字输出", errors);
            ValidateEmergencyStopInput(emergencyStopInputName, inputList, errors);

            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    "设备硬件配置校验失败：" + Environment.NewLine + "- " + string.Join(Environment.NewLine + "- ", errors));
            }
        }

        /// <summary>检查控制卡列表非空，并确保卡标识、适配器和连接配置均有效。</summary>
        private static void ValidateMotionCards(
            IReadOnlyCollection<MotionCardHardwareDefinition> cards,
            ICollection<string> errors)
        {
            if (cards.Count == 0)
            {
                errors.Add("至少需要配置一张运动控制卡。");
                return;
            }

            var controllerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards)
            {
                if (card == null)
                {
                    errors.Add("运动卡列表中包含空配置。");
                    continue;
                }

                if (!HasTrimmedText(card.ControllerId))
                {
                    errors.Add("运动卡缺少有效的 ControllerId。");
                }
                else if (!controllerIds.Add(card.ControllerId))
                {
                    errors.Add("运动卡 ControllerId 重复：" + card.ControllerId + "。");
                }

                if (!HasTrimmedText(card.AdapterType))
                {
                    errors.Add("控制卡 '" + card.ControllerId + "' 缺少 AdapterType。");
                }

                if (string.IsNullOrWhiteSpace(card.ConnectionConfigurationFile)
                    || System.IO.Path.IsPathRooted(card.ConnectionConfigurationFile)
                    || card.ConnectionConfigurationFile.Split('\\', '/').Any(part => part == ".."))
                {
                    errors.Add("控制卡 '" + card.ControllerId + "' 的连接配置必须是设备目录内的相对路径。");
                }
            }
        }

        /// <summary>检查轴的控制卡关联、逻辑名称、物理轴号和软限位范围。</summary>
        private static void ValidateAxes(
            IReadOnlyCollection<AxisHardwareDefinition> axes,
            IReadOnlyCollection<MotionCardHardwareDefinition> cards,
            ICollection<string> errors)
        {
            if (axes.Count == 0)
            {
                errors.Add("至少需要配置一个逻辑轴。");
                return;
            }

            var cardIds = new HashSet<string>(
                cards.Where(card => card != null && HasTrimmedText(card.ControllerId))
                    .Select(card => card.ControllerId),
                StringComparer.OrdinalIgnoreCase);
            var axisNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var physicalAxisNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var axis in axes)
            {
                if (axis == null)
                {
                    errors.Add("逻辑轴列表中包含空配置。");
                    continue;
                }

                var displayName = string.IsNullOrWhiteSpace(axis.Name) ? "未命名轴" : axis.Name;
                if (!HasTrimmedText(axis.Name))
                {
                    errors.Add("逻辑轴名称不能为空或包含首尾空格。");
                }
                else if (!axisNames.Add(axis.Name))
                {
                    errors.Add("逻辑轴名称重复：" + axis.Name + "。");
                }

                if (!HasTrimmedText(axis.ControllerId) || !cardIds.Contains(axis.ControllerId))
                {
                    errors.Add("逻辑轴 '" + displayName + "' 关联的控制卡未配置：" + axis.ControllerId + "。");
                }

                if (axis.AxisNumber < 0)
                {
                    errors.Add("逻辑轴 '" + displayName + "' 的物理轴号不能小于 0。");
                }
                else if (HasTrimmedText(axis.ControllerId)
                    && !physicalAxisNames.Add(axis.ControllerId + "/" + axis.AxisNumber))
                {
                    errors.Add("控制卡 '" + axis.ControllerId + "' 的物理轴号重复：" + axis.AxisNumber + "。");
                }

                if (!IsFinite(axis.MinimumPosition)
                    || !IsFinite(axis.MaximumPosition)
                    || axis.MinimumPosition >= axis.MaximumPosition)
                {
                    errors.Add("逻辑轴 '" + displayName + "' 的最小位置必须小于有效的最大位置。");
                }

                if (!IsFinite(axis.ManualSpeed) || axis.ManualSpeed <= 0)
                {
                    errors.Add("逻辑轴 '" + displayName
                        + "' 的手动速度必须是大于 0 的有限数值。");
                }

                if (!IsFinite(axis.ManualAcceleration) || axis.ManualAcceleration < 0
                    || !IsFinite(axis.ManualDeceleration) || axis.ManualDeceleration < 0)
                {
                    errors.Add("逻辑轴 '" + displayName
                        + "' 的手动加减速度如有填写，必须是大于 0 的有限数值；0 表示未填写。");
                }
            }
        }

        /// <summary>检查 DI 或 DO 名称、卡关联、通道范围和物理地址重复。</summary>
        private static void ValidateIoPoints(
            IReadOnlyCollection<DigitalIoHardwareDefinition> points,
            IReadOnlyCollection<MotionCardHardwareDefinition> cards,
            string ioTypeName,
            ICollection<string> errors)
        {
            var cardIds = new HashSet<string>(
                cards.Where(card => card != null && HasTrimmedText(card.ControllerId))
                    .Select(card => card.ControllerId),
                StringComparer.OrdinalIgnoreCase);
            var logicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var physicalChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var point in points)
            {
                if (point == null)
                {
                    errors.Add(ioTypeName + "列表中包含空配置。");
                    continue;
                }

                var displayName = string.IsNullOrWhiteSpace(point.Name) ? "未命名点位" : point.Name;
                if (!HasTrimmedText(point.Name))
                {
                    errors.Add(ioTypeName + "名称不能为空或包含首尾空格。");
                }
                else if (!logicalNames.Add(point.Name))
                {
                    errors.Add(ioTypeName + "名称重复：" + point.Name + "。");
                }

                if (!HasTrimmedText(point.ControllerId) || !cardIds.Contains(point.ControllerId))
                {
                    errors.Add(ioTypeName + " '" + displayName + "' 关联的控制卡未配置：" + point.ControllerId + "。");
                }

                if (point.Channel < 0)
                {
                    errors.Add(ioTypeName + " '" + displayName + "' 的物理通道号不能小于 0。");
                }
                else if (HasTrimmedText(point.ControllerId)
                    && !physicalChannels.Add(point.ControllerId + "/" + point.Channel))
                {
                    errors.Add(ioTypeName + " '" + displayName + "' 与其他点位重复使用物理通道 "
                        + point.ControllerId + "/" + point.Channel + "。");
                }
            }
        }

        /// <summary>确保急停逻辑输入已配置，且只对应一个数字输入点位。</summary>
        private static void ValidateEmergencyStopInput(
            string emergencyStopInputName,
            IReadOnlyCollection<DigitalIoHardwareDefinition> digitalInputs,
            ICollection<string> errors)
        {
            if (!HasTrimmedText(emergencyStopInputName))
            {
                errors.Add("必须配置急停状态对应的逻辑输入名称。");
                return;
            }

            var matches = digitalInputs.Count(input => input != null
                && string.Equals(input.Name, emergencyStopInputName, StringComparison.OrdinalIgnoreCase));
            if (matches != 1)
            {
                errors.Add("急停逻辑输入 '" + emergencyStopInputName + "' 必须在数字输入列表中配置一次。");
            }
        }

        /// <summary>判断字符串是否非空、首尾没有空格。</summary>
        private static bool HasTrimmedText(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && string.Equals(value, value.Trim(), StringComparison.Ordinal);
        }

        /// <summary>判断数值不是 NaN 或正负无穷。</summary>
        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
