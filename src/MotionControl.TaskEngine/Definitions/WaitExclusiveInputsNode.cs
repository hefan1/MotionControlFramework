using System.Collections.Generic;

namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>等待两个锁存输入恰好一个有效，并把同轮采样写入两个布尔变量。</summary>
    public sealed class WaitExclusiveInputsNode : TaskNode
    {
        /// <summary>互斥输入等待步骤的固定类型。</summary>
        public override string Kind => "WaitExclusiveInputs";
        /// <summary>第一路逻辑输入名称。</summary>
        public string FirstInputName { get; set; }
        /// <summary>第二路逻辑输入名称，不能与第一路相同。</summary>
        public string SecondInputName { get; set; }
        /// <summary>接收第一路采样的布尔设备变量名称。</summary>
        public string FirstVariableName { get; set; }
        /// <summary>接收第二路采样的布尔设备变量名称，不能与第一项相同。</summary>
        public string SecondVariableName { get; set; }
        /// <summary>为空时任意一路有效即可；有值时第一路必须为该值且第二路相反。</summary>
        public bool? RequiredFirstValue { get; set; }
        /// <summary>等待超时关联的设备报警代码，至少一条。</summary>
        public List<string> TimeoutAlarmCodes { get; set; } = new List<string>();
    }
}
