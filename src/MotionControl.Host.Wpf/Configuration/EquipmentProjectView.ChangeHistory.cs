using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Configuration;

namespace MotionControl.Host.Wpf.Configuration
{
    /// <summary>设备工程应用结果的持久记录与只读查询。</summary>
    public partial class EquipmentProjectView
    {
        /// <summary>切页或切语言后作废旧查询返回值。</summary>
        private long _historyGeneration;
        /// <summary>整个工程页被移出可视树后，不启动或展示新的后台查询。</summary>
        private bool _historyDetached;

        /// <summary>在现有工程门内执行一次 Apply；原服务结果、重启锁和历史写入分别反馈。</summary>
        private async Task ApplyAndReportAsync(MachineProjectPreview preview)
        {
            string backup = null;
            Exception applyError = null;
            string gateReason = null;
            DateTimeOffset completedUtc = default(DateTimeOffset);
            await Task.Run(() =>
            {
                try
                {
                    backup = _service.Apply(preview, () => Dispatcher.Invoke(() =>
                    {
                        gateReason = _getApplyBlockedReason(true);
                        return string.IsNullOrWhiteSpace(gateReason);
                    }));
                }
                catch (Exception error) { applyError = error; }
                finally { completedUtc = DateTimeOffset.UtcNow; }
            });
            string restartError = null;
            try { EnterRestartRequired(); }
            catch (Exception error) { restartError = error.Message; }
            string detail = applyError == null ? T("备份：", "Backup: ") + backup : ApplyFailureDetails(applyError);
            if (applyError != null && !string.IsNullOrWhiteSpace(gateReason)) detail += Environment.NewLine + gateReason;
            string status = applyError == null
                ? T("工程已应用。", "Project applied. ") + detail + T("。请关闭软件后重新打开并准备设备。", ". Close and reopen, then prepare the equipment.")
                : T("工程未能完成应用：", "Project application failed: ") + detail + T("。请关闭软件后重新打开并核查配置。", ". Close and reopen, then inspect the configuration.");
            if (restartError != null) status += Environment.NewLine + T("重启锁定通知异常：", "Restart notification failed: ") + restartError;

            string recordError;
            try
            {
                recordError = _journal == null ? T("未接入记录服务。", "History service is unavailable.")
                    : await Task.Run(() => _journal.TryAppend(new ConfigurationChangeRecord {
                        RecordId = Guid.NewGuid(), MachineId = _journal.MachineId, TimeUtc = completedUtc,
                        Stage = ConfigurationChangeStage.Apply,
                        Outcome = applyError == null ? ConfigurationChangeOutcome.Succeeded : ConfigurationChangeOutcome.Failed,
                        ObjectType = "设备工程", ObjectId = _journal.MachineId, Source = "MachineProjectPackageService.Apply",
                        Detail = detail
                    }));
            }
            catch (Exception auditError) { recordError = auditError.Message; }
            if (recordError != null) status += Environment.NewLine + T("变更记录未保存：", "Change history not saved: ") + recordError;
            ShowStatus(status, applyError != null || recordError != null || restartError != null);
            if (DetailsTabs.SelectedItem == ChangeHistoryTab && _journal != null) await RefreshHistoryAsync();
        }

        /// <summary>保留恢复结论及原始应用、恢复失败原因，只提取异常消息，不包含堆栈和异常附加数据。</summary>
        private static string ApplyFailureDetails(Exception error)
        {
            var messages = new List<string>();
            var seen = new HashSet<Exception>();
            AddCause(error);
            return string.Join(Environment.NewLine, messages);

            // 聚合异常可能同时含应用失败与恢复失败；每个异常对象只展开一次。
            void AddCause(Exception cause)
            {
                if (cause == null || !seen.Add(cause)) return;
                if (!messages.Contains(cause.Message)) messages.Add(cause.Message);
                if (cause is AggregateException aggregate)
                {
                    foreach (Exception inner in aggregate.InnerExceptions) AddCause(inner);
                }
                else AddCause(cause.InnerException);
            }
        }

