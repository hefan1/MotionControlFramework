using System;
using System.Collections.Generic;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>执行时采集的只读明细；监控页不通过当前设备值反推过去动作。</summary>
    public sealed class TaskStepDiagnostics
    {
        /// <summary>本次动作或判断的说明，完成后仍保留。</summary>
        public string Summary { get; }
        /// <summary>轴到位、输入或比较条件的采样记录。</summary>
        public IReadOnlyList<TaskObservedValue> Values { get; }
        /// <summary>条件分支本次的结论；未判断时为空。</summary>
        public bool? Decision { get; }
        /// <summary>实际选择的子节点编号；空字符串表示没有对应分支。</summary>
        public string SelectedBranchId { get; }
        /// <summary>执行器计算的工艺延时剩余毫秒，暂停时冻结。</summary>
        public double? RemainingDelayMilliseconds { get; }
        /// <summary>本组数据的 UTC 采样时间；不是硬件刷新周期保证。</summary>
        public DateTime CapturedAtUtc { get; }

        /// <summary>复制采样行，保证旧快照不会跟随下一次执行发生变化。</summary>
        internal TaskStepDiagnostics(string summary, IEnumerable<TaskObservedValue> values = null,
            bool? decision = null, string selectedBranchId = null, double? remainingDelayMilliseconds = null)
        {
            Summary = summary;
            Values = new List<TaskObservedValue>(values ?? new TaskObservedValue[0]).AsReadOnly();
            Decision = decision;
            SelectedBranchId = selectedBranchId;
            RemainingDelayMilliseconds = remainingDelayMilliseconds;
            CapturedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>一次明确的目标与实际值记录，未知与不满足分别表达。</summary>
    public sealed class TaskObservedValue
    {
        /// <summary>轴、输入、变量或比较条件名称。</summary>
        public string Name { get; }
        /// <summary>动作接受的目标值或本次比较的期望值。</summary>
        public string Expected { get; }
        /// <summary>本次采样实际值；为空表示未知。</summary>
        public string Actual { get; }
        /// <summary>是否满足；为空表示未能判断。</summary>
        public bool? Satisfied { get; }

        /// <summary>保存一次采样，不保留可变设备对象。</summary>
        internal TaskObservedValue(string name, string expected, string actual, bool? satisfied)
        {
            Name = name; Expected = expected; Actual = actual; Satisfied = satisfied;
        }
    }
}
