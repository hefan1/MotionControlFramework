using System;
using System.Collections.Generic;

namespace MotionControl.Framework.Contracts.Flow
{
    /// <summary>工位可选的只读诊断入口；实现方只返回已记录的内存快照，不读硬件、不推进步骤。</summary>
    public interface IStationOperationStatusProvider
    {
        /// <summary>返回本工位真实状态；返回 null 表示暂未提供，界面不能用整机状态代替。</summary>
        StationOperationStatus ReadOperationStatus();
    }

    /// <summary>独立工位的运行阶段；默认值表示来源尚未提供。</summary>
    public enum StationOperationState
    {
        /// <summary>未提供独立工位状态。</summary>
        NotProvided,
        /// <summary>工位空闲，尚未开始当前动作。</summary>
        Idle,
        /// <summary>工位正在执行动作。</summary>
        Running,
        /// <summary>工位正在等待明确条件。</summary>
        Waiting,
        /// <summary>工位已暂停并保留进度。</summary>
        Paused,
        /// <summary>工位故障已锁存，等待恢复条件和复位。</summary>
        WaitingReset,
        /// <summary>工位本次动作已完成。</summary>
        Completed,
        /// <summary>工位已经停止。</summary>
        Stopped,
        /// <summary>工位未参加本次自动运行。</summary>
        Disabled
    }

    /// <summary>等待的稳定类别，供列表任务和 C# 工位共用；不能根据显示文字推断。</summary>
    public enum StationWaitKind
    {
        /// <summary>未提供等待类别。</summary>
        NotProvided,
        /// <summary>等待逻辑输入。</summary>
        Input,
        /// <summary>等待读回输出命令状态，不代表机构到位。</summary>
        Output,
        /// <summary>等待轴的明确反馈。</summary>
        Axis,
        /// <summary>等待变量比较等条件。</summary>
        Condition,
        /// <summary>等待共享资源占用。</summary>
        Resource,
        /// <summary>等待进入或退出互锁。</summary>
        Interlock,
        /// <summary>等待工艺延时。</summary>
        Delay,
        /// <summary>等待并行子步骤汇合。</summary>
        Parallel,
        /// <summary>等待请求或完成握手。</summary>
        Handshake,
        /// <summary>等待有效且稳定的采样。</summary>
        Sampling
    }

    /// <summary>等待项明确引用的对象类别；标识原样保存，名称中的标点不参与解析。</summary>
    public enum StationObjectKind
    {
        /// <summary>来源未提供可定位对象。</summary>
        NotProvided,
        /// <summary>逻辑数字输入。</summary>
        Input,
        /// <summary>逻辑数字输出。</summary>
        Output,
        /// <summary>逻辑轴。</summary>
        Axis,
        /// <summary>设备变量。</summary>
        Variable,
        /// <summary>共享资源的完整稳定键。</summary>
        Resource,
        /// <summary>流程定义中的步骤。</summary>
        Step
    }

    /// <summary>本次记录的值质量；未知、不稳定和读取失败均不能当作有效结果。</summary>
    public enum StationValueQuality
    {
        /// <summary>没有提供该值的采样质量。</summary>
        NotProvided,
        /// <summary>本次读取成功且值可用于该判断。</summary>
        Good,
        /// <summary>已尝试采样，但没有已知值。</summary>
        Unknown,
        /// <summary>多次采样不一致，不能用于放行。</summary>
        Unstable,
        /// <summary>读取失败，原始错误应在说明中保留。</summary>
        ReadFailed
    }

