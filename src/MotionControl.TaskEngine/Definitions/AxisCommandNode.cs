using MotionControl.Framework.Contracts.Motion;
namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>回零或停止的定义；回零参数继续由原有轴配置负责。</summary>
    public sealed class AxisCommandNode : TaskNode
    {
        /// <summary>轴命令的固定类型。</summary>
        public override string Kind => "AxisCommand";
        /// <summary>现有逻辑轴名称。</summary>
        public string AxisName { get; set; }
        /// <summary>回零或停止。</summary>
        public TaskAxisCommand Command { get; set; }
        /// <summary>停止命令使用的停止方式；回零时必须留空。</summary>
        public AxisStopMode? StopMode { get; set; }
    }

    /// <summary>本阶段定义的轴命令。</summary>
    public enum TaskAxisCommand
    {
        /// <summary>按现有轴配置回零。</summary>
        Home,
        /// <summary>停止轴并由未来执行器确认停稳。</summary>
        Stop
    }
}
