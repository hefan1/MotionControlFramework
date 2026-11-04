using System;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Host.Wpf;

/// <summary>验证只读底栏摘要在长报警和未知数据下保留核心状态。</summary>
internal static class Program
{
    /// <summary>运行中英文边界场景，不连接设备或创建界面。</summary>
    private static int Main()
    {
        var empty = new MachineAlarmRecord[0];
        var alarm = new MachineAlarmRecord(new MachineAlarmDefinition("STOP", new string('X', 400), MachineAlarmKind.StopMachine),
            "Station", "", false, DateTime.UtcNow, DateTime.UtcNow);
        var notice = new MachineAlarmRecord(new MachineAlarmDefinition("INFO", "Notice", MachineAlarmKind.NonStop),
            "Other", "", false, DateTime.UtcNow, DateTime.UtcNow);
        Check(!FooterExceptionText.NeedsSettlementNotice(MachineControlOwner.StationFlows, false, false), "运行不误报");
        Check(!FooterExceptionText.NeedsSettlementNotice(MachineControlOwner.TaskEngine, true, false), "任务入口不误报");
        Check(FooterExceptionText.NeedsSettlementNotice(MachineControlOwner.StationFlows, true, false), "就绪但收尾未确认");
        foreach (bool english in new[] { false, true })
        {
            Check(FooterExceptionText.Format(new[] { alarm }, true, true, false, false, null, "long resource", english, false, false, true).Contains(english ? "Flow settling unconfirmed" : "流程收尾待确认"), "流程收尾待确认与报警资源并存");
            string unknownSettlement = FooterExceptionText.Format(new[] { alarm }, true, true, true, false, null, null, english, false, true, false, true);
            Check(unknownSettlement.Contains(english ? "Flow settlement unknown" : "流程收尾未知") && unknownSettlement.Contains(english ? "Alarms 1/Reset 1" : "报警 1/待复位 1") && unknownSettlement.Contains(english ? "Resources unknown" : "资源状态未知") && unknownSettlement.Contains(english ? "IO read failed" : "IO 读取失败"), "所有读取异常仍保留报警");
            string recoverySummary = FooterExceptionText.Format(new[] { alarm }, true, false, false, false, new string('R', 400), null, english, recoveryPending: true);
            Check(recoverySummary.StartsWith(english ? "Alarms 1/Reset 1" : "报警 1/待复位 1") && recoverySummary.Contains(english ? "Recovery unconfirmed" : "恢复待确认") && recoverySummary.Contains(english ? "Controller disconnected" : "控制器未连接") && !recoverySummary.Contains(new string('R', 400)), "恢复阻断保留报警优先级和断连且不展开长文");
            Check(!recoverySummary.Contains(english ? "Start blocked" : "启动受阻"), "恢复与一般启动阻断明确区分");
            string recoveryDetail = FooterExceptionText.Format(empty, true, true, false, false, "recovery evidence", null, english, fullDetail: true, recoveryPending: true);
            Check(recoveryDetail.Contains("recovery evidence") && recoveryDetail.Contains(english ? "Recovery unconfirmed" : "恢复待确认"), "恢复详情保留原始证据");
            string unknownResources = FooterExceptionText.Format(new[] { alarm }, true, true, false, false, null, null, english, false, true);
            Check(unknownResources.Contains(english ? "Alarms 1/Reset 1" : "报警 1/待复位 1") && unknownResources.Contains(english ? "Resources unknown" : "资源状态未知"), "资源读取失败不掩盖锁存报警");
            string longResource = "Resources " + new string('Y', 400) + "\nMore detail";
            string compact = FooterExceptionText.Format(new[] { notice, alarm }, true, true, false, false, "Long block", longResource, english);
            Check(compact.Contains(english ? "Alarms 1/Reset 1" : "报警 1/待复位 1"), "锁存计数");
            Check(compact.Contains(english ? "Inspect resources" : "资源待确认") && compact.Length < 70 && !compact.Contains("\n"), "长文不隐藏资源状态");
            Check(compact.Contains(english ? "Start blocked" : "启动受阻"), "同时保留启动阻断");
            string detail = FooterExceptionText.Format(new[] { alarm }, true, true, false, false, "Long block", longResource, english, true);
            Check(detail.Contains(longResource) && detail.Contains(new string('X', 400)), "详情保留完整证据");
            Check(FooterExceptionText.Format(null, true, true, false, false, null, null, english).Contains(english ? "Alarms unknown" : "报警未知"), "空引用报警未知");
            Check(FooterExceptionText.Format(empty, false, false, true, false, null, null, english).Contains(english ? "Runtime not read" : "运行状态未读取"), "未读运行时");
            Check(FooterExceptionText.Format(empty, true, true, true, false, null, null, english).Contains(english ? "IO read failed" : "IO 读取失败"), "读取失败");
            Check(FooterExceptionText.Format(new[] { notice }, true, true, false, false, null, null, english) == (english ? "No pending alarms" : "无待处理报警"), "恢复非停机报警清除");
        }
        Console.WriteLine("Footer summary: 31/31 passed");
        return 0;
    }
    /// <summary>预期不成立时输出具体场景。</summary>
    private static void Check(bool passed, string scenario)
    {
        if (!passed) throw new InvalidOperationException(scenario);
    }
}