    /// <summary>一项不可变的等待证据；只用于解释状态，不包含控制命令。</summary>
    public sealed class StationWaitItem
    {
        /// <summary>稳定等待类别。</summary>
        public StationWaitKind Kind { get; }
        /// <summary>稳定原因代码；未提供时为空，不从中文说明生成。</summary>
        public string ReasonCode { get; }
        /// <summary>明确引用的对象类别。</summary>
        public StationObjectKind ObjectKind { get; }
        /// <summary>原始稳定对象标识；可以包含分号和等号。</summary>
        public string ObjectId { get; }
        /// <summary>显示名称；只用于显示，不能替代稳定标识。</summary>
        public string ObjectName { get; }
        /// <summary>本次判断使用的期望值或条件说明；为空表示未提供。</summary>
        public string Expected { get; }
        /// <summary>本次实际采样值；为空且质量为 Unknown 表示未知。</summary>
        public string Actual { get; }
        /// <summary>期望值和实际值的工程单位；为空表示来源未定义，不推测为毫米。</summary>
        public string Unit { get; }
        /// <summary>实际值质量，不因显示语言变化。</summary>
        public StationValueQuality Quality { get; }
        /// <summary>是否满足本项；为空表示未能判断。</summary>
        public bool? Satisfied { get; }
        /// <summary>执行方实际采用的时限，单位毫秒；为空表示未提供或没有活动时限。</summary>
        public int? TimeoutMilliseconds { get; }
        /// <summary>原始说明或读取错误；不用于分类和定位。</summary>
        public string Description { get; }

        /// <summary>复制一项等待证据；调用方负责在同一次采样中提供匹配的值和质量。</summary>
        public StationWaitItem(StationWaitKind kind, string reasonCode, StationObjectKind objectKind,
            string objectId, string objectName, string expected, string actual, StationValueQuality quality,
            bool? satisfied = null, int? timeoutMilliseconds = null, string description = null, string unit = null)
        {
            Kind = kind;
            ReasonCode = reasonCode;
            ObjectKind = objectKind;
            ObjectId = objectId;
            ObjectName = objectName;
            Expected = expected;
            Actual = actual;
            Quality = quality;
            Unit = unit;
            Satisfied = satisfied;
            TimeoutMilliseconds = timeoutMilliseconds;
            Description = description;
        }
    }

    /// <summary>C# 工位主动提供的只读状态；与列表任务采用相同的等待项和质量语义。</summary>
    public sealed class StationOperationStatus
    {
        /// <summary>设备定义的稳定工位标识；未提供时为空，不能以同名工位替换。</summary>
        public string StationId { get; }
        /// <summary>设备定义的工位显示名。</summary>
        public string StationName { get; }
        /// <summary>当前真实步骤的稳定标识；未提供时为空。</summary>
        public string StepId { get; }
        /// <summary>当前真实步骤名称。</summary>
        public string StepName { get; }
        /// <summary>工位主动报告的独立状态。</summary>
        public StationOperationState State { get; }
        /// <summary>所有等待项；顺序由工位定义，不由界面任意挑选。</summary>
        public IReadOnlyList<StationWaitItem> WaitItems { get; }
        /// <summary>true 表示完整提供等待集合；false 时仍可保留已知项，但不能声称集合完整。</summary>
        public bool HasWaitInformation { get; }
        /// <summary>当前动作、等待或异常的原始说明。</summary>
        public string Description { get; }
        /// <summary>设备流程提供的下一步处理建议；仅为说明，不授予操作许可。</summary>
        public string NextAction { get; }
        /// <summary>设备流程提供的本步完成判据；未提供时为空。</summary>
        public string CompletionCondition { get; }
        /// <summary>快照记录时间，不代表硬件采样周期保证。</summary>
        public DateTime CapturedAtUtc { get; }

        /// <summary>创建不可变工位快照；等待项为 null 表示未提供，部分诊断缺失时显式传 hasWaitInformation=false。</summary>
        public StationOperationStatus(string stationId, string stationName, string stepId, string stepName,
            StationOperationState state, IEnumerable<StationWaitItem> waitItems = null, string description = null,
            string nextAction = null, string completionCondition = null, DateTime? capturedAtUtc = null,
            bool? hasWaitInformation = null)
        {
            StationId = stationId;
            StationName = stationName;
            StepId = stepId;
            StepName = stepName;
            State = state;
            HasWaitInformation = hasWaitInformation ?? (waitItems != null);
            WaitItems = new List<StationWaitItem>(waitItems ?? new StationWaitItem[0]).AsReadOnly();
            Description = description;
            NextAction = nextAction;
            CompletionCondition = completionCondition;
            CapturedAtUtc = capturedAtUtc ?? DateTime.UtcNow;
        }
    }
}
