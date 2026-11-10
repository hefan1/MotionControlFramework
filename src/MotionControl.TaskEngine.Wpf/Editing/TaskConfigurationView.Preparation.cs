using System;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    public partial class TaskConfigurationView
    {
        /// <summary>本页最近观察到的准备文件字节签名；不存在时为 null，不代表准备已完成。</summary>
        private string _observedPreparationSignature;
        /// <summary>最近完整读取的任务来源签名，用于识别相邻观察之间的变化，不代替草稿基准。</summary>
        private string _observedTaskSourceSignature;
        /// <summary>人工读取、工作区替换或离页后递增，拒绝更早启动的后台观察结果。</summary>
        private long _sourceObservationRevision;
        /// <summary>只有页面已载入时才轮询完整来源，隐藏页面仍保留草稿。</summary>
        private bool _sourceObservationEnabled;
        /// <summary>同一页面只允许一项完整来源读取，避免慢磁盘造成后台读取堆积。</summary>
        private bool _sourceObservationRunning;
        /// <summary>完整来源观察之间至少间隔两秒；准备单文件仍即时核对。</summary>
        private DateTime _nextSourceObservationUtc;
        /// <summary>来源检查读取失败时使旧检查结论失效，恢复读取后仍需重新检查。</summary>
        private bool _sourceReadFailed;
        /// <summary>保留读取失败的实际原因，重复阻断来源处理时仍可查看完整诊断。</summary>
        private Exception _sourceReadError;

        /// <summary>即时核对准备单文件；变化先撤销检查并要求核查，完整来源由后台或明确操作确认。</summary>
        private void RefreshPreparationSource()
        {
            if (_workspace == null) return;
            try
            {
                string current = _store.ComputePreparationSignature(_machineId);
                if (string.Equals(current, _observedPreparationSignature, StringComparison.Ordinal)) return;
                _observedPreparationSignature = current;
                _sourceChanged = true;
                InvalidateSourceObservation();
                InvalidateSourceChecks();
                SetStatusText(T("准备配置已变化，旧检查结论失效，请重新检查或装载"));
            }
            catch (Exception error) { RecordSourceReadFailure(error); }
        }

        /// <summary>在后台串行读取完整来源；返回时只更新原工作区和原观察代次，绝不重建正在编辑的表单。</summary>
        private async void ObserveTaskSources()
        {
            if (!_sourceObservationEnabled || _workspace == null || _sourceObservationRunning
                || DateTime.UtcNow < _nextSourceObservationUtc) return;
            var workspace = _workspace;
            long revision = _sourceObservationRevision;
            string signature = null;
            Exception readError = null;
            _sourceObservationRunning = true;
            try { signature = await Task.Run(() => _store.ComputeSignature(_machineId)); }
            catch (Exception error) { readError = error; }
            finally { _sourceObservationRunning = false; }

            if (!_sourceObservationEnabled || !ReferenceEquals(workspace, _workspace)
                || revision != _sourceObservationRevision) return;
            if (readError != null) RecordSourceReadFailure(readError);
            else
            {
                bool recovered = _sourceReadFailed;
                if (RememberSourceSignature(signature))
                    SetStatusText(T(recovered ? "任务来源读取已恢复，旧检查结论失效，请重新检查或装载"
                        : "任务来源已变化，旧检查结论失效，请重新检查或装载"));
            }
            // 失败也节流；持续失读不能形成无间隔的重试循环。
            _nextSourceObservationUtc = DateTime.UtcNow.AddSeconds(2);
            RefreshPermission(null, EventArgs.Empty);
        }

        /// <summary>撤销在途观察的发布资格，并允许下次可见刷新尽快核对最新文件。</summary>
        private void InvalidateSourceObservation()
        {
            _sourceObservationRevision++;
            _nextSourceObservationUtc = DateTime.MinValue;
        }

        /// <summary>记录一次完整来源读取；只有字节变化或失读恢复才清检查，普通轮询不撤销有效结果。</summary>
        private bool RememberSourceSignature(string signature)
        {
            bool checksInvalidated = _sourceReadFailed || (_observedTaskSourceSignature != null
                && !string.Equals(signature, _observedTaskSourceSignature, StringComparison.Ordinal));
            _observedTaskSourceSignature = signature;
            _sourceChanged = _workspace != null && signature != _workspace.BaseSignature;
            _sourceReadFailed = false;
            _sourceReadError = null;
            _nextSourceObservationUtc = DateTime.UtcNow.AddSeconds(2);
            if (checksInvalidated) InvalidateSourceChecks();
            return checksInvalidated;
        }

        /// <summary>同时撤销父页与子编辑器的检查显示，保留草稿对象、控件、选择及尚未完成的输入。</summary>
        private void InvalidateSourceChecks()
        {
            _lastCheckIssues = null;
            ShowIssues(new TaskValidationIssue[0]);
            RefreshLibraryRows();
            _editor?.InvalidateSourceCheck(_sourceReadFailed);
        }

        /// <summary>读取失败按未知来源处理并保留原始诊断，重复失败不反复抢走操作员滚动位置。</summary>
        private void RecordSourceReadFailure(Exception error)
        {
            bool firstFailure = !_sourceReadFailed;
            _sourceReadFailed = true;
            _sourceReadError = error;
            _sourceChanged = true;
            var diagnostic = new InvalidOperationException(T("任务来源读取失败，旧检查结论失效"), error);
            if (firstFailure)
            {
                InvalidateSourceObservation();
                InvalidateSourceChecks();
                ShowError(diagnostic);
            }
            else ErrorDiagnosticsText.Text = diagnostic.ToString();
        }

        /// <summary>读取故障时不把未知来源当成已确认的冲突去同步或丢弃草稿。</summary>
        private bool CanResolveTaskSources()
        {
            RefreshPreparationSource();
            if (_workspace != null)
            {
                try
                {
                    string preparationSignature;
                    ComputeSourceSignatureForAction(out preparationSignature);
                    RefreshPermission(null, EventArgs.Empty);
                }
                catch { /* 读取失败的原因已由 ComputeSourceSignatureForAction 保留。 */ }
            }
            if (!_sourceReadFailed) return true;
            ShowError(new InvalidOperationException(T("任务来源无法读取，请先恢复读取"), _sourceReadError));
            RefreshPermission(null, EventArgs.Empty);
            return false;
        }

        /// <summary>明确操作时同步核查完整来源并撤销更早轮询；失败保留未知状态，成功记录本次实际来源。</summary>
        private string ComputeSourceSignatureForAction(out string preparationSignature)
        {
            InvalidateSourceObservation();
            try
            {
                preparationSignature = _store.ComputePreparationSignature(_machineId);
                string signature = _store.ComputeSignature(_machineId);
                _observedPreparationSignature = preparationSignature;
                RememberSourceSignature(signature);
                return signature;
            }
            catch (Exception error)
            {
                RecordSourceReadFailure(error);
                throw;
            }
        }

        /// <summary>检查或同步中途失败后重核来源，不能沿用异常发生前的按钮状态。</summary>
        private void RefreshSourceAfterActionFailure()
        {
            InvalidateSourceChecks();
            if (_workspace == null) return;
            try
            {
                string preparationSignature;
                ComputeSourceSignatureForAction(out preparationSignature);
            }
            catch { /* 读取失败时保留未知状态与原始异常，交由页面展示。 */ }
        }

        /// <summary>子编辑器明确检查时读取最新目录并核对前后来源，旧轮询不能覆盖这次人工检查。</summary>
        private TaskResourceCatalog LoadCatalogForEditorCheck()
        {
            try
            {
                string preparationSignature;
                string before = ComputeSourceSignatureForAction(out preparationSignature);
                var catalog = _store.LoadCatalog(_workspace);
                string after = ComputeSourceSignatureForAction(out preparationSignature);
                if (before != after)
                    throw new InvalidOperationException(T("来源文件再次变化，请重新检查"));
                return catalog;
            }
            finally { RefreshPermission(null, EventArgs.Empty); }
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
