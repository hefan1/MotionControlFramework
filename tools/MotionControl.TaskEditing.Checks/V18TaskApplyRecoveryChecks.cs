using System;
using System.IO;
using System.Linq;
using System.Text;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Editing;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEditing.Checks
{
    internal static partial class Program
    {
        /// <summary>pending 路径被目录占用时，工作区和直接任务装载都必须阻断，不能按无恢复记录继续应用。</summary>
        private static void V18TaskPendingMarkerDirectory()
        {
            using (var test = new Workspace())
            {
                TaskWorkspaceDocument draft = test.Store.Load(Workspace.Id, false);
                var catalog = test.Store.LoadCatalog(draft);
                string before = test.ActiveBytes();
                string pending = Path.Combine(test.Configuration.GetMachineDirectory(Workspace.Id), "Drafts/TaskApplyPending.json");
                Directory.CreateDirectory(pending);
                Assert(test.Store.HasPendingApply(Workspace.Id), "恢复路径是目录时被误判为没有标记。");
                Throws(() => test.Store.Load(Workspace.Id, false));
                Throws(() => new TaskDefinitionStore(test.Configuration).Load(Workspace.Id, draft.Tasks[0].FileName, catalog,
                    draft.Tasks.Skip(1).Select(item => item.Definition)));
                Throws(() => new TaskMachineConfigurationStore(test.Configuration).Load(Workspace.Id,
                    draft.Tasks.Select(item => item.Definition), catalog));
                string warning;
                Throws(() => test.Store.Apply(draft, out warning));
                Throws(() => test.Store.RecoverPendingApply(Workspace.Id));
                Assert(before == test.ActiveBytes() && Directory.Exists(pending), "异常恢复路径导致运行文件变化或被自动删除。");
            }
        }

        /// <summary>基于真实 Apply 生成的清单模拟中断；原内容、应用内容、新增和删除文件都按原字节恢复。</summary>
        private static void V18TaskApplyRecoveryContents()
        {
            using (var test = new Workspace())
            {
                var baseline = test.Store.Load(Workspace.Id, false);
                baseline.Tasks.Add(new TaskWorkspaceTask { FileName = "PendingRemoved.json", Definition = NewPreparationCheckTask("PendingRemoved") });
                string warning;
                test.Store.Apply(baseline, out warning);
                string root = test.Configuration.GetMachineDirectory(Workspace.Id);
                string oldTask = Path.Combine(root, "Tasks", baseline.Tasks[0].FileName);
                byte[] originalWithBom = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(File.ReadAllText(oldTask) + " \r\n")).ToArray();
                File.WriteAllBytes(oldTask, originalWithBom);
                string before = test.ActiveBytes();

                string backup = CreateInterruptedTaskApply(test, "PendingRemoved");
                JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(backup, "BackupManifest.json")));
                Assert((int)manifest["schemaVersion"] == 2, "真实应用未生成带哈希的版本 2 清单。");
                Assert(((JArray)manifest["files"]).Any(item => (bool)item["existed"] && !(bool)item["appliedExists"]),
                    "被删除文件没有保留原存在性和原内容身份。");
                Assert(((JArray)manifest["files"]).Any(item => !(bool)item["existed"] && (bool)item["appliedExists"]),
                    "新增文件没有区分原不存在与空文件。");

                // 模拟中断时有一个目标尚未应用，其余仍为本次应用字节。
                string relative = (string)((JArray)manifest["files"]).First(item => (bool)item["existed"] && (bool)item["appliedExists"])["relativePath"];
                File.Copy(Path.Combine(backup, relative), Path.Combine(root, relative), true);
                test.Store.RecoverPendingApply(Workspace.Id);
                Assert(before == test.ActiveBytes() && !test.Store.HasPendingApply(Workspace.Id), "恢复没有完整还原原字节及文件存在性。");
                Assert(File.ReadAllBytes(oldTask).SequenceEqual(originalWithBom), "恢复丢失了原 BOM、空格或换行。");
                Assert(Directory.Exists(backup), "恢复成功不应删除历史备份证据。");
            }
        }

        /// <summary>备份缺失、备份篡改、目标另改和清单损坏均在任何恢复写入前拒绝，并保留完整证据。</summary>
        private static void V18TaskApplyRecoveryConflicts()
        {
            foreach (string scenario in new[] { "缺失备份", "备份改写", "目标改写", "目标删除", "新增目标另改", "清单不一致", "旧版记录", "越界路径" })
            {
                using (var test = new Workspace())
                {
                    string backup = CreateInterruptedTaskApply(test);
                    string root = test.Configuration.GetMachineDirectory(Workspace.Id);
                    string pendingPath = Path.Combine(root, "Drafts/TaskApplyPending.json");
                    JObject pending = JObject.Parse(File.ReadAllText(pendingPath));
                    JToken item = ((JArray)pending["files"]).Last(value => (bool)value["existed"]);
                    string relative = (string)item["relativePath"];
                    string expected;
                    switch (scenario)
                    {
                        case "缺失备份":
                            File.Delete(Path.Combine(backup, relative)); expected = "备份缺失或内容已改变"; break;
                        case "备份改写":
                            File.AppendAllText(Path.Combine(backup, relative), "外部篡改"); expected = "备份缺失或内容已改变"; break;
                        case "目标改写":
                            File.WriteAllText(Path.Combine(root, relative), "第三方后续维护内容"); expected = "其他操作改变"; break;
                        case "目标删除":
                            File.Delete(Path.Combine(root, relative)); expected = "其他操作改变"; break;
                        case "新增目标另改":
                            relative = (string)((JArray)pending["files"]).Single(value => !(bool)value["existed"])["relativePath"];
                            File.WriteAllText(Path.Combine(root, relative), "第三方新增文件内容"); expected = "其他操作改变"; break;
                        case "清单不一致":
                            item["appliedSha256"] = new string('0', 64);
                            File.WriteAllText(pendingPath, pending.ToString()); expected = "与备份清单不一致"; break;
                        case "旧版记录":
                            pending.Remove("schemaVersion");
                            File.WriteAllText(pendingPath, pending.ToString()); expected = "不能自动覆盖"; break;
                        default:
                            item["relativePath"] = "../Outside.json";
                            File.WriteAllText(pendingPath, pending.ToString());
                            File.WriteAllText(Path.Combine(backup, "BackupManifest.json"), pending.ToString());
                            expected = "超出允许目录"; break;
                    }
                    string beforeRecovery = test.ActiveBytes();
                    ExpectTaskRecoveryFailure(() => test.Store.RecoverPendingApply(Workspace.Id), expected);
                    Assert(beforeRecovery == test.ActiveBytes(), scenario + "导致了部分恢复，应该先检查所有文件再写回。");
                    Assert(test.Store.HasPendingApply(Workspace.Id) && Directory.Exists(backup), scenario + "错误清除了恢复标记或备份。");
                    Throws(() => test.Store.Load(Workspace.Id, false));
                }
            }
        }

        /// <summary>恢复遇到实际文件占用时说明部分完成并保留 pending；解除占用后可以继续恢复原版本。</summary>
        private static void V18TaskApplyPartialRecovery()
        {
            using (var test = new Workspace())
            {
                string before = test.ActiveBytes();
                string backup = CreateInterruptedTaskApply(test);
                string root = test.Configuration.GetMachineDirectory(Workspace.Id);
                JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(backup, "BackupManifest.json")));
                string blockedRelative = (string)((JArray)manifest["files"]).Last(item => (bool)item["existed"]
                    && ((string)item["relativePath"]).StartsWith("Tasks/", StringComparison.Ordinal)
                    && (string)item["originalSha256"] != (string)item["appliedSha256"])["relativePath"];
                using (var locked = new FileStream(Path.Combine(root, blockedRelative), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    ExpectTaskRecoveryFailure(() => test.Store.RecoverPendingApply(Workspace.Id), "部分完成");
                    Assert(test.Store.HasPendingApply(Workspace.Id), "部分恢复失败后清除了恢复标记。");
                }
                test.Store.RecoverPendingApply(Workspace.Id);
                Assert(before == test.ActiveBytes() && !test.Store.HasPendingApply(Workspace.Id), "解除文件占用后未能继续恢复完整原内容。");
            }
        }

        /// <summary>使用真实应用生成备份和清单，再恢复 pending，模拟全部文件写完但中断标记尚未清理的时点。</summary>
        private static string CreateInterruptedTaskApply(Workspace test, string removedTaskId = null)
        {
            string root = test.Configuration.GetMachineDirectory(Workspace.Id);
            string backups = Path.Combine(root, "Backups");
            string[] oldBackups = Directory.Exists(backups) ? Directory.GetDirectories(backups, "TaskApply-*") : new string[0];
            TaskWorkspaceDocument candidate = test.Store.Load(Workspace.Id, false);
            candidate.Machine.Name += " 中断检查";
            foreach (TaskWorkspaceTask item in candidate.Tasks.Take(2)) item.Definition.Name += " 本次应用";
            if (removedTaskId != null) candidate.Tasks.RemoveAll(item => item.Definition.TaskId == removedTaskId);
            candidate.Tasks.Add(new TaskWorkspaceTask { FileName = "PendingNew.json", Definition = NewPreparationCheckTask("PendingNew") });
            string warning;
            test.Store.Apply(candidate, out warning);
            string backup = Directory.GetDirectories(backups, "TaskApply-*").Single(path => !oldBackups.Contains(path));
            File.Copy(Path.Combine(backup, "BackupManifest.json"), Path.Combine(root, "Drafts/TaskApplyPending.json"));
            return backup;
        }

        /// <summary>拒绝原因必须包含具体预期场景；不能把任意异常都当作正确的恢复保护。</summary>
        private static void ExpectTaskRecoveryFailure(Action action, string expected)
        {
            try { action(); }
            catch (Exception error)
            {
                Assert(error.ToString().Contains(expected), "恢复拒绝原因不符：" + error);
                return;
            }
            throw new InvalidOperationException("恢复应拒绝却成功：" + expected);
        }
    }
}
