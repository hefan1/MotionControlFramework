using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;

/// <summary>验证瞬时报警的历史落盘和重复发生时的分离。</summary>
internal static class Program
{
    /// <summary>使用独立临时配置目录运行检查。</summary>
    private static void Main()
    {
        string path = Path.Combine(Path.GetTempPath(), "MotionControlAlarmHistory-" + Guid.NewGuid().ToString("N"));
        var store = new MachineConfigurationStore(path);
        var service = new MachineAlarmService();
        var stop = new MachineAlarmDefinition("TEST_STOP", "测试停机", MachineAlarmKind.StopMachine, "检查", "复位");
        var notice = new MachineAlarmDefinition("TEST_NOTICE", "测试提醒", MachineAlarmKind.NonStop, "检查", "恢复");
        using (var history = new AlarmHistoryJournal(service, store, "Machine01"))
        {
            service.ReportCondition(stop, "站 A", true, "输入异常");
            service.ReportCondition(stop, "站 A", false, "输入恢复");
            service.ResetStopAlarms();
            service.ReportCondition(notice, "站 B", true, "到位异常");
            service.ReportCondition(notice, "站 B", false, "恢复");
        }
        using (var history = new AlarmHistoryJournal(new MachineAlarmService(), store, "Machine01"))
        {
            var entries = history.ReadRecent(1, 100);
            if (entries.Count != 5 || entries.Count(x => x.Action == "发生") != 2 || !entries.Any(x => x.Action == "报警复位")
                || !entries.Any(x => x.Action == "条件恢复并自动清除"))
                throw new Exception("报警历史发生、恢复或复位记录不完整。");
            Console.WriteLine("报警历史检查通过：快速发生、恢复、复位和重新读取。");
        }
        CheckBoundedRecentAndReadFailure(store);
        CheckCloseReportsWriteFailure(store);
        CheckPendingStopNotification();
        CheckAlarmStartGate();
    }

