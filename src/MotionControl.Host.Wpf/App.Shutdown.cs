using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Host.Wpf
{
    /// <summary>Host 关窗与退出事件：负责界面确认、工程门和结果提示，设备步骤交给退出协调对象。</summary>
    public partial class App
    {
        /// <summary>应用退出时由退出路径统一等待并报告历史收尾结果。</summary>
        private bool _exitingApplication;

        /// <summary>关闭窗口时正在等待控制卡安全断开，避免重复提交关窗请求。</summary>
        private bool _shutdownDisconnectInProgress;

        /// <summary>关闭前持有工程门，防止配置回滚与断卡或退出重叠。</summary>
        private IDisposable _shutdownProjectLease;

        /// <summary>异步收尾重入关闭事件时不重复询问同一批草稿。</summary>
        private bool _taskEditsCloseConfirmed;

        /// <summary>退出时保留了旧任务资源记录，后续不能用断卡后的空上下文覆盖旧快照。</summary>
        private bool _shutdownRetainedBlockedEvidence;

        /// <summary>关闭前自动等待任务和工位受控停止、保存现场记录并安全断卡。</summary>
        private async void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_shutdownDisconnectInProgress)
            {
                e.Cancel = true;
                return;
            }
            if (HasUnfinishedConnectionCleanup)
            {
                // 未发布对象可能没有当前 Runtime，必须先重试原监控，再重新进入正常关窗判断。
                e.Cancel = true;
                _shutdownDisconnectInProgress = true;
                long cleanupVersion = ++_connectionSynchronizationVersion;
                var cleanupWindow = _mainWindow;
                var cleanupRuntime = _machineRuntime;
                var cleanupConnection = _cardConnection;
                var cleanupTasks = _taskMonitorLauncher;
                var cleanupRecovery = _machineRecoveryCoordinator;
                var cleanupContinuity = _alarmContinuity;
                bool preserveRecoveryFiles = _equipmentProjectRestartRequired;
                try
                {
                    IMotionCard cleanupCard = cleanupConnection?.ConnectedMotionCard;
                    ILogicalIo cleanupIo = cleanupConnection?.ConnectedLogicalIo;
                    bool wasConnected = cleanupConnection != null && cleanupConnection.IsConnected;
                    await FinishUnpublishedConnectionsAsync();
                    // 即使收尾同步完成，也先让原 Closing 返回；WPF 禁止在原关闭事件中再次 Close。
                    await Task.Yield();
                    // 收尾期间若又换了设备，旧关闭请求到此结束，不能重新请求关闭后来设备。
                    if (_exitingApplication || cleanupVersion != _connectionSynchronizationVersion
                        || !ReferenceEquals(cleanupWindow, _mainWindow) || !ReferenceEquals(cleanupRuntime, _machineRuntime)
                        || !ReferenceEquals(cleanupConnection, _cardConnection) || !ReferenceEquals(cleanupTasks, _taskMonitorLauncher)
                        || !ReferenceEquals(cleanupRecovery, _machineRecoveryCoordinator) || !ReferenceEquals(cleanupContinuity, _alarmContinuity)
                        || preserveRecoveryFiles != _equipmentProjectRestartRequired
                        || wasConnected != (cleanupConnection != null && cleanupConnection.IsConnected)
                        || !ReferenceEquals(cleanupCard, cleanupConnection?.ConnectedMotionCard)
                        || !ReferenceEquals(cleanupIo, cleanupConnection?.ConnectedLogicalIo))
                        throw new InvalidOperationException("旧连接收尾期间设备上下文已变化，请核对当前设备后重试关闭。");
                }
                catch (Exception error)
                {
                    RecordConnectionSynchronizationError(cleanupContinuity, error);
                    _taskEditsCloseConfirmed = false;
                    ReleaseShutdownProjectGate();
                    MessageBox.Show("旧连接收尾或当前设备来源尚未确认，软件暂不关闭。\n" + error.Message,
                        "暂不能关闭软件", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                finally { _shutdownDisconnectInProgress = false; }
                cleanupWindow?.Close();
                return;
            }
            MachineRunState? runtimeState = _machineRuntime == null
                ? (MachineRunState?)null
                : _machineRuntime.State;
            // 现场 DI 与整机命令可能并发。复用轴映射页的安全断卡顺序，并等待
            // Host 完成连接状态同步后再保存；断卡期间不禁用停止按钮。
            if ((_cardConnection != null && (_cardConnection.IsConnected || _cardConnection.ConnectedMotionCard != null))
                || (runtimeState.HasValue && runtimeState != MachineRunState.Disconnected)
                || (_taskMonitorLauncher != null && (_taskMonitorLauncher.HasUnsettledTaskSession || _taskMonitorLauncher.IsCommandInProgress)))
            {
                e.Cancel = true;
                if (_machineRuntime == null)
                {
                    MessageBox.Show("设备运行时尚未就绪，无法确认安全断卡。请在轴映射页检查连接状态后重试。",
                        "暂不能关闭软件", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!_taskEditsCloseConfirmed)
                {
                    if (_taskConfigurationPage != null && !_taskConfigurationPage.ConfirmClose()) return;
                    if (_alarmCatalogPage != null && !_alarmCatalogPage.ConfirmClose()) return;
                    _taskEditsCloseConfirmed = true;
                }

                _shutdownDisconnectInProgress = true;
                // 草稿确认完成后才正式受理关闭；在首次等待前撤销仍在装配的旧连接同步。
                long expectedConnectionVersion = ++_connectionSynchronizationVersion;
                var window = _mainWindow;
                var runtime = _machineRuntime;
                var connection = _cardConnection;
                IMotionCard card = null;
                ILogicalIo logicalIo = null;
                var tasks = _taskMonitorLauncher;
                var recovery = _machineRecoveryCoordinator;
                bool preserveRecoveryFiles = _equipmentProjectRestartRequired;

                // 核对关闭所捕获的组合和同步代次；不能把后来连接的结果当成本次断卡完成。
                bool IsShutdownSourceCurrent()
                {
                    return !_exitingApplication && expectedConnectionVersion == _connectionSynchronizationVersion
                        && ReferenceEquals(window, _mainWindow) && ReferenceEquals(connection, _cardConnection)
                        && ReferenceEquals(tasks, _taskMonitorLauncher) && ReferenceEquals(recovery, _machineRecoveryCoordinator)
                        && preserveRecoveryFiles == _equipmentProjectRestartRequired;
                }

                // 停止与保存阶段仍须持有原 Runtime；原卡或 IO 被替换时也不得继续操作。
                void CheckShutdownContext()
                {
                    if (!IsShutdownSourceCurrent() || !ReferenceEquals(runtime, _machineRuntime)
                        || (connection != null && connection.ConnectedMotionCard != null
                            && !ReferenceEquals(card, connection.ConnectedMotionCard))
                        || (connection != null && connection.ConnectedLogicalIo != null
                            && !ReferenceEquals(logicalIo, connection.ConnectedLogicalIo)))
                        throw new InvalidOperationException("退出期间设备上下文已变化，不能用旧退出结果操作当前设备。");
                }

                // 正常断卡会使原会话失效，随后同步会清空 Runtime；这两种变化不等于换成了新设备。
                void CheckDisconnectedShutdownContext(bool synchronizationFinished)
                {
                    if (!IsShutdownSourceCurrent() || connection == null || connection.IsConnected
                        || connection.ConnectedMotionCard != null || connection.ConnectedLogicalIo != null
                        || (_machineRuntime != null && (!ReferenceEquals(runtime, _machineRuntime)
                            || runtime.State != MachineRunState.Disconnected || runtime.ConnectionSessionId.HasValue))
                        || (synchronizationFinished && (_machineRuntime != null
                            || (tasks != null && (tasks.HasUnsettledTaskSession || tasks.IsCommandInProgress)))))
                        throw new InvalidOperationException("退出期间连接来源已变化或断开收尾尚未完成，不能关闭当前设备。");
                }
                HostShutdownOperation shutdown = null;
                try
                {
                    card = connection?.ConnectedMotionCard;
                    logicalIo = connection?.ConnectedLogicalIo;
                    shutdown = new HostShutdownOperation(runtime, connection, tasks, recovery,
                        preserveRecoveryFiles, CheckShutdownContext);
                    await shutdown.StopAsync();
                    await AcquireShutdownProjectGateAsync();
                    await shutdown.VerifyAndSaveAsync();
                    await DisconnectMachineBeforeCardAsync(CancellationToken.None);
                    CheckShutdownContext();
                    if (connection != null) await connection.DisconnectAsync(CancellationToken.None);
                    CheckDisconnectedShutdownContext(false);
                    // 本次同步入口会递增一次；等待中若又有同步进入，完成时必须拒绝旧关闭。
                    expectedConnectionVersion++;
                    await SynchronizeCardConnectionAsync();
                    CheckDisconnectedShutdownContext(true);
                }
                catch (Exception error)
                {
                    // 只整理仍属于本次来源的断卡结果；同一连接服务中的后来卡也不能被旧 catch 清理。
                    try
                    {
                        if (IsShutdownSourceCurrent() && connection != null && !connection.IsConnected
                            && (_machineRuntime == null || ReferenceEquals(runtime, _machineRuntime))
                            && (connection.ConnectedMotionCard == null || ReferenceEquals(card, connection.ConnectedMotionCard))
                            && (connection.ConnectedLogicalIo == null || ReferenceEquals(logicalIo, connection.ConnectedLogicalIo)))
                            await SynchronizeCardConnectionAsync();
                    }
                    catch (Exception syncError) { Trace.TraceError("关窗断卡后同步失败：" + syncError); }
                    _taskEditsCloseConfirmed = false;
                    ReleaseShutdownProjectGate();
                    bool released = false;
                    Exception reportedError = error;
                    try
                    {
                        if (shutdown != null) released = await shutdown.TryCancelAsync();
                    }
                    catch (Exception cancelError)
                    {
                        reportedError = new AggregateException("退出失败，且原设备退出保护未能确认撤回。", error, cancelError);
                        Trace.TraceError(reportedError.ToString());
                    }
                    string lockNote = shutdown != null && shutdown.HasBegun && !released
                        ? "\n退出保护仍在生效，仅允许停止及现场核查；处理原因后重试关闭。" : string.Empty;
                    MessageBox.Show("设备停止、会话收尾或控制卡断开尚未确认，软件暂不关闭。请检查现场并根据以下原因处理后重试。\n" + reportedError.Message + lockNote,
                        "暂不能关闭软件", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                finally
                {
                    // 即使后续保存或断卡失败，已交接的资源证据也必须保留。
                    if (shutdown != null && shutdown.RetainsBlockedEvidence) _shutdownRetainedBlockedEvidence = true;
                    _shutdownDisconnectInProgress = false;
                }

                // 等待期间操作员可能编辑配置；重新检查未保存内容。
                _taskEditsCloseConfirmed = false;
                window.Close();
                return;
            }

            // 离线关闭也要等待配置页面的失败回滚续体完成。
            if (_shutdownProjectLease == null)
            {
                Task waitForProject = AcquireShutdownProjectGateAsync();
                if (!waitForProject.IsCompleted)
                {
                    e.Cancel = true;
                    _shutdownDisconnectInProgress = true;
                    try { await waitForProject; }
                    catch (Exception error)
                    {
                        MessageBox.Show("工程配置仍在保存或恢复，软件暂不关闭。\n" + error.Message,
                            "暂不能关闭软件", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    finally { _shutdownDisconnectInProgress = false; }
                    _mainWindow.Close();
                    return;
                }
                try { await waitForProject; }
                catch (Exception error)
                {
                    e.Cancel = true;
                    MessageBox.Show("工程配置仍在保存或恢复，软件暂不关闭。\n" + error.Message,
                        "暂不能关闭软件", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (!_taskEditsCloseConfirmed)
            {
                if (_taskConfigurationPage != null && !_taskConfigurationPage.ConfirmClose()) { ReleaseShutdownProjectGate(); e.Cancel = true; return; }
                if (_alarmCatalogPage != null && !_alarmCatalogPage.ConfirmClose()) { ReleaseShutdownProjectGate(); e.Cancel = true; return; }
                _taskEditsCloseConfirmed = true;
            }
            try
            {
                bool savePausedFlow = runtimeState == MachineRunState.Paused;
                if (!_shutdownRetainedBlockedEvidence && !_equipmentProjectRestartRequired)
                    _machineRecoveryCoordinator.SaveForShutdown(_machineRuntime, savePausedFlow);
            }
            catch (Exception error)
            {
                e.Cancel = true;
                _taskEditsCloseConfirmed = false;
                ReleaseShutdownProjectGate();
                MessageBox.Show(
                    "设备恢复状态保存失败，软件暂不关闭。请处理存储问题后重试。\n" + error.Message,
                    "恢复状态保存失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
            ReleaseShutdownProjectGate();
        }

        /// <summary>等待工程与配置回滚完成；超时不关闭或断卡。</summary>
        private async Task AcquireShutdownProjectGateAsync()
        {
            if (_shutdownProjectLease != null) return;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try { _shutdownProjectLease = await HostProjectOperationGate.EnterImportAsync(timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                { throw new TimeoutException("等待工程配置收尾超时，请稍后重试。"); }
            }
        }

        /// <summary>关闭受阻或成功时归还工程门。</summary>
        private void ReleaseShutdownProjectGate()
        {
            IDisposable lease = _shutdownProjectLease;
            _shutdownProjectLease = null;
            if (lease != null) lease.Dispose();
        }

        /// <summary>应用退出时断开已连接的控制卡，并报告句柄释放失败。</summary>
        protected override void OnExit(ExitEventArgs e)
        {
            // 退出入口即撤销旧连接同步，后续任何迟到续体都不得再发布运行时或页面。
            _exitingApplication = true;
            ++_connectionSynchronizationVersion;
            if (_softwareConnectionTimer != null)
            {
                _softwareConnectionTimer.Stop();
                _softwareConnectionTimer.Tick -= SoftwareConnectionTimer_Tick;
            }
            // 正常关闭在主窗口关闭事件已要求任务收尾。系统强制退出时不能在 UI
            // 线程同步等待异步会话关闭；保留运行标记，让下次启动要求核对现场。
            if (_taskMonitorLauncher != null && _taskMonitorLauncher.HasUnsettledTaskSession)
                Trace.TraceWarning("应用退出时任务会话尚未关闭，保留现有恢复记录以供下次核查。");

            MachineRuntime exitingRuntime = _machineRuntime;
            var exitingHistory = _alarmHistoryJournal;
            bool currentRuntimeWasPending = IsUnfinishedConnectionRuntime(exitingRuntime);
            FinishUnpublishedConnectionsAtExit();
            if (exitingRuntime != null && !currentRuntimeWasPending)
            {
                try
                {
                    exitingRuntime.StopControlInputMonitoringAsync().GetAwaiter().GetResult();
                }
                catch (Exception monitorError)
                {
                    // 强制退出也不能把仍未结束的当前监控及其历史当成已正常收尾。
                    RememberUnfinishedConnection(exitingRuntime, exitingHistory);
                    RecordConnectionSynchronizationError(_alarmContinuity, monitorError);
                }
            }

            if (_cardConnection != null && _cardConnection.IsConnected)
            {
                try
                {
                    _cardConnection.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception error)
                {
                    MessageBox.Show(
                        "关闭应用时断开运动控制卡失败：" + error.Message,
                        "运动控制卡连接",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            if (!IsUnfinishedConnectionHistory(_alarmHistoryJournal)) CloseAlarmHistory();
            FinishAlarmHistoryAtExit();
            // 只有设备项目明确约束所有报警来源，且正常断卡已确认收尾时才能结束本会话。
            // 系统强退、异常断连、旧报警未复位或设备项目未声明时保留原记录。
            bool alarmHandoffComplete = _alarmHandoffSettled && !_shutdownRetainedBlockedEvidence
                && (_taskMonitorLauncher == null || (!_taskMonitorLauncher.HasUnsettledTaskSession && !_taskMonitorLauncher.IsCommandInProgress))
                && (_cardConnection == null || (!_cardConnection.IsConnected && _cardConnection.ConnectedMotionCard == null));
            bool defaultProjectNeverConnected = _usingSavedSimulationProject && !_savedControllerConnectionAttempted
                && (_taskMonitorLauncher == null || (!_taskMonitorLauncher.HasUnsettledTaskSession && !_taskMonitorLauncher.IsCommandInProgress))
                && _machineRuntime == null && (_cardConnection == null
                    || (!_cardConnection.IsConnected && _cardConnection.ConnectedMotionCard == null));
            _alarmContinuity?.CloseIfSettled(!HasUnfinishedConnectionCleanup
                && (alarmHandoffComplete || defaultProjectNeverConnected));
            ReleaseSingleInstanceMutex();

            base.OnExit(e);
        }
    }
}
