using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Framework.Contracts.Clearance
{
    /// <summary>设备明确声明的清料步骤；仅支持固定绝对定位和固定输出电平。</summary>
    public sealed class ControlledClearancePlan
    {
        /// <summary>复制固定步骤，并计算需要由调用方申请的资源。</summary>
        public ControlledClearancePlan(IEnumerable<ControlledClearanceAction> actions, IEnumerable<string> sharedResources = null)
        {
            if (actions == null) throw new ArgumentNullException(nameof(actions));
            var copy = actions.ToArray();
            if (copy.Length == 0 || copy.Any(x => x == null)) throw new ArgumentException("清料步骤不能为空。", nameof(actions));
            ValidateOutputSequence(copy);
            var shared = (sharedResources ?? Enumerable.Empty<string>()).ToArray();
            if (shared.Any(x => string.IsNullOrWhiteSpace(x) || x != x.Trim() || x.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase)
                || x.StartsWith("Output:", StringComparison.OrdinalIgnoreCase))
                || shared.Distinct(StringComparer.OrdinalIgnoreCase).Count() != shared.Length)
                throw new ArgumentException("共享资源不能为空、重复或使用轴和输出保留前缀。", nameof(sharedResources));
            Actions = Array.AsReadOnly(copy);
            RequiredResources = Array.AsReadOnly(copy.SelectMany(action => action.Axes.Concat(action.MaintainAxes).Select(axis => "Axis:" + axis.Name)
                .Concat(action.MaintainOutputs.Select(condition => "Output:" + condition.Name))
                .Concat(action is ClearanceSetOutput output ? new[] { "Output:" + output.OutputName } : new string[0]))
                .Concat(shared)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        /// <summary>按顺序执行的不可变步骤集合。</summary>
        public IReadOnlyList<ControlledClearanceAction> Actions { get; }
        /// <summary>预先申请的轴和输出资源；安全条件轴也包含在内。</summary>
        public IReadOnlyCollection<string> RequiredResources { get; }

        /// <summary>固定计划中，已经声明的输出状态不能在相邻步骤间无指令地改变。</summary>
        private static void ValidateOutputSequence(IEnumerable<ControlledClearanceAction> actions)
        {
            var known = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (ControlledClearanceAction action in actions)
            {
                foreach (ClearanceOutputCondition condition in action.MaintainOutputs)
                {
                    bool previous;
                    if (known.TryGetValue(condition.Name, out previous) && previous != condition.Expected)
                        throw new ArgumentException("清料步骤间输出维持状态不一致：" + condition.Name);
                    known[condition.Name] = condition.Expected;
                }
                var output = action as ClearanceSetOutput;
                if (output == null) continue;
                bool old;
                if (known.TryGetValue(output.OutputName, out old) && old != output.BeforeValue)
                    throw new ArgumentException("清料输出原状态与前一步不一致：" + output.OutputName);
                known[output.OutputName] = output.Value;
            }
        }
    }

    /// <summary>设备要求在动作前和动作期间保持的逻辑输出电平。</summary>
    public sealed class ClearanceOutputCondition
    {
        /// <summary>指定逻辑输出及其必须保持的电平。</summary>
        public ClearanceOutputCondition(string name, bool expected)
        { Name = ClearanceValues.Name(name); Expected = expected; }
        /// <summary>需要读取和独占的逻辑输出。</summary>
        public string Name { get; }
        /// <summary>动作前和动作期间要求的状态。</summary>
        public bool Expected { get; }
    }

    /// <summary>一个必须满足指定状态的逻辑输入。</summary>
    public sealed class ClearanceInputCondition
    {
        /// <summary>指定逻辑输入及所需有效状态。</summary>
        public ClearanceInputCondition(string name, bool expected)
        { Name = ClearanceValues.Name(name); Expected = expected; }
        /// <summary>逻辑输入名称。</summary>
        public string Name { get; }
        /// <summary>动作前必须读取到的输入状态。</summary>
        public bool Expected { get; }
    }

    /// <summary>设备明确声明的互斥输入；两个均有效时表示反馈冲突。</summary>
    public sealed class ClearanceInputConflict
    {
        /// <summary>定义两路互斥的逻辑输入。</summary>
        public ClearanceInputConflict(string first, string second)
        {
            First = ClearanceValues.Name(first); Second = ClearanceValues.Name(second);
            if (string.Equals(First, Second, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("互斥输入不能相同。");
        }
        /// <summary>第一路逻辑输入。</summary>
        public string First { get; }
        /// <summary>第二路逻辑输入。</summary>
        public string Second { get; }
    }

    /// <summary>必须停稳并处于指定范围的逻辑轴。</summary>
    public sealed class ClearanceAxisCondition
    {
        /// <summary>指定轴及允许的工程单位位置区间。</summary>
        public ClearanceAxisCondition(string name, double minimum, double maximum)
        {
            Name = ClearanceValues.Name(name);
            if (!ClearanceValues.Finite(minimum) || !ClearanceValues.Finite(maximum) || minimum > maximum)
                throw new ArgumentException("轴条件位置范围无效。");
            Minimum = minimum; Maximum = maximum;
        }
        /// <summary>逻辑轴名称。</summary>
        public string Name { get; }
        /// <summary>允许的最小位置，包含边界。</summary>
        public double Minimum { get; }
        /// <summary>允许的最大位置，包含边界。</summary>
        public double Maximum { get; }
    }

    /// <summary>只读固定动作声明；输入、轴条件均需由设备明确配置。</summary>
    public abstract class ControlledClearanceAction
    {
        /// <summary>复制固定条件并检查重复名称及步骤时限。</summary>
        internal ControlledClearanceAction(string name, IEnumerable<ClearanceInputCondition> inputs,
            IEnumerable<ClearanceAxisCondition> axes, TimeSpan timeout, IEnumerable<ClearanceInputCondition> maintainInputs,
            IEnumerable<ClearanceAxisCondition> maintainAxes, IEnumerable<ClearanceOutputCondition> maintainOutputs)
        {
            Name = ClearanceValues.Name(name);
            if (inputs == null || axes == null) throw new ArgumentNullException(inputs == null ? nameof(inputs) : nameof(axes));
            var inputCopy = inputs.ToArray(); var axisCopy = axes.ToArray();
            if (inputCopy.Length == 0 || axisCopy.Length == 0 || inputCopy.Any(x => x == null) || axisCopy.Any(x => x == null)
                || inputCopy.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputCopy.Length
                || axisCopy.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != axisCopy.Length)
                throw new ArgumentException("清料动作必须具有不重复的输入和轴条件。");
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
            var maintainInputCopy = (maintainInputs ?? Enumerable.Empty<ClearanceInputCondition>()).ToArray();
            var maintainAxisCopy = (maintainAxes ?? Enumerable.Empty<ClearanceAxisCondition>()).ToArray();
            var maintainOutputCopy = (maintainOutputs ?? Enumerable.Empty<ClearanceOutputCondition>()).ToArray();
            if (maintainInputCopy.Length == 0 || maintainInputCopy.Any(x => x == null) || maintainAxisCopy.Any(x => x == null)
                || maintainOutputCopy.Any(x => x == null)
                || maintainInputCopy.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != maintainInputCopy.Length
                || maintainAxisCopy.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != maintainAxisCopy.Length
                || maintainOutputCopy.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != maintainOutputCopy.Length)
                throw new ArgumentException("清料动作必须声明不重复的持续输入条件。");
            Inputs = Array.AsReadOnly(inputCopy); Axes = Array.AsReadOnly(axisCopy); Timeout = timeout;
            MaintainInputs = Array.AsReadOnly(maintainInputCopy); MaintainAxes = Array.AsReadOnly(maintainAxisCopy);
            MaintainOutputs = Array.AsReadOnly(maintainOutputCopy);
        }
        /// <summary>操作员可识别的步骤名称。</summary>
        public string Name { get; }
        /// <summary>执行前必须满足的输入条件。</summary>
        public IReadOnlyList<ClearanceInputCondition> Inputs { get; }
        /// <summary>执行前必须满足的轴停稳和位置条件。</summary>
        public IReadOnlyList<ClearanceAxisCondition> Axes { get; }
        /// <summary>该步骤从前置检查开始的最长允许时间。</summary>
        public TimeSpan Timeout { get; }
        /// <summary>动作期间周期性检查的设备许可输入。</summary>
        public IReadOnlyList<ClearanceInputCondition> MaintainInputs { get; }
        /// <summary>动作期间周期性检查的其他轴停稳位置条件。</summary>
        public IReadOnlyList<ClearanceAxisCondition> MaintainAxes { get; }
        /// <summary>动作前和动作期间必须保持的设备输出状态。</summary>
        public IReadOnlyList<ClearanceOutputCondition> MaintainOutputs { get; }
    }

    /// <summary>固定绝对坐标与曲线；目标容差为设备指定的工程单位。</summary>
    public sealed class ClearanceAbsoluteMove : ControlledClearanceAction
    {
        /// <summary>声明固定目标、到位容差、运动曲线及前置条件。</summary>
        public ClearanceAbsoluteMove(string name, string axisName, double target, double tolerance, AxisMotionProfile profile,
            IEnumerable<ClearanceInputCondition> inputs, IEnumerable<ClearanceAxisCondition> axes, TimeSpan timeout,
            IEnumerable<ClearanceInputCondition> maintainInputs = null, IEnumerable<ClearanceAxisCondition> maintainAxes = null,
            IEnumerable<ClearanceOutputCondition> maintainOutputs = null)
            : base(name, inputs, axes, timeout, maintainInputs, maintainAxes, maintainOutputs)
        {
            AxisName = ClearanceValues.Name(axisName);
            if (!ClearanceValues.Finite(target) || !ClearanceValues.Finite(tolerance) || tolerance < 0) throw new ArgumentException("目标或到位容差无效。");
            if (!Axes.Any(x => string.Equals(x.Name, AxisName, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("定位轴必须具有停稳位置前置条件。");
            if (MaintainAxes.Any(x => string.Equals(x.Name, AxisName, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("运动轴不能作为自身的停稳维持条件。");
            Target = target; Tolerance = tolerance; Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }
        /// <summary>执行定位的逻辑轴。</summary>
        public string AxisName { get; }
        /// <summary>设备声明的固定绝对目标。</summary>
        public double Target { get; }
        /// <summary>该动作允许的目标反馈位置误差。</summary>
        public double Tolerance { get; }
        /// <summary>设备声明的速度与加减速度。</summary>
        public AxisMotionProfile Profile { get; }
    }

    /// <summary>固定逻辑输出及电平，不附带任意回调。</summary>
    public sealed class ClearanceSetOutput : ControlledClearanceAction
    {
        /// <summary>声明固定输出、目标状态和前置条件。</summary>
        public ClearanceSetOutput(string name, string outputName, bool beforeValue, bool value, IEnumerable<ClearanceInputCondition> inputs,
            IEnumerable<ClearanceAxisCondition> axes, IEnumerable<ClearanceInputCondition> completionInputs,
            TimeSpan timeout, IEnumerable<ClearanceInputConflict> completionConflicts = null,
            IEnumerable<ClearanceInputCondition> maintainInputs = null, IEnumerable<ClearanceAxisCondition> maintainAxes = null,
            IEnumerable<ClearanceOutputCondition> maintainOutputs = null)
            : base(name, inputs, axes, timeout, maintainInputs, maintainAxes, maintainOutputs)
        {
            OutputName = ClearanceValues.Name(outputName); Value = value;
            if (beforeValue == value) throw new ArgumentException("清料输出动作必须声明不同的原状态与目标状态。", nameof(beforeValue));
            BeforeValue = beforeValue;
            if (MaintainOutputs.Any(item => string.Equals(item.Name, OutputName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("输出动作自身不能同时声明为固定维持输出。", nameof(maintainOutputs));
            if (completionInputs == null) throw new ArgumentNullException(nameof(completionInputs));
            var feedback = completionInputs.ToArray();
            if (feedback.Length == 0 || feedback.Any(x => x == null)
                || feedback.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != feedback.Length)
                throw new ArgumentException("输出动作必须声明不重复的独立输入反馈。", nameof(completionInputs));
            var conflicts = (completionConflicts ?? Enumerable.Empty<ClearanceInputConflict>()).ToArray();
            var names = new HashSet<string>(feedback.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            if (conflicts.Any(x => x == null || !names.Contains(x.First) || !names.Contains(x.Second)))
                throw new ArgumentException("互斥输入必须包含在完成反馈中。", nameof(completionConflicts));
            CompletionInputs = Array.AsReadOnly(feedback); CompletionConflicts = Array.AsReadOnly(conflicts);
        }
        /// <summary>逻辑输出名称。</summary>
        public string OutputName { get; }
        /// <summary>下发前必须独立读取确认的原输出状态。</summary>
        public bool BeforeValue { get; }
        /// <summary>设备声明的输出状态。</summary>
        public bool Value { get; }
        /// <summary>动作完成时要求稳定满足的独立逻辑输入。</summary>
        public IReadOnlyList<ClearanceInputCondition> CompletionInputs { get; }
        /// <summary>设备声明的互斥反馈；同时有效时禁止完成。</summary>
        public IReadOnlyList<ClearanceInputConflict> CompletionConflicts { get; }
    }

    internal static class ClearanceValues
    {
        /// <summary>拒绝空名称。</summary>
        internal static string Name(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim()) throw new ArgumentException("名称不能为空或包含首尾空格。");
            return value;
        }
        /// <summary>检查浮点值可用于设备判定。</summary>
        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
