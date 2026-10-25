using System.Windows;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>集中放置不常用的任务备份、恢复和草稿重载入口。</summary>
    public partial class TaskBackupActionsWindow : Window
    {
        /// <summary>当前人员选中的维护操作。</summary>
        internal TaskBackupAction SelectedAction { get; private set; }

        /// <summary>按主页面语言显示维护操作对话框。</summary>
        public TaskBackupActionsWindow(string language)
        {
            InitializeComponent();
            TaskEditingUi.SetLanguage(this, language);
        }

        /// <summary>选择导出设备任务配置备份。</summary>
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = TaskBackupAction.Export;
            DialogResult = true;
        }

        /// <summary>选择从备份文件恢复为当前设备草稿。</summary>
        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = TaskBackupAction.Restore;
            DialogResult = true;
        }

        /// <summary>选择读取当前设备目录中的最新草稿。</summary>
        private void LoadLatest_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = TaskBackupAction.LoadLatestDraft;
            DialogResult = true;
        }

        /// <summary>选择放弃草稿并重新读取当前运行任务文件。</summary>
        private void ReloadRuntime_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = TaskBackupAction.ReloadRuntimeFiles;
            DialogResult = true;
        }
    }

    /// <summary>备份与恢复对话框中可执行的四种明确操作。</summary>
    internal enum TaskBackupAction
    {
        /// <summary>没有选择维护操作。</summary>
        None,
        /// <summary>导出完整设备任务工作区。</summary>
        Export,
        /// <summary>从外部备份恢复到草稿。</summary>
        Restore,
        /// <summary>读取其他窗口或程序刚保存的草稿。</summary>
        LoadLatestDraft,
        /// <summary>放弃草稿并回到当前任务运行文件。</summary>
        ReloadRuntimeFiles
    }
}