    /// <summary>报警锁存与纯内存启动提交有确定顺序，已经锁存的报警不能放行。</summary>
    private static void CheckAlarmStartGate()
    {
        var service = new MachineAlarmService();
        var alarm = new MachineAlarmDefinition("START_GATE", "测试停机", MachineAlarmKind.StopMachine, "检查", "复位");
        using (var committed = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            Task start = Task.Run(() => service.ExecuteWhenNoStopAlarm(() =>
            {
                committed.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("启动门禁测试超时。");
            }));
            try
            {
                if (!committed.Wait(TimeSpan.FromSeconds(5))) throw new Exception("启动提交未进入门禁。");
                Task report = Task.Run(() => service.ReportCondition(alarm, "工位", true, "测试"));
                if (report.Wait(TimeSpan.FromMilliseconds(100))) throw new Exception("报警锁存穿过了启动提交。");
                release.Set();
                start.GetAwaiter().GetResult();
                report.GetAwaiter().GetResult();
                bool rejected = false;
                try { service.ExecuteWhenNoStopAlarm(() => { throw new Exception("不应提交启动。"); }); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new Exception("已锁存停机报警时错误允许启动。");
            }
            finally { release.Set(); }
        }
        Console.WriteLine("报警检查通过：停机报警和启动许可原子排序。");
    }

    /// <summary>报警发布尚未交接时，即使条件已经恢复也不能先复位。</summary>
    private static void CheckPendingStopNotification()
    {
        var service = new MachineAlarmService();
        var alarm = new MachineAlarmDefinition("NOTICE_GATE", "测试停机", MachineAlarmKind.StopMachine, "检查", "复位");
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            service.StopAlarmRaised += (sender, args) =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("测试停机通知等待超时。");
            };
            Task publish = Task.Run(() => service.ReportCondition(alarm, "工位", true, "测试"));
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("停机通知没有进入订阅者。");
                Task drained = service.WaitForStopNotificationDispatchAsync();
                if (drained.IsCompleted) throw new Exception("停机通知尚未交接时错误显示排空完成。");
                service.ReportCondition(alarm, "工位", false, "条件已恢复");
                bool rejected = false;
                try { service.ResetStopAlarms(); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected || !service.HasLatchedStopAlarm)
                    throw new Exception("停机通知尚未交接时错误允许复位。");
            }
            finally { release.Set(); }
            publish.GetAwaiter().GetResult();
            service.WaitForStopNotificationDispatchAsync().GetAwaiter().GetResult();
            service.ResetStopAlarms();
            if (service.HasLatchedStopAlarm) throw new Exception("通知交接完成后无法复位。");
        }
        var failedService = new MachineAlarmService();
        failedService.StopAlarmRaised += (sender, args) => { throw new InvalidOperationException("订阅者失败"); };
        bool notificationFailed = false;
        try { failedService.ReportCondition(alarm, "工位", true, "测试"); }
        catch (InvalidOperationException) { notificationFailed = true; }
        if (!notificationFailed) throw new Exception("停机通知异常未反馈给上报方。");
        failedService.ReportCondition(alarm, "工位", false, "条件已恢复");
        bool failureRejected = false;
        try { failedService.ResetStopAlarms(); }
        catch (InvalidOperationException) { failureRejected = true; }
        if (!failureRejected || !failedService.HasLatchedStopAlarm)
            throw new Exception("停机通知失败后错误允许复位。");
        bool drainRejected = false;
        try { failedService.WaitForStopNotificationDispatchAsync().GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { drainRejected = true; }
        if (!drainRejected) throw new Exception("停机通知失败后错误显示排空成功。");
        Console.WriteLine("报警检查通过：待发布停机通知阻止提前复位。");
    }

    /// <summary>写入目录不可用时明确报告未写事件，且重复收尾不伪装成功。</summary>
    private static void CheckCloseReportsWriteFailure(MachineConfigurationStore store)
    {
        string machine = store.GetMachineDirectory("WriteFailure");
        Directory.CreateDirectory(machine);
        // 用普通文件占用目录名称，稳定地制造写入失败，无需修改系统权限。
        File.WriteAllText(Path.Combine(machine, "Runtime"), "not a directory");
        var service = new MachineAlarmService();
        var history = new AlarmHistoryJournal(service, store, "WriteFailure");
        var alarm = new MachineAlarmDefinition("FAIL_TEST", "测试报警", MachineAlarmKind.NonStop, "检查", "恢复");
        service.ReportCondition(alarm, "测试工位", true, "待保存记录");
        AlarmHistoryCloseResult result = history.Close(TimeSpan.FromSeconds(5));
        if (result.IsComplete || !result.WriterStopped || result.PendingCount != 1
            || string.IsNullOrWhiteSpace(result.Error) || history.GetPendingSnapshot().Count != 1)
            throw new Exception("写入失败后未明确报告未保存记录。");
        service.ReportCondition(alarm, "测试工位", false, "关闭后变化");
        if (history.GetPendingSnapshot().Count != 1 || history.Close(TimeSpan.FromSeconds(1)).IsComplete)
            throw new Exception("关闭后错误接收入队或将写入失败显示为成功。");
        File.Delete(Path.Combine(machine, "Runtime"));
        result = history.RetryPending(TimeSpan.FromSeconds(5));
        if (!result.IsComplete || history.GetPendingSnapshot().Count != 0 || history.ReadRecent(1, 10).Count != 1)
            throw new Exception("写入条件恢复后没有成功保存待写记录。");
        Console.WriteLine("报警历史检查通过：写入失败、待写数量与关闭结果。");
    }

    /// <summary>乱序大文件只返回最新指定条数；损坏或无法打开的日志不能当作无记录。</summary>
    private static void CheckBoundedRecentAndReadFailure(MachineConfigurationStore store)
    {
        string directory = Path.Combine(store.GetMachineDirectory("Limited"), "Runtime", "AlarmHistory");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");
        DateTime start = DateTime.UtcNow.Date;
        using (var writer = new StreamWriter(file, false))
        {
            // 倒序写入验证查询不是简单取文件尾，也不假设时钟永不回拨。
            for (int i = 1999; i >= 0; i--)
                writer.WriteLine("{\"TimeUtc\":\"" + start.AddSeconds(i).ToString("o") + "\",\"Message\":\"" + i + "\"}");
        }
        using (var history = new AlarmHistoryJournal(new MachineAlarmService(), store, "Limited"))
        {
            var entries = history.ReadRecent(1, 1000);
            if (entries.Count != 1000 || entries[0].Message != "1999" || entries[999].Message != "1000")
                throw new Exception("大文件最新记录裁剪或排序错误。");
            File.AppendAllText(file, "{无效记录" + Environment.NewLine);
            bool rejected = false;
            try { history.ReadRecent(1, 1000); }
            catch (Exception error) when (error is Newtonsoft.Json.JsonException || error is InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("损坏日志被静默当成可用记录。");
        }
        string badDirectory = Path.Combine(store.GetMachineDirectory("Unreadable"), "Runtime", "AlarmHistory");
        Directory.CreateDirectory(Path.Combine(badDirectory, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"));
        using (var history = new AlarmHistoryJournal(new MachineAlarmService(), store, "Unreadable"))
        {
            bool rejected = false;
            try { history.ReadRecent(1, 1000); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { rejected = true; }
            if (!rejected) throw new Exception("无法读取的路径被当作无记录。");
        }
        Console.WriteLine("报警历史检查通过：有界查询、乱序、损坏与不可读路径。");
    }
}
