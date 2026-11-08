using System;
using System.Linq;
using MotionControl.TaskEngine.Editing;
namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>只解释页面持有的草稿和文件快照，不读取磁盘或推断会话来源。</summary>
    internal static class TaskSourceSummary
    {
        /// <summary>工作区保存时间与任务修订只代表对应源，当前会话元数据缺失保持未知。</summary>
        internal static string Build(TaskWorkspaceDocument draft, TaskWorkspaceDocument files, bool dirty, bool changed, bool english)
        {
            string saved = draft == null || draft.SavedAt == default(DateTime) ? (english ? "not provided" : "未提供") : draft.SavedAt.ToString("yyyy-MM-dd HH:mm:ss");
            string versions = files == null ? (english ? "not provided" : "未提供")
                : string.Join(", ", files.Tasks.Take(10).Select(item => item.Definition.TaskId + " R" + item.Definition.Revision));
            if (files != null)
            {
                int total = files.Tasks.Count;
                versions = (english ? $"Showing {Math.Min(10, total)} / {total} loaded file tasks: " : $"列出 {Math.Min(10, total)} / 共 {total} 个已载入文件任务：") + versions;
                if (total == 0) versions += english ? "empty file snapshot" : "文件快照为空";
                if (total > 10) versions += english ? $"; entries 11–{total} are not displayed. Full definitions remain in the loaded file snapshot; this summary is incomplete."
                    : $"；第11–{total}项未显示，完整定义仍保留在已载入文件快照中；此摘要并非完整列表。";
            }
            return english
                ? "Draft: " + (draft?.MachineId ?? "not provided") + (draft == null ? " · draft / editing state unknown" : dirty ? " · unsaved workspace changes" : " · no pending workspace edits") + "; full workspace saved at: " + saved
                    + "\nLoaded runtime-file snapshot (not the executing session): " + versions + " · snapshot agreement with current files is unverified."
                    + (draft == null ? "\nDraft base not provided." : changed ? "\nThe draft base needs review; source changed or could not be verified." : "\nThe latest check reported no draft-base mismatch.")
                    + "\nCurrent session: version / session ID / load time not provided. Saving or updating files does not load a session."
                : "草稿：" + (draft?.MachineId ?? "未提供") + (draft == null ? " · 草稿 / 编辑状态未知" : dirty ? " · 工作区有未保存修改" : " · 工作区无待保存编辑") + "；完整工作区保存时间：" + saved
                    + "\n已载入运行文件快照（不是正在执行的会话）：" + versions + " · 文件快照与当前文件的一致性未确认。"
                    + (draft == null ? "\n草稿基准未提供。" : changed ? "\n草稿基准待核查：来源已变化或无法确认。" : "\n最近检查未报告草稿基准不一致。")
                    + "\n当前会话：版本 / 会话标识 / 加载时间未提供。保存或更新文件不等于载入会话。";
        }
    }
}
