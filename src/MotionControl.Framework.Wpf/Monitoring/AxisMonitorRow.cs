using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Framework.Wpf.Monitoring
{
    /// <summary>保存单个逻辑轴在只读监控表格中的显示数据。</summary>
    internal sealed class AxisMonitorRow
    {
        /// <summary>当前连接卡提供的逻辑轴对象；断开连接时为空。</summary>
        public IAxis Axis { get; set; }

        /// <summary>设备流程和配置使用的逻辑轴名称。</summary>
        public string LogicalAxisName { get; set; }

        /// <summary>映射到的控制卡标识和物理轴号。</summary>
        public string PhysicalAxisText { get; set; }

        /// <summary>逻辑轴位置和速度所用的工程单位。</summary>
        public string EngineeringUnit { get; set; }

        /// <summary>控制器最近一次成功返回的轴状态，用于切换语言时重绘。</summary>
        public AxisStatus LastStatus { get; set; }

        /// <summary>反馈位置和控制器指令位置的显示文字，均带工程单位。</summary>
        public string PositionText { get; set; }

        /// <summary>当前速度的显示文字，包含每秒工程单位。</summary>
        public string VelocityText { get; set; }

        /// <summary>当前伺服使能状态的显示文字。</summary>
        public string ServoStateText { get; set; }

        /// <summary>当前轴运动或停止状态的显示文字。</summary>
        public string MotionStateText { get; set; }

        /// <summary>当前轴是否已完成回零的显示文字。</summary>
        public string HomedStateText { get; set; }

        /// <summary>当前控制器原点输入的状态；未知时提示维护人员检查回零输入配置。</summary>
        public string OriginInputStateText { get; set; }

        /// <summary>正向硬限位和软件限位的状态文字。</summary>
        public string PositiveLimitText { get; set; }

        /// <summary>负向硬限位和软件限位的状态文字。</summary>
        public string NegativeLimitText { get; set; }

        /// <summary>当前报警状态或报警原因的显示文字。</summary>
        public string AlarmStateText { get; set; }

        /// <summary>轴状态读取结果的显示文字。</summary>
        public string ReadStateText { get; set; }

        /// <summary>最近一次状态读取的故障原因，显示为单元格提示。</summary>
        public string ReadErrorMessage { get; set; }

        /// <summary>标记当前轴状态是否读取失败。</summary>
        public bool HasReadError { get; set; }

        /// <summary>已读到轴状态，但数值无效或运动标志与速度不一致。</summary>
        public bool HasUncertainFeedback { get; set; }

        /// <summary>标记当前状态行是否对应已连接的控制卡。</summary>
        public bool IsConnected { get; set; }

        /// <summary>标记最新成功读取到的轴状态是否包含报警。</summary>
        public bool HasAlarm { get; set; }
    }
}
