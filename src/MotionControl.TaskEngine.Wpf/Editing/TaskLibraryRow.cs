using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Editing;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>任务库的只读显示行，版本取自应用文件，草稿继续使用原模型。</summary>
    public sealed class TaskLibraryRow
    {
        /// <summary>当前草稿任务，不复制第二份可写定义。</summary>
        public TaskWorkspaceTask Source { get; private set; }
        /// <summary>沿用原列绑定的定义。</summary>
        public TaskDefinition Definition { get { return Source.Definition; } }
        /// <summary>稳定文件名。</summary>
        public string FileName { get { return Source.FileName; } }
        /// <summary>已应用文件的版本，不能当作运行会话版本。</summary>
        public string AppliedVersion { get; private set; }
        /// <summary>当前草稿相对文件的变化和检查状态。</summary>
        public string DraftState { get; private set; }
        /// <summary>组合明确的可读显示，不改变源任务。</summary>
        internal TaskLibraryRow(TaskWorkspaceTask source, string appliedVersion, string draftState)
        { Source = source; AppliedVersion = appliedVersion; DraftState = draftState; }
    }
}
