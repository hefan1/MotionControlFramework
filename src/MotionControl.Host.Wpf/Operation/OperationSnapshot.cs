using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Host.Wpf.Operation
{
    /// <summary>总览与报警只读展示的数据，不包含任何运动或输出入口。</summary>
    public sealed class OperationSnapshot
    {
        /// <summary>是否为明确标记的离线页面示例。</summary>
        public bool IsPreview { get; set; }
        /// <summary>是否有运行时来源；没有来源不能显示正常或零报警。</summary>
        public bool HasRuntime { get; set; }
        /// <summary>整机软件状态，未知时为空。</summary>
        public MachineRunState? State { get; set; }
        /// <summary>当前设备控制入口，用于避免展示其他入口的工位步骤。</summary>
        public MachineControlOwner? ControlOwner { get; set; }
        /// <summary>软件状态采样时间，不代表轴或 IO 的硬件采集时间。</summary>
        public DateTime SampledAt { get; set; }
        /// <summary>按设备配置顺序排列的工位。</summary>
        public List<StationOverviewData> Stations { get; set; } = new List<StationOverviewData>();
        /// <summary>当前报警快照；停机报警条件解除后仍可保留。</summary>
        public List<MachineAlarmRecord> Alarms { get; set; } = new List<MachineAlarmRecord>();
        /// <summary>最近整机异常的完整诊断，不能当作每条报警的根因。</summary>
        public string LastError { get; set; }
        /// <summary>单工位时的完整循环数；多工位不将不同工位计数相加。</summary>
        public long? CompletedCount { get; set; }
        /// <summary>单工位时最近一个完整循环的周期秒数。</summary>
        public double? CycleSeconds { get; set; }
        /// <summary>只读采样当前 Runtime，保留当前步骤原文，不从文字推断等待条件或独立工位状态。</summary>
        public static OperationSnapshot Read(MachineRuntime runtime)
        {
            var result = new OperationSnapshot { SampledAt = DateTime.Now, HasRuntime = runtime != null };
            if (runtime == null) return result;
            result.State = runtime.State;
            result.ControlOwner = runtime.ControlOwner;
            result.Alarms = runtime.AlarmService.Alarms.ToList();
            result.LastError = runtime.LastError?.ToString();
            // 任务列表控制设备时，传统 C# 工位对象仍然存在，但不是实际运行步骤。
            if (result.ControlOwner != MachineControlOwner.StationFlows) return result;
            foreach (var flow in runtime.StationFlows)
            {
                var statistics = flow as IStationCycleStatistics;
                var inputWait = flow as IStationInputWaitState;
                result.Stations.Add(new StationOverviewData
                {
                    Name = flow.StationName,
                    CurrentStep = flow.CurrentStepName,
                    WaitingInputName = inputWait?.WaitingInputName,
                    CompletedCycles = statistics?.CompletedCycles,
                    LastCycleSeconds = statistics?.LastCycleDuration?.TotalSeconds
                });
            }

            if (result.Stations.Count == 1)
            {
                result.CompletedCount = result.Stations[0].CompletedCycles;
                result.CycleSeconds = result.Stations[0].LastCycleSeconds;
            }

            return result;
        }
    }

    /// <summary>一个工位卡片的数据；不把整机状态冒充独立工位状态。</summary>
    public sealed class StationOverviewData
    {
        /// <summary>设备定义的工位名称。</summary>
        public string Name { get; set; }
        /// <summary>工位提供的当前动作或保留步骤原文。</summary>
        public string CurrentStep { get; set; }
        /// <summary>流程明确报告的当前等待逻辑 DI；未等待输入时为空。</summary>
        public string WaitingInputName { get; set; }
        /// <summary>工位提供的完整循环数；为空表示该工位没有此统计能力。</summary>
        public long? CompletedCycles { get; set; }
        /// <summary>工位最近完整循环时长；为空表示尚无可测量的完整循环。</summary>
        public double? LastCycleSeconds { get; set; }
        /// <summary>仅离线示例使用的结构化等待说明；传统工位没有该数据。</summary>
        public string PreviewReason { get; set; }
    }

    /// <summary>新页只读导航的明确目标，不携带轴动作或 IO 写入命令。</summary>
    public sealed class OperatorNavigationEventArgs : EventArgs
    {
        /// <summary>已有页面的中文资源键。</summary>
        public string Page { get; private set; }
        /// <summary>可以按名称定位的工位，其他页面只打开页面本身。</summary>
        public string StationName { get; private set; }
        /// <summary>可以在 IO 监控中定位的逻辑 IO 名称；为空时不切换具体通道。</summary>
        public string IoPointName { get; private set; }
        /// <summary>创建只读导航请求。</summary>
        public OperatorNavigationEventArgs(string page, string stationName = null, string ioPointName = null)
        { Page = page; StationName = stationName; IoPointName = ioPointName; }
    }
}
