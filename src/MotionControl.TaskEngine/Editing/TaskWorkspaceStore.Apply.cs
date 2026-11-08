using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Validation;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Editing
{
    /// <summary>应用任务文件的明确备份、写入与恢复顺序。</summary>
    public sealed partial class TaskWorkspaceStore
    {
        /// <summary>调用方先确认运行会话已结束；锁内校验定义、准备引用、能力和来源后，按冻结字节应用。</summary>
        public TaskWorkspaceDocument Apply(TaskWorkspaceDocument draft, out string warning)
        {
            warning = null;
            RequireDraftShape(draft?.MachineId, draft);
            using (AcquireEditLock(draft.MachineId))
            {
                RequireNoPendingApply(draft.MachineId);
                RequireCurrentDraft(draft);
                if (ComputeSignature(draft.MachineId) != draft.BaseSignature)
                    throw new InvalidOperationException("运行任务或关联配置已经变化，请重新载入后检查；当前草稿仍保留。");
                var errors = Check(draft).Where(issue => issue.Severity == TaskIssueSeverity.Error).ToList();
                if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
                TaskWorkspaceDocument active = Load(draft.MachineId, false);
                TaskWorkspaceDocument candidate = ReadDraft(JObject.FromObject(draft, TaskDefinitionJson.CreateSerializer()).ToString());
                PrepareRevisions(active, candidate);
                Dictionary<string, byte[]> appliedContents = CreateAppliedContents(active, candidate);
                TaskApplyJournal journal = CreateBackup(candidate.MachineId, appliedContents);
                if (ComputeSignature(draft.MachineId) != draft.BaseSignature)
                    throw new InvalidOperationException("备份期间来源文件发生变化，未写入运行文件，请重新载入。");
                _store.SaveJson(draft.MachineId, PendingPath, JObject.FromObject(journal, TaskDefinitionJson.CreateSerializer()));
                string root = _store.GetMachineDirectory(candidate.MachineId);
                try
                {
                    foreach (TaskApplyFile file in journal.Files)
                    {
                        string destination = Path.Combine(root, file.RelativePath);
                        if (CurrentTaskFileHash(destination) != file.OriginalSha256)
                            throw new InvalidOperationException("任务文件在应用前被其他操作改变：" + file.RelativePath);
                        if (file.OriginalSha256 == file.AppliedSha256 && file.Existed == file.AppliedExists) continue;
                        byte[] bytes = appliedContents[file.RelativePath];
                        if (bytes == null) File.Delete(destination);
                        else WriteTaskFileBytes(destination, bytes, file.OriginalSha256);
                    }
                    foreach (TaskApplyFile file in journal.Files)
                        if (CurrentTaskFileHash(Path.Combine(root, file.RelativePath)) != file.AppliedSha256)
                            throw new IOException("任务应用后的内容核对失败：" + file.RelativePath);
                    File.Delete(Path.Combine(root, PendingPath));
                }
                catch (Exception applyError)
                {
                    try { RestoreBackup(draft.MachineId, journal); }
                    catch (Exception restoreError)
                    {
                        throw new AggregateException("应用失败且自动恢复未完成，现场文件、备份和恢复标记均保留，禁止加载运行任务。", applyError, restoreError);
                    }
                    throw new InvalidOperationException("任务应用失败，运行文件已恢复，草稿仍保留。", applyError);
                }
                candidate.BaseSignature = ComputeSignature(candidate.MachineId);
                try { File.Delete(Path.Combine(root, DraftPath)); }
                catch (Exception error) { warning = "运行文件已应用，但旧草稿未能清理：" + error.Message; }
                candidate.DraftSignature = CurrentDraftSignature(candidate.MachineId);
                return candidate;
            }
        }

        /// <summary>意外退出或恢复路径异常时均阻止正常装载，不能把权限错误和目录标记当作不存在。</summary>
        public void RequireNoPendingApply(string machineId)
        {
            if (HasPendingApply(machineId))
                throw new InvalidOperationException("上次任务应用未完成，请在任务配置页恢复上次应用，再载入任务。");
        }

        /// <summary>运行会话结束后由工程入口调用；恢复前检查整个清单，拒绝覆盖第三方修改。</summary>
        public void RecoverPendingApply(string machineId)
        {
            using (AcquireEditLock(machineId))
            {
                string path = Path.Combine(_store.GetMachineDirectory(machineId), PendingPath);
                byte[] bytes = ReadTaskFileBytes(path);
                if (bytes == null) return;
                TaskApplyJournal journal = ReadApplyJournal(Encoding.UTF8.GetString(bytes));
                RestoreBackup(machineId, journal);
            }
        }

        /// <summary>只有内容变化才递增修订号；界面不让人员猜下一个编号。</summary>
        private static void PrepareRevisions(TaskWorkspaceDocument active, TaskWorkspaceDocument candidate)
        {
            foreach (TaskWorkspaceTask item in candidate.Tasks)
            {
                var old = active.Tasks.FirstOrDefault(value => string.Equals(value.FileName, item.FileName, StringComparison.OrdinalIgnoreCase));
                if (old == null) { item.Definition.Revision = 1; continue; }
                item.Definition.Revision = old.Definition.Revision;
                if (TaskDefinitionJson.Serialize(item.Definition) != TaskDefinitionJson.Serialize(old.Definition))
                    item.Definition.Revision = checked(old.Definition.Revision + 1);
            }
            candidate.Machine.Revision = active.Machine.Revision;
            if (!JToken.DeepEquals(JObject.FromObject(candidate.Machine, TaskDefinitionJson.CreateSerializer()), JObject.FromObject(active.Machine, TaskDefinitionJson.CreateSerializer())))
                candidate.Machine.Revision = checked(active.Machine.Revision + 1);
        }

        /// <summary>在写文件前生成全部最终字节；未改变的文件保留原字节，删除项用 null 明确表示。</summary>
        private Dictionary<string, byte[]> CreateAppliedContents(TaskWorkspaceDocument active, TaskWorkspaceDocument candidate)
        {
            string root = _store.GetMachineDirectory(candidate.MachineId);
            var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (TaskWorkspaceTask item in candidate.Tasks)
            {
                TaskWorkspaceTask old = active.Tasks.FirstOrDefault(value => string.Equals(value.FileName, item.FileName, StringComparison.OrdinalIgnoreCase));
                if (old != null && !string.Equals(old.Definition.TaskId, item.Definition.TaskId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("不能用另一任务 ID 覆盖已有文件，请使用新文件名：" + item.FileName);
                string json = TaskDefinitionJson.Serialize(item.Definition);
                TaskDefinitionJson.Deserialize(json);
                string relative = "Tasks/" + item.FileName;
                byte[] bytes = new UTF8Encoding(false).GetBytes(json + Environment.NewLine);
                if (old != null && json == TaskDefinitionJson.Serialize(old.Definition))
                    bytes = ReadTaskFileBytes(Path.Combine(root, relative)) ?? bytes;
                contents.Add(relative, bytes);
            }
            foreach (TaskWorkspaceTask removed in active.Tasks)
            {
                string relative = "Tasks/" + removed.FileName;
                if (!contents.ContainsKey(relative)) contents.Add(relative, null);
            }
            string machineJson = JObject.FromObject(candidate.Machine, TaskDefinitionJson.CreateSerializer()).ToString();
            TaskMachineConfigurationStore.Deserialize(machineJson);
            byte[] machineBytes = new UTF8Encoding(false).GetBytes(machineJson + Environment.NewLine);
            if (JToken.DeepEquals(JObject.Parse(machineJson), JObject.FromObject(active.Machine, TaskDefinitionJson.CreateSerializer())))
                machineBytes = ReadTaskFileBytes(Path.Combine(root, "Parameters/TaskMachine.json")) ?? machineBytes;
            contents.Add("Parameters/TaskMachine.json", machineBytes);
            return contents;
        }
    }
}
