using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.ConfigurationChange.Checks
{
    /// <summary>在隔离的临时配置目录验证提交、查询和故障后的独立结果。</summary>
    internal static class Program
    {
        /// <summary>每次使用全新的隔离目录，结束后清理本轮测试数据。</summary>
        private static void Main()
        {
            string temp = Path.GetFullPath(Path.GetTempPath());
            string root = Path.Combine(temp, "configuration-change-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new MachineConfigurationStore(root);
                SaveAndAuditFailure(store);
                ConcurrentAndPartial(store);
                LimitsAndInvalidInput(store);
                Console.WriteLine("三组配置变更持久记录独立检查通过。");
            }
            finally
            {
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(root)), temp.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("临时目录校验失败。");
                Directory.Delete(root, true);
            }
        }

        /// <summary>构造独立测试回执，记录操作前后真实可得的时刻和来源，不补填版本。</summary>
        private static ConfigurationChangeRecord Receipt(string machine)
        {
            return new ConfigurationChangeRecord { MachineId = machine, RecordId = Guid.NewGuid(), TimeUtc = DateTimeOffset.UtcNow,
                ObjectType = "文件", ObjectId = "Test.json", Source = "隔离测试", Stage = ConfigurationChangeStage.SaveFiles,
                Outcome = ConfigurationChangeOutcome.Succeeded };
        }

        /// <summary>失败时打印可定位的断言说明。</summary>
        private static void Require(bool result, string message)
        {
            if (!result) throw new InvalidOperationException(message);
        }

        /// <summary>使用真实 SaveJson 返回结果；模拟记录目录不可写，核查业务文件仍可读。</summary>
        private static void SaveAndAuditFailure(MachineConfigurationStore store)
        {
            const string machine = "Saved";
            var journal = new ConfigurationChangeJournal(store, machine);
            store.SaveJson(machine, "Test.json", new { Value = 71 });
            string runtime = Path.Combine(store.GetMachineDirectory(machine), "Runtime");
            File.WriteAllText(runtime, "文件占用原本需要的目录路径");
            Require(journal.TryAppend(Receipt(machine)) != null, "目录失败应单独报告。");
            bool readFailed = false;
            try { IReadOnlyList<string> ignored; journal.ReadRecent(10, out ignored); }
            catch (IOException) { readFailed = true; }
            Require(readFailed, "目录路径被文件占用时不得显示为无记录。");
            Require(File.ReadAllText(Path.Combine(store.GetMachineDirectory(machine), "Test.json")).Contains("71"),
                "记录失败影响了成功保存的业务数据。");
            File.Delete(runtime);
            IReadOnlyList<string> diagnostics;
            Require(journal.ReadRecent(10, out diagnostics).Count == 0, "恢复目录后不能伪造历史或自动重试。");
            var saved = Receipt(machine);
            Require(journal.TryAppend(saved) == null, "有效记录未写入。");
            var reload = new ConfigurationChangeJournal(store, machine).ReadRecent(1, out diagnostics).Single();
            Require(reload.RecordId == saved.RecordId && reload.TimeUtc == saved.TimeUtc && reload.Actor == null &&
                reload.AfterVersion == null && reload.Stage == ConfigurationChangeStage.SaveFiles, "重开后来源或阶段信息变化。");
        }

        /// <summary>并发追加、重复标识、独占锁、损坏提交与截断临时文件均不破坏既有记录。</summary>
        private static void ConcurrentAndPartial(MachineConfigurationStore store)
        {
            const string machine = "Concurrent";
            var journal = new ConfigurationChangeJournal(store, machine);
            var rows = Enumerable.Range(0, 12).Select(i => Receipt(machine)).ToList();
            Parallel.ForEach(rows, row => Require(new ConfigurationChangeJournal(store, machine).TryAppend(row) == null, "并发记录丢失。"));
            IReadOnlyList<string> errors;
            Require(journal.ReadRecent(100, out errors).Count == rows.Count && errors.Count == 0, "并发记录不全。");
            Require(journal.TryAppend(rows[0]) != null, "重复标识不能覆盖。");
            string directory = Path.Combine(store.GetMachineDirectory(machine), "Runtime", "ConfigurationChanges");
            using (new FileStream(Path.Combine(directory, "writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Require(journal.TryAppend(Receipt(machine)) != null, "外部写锁必须返回独立审计失败。");
            string broken = Directory.EnumerateFiles(directory, "*.json").First();
            File.WriteAllText(broken, "{");
            File.WriteAllText(Path.Combine(directory, "crash.pending"), "{");
            Require(journal.ReadRecent(100, out errors).Count == rows.Count - 1 && errors.Count == 2, "截断应保留其余完整记录及诊断。");
            string duplicate = Directory.EnumerateFiles(directory, "*.json").First(path => path != broken);
            string text = File.ReadAllText(duplicate);
            File.WriteAllText(duplicate, text.Substring(0, text.Length - 1) + ",\"outcome\":\"Rejected\"}");
            Require(journal.ReadRecent(100, out errors).Count == rows.Count - 2 && errors.Any(item => item.Contains("字段重复")),
                "重复结果字段不能覆盖原结果。");
            var failed = Receipt(machine);
            failed.Stage = ConfigurationChangeStage.Check;
            failed.Outcome = ConfigurationChangeOutcome.Failed;
            failed.Detail = "示例检查：轴引用失效";
            Require(journal.TryAppend(failed) == null, "未提交临时文件不应破坏后续记录。");
            Require(journal.ReadRecent(100, out errors).Any(row => row.RecordId == failed.RecordId &&
                row.Detail == failed.Detail && row.Outcome == ConfigurationChangeOutcome.Failed), "业务失败原因丢失。");
        }

        /// <summary>验证查询上限、错误元数据和目录满时保留此前数据。</summary>
        private static void LimitsAndInvalidInput(MachineConfigurationStore store)
        {
            const string machine = "Limits";
            var journal = new ConfigurationChangeJournal(store, machine);
            var invalid = Receipt(machine);
            invalid.TimeUtc = default(DateTimeOffset);
            Require(journal.TryAppend(invalid) != null && journal.TryAppend(Receipt("WrongMachine")) != null, "未知时间或跨设备记录不能落盘。");
            bool rejected = false;
            try { IReadOnlyList<string> errors; journal.ReadRecent(101, out errors); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "查询超过预算未拒绝。");
            var first = Receipt(machine);
            Require(journal.TryAppend(first) == null, "首条写入失败。");
            string directory = Path.Combine(store.GetMachineDirectory(machine), "Runtime", "ConfigurationChanges");
            for (int i = 1; i < ConfigurationChangeJournal.MaximumRecords; i++)
                File.WriteAllText(Path.Combine(directory, i + ".pending"), "");
            Require(journal.TryAppend(Receipt(machine)) != null, "满目录不能移除旧记录或继续写。");
            IReadOnlyList<string> notices;
            Require(journal.ReadRecent(1, out notices).Single().RecordId == first.RecordId && notices.Count > 0, "目录满时已有记录仍可读。");
        }
    }
}
