using System;
using System.Collections.Generic;
using System.Linq;

namespace MotionControl.Framework.Contracts.Clearance
{
    /// <summary>设备项目声明的一套固定清料计划及绑定的现场恢复条件。</summary>
    public sealed class ControlledClearanceProfile
    {
        /// <summary>检查计划、轴边界、全部反馈和共享区域条件并冻结集合。</summary>
        public ControlledClearanceProfile(string id, ControlledClearancePlan plan, ControlledClearanceRecovery recovery,
            IEnumerable<ClearanceAxisLimit> axisLimits, IEnumerable<ClearanceSharedFeedback> sharedFeedback = null)
        {
            Id = ClearanceValues.Name(id);
            if (Id != Id.Trim()) throw new ArgumentException("清料配置编号不能包含首尾空格。", nameof(id));
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            Recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
            var axes = (axisLimits ?? throw new ArgumentNullException(nameof(axisLimits))).ToArray();
            var areas = (sharedFeedback ?? Enumerable.Empty<ClearanceSharedFeedback>()).ToArray();
            if (axes.Any(item => item == null) || areas.Any(item => item == null)
                || axes.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != axes.Length
                || areas.Select(item => item.ResourceName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != areas.Length)
                throw new ArgumentException("轴安全边界和共享区域反馈不能包含空项或重复项。");
            AxisLimits = Array.AsReadOnly(axes);
            SharedFeedback = Array.AsReadOnly(areas);
            RequiredResources = Array.AsReadOnly(Plan.RequiredResources
                .Concat(Recovery.Axes.Select(item => "Axis:" + item.Name))
                .Concat(Recovery.Outputs.Select(item => "Output:" + item.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            Validate();
        }

        /// <summary>当前设备范围内稳定且唯一的配置编号。</summary>
        public string Id { get; }
        /// <summary>固定动作清单；运行时不能由操作员替换。</summary>
        public ControlledClearancePlan Plan { get; }
        /// <summary>与动作清单绑定的现场恢复条件。</summary>
        public ControlledClearanceRecovery Recovery { get; }
        /// <summary>设备项目给出的轴目标与速度上限。</summary>
        public IReadOnlyList<ClearanceAxisLimit> AxisLimits { get; }
        /// <summary>每个共享区域对应的设备反馈。</summary>
        public IReadOnlyList<ClearanceSharedFeedback> SharedFeedback { get; }
        /// <summary>计划和恢复条件涉及的全部轴、输出与共享区域，须在动作前一起独占。</summary>
        public IReadOnlyList<string> RequiredResources { get; }

        /// <summary>校验完整性；这些软件条件不替代设备硬件安全回路。</summary>
        private void Validate()
        {
            var limits = AxisLimits.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
            foreach (string name in Plan.RequiredResources.Where(item => item.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase)).Select(item => item.Substring(5)))
                if (!limits.ContainsKey(name)) throw new ArgumentException("清料轴缺少设备声明的安全边界：" + name);
            foreach (ClearanceAxisCondition axis in Plan.Actions.SelectMany(item => item.Axes.Concat(item.MaintainAxes)).Concat(Recovery.Axes))
            {
                ClearanceAxisLimit limit;
                if (!limits.TryGetValue(axis.Name, out limit) || axis.Minimum < limit.Minimum || axis.Maximum > limit.Maximum)
                    throw new ArgumentException("清料或恢复轴条件超出设备声明的安全边界：" + axis.Name);
            }
            foreach (ClearanceAbsoluteMove move in Plan.Actions.OfType<ClearanceAbsoluteMove>())
            {
                ClearanceAxisLimit limit = limits[move.AxisName];
                if (!ClearanceValues.Finite(move.Target - move.Tolerance) || !ClearanceValues.Finite(move.Target + move.Tolerance)
                    || move.Target - move.Tolerance < limit.Minimum || move.Target + move.Tolerance > limit.Maximum
                    || move.Profile.Speed > limit.MaximumSpeed || move.Profile.Acceleration > limit.MaximumAcceleration
                    || move.Profile.Deceleration > limit.MaximumDeceleration)
                    throw new ArgumentException("清料目标、容差或速度超出设备声明的安全边界：" + move.AxisName);
            }
            var requiredInputs = Plan.Actions.SelectMany(item => item.Inputs.Concat(item.MaintainInputs)
                .Concat(item is ClearanceSetOutput output ? output.CompletionInputs : Enumerable.Empty<ClearanceInputCondition>()))
                .Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (string name in requiredInputs)
                if (!Recovery.Inputs.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("恢复条件遗漏清料输入：" + name);
            foreach (string name in Plan.RequiredResources)
            {
                if (name.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Recovery.Axes.Any(item => string.Equals(item.Name, name.Substring(5), StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException("恢复条件遗漏清料轴：" + name.Substring(5));
                }
                else if (name.StartsWith("Output:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Recovery.Outputs.Any(item => string.Equals(item.Name, name.Substring(7), StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException("恢复条件遗漏清料输出：" + name.Substring(7));
                }
                else
                {
                    ClearanceSharedFeedback area = SharedFeedback.SingleOrDefault(item => string.Equals(item.ResourceName, name, StringComparison.OrdinalIgnoreCase));
                    if (area == null) throw new ArgumentException("共享区域缺少设备声明的安全反馈：" + name);
                    foreach (ClearanceInputCondition condition in area.Inputs)
                        if (!Recovery.Inputs.Any(item => string.Equals(item.Name, condition.Name, StringComparison.OrdinalIgnoreCase)
                            && item.Expected == condition.Expected))
                            throw new ArgumentException("共享区域反馈未包含在恢复条件中：" + name + "/" + condition.Name);
                }
            }
            foreach (ClearanceSharedFeedback area in SharedFeedback)
                if (!Plan.RequiredResources.Contains(area.ResourceName, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("共享区域反馈没有对应的计划资源：" + area.ResourceName);
        }
    }

    /// <summary>设备为逻辑轴声明的可用绝对范围及曲线上限。</summary>
    public sealed class ClearanceAxisLimit
    {
        /// <summary>所有数值使用对应轴的工程单位，必须有限且速度上限大于零。</summary>
        public ClearanceAxisLimit(string name, double minimum, double maximum, double maximumSpeed,
            double maximumAcceleration, double maximumDeceleration)
        {
            Name = ClearanceValues.Name(name);
            if (!ClearanceValues.Finite(minimum) || !ClearanceValues.Finite(maximum) || minimum > maximum
                || !ClearanceValues.Finite(maximumSpeed) || maximumSpeed <= 0
                || !ClearanceValues.Finite(maximumAcceleration) || maximumAcceleration <= 0
                || !ClearanceValues.Finite(maximumDeceleration) || maximumDeceleration <= 0)
                throw new ArgumentException("清料轴目标范围或速度上限无效：" + name);
            Minimum = minimum; Maximum = maximum; MaximumSpeed = maximumSpeed;
            MaximumAcceleration = maximumAcceleration; MaximumDeceleration = maximumDeceleration;
        }
        public string Name { get; }
        public double Minimum { get; }
        public double Maximum { get; }
        public double MaximumSpeed { get; }
        public double MaximumAcceleration { get; }
        public double MaximumDeceleration { get; }
    }

    /// <summary>设备明确指定共享资源恢复需要满足的独立输入条件。</summary>
    public sealed class ClearanceSharedFeedback
    {
        /// <summary>反馈必须至少包含一项输入，且输入名称不可重复。</summary>
        public ClearanceSharedFeedback(string resourceName, IEnumerable<ClearanceInputCondition> safetyInputs)
        {
            ResourceName = ClearanceValues.Name(resourceName);
            if (ResourceName.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase)
                || ResourceName.StartsWith("Output:", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("共享区域名称不能使用轴或输出前缀。", nameof(resourceName));
            var copy = (safetyInputs ?? throw new ArgumentNullException(nameof(safetyInputs))).ToArray();
            if (copy.Length == 0 || copy.Any(item => item == null)
                || copy.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length)
                throw new ArgumentException("共享区域必须声明不重复的安全反馈输入。", nameof(safetyInputs));
            Inputs = Array.AsReadOnly(copy);
        }
        public string ResourceName { get; }
        public IReadOnlyList<ClearanceInputCondition> Inputs { get; }
    }
}
