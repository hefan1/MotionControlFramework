using System.Collections.Generic;

namespace MotionControl.TaskEngine.MachineDevice.Preparation
{
    /// <summary>独立的设备准备清单；任务编辑器保存生产清单时不会覆盖本文件。</summary>
    public sealed class TaskStationPreparationConfiguration
    {
        /// <summary>当前支持版本 1。</summary>
        public int SchemaVersion { get; set; }
        /// <summary>本设备参与准备的工位，未列出的工位不伪造已准备状态。</summary>
        public List<TaskStationPreparationDefinition> Stations { get; set; } = new List<TaskStationPreparationDefinition>();
    }

    /// <summary>一个工位的准备任务及必须通过的现场验收条件；全部名称引用已有配置。</summary>
    public sealed class TaskStationPreparationDefinition
    {
        /// <summary>准备工位的稳定标识。</summary>
        public string Id { get; set; }
        /// <summary>向操作员显示的工位名称。</summary>
        public string Name { get; set; }
        /// <summary>已有任务库中的独立准备任务编号。</summary>
        public string TaskId { get; set; }
        /// <summary>必须先完成并通过现场复核的其他准备工位标识。</summary>
        public List<string> Dependencies { get; set; } = new List<string>();
        /// <summary>需要建立坐标、停稳并到达待机位置的轴。</summary>
        public List<TaskPreparationAxis> Axes { get; set; } = new List<TaskPreparationAxis>();
        /// <summary>准备结束时应达到指定逻辑状态的输入。</summary>
        public List<TaskPreparationSignal> Inputs { get; set; } = new List<TaskPreparationSignal>();
        /// <summary>开始和结束时应达到指定逻辑状态的输出，避免带动作准备。</summary>
        public List<TaskPreparationSignal> Outputs { get; set; } = new List<TaskPreparationSignal>();
        /// <summary>必须明确为 false 的布尔占用变量；只读取，不清空在制品。</summary>
        public List<string> EmptyVariables { get; set; } = new List<string>();
    }

    /// <summary>待机位置采用已有逻辑轴的工程单位，不引入物理通道或厂商参数。</summary>
    public sealed class TaskPreparationAxis
    {
        /// <summary>已有逻辑轴名称。</summary>
        public string Name { get; set; }
        /// <summary>准备完成时的位置；必须明确填写。</summary>
        public double? Position { get; set; }
        /// <summary>验收允许的位置误差，必须大于零且明确填写。</summary>
        public double? Tolerance { get; set; }
    }

    /// <summary>一个既有逻辑信号的期望状态。</summary>
    public sealed class TaskPreparationSignal
    {
        /// <summary>已有逻辑输入或输出名称，方向由所在列表决定。</summary>
        public string Name { get; set; }
        /// <summary>必须明确填写的期望逻辑状态，未知输入不能通过。</summary>
        public bool? Expected { get; set; }
    }
}
