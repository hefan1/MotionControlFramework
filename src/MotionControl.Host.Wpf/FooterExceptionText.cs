using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;

namespace MotionControl.Host.Wpf
{
    /// <summary>只格式化已经读取的报警快照，不访问控制器或触发设备操作。</summary>
    internal static class FooterExceptionText
    {
        /// <summary>该属性只在 C# 就绪态表达上次流程收尾结果；运行期间的 false 不作为异常。</summary>
        internal static bool NeedsSettlementNotice(MachineControlOwner owner, bool ready, bool confirmed)
        {
            return owner == MachineControlOwner.StationFlows && ready && !confirmed;
        }

        /// <summary>把锁存报警放在摘要开头，并保留阻断和未知状态，避免把未读取解释为正常。</summary>
        internal static string Format(IEnumerable<MachineAlarmRecord> records, bool runtimeKnown,
            bool connected, bool ioFailed, bool checking, string block, string taskBlock, bool english, bool fullDetail = false, bool resourcesUnknown = false, bool settlementPending = false, bool settlementUnknown = false, bool recoveryPending = false)
        {
            var details = new List<string>();
            var alarms = (records ?? Enumerable.Empty<MachineAlarmRecord>())
                .Where(alarm => alarm.ConditionActive || alarm.RequiresReset)
                .OrderByDescending(alarm => alarm.RequiresReset).ToArray();
            if (alarms.Length > 0)
            {
                var first = alarms[0];
                int resetCount = alarms.Count(alarm => alarm.RequiresReset && !alarm.ConditionActive);
                details.Add((english ? "Alarms " : "报警 ") + alarms.Length
                    + (resetCount > 0 ? (english ? "/Reset " : "/待复位 ") + resetCount : "")
                    + (fullDetail ? "：" + first.SourceName + " / " + first.Definition.Message : ""));
            }
            if (settlementUnknown) details.Add(english ? "Flow settlement unknown" : "流程收尾未知");
            if (settlementPending) details.Add(english ? "Flow settling unconfirmed" : "流程收尾待确认");
            if (records == null) details.Add(english ? "Alarms unknown" : "报警未知");
            if (resourcesUnknown) details.Add(english ? "Resources unknown" : "资源状态未知");
            if (!string.IsNullOrWhiteSpace(taskBlock))
                details.Add(fullDetail ? taskBlock : (english ? "Inspect resources" : "资源待确认"));
            if (!string.IsNullOrWhiteSpace(block))
                details.Add(fullDetail ? (recoveryPending ? (english ? "Recovery unconfirmed: " : "恢复待确认：") : (english ? "Start blocked: " : "启动受阻：")) + block
                    : (recoveryPending ? (english ? "Recovery unconfirmed" : "恢复待确认") : (english ? "Start blocked" : "启动受阻")));
            if (!runtimeKnown) details.Add(english ? "Runtime not read" : "运行状态未读取");
            if (!connected) details.Add(english ? "Controller disconnected" : "控制器未连接");
            if (ioFailed) details.Add(english ? "IO read failed" : "IO 读取失败");
            if (checking) details.Add(english ? "Checking start conditions" : "启动条件检查中");
            return details.Count == 0 ? (english ? "No pending alarms" : "无待处理报警") : string.Join("；", details);
        }
    }
}
