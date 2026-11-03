using System;

namespace MotionControl.Framework.Contracts.Alarms
{
    /// <summary>将启动许可提交与停机报警锁存按顺序协调。</summary>
    public interface IMachineAlarmStartGate
    {
        /// <summary>无停机报警及待处理通知时提交纯内存许可；回调不得调用设备或等待。</summary>
        void ExecuteWhenNoStopAlarm(Action commit);
    }
}