        /// <summary>重新进入缓存的工程页面时从磁盘重读当前历史。</summary>
        private async void HistoryPage_Loaded(object sender, RoutedEventArgs e)
        {
            _historyDetached = false;
            if (_journal != null && DetailsTabs.SelectedItem == ChangeHistoryTab) await RefreshHistoryAsync();
        }

        /// <summary>离开整个工程页面时使旧请求无效，清空已展示的历史。</summary>
        private void HistoryPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _historyDetached = true;
            _historyGeneration++;
            HistoryList.ItemsSource = null;
            HistoryDetail.Clear();
            if (_journal != null) ChangeHistoryMessage.Text = HistoryScope();
        }

        /// <summary>说明当前有实际记录源的业务范围。</summary>
        private string HistoryScope()
        {
            return T("当前接入工程导入、报警库保存、变量定义文件保存及内存更新；其他配置来源尚未全部接入。最多显示最近 50 条。",
                "Recorded sources include project import, alarm catalog saves, and variable definition file saves and memory updates; other configuration sources are not fully connected. At most the latest 50 records are shown.");
        }

        /// <summary>仅选中历史页时查询，其他页选择不会取得控制许可。</summary>
        private async void HistoryTab_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource != DetailsTabs) return;
            _historyGeneration++;
            if (ChangeHistoryTab != null && DetailsTabs.SelectedItem == ChangeHistoryTab && _journal != null)
                await RefreshHistoryAsync();
        }

        /// <summary>手动刷新持久记录，不写配置。</summary>
        private async void RefreshHistory_Click(object sender, RoutedEventArgs e) { await RefreshHistoryAsync(); }

        /// <summary>在后台读取最多 50 条，清除旧状态并拒绝迟到结果。</summary>
        private Task RefreshHistoryAsync()
        {
            if (_journal == null || _historyDetached) return Task.CompletedTask;
            return DisplayHistoryQueryAsync(Task.Run(() =>
            {
                var records = _journal.ReadRecent(50, out var notices);
                return (records, notices);
            }));
        }

        /// <summary>只将本次有效请求的结果填入同一工程页，迟到的成功与异常都不覆盖新内容。</summary>
        private async Task DisplayHistoryQueryAsync(Task<(IReadOnlyList<ConfigurationChangeRecord> Records,
            IReadOnlyList<string> Notices)> query)
        {
            long generation = ++_historyGeneration;
            Guid? selected = HistoryList.SelectedItem is KeyValuePair<string, ConfigurationChangeRecord> previous
                ? previous.Value.RecordId : (Guid?)null;
            HistoryList.ItemsSource = null;
            HistoryDetail.Clear();
            ChangeHistoryMessage.Text = T("正在读取变更记录…", "Reading change history…");
            RefreshHistoryButton.IsEnabled = false;
            try
            {
                var (records, notices) = await query;
                if (generation != _historyGeneration) return;
                var rows = records.Select(row => new KeyValuePair<string, ConfigurationChangeRecord>(
                    row.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "  " + row.ObjectType + "  "
                        + RecordStageText(row) + "  " + RecordOutcomeText(row), row)).ToList();
                ChangeHistoryMessage.Text = HistoryScope() + " " + (notices.Count > 0
                    ? T("历史读取不完整：", "History is incomplete: ") + string.Join(" | ", notices.Take(3))
                    : rows.Count == 0 ? T("当前工程尚无持久记录。", "No persistent records for this project.")
                    : T("已读取最近记录：", "Recent records: ") + rows.Count);
                HistoryList.ItemsSource = rows;
                if (rows.Count > 0) HistoryList.SelectedIndex = Math.Max(0, rows.FindIndex(row => row.Value.RecordId == selected));
            }
            catch (Exception error)
            {
                if (generation == _historyGeneration) ChangeHistoryMessage.Text = T("变更记录读取失败：", "Could not read change history: ") + error.Message;
            }
            finally { if (generation == _historyGeneration) RefreshHistoryButton.IsEnabled = true; }
        }

        /// <summary>显示所选阶段的真实数据；缺失信息不推定，与当前会话状态分开。</summary>
        private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HistoryDetail == null) return;
            if (!(HistoryList.SelectedItem is KeyValuePair<string, ConfigurationChangeRecord> pair)) { HistoryDetail.Clear(); return; }
            var row = pair.Value;
            string missing = T("未提供", "Not provided");
            HistoryDetail.Text = string.Join(Environment.NewLine, new[] {
                T("记录 ID　", "Record ID  ") + row.RecordId, T("设备　", "Machine  ") + row.MachineId,
                T("对象　", "Object  ") + row.ObjectType + " / " + row.ObjectId,
                T("阶段　", "Stage  ") + RecordStageText(row), T("结果　", "Outcome  ") + RecordOutcomeText(row),
                T("时间　", "Time  ") + row.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
                T("操作关联 ID　", "Operation ID  ") + (row.OperationId ?? missing),
                T("来源　", "Source  ") + row.Source, T("操作者　", "Operator  ") + (row.Actor ?? missing),
                T("变更前版本　", "Previous version  ") + (row.BeforeVersion ?? missing),
                T("变更后版本　", "New version  ") + (row.AfterVersion ?? missing),
                T("字段　", "Field  ") + (row.Field ?? missing), T("旧值　", "Old value  ") + (row.BeforeValue ?? missing),
                T("新值　", "New value  ") + (row.AfterValue ?? missing), T("单位　", "Unit  ") + (row.Unit ?? missing),
                T("说明　", "Details  ") + (row.Detail ?? missing) });
        }

        /// <summary>变量内存更新的回执只说明调用结果，不能推断内存没有变化。</summary>
        private string RecordStageText(ConfigurationChangeRecord row)
        {
            return row.Stage == ConfigurationChangeStage.Apply && row.Source == "IDeviceVariableStore.ReplaceDefinitions"
                ? T("变量内存更新", "Variable memory update") : StageText(row.Stage);
        }

        /// <summary>区分变量更新调用异常和确定未执行，其他来源保留原结果。</summary>
        private string RecordOutcomeText(ConfigurationChangeRecord row)
        {
            if (row.Stage == ConfigurationChangeStage.Apply && row.Source == "IDeviceVariableStore.ReplaceDefinitions")
            {
                if (row.Outcome == ConfigurationChangeOutcome.Succeeded) return T("调用完成", "Call completed");
                if (row.Outcome == ConfigurationChangeOutcome.Failed) return T("调用异常，内存状态待核查", "Call failed; memory state requires inspection");
            }
            return OutcomeText(row.Outcome);
        }

        /// <summary>仅呈现记录本身的阶段。</summary>
        private string StageText(ConfigurationChangeStage stage)
        {
            switch (stage)
            {
                case ConfigurationChangeStage.SaveDraft: return T("保存草稿", "Save draft");
                case ConfigurationChangeStage.Check: return T("检查", "Check");
                case ConfigurationChangeStage.SaveFiles: return T("写入文件", "Save files");
                case ConfigurationChangeStage.Apply: return T("应用", "Apply");
                case ConfigurationChangeStage.LoadSession: return T("载入会话", "Load session");
                default: return T("未知阶段", "Unknown stage");
            }
        }

        /// <summary>展示源业务阶段的终态。</summary>
        private string OutcomeText(ConfigurationChangeOutcome outcome)
        {
            switch (outcome)
            {
                case ConfigurationChangeOutcome.Succeeded: return T("成功", "Succeeded");
                case ConfigurationChangeOutcome.Failed: return T("失败", "Failed");
                case ConfigurationChangeOutcome.Rejected: return T("拒绝", "Rejected");
                default: return T("未知结果", "Unknown outcome");
            }
        }
    }
}
