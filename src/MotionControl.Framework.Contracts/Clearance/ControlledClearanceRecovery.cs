using System;
using System.Collections.Generic;
using System.Linq;

namespace MotionControl.Framework.Contracts.Clearance
{
    /// <summary>设备声明的清料中断后现场核查条件；不代表硬件安全回路。</summary>
    public sealed class ControlledClearanceRecovery
    {
        public ControlledClearanceRecovery(IEnumerable<ClearanceInputCondition> inputs,
            IEnumerable<ClearanceAxisCondition> axes, IEnumerable<ClearanceRecoveryOutput> outputs)
        {
            var inputItems = (inputs ?? throw new ArgumentNullException(nameof(inputs))).ToArray();
            var axisItems = (axes ?? throw new ArgumentNullException(nameof(axes))).ToArray();
            var outputItems = (outputs ?? throw new ArgumentNullException(nameof(outputs))).ToArray();
            if (inputItems.Length == 0 || axisItems.Length == 0 || inputItems.Any(item => item == null)
                || axisItems.Any(item => item == null) || outputItems.Any(item => item == null)
                || inputItems.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputItems.Length
                || axisItems.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != axisItems.Length
                || outputItems.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputItems.Length)
                throw new ArgumentException("恢复条件必须包含明确且不重复的输入和轴，输出不得重复。");
            Inputs = Array.AsReadOnly(inputItems);
            Axes = Array.AsReadOnly(axisItems);
            Outputs = Array.AsReadOnly(outputItems);
        }
        public IReadOnlyList<ClearanceInputCondition> Inputs { get; }
        public IReadOnlyList<ClearanceAxisCondition> Axes { get; }
        public IReadOnlyList<ClearanceRecoveryOutput> Outputs { get; }
    }

    /// <summary>现场核查要求的明确输出电平。</summary>
    public sealed class ClearanceRecoveryOutput
    {
        public ClearanceRecoveryOutput(string name, bool value)
        { Name = ClearanceValues.Name(name); Value = value; }
        public string Name { get; }
        public bool Value { get; }
    }
}
