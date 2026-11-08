using System;
using System.Collections.Generic;
using MotionControl.Framework.Contracts.Flow;

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
        /// <summary>稳定等待类别；默认值表示执行方尚未提供。</summary>
        public StationWaitKind WaitKind { get; }
        /// <summary>稳定原因代码；只用于诊断，不从 Summary 或 Detail 文本解析。</summary>
        public string ReasonCode { get; }
        /// <summary>全部等待项；一个并行步骤或互锁可以同时涉及多个对象。</summary>
        public IReadOnlyList<StationWaitItem> WaitItems { get; }
        /// <summary>是否明确提供等待集合；空集合与未提供不同。</summary>
        public bool HasWaitInformation { get; }
        /// <summary>实际生效的父级或本步活动时限，单位毫秒；为空表示未提供。</summary>
        public int? TimeoutMilliseconds { get; }
        /// <summary>活动时限所属节点编号；不能用当前节点定义时限代替父级时限。</summary>
        public string TimeoutNodeId { get; }
        /// <summary>是否实际发生过时限到期，不能仅根据说明中的文字判断。</summary>
        public bool IsTimeout { get; }
        /// <summary>不含执行编号前缀的原始错误原因；完整错误仍保留在节点 Detail 中。</summary>
        public string FailureMessage { get; }
        /// <summary>本组数据的 UTC 采样时间；不是硬件刷新周期保证。</summary>
        public DateTime CapturedAtUtc { get; }

        /// <summary>复制采样行，保证旧快照不会跟随下一次执行发生变化。</summary>
        internal TaskStepDiagnostics(string summary, IEnumerable<TaskObservedValue> values = null,
            bool? decision = null, string selectedBranchId = null, double? remainingDelayMilliseconds = null,
            StationWaitKind waitKind = StationWaitKind.NotProvided, string reasonCode = null,
            IEnumerable<StationWaitItem> waitItems = null, int? timeoutMilliseconds = null,
            string timeoutNodeId = null, bool isTimeout = false, string failureMessage = null,
            DateTime? capturedAtUtc = null)
        {
            Summary = summary;
            Values = new List<TaskObservedValue>(values ?? new TaskObservedValue[0]).AsReadOnly();
            Decision = decision;
            SelectedBranchId = selectedBranchId;
            RemainingDelayMilliseconds = remainingDelayMilliseconds;
            WaitKind = waitKind;
            ReasonCode = reasonCode;
            HasWaitInformation = waitItems != null;
            WaitItems = new List<StationWaitItem>(waitItems ?? new StationWaitItem[0]).AsReadOnly();
            TimeoutMilliseconds = timeoutMilliseconds;
            TimeoutNodeId = timeoutNodeId;
            IsTimeout = isTimeout;
            FailureMessage = failureMessage;
            CapturedAtUtc = capturedAtUtc ?? DateTime.UtcNow;
        }
        /// <summary>记录实际生效时限，同时保留同一次采样的值和时间。</summary>
        internal TaskStepDiagnostics WithTiming(int? timeoutMilliseconds, string timeoutNodeId)
        {
            return new TaskStepDiagnostics(Summary, Values, Decision, SelectedBranchId, RemainingDelayMilliseconds,
                WaitKind, ReasonCode, HasWaitInformation ? WaitItems : null, timeoutMilliseconds, timeoutNodeId,
                IsTimeout, FailureMessage, CapturedAtUtc);
        }

        /// <summary>保存结构化失败原因；原等待对象和最后采样不因故障丢失。</summary>
        internal TaskStepDiagnostics WithFailure(TaskExecutionException error)
        {
            return new TaskStepDiagnostics(Summary, Values, Decision, SelectedBranchId, RemainingDelayMilliseconds,
                WaitKind, error.ReasonCode,
                HasWaitInformation ? WaitItems : null, error.TimeoutMilliseconds ?? TimeoutMilliseconds,
                error.TimeoutMilliseconds.HasValue ? error.NodeId : TimeoutNodeId,
                error.FailureKind == TaskExecutionFailureKind.Timeout, error.Cause, CapturedAtUtc);
        }
    }

    /// <summary>一次明确的目标与实际值记录，未知与不满足分别表达。</summary>
    public sealed class TaskObservedValue
    {
        /// <summary>轴、输入、变量或比较条件名称。</summary>
        public string Name { get; }
        /// <summary>动作接受的目标值或本次比较的期望值。</summary>
        public string Expected { get; }
        /// <summary>本次采样实际值；为空时结合 Quality 区分尚未提供与已采样但未知。</summary>
        public string Actual { get; }
        /// <summary>是否满足；为空表示未能判断。</summary>
        public bool? Satisfied { get; }

        /// <summary>被采样的对象类别；未提供时不从名称推断。</summary>
        public StationObjectKind ObjectKind { get; }
        /// <summary>稳定对象标识，按原文保存。</summary>
        public string ObjectId { get; }
        /// <summary>数值的工程单位；为空表示目录没有定义。</summary>
        public string Unit { get; }
        /// <summary>采样质量；未提供与已采样但未知分别表达。</summary>
        public StationValueQuality Quality { get; }

        /// <summary>保存一次采样，不保留可变设备对象。</summary>
        internal TaskObservedValue(string name, string expected, string actual, bool? satisfied,
            StationObjectKind objectKind = StationObjectKind.NotProvided, string objectId = null,
            StationValueQuality quality = StationValueQuality.NotProvided, string unit = null)
        {
            Name = name; Expected = expected; Actual = actual; Satisfied = satisfied;
            ObjectKind = objectKind; ObjectId = objectId; Quality = quality; Unit = unit;
        }
    }
}
