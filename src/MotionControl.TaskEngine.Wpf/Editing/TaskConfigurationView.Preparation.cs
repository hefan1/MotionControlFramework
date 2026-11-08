using System;
using System.Linq;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    public partial class TaskConfigurationView
    {
        /// <summary>本页最近观察到的准备文件字节签名；不存在时为 null，不代表准备已完成。</summary>
        private string _observedPreparationSignature;
        /// <summary>准备文件读取失败时使旧检查结论失效，恢复读取后仍需重新检查。</summary>
        private bool _preparationReadFailed;

        /// <summary>只检查准备文件是否外部变化；发生变化才重算整体来源，避免每次计时器都重读整套任务。</summary>
        private void RefreshPreparationSource()
        {
            if (_workspace == null) return;
            try
            {
                string current = _store.ComputePreparationSignature(_machineId);
                if (!_preparationReadFailed && string.Equals(current, _observedPreparationSignature, StringComparison.Ordinal)) return;

                _sourceChanged = _store.ComputeSignature(_machineId) != _workspace.BaseSignature;
                _observedPreparationSignature = current;
                _preparationReadFailed = false;
                _lastCheckIssues = null;
                ShowIssues(new TaskValidationIssue[0]);
                RefreshLibraryRows();
                SetStatusText(T("准备配置已变化，旧检查结论失效，请重新检查或装载"));
            }
            catch (Exception error)
            {
                _sourceChanged = true;
                if (!_preparationReadFailed)
                {
                    _preparationReadFailed = true;
                    _lastCheckIssues = null;
                    ShowIssues(new TaskValidationIssue[0]);
                    RefreshLibraryRows();
                    ShowError(new InvalidOperationException(T("准备配置读取失败，旧检查结论失效"), error));
                }
            }
        }

        /// <summary>把引用类别翻译为说明文字；任务匹配始终使用稳定标识，不依赖翻译文本。</summary>
        private string ReferenceText(TaskWorkspaceTaskReference reference)
        {
            string kind;
            switch (reference.Kind)
            {
                case TaskWorkspaceReferenceKind.ProductionStation: kind = T("工位"); break;
                case TaskWorkspaceReferenceKind.Initialization: kind = T("初始化任务"); break;
                case TaskWorkspaceReferenceKind.Preparation: kind = T("准备配置"); break;
                default: kind = T("子任务引用"); break;
            }
            return kind + "：" + reference.OwnerName + " [" + reference.OwnerId + "]\n" + reference.FieldPath;
        }

        /// <summary>有引用时直接禁用删除并说明位置；无法读取引用时也保留任务，点击处理仍再次核对。</summary>
        private void RefreshTaskDeletePermission()
        {
            if (RemoveTaskButton == null) return;
            var task = SelectedLibraryTask;
            RemoveTaskButton.IsEnabled = false;
            RemoveTaskButton.ToolTip = T("未选择或引用失效");
            if (_workspace == null || task == null) return;
            try
            {
                var references = _store.FindTaskReferences(_workspace, task.Definition.TaskId);
                RemoveTaskButton.IsEnabled = references.Count == 0;
                RemoveTaskButton.ToolTip = references.Count == 0 ? null
                    : T("任务仍被引用，不能删除") + "\n" + string.Join("\n", references.Select(ReferenceText));
            }
            catch (Exception error)
            {
                RemoveTaskButton.ToolTip = T("无法确认任务引用，不能删除") + "\n" + error.Message;
            }
        }
    }
}
