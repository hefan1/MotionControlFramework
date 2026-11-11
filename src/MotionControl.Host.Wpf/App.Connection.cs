using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MotionControl.Framework.Contracts.Clearance;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.Framework.Wpf.Monitoring;
using MotionControl.Host.Wpf.Recovery;

namespace MotionControl.Host.Wpf
{
    /// <summary>连接通知的装配与收尾；等待后只发布仍属于当前来源的运行时和页面。</summary>
    public partial class App
    {
        /// <summary>每次同步或开始关闭时递增，撤销还在等待的旧连接请求。</summary>
        private long _connectionSynchronizationVersion;

        /// <summary>已完整发布的连接服务，用于识别同一连接的重复通知。</summary>
        private IMotionCardHostConnection _publishedCardConnection;

        /// <summary>与已发布 Runtime 同时生效的逻辑 IO，不能只按卡对象判断同源。</summary>
        private ILogicalIo _publishedLogicalIo;

        /// <summary>尚未确认监控结束的未发布运行时；失败后继续保留具体对象和对应历史。</summary>
        private readonly List<UnfinishedConnectionCleanup> _unfinishedConnectionCleanups = new List<UnfinishedConnectionCleanup>();

        /// <summary>一次未发布运行时的收尾记录，停止任务必须是 Runtime 返回的原始任务。</summary>
        private sealed class UnfinishedConnectionCleanup
        {
            /// <summary>需要确认现场按钮监控已经结束的原运行时。</summary>
            internal readonly MachineRuntime Runtime;
            /// <summary>只有原监控结束后才能关闭的报警历史。</summary>
            internal readonly AlarmHistoryJournal History;
            /// <summary>共享仍在进行的停止；失败后下一次请求可以重新尝试。</summary>
            internal Task StopTask;

            /// <summary>保存具体来源，不发出停止命令或结束历史。</summary>
            internal UnfinishedConnectionCleanup(MachineRuntime runtime, AlarmHistoryJournal history)
            {
                Runtime = runtime;
                History = history;
            }
        }

        /// <summary>是否还有未确认结束的连接监控；关闭与同步都必须先处理这些对象。</summary>
        private bool HasUnfinishedConnectionCleanup
        {
            get { return _unfinishedConnectionCleanups != null && _unfinishedConnectionCleanups.Count != 0; }
        }

        /// <summary>同步连接状态；先收尾捕获的旧上下文，再发布当前来源，旧续体不能清理新连接。</summary>
        private async Task SynchronizeCardConnectionAsync()
        {
            if (_mainWindow == null || _cardConnection == null || _exitingApplication) return;

            long requestVersion = ++_connectionSynchronizationVersion;
            var connection = _cardConnection;
            bool connected = connection.IsConnected;
            IMotionCard card = connection.ConnectedMotionCard;
            ILogicalIo logicalIo = connection.ConnectedLogicalIo;
            var window = _mainWindow;
            var tasks = _taskMonitorLauncher;
            var configuration = _configurationStore;
            var variables = _deviceVariableStore;
            var variablePage = _deviceVariablePage;
            var recovery = _machineRecoveryCoordinator;
            var continuity = _alarmContinuity;

            // 组合对象和代次都必须仍属于本次请求；物理失联可能不发送新的同步通知。
            bool IsLatestRequest()
            {
                return !_exitingApplication && requestVersion == _connectionSynchronizationVersion
                    && ReferenceEquals(connection, _cardConnection) && ReferenceEquals(window, _mainWindow)
                    && ReferenceEquals(tasks, _taskMonitorLauncher) && ReferenceEquals(configuration, _configurationStore)
                    && ReferenceEquals(variables, _deviceVariableStore) && ReferenceEquals(variablePage, _deviceVariablePage)
                    && ReferenceEquals(recovery, _machineRecoveryCoordinator) && ReferenceEquals(continuity, _alarmContinuity);
            }

            // 心跳失联只更新卡状态，因此每次等待后还要重新核对实际卡和 IO 身份。
            bool IsSourceCurrent()
            {
                return IsLatestRequest() && connection.IsConnected == connected
                    && ReferenceEquals(card, connection.ConnectedMotionCard)
                    && ReferenceEquals(logicalIo, connection.ConnectedLogicalIo);
            }

            _safetyInterlockPage?.RefreshDigitalInputNames();
            bool softwareController = connection is VirtualAxisMappingCardConnection;
            if (softwareController) _lastSoftwareConnectionStatus = connected;
            window.SetMotionCardConnectionStatus(connected, _axisMappingPage.CardConnectionStatusMessage, softwareController);

            MachineRuntime previousRuntime = _machineRuntime;
            AlarmHistoryJournal previousHistory = _alarmHistoryJournal;
            MachineRuntime createdRuntime = null;
            AlarmHistoryJournal createdHistory = null;
            bool published = false;
            Exception failure = null;
            string recoveryMessage = string.Empty;
            string stationConfigurationWarning = null;
            try
            {
                // 即使当前卡与已发布卡相同，也不能绕过上一轮失败后仍保留的监控收尾。
                await FinishUnpublishedConnectionsAsync();
                if (!IsSourceCurrent() || !ReferenceEquals(previousRuntime, _machineRuntime)) return;
                if (connected && ReferenceEquals(connection, _publishedCardConnection)
                    && ReferenceEquals(logicalIo, _publishedLogicalIo) && previousRuntime != null
                    && previousRuntime.MotionCards.Cards.Count == 1
                    && ReferenceEquals(previousRuntime.MotionCards.Cards.First(), card)
                    && tasks != null && tasks.HasMachineContext(previousRuntime, logicalIo, variables)
                    && previousRuntime.IsConnectionSessionCurrent(previousRuntime.ConnectionSessionId))
                {
                    // 已发布的同一来源只更新连接显示，不关闭任务、不换报警历史或重复启动监控。
                    return;
                }

                // 底层卡已连接不等于页面已可操作；旧对象收尾期间明确保留未就绪状态。
                if (connected) window.SetMotionCardConnectionStatus(false, "设备连接尚未就绪，请等待或检查连接状态。", softwareController);

                // 换源或失联时立即撤销旧操作资格，保留原对象供安全收尾和失败后重试。
                if (previousRuntime != null)
                {
                    if (previousRuntime.State != MachineRunState.Disconnected)
                    {
                        RetainUnconfirmedAlarms(previousRuntime);
                        continuity?.MarkUnconfirmed("控制器未经过已确认的断开交接。");
                    }
                    previousRuntime.InvalidateConnectionSessionOnLoss();
                    previousRuntime.StatusChanged -= MachineRuntime_RecoveryStatusChanged;
                }
                tasks?.MarkControllerConnectionLost();
                _flowStatusPage?.MarkControllerConnectionLost();

                Exception cleanupFailure = null;
                try
                {
                    if (tasks != null) await tasks.SetMachineContextAsync(null, null, null, null);
                }
                catch (Exception error) { cleanupFailure = error; }
                try
                {
                    if (previousRuntime != null) await previousRuntime.StopControlInputMonitoringAsync();
                }
                catch (Exception error)
                {
                    cleanupFailure = cleanupFailure == null ? error
                        : new AggregateException("旧任务与现场按钮监控均未确认收尾。", cleanupFailure, error);
                }
                if (cleanupFailure != null)
                    throw new InvalidOperationException("旧设备上下文未完成收尾：" + cleanupFailure.Message, cleanupFailure);

                // 即使本请求已过期，也只结束自己捕获的日志；不会取后来连接的全局日志。
                CloseOwnedAlarmHistory(previousHistory);
                if (!IsSourceCurrent() || !ReferenceEquals(previousRuntime, _machineRuntime)) return;
                ClearConnectionPages(previousRuntime);
                if (!connected) return;
                if (card == null) throw new InvalidOperationException("控制卡连接状态有效，但没有可供设备运行的卡对象。");
                if (logicalIo == null) throw new InvalidOperationException("控制卡已连接，但没有可供设备运行的逻辑 IO 服务。");

                var motionCards = new MotionCardGroup(new[] { card });
                continuity.Open();
                SafetyInterlockConfigurationDocument controlInputs;
                var safety = CreateSafetyStatus(logicalIo, out controlInputs);
                var alarms = new MachineAlarmService();
                if (!string.IsNullOrWhiteSpace(continuity.BlockReason))
                    ReportUnconfirmedAlarm(alarms, continuity.BlockReason
                        + (string.IsNullOrWhiteSpace(_retainedAlarmDetails) ? string.Empty : " 旧报警线索：" + _retainedAlarmDetails));
                // 旧来源的真实迟到报警仍须闭锁当前会话，不能为清理页面而解除这项保护。
                alarms.StopAlarmRaised += AlarmService_StopAlarmRaised;
                createdHistory = new AlarmHistoryJournal(alarms, configuration, MachineId);
                IStationFlow[] stationFlows;
                IReadOnlyList<IStationPreparation> preparations;
                IReadOnlyList<ControlledClearanceProfile> clearanceProfiles;
                bool trackedAlarmSources;
                try
                {
                    stationFlows = CreateEquipmentStationFlows(motionCards, logicalIo, alarms,
                        out preparations, out clearanceProfiles, out trackedAlarmSources);
                }
                catch (Exception error)
                {
                    stationFlows = new IStationFlow[0];
                    preparations = new IStationPreparation[0];
                    clearanceProfiles = new ControlledClearanceProfile[0];
                    trackedAlarmSources = false;
                    stationConfigurationWarning = "设备工位配置无法启用，自动流程已关闭；轴手动和 IO 监控仍可使用。\n" + error.Message;
                }

                recovery.TryRestoreStationFlows(stationFlows, out recoveryMessage);
                createdRuntime = new MachineRuntime(motionCards, stationFlows, alarms, safety,
                    controlInputIo: logicalIo, startInputName: controlInputs.StartInputName,
                    pauseInputName: controlInputs.PauseInputName, resetInputName: controlInputs.ResetInputName,
                    controlOwner: HostEquipmentSelection.ResolveExecutionOwner(configuration, MachineId));
                createdRuntime.RequestInitializationRecovery(recovery.HasCompletedInitializationCheckpoint);
                createdRuntime.SetRecoveryBlockReason(recovery.RecoveryBlockReason);
                createdRuntime.SetAlarmContinuityBlockReason(continuity.BlockReason);
                createdRuntime.SetBeforeAutomaticRun(() => recovery.InvalidateBeforeAutomaticRun(createdRuntime));
                createdRuntime.SetPreparationRecordCallbacks(() => recovery.MarkBeforePreparation(createdRuntime), recovery.SaveAfterSafePreparation);
                createdRuntime.SetClearanceRecordCallbacks(recovery.MarkBeforeClearance,
                    recovery.SaveClearanceActionsObserved, recovery.RecordClearanceInspection);
                createdRuntime.ConfigureControlledClearanceProfiles(clearanceProfiles);
                createdRuntime.ConfigurePreparations(preparations);
                if (!IsSourceCurrent()) return;
                await createdRuntime.ConnectAsync(CancellationToken.None);
                if (!IsSourceCurrent()) return;
                await tasks.SetMachineContextAsync(createdRuntime, motionCards, logicalIo, variables, preparations);
                if (!IsSourceCurrent()) return;

                // 任务入口先就位；这里没有异步等待，所有可见页面使用同一组已确认对象。
                _machineRuntime = createdRuntime;
                _machineSafetyStatus = safety;
                Volatile.Write(ref _currentAlarmService, alarms);
                _alarmHistoryJournal = createdHistory;
                _trackedAlarmSources = trackedAlarmSources;
                _alarmHandoffSettled = false;
                if (!string.IsNullOrWhiteSpace(continuity.BlockReason))
                    createdRuntime.MarkAlarmContinuityUnconfirmed(continuity.BlockReason);
                createdRuntime.StatusChanged += MachineRuntime_RecoveryStatusChanged;
                variablePage.SetMachineRuntime(createdRuntime);
                _flowStatusPage = new FlowStatusView(createdRuntime, () => tasks.HasTaskSession);
                window.SetFlowStatusPage(_flowStatusPage);
                RefreshManualDebugPages(createdRuntime, motionCards, logicalIo);
                window.SetMachineRuntime(createdRuntime);
                window.SetMotionCardConnectionStatus(true, _axisMappingPage.CardConnectionStatusMessage, softwareController);
                _publishedCardConnection = connection;
                _publishedLogicalIo = logicalIo;
                published = true;
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (!published && createdRuntime != null)
                {
                    // 先登记再发起撤销和停止；任一步失败都不能丢失原对象的后续重试入口。
                    UnfinishedConnectionCleanup cleanup = RememberUnfinishedConnection(createdRuntime, createdHistory);
                    try
                    {
                        createdRuntime.StatusChanged -= MachineRuntime_RecoveryStatusChanged;
                        createdRuntime.InvalidateConnectionSessionOnLoss();
                        tasks?.RevokeMachineContext(createdRuntime);
                    }
                    catch (Exception error)
                    {
                        failure = failure == null ? error
                            : new AggregateException("连接初始化失败，且本轮操作资格撤销失败。", failure, error);
                    }
                    try { await FinishUnpublishedConnectionAsync(cleanup); }
                    catch (Exception error)
                    {
                        failure = failure == null ? error
                            : new AggregateException("连接初始化失败，且本轮监控未确认收尾。", failure, error);
                    }
                }
                // Runtime 尚未创建时没有按钮监控；已创建的历史由其具体收尾记录负责关闭。
                if (!published && createdRuntime == null && createdHistory != null) CloseOwnedAlarmHistory(createdHistory);
                if (failure != null) RecordConnectionSynchronizationError(continuity, failure);
                if (!published && createdRuntime != null && IsLatestRequest()
                    && ReferenceEquals(createdRuntime, _machineRuntime)) ClearConnectionPages(createdRuntime);
            }

            // 旧失败保留诊断和报警交接证据，但不在后来设备上弹出初始化失败提示。
            if (!IsSourceCurrent()) return;
            if (failure != null)
            {
                MessageBox.Show("连接上下文尚未就绪，未启用新的运行时或页面：" + failure.Message,
                    "设备运行时", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!string.IsNullOrWhiteSpace(stationConfigurationWarning))
                MessageBox.Show(stationConfigurationWarning, "设备工位配置", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (IsSourceCurrent() && !string.IsNullOrWhiteSpace(recoveryMessage))
                MessageBox.Show(recoveryMessage, "设备流程恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>保留未完成收尾的具体运行时和历史，同一对象不会登记两次。</summary>
        private UnfinishedConnectionCleanup RememberUnfinishedConnection(MachineRuntime runtime, AlarmHistoryJournal history)
        {
            UnfinishedConnectionCleanup cleanup = _unfinishedConnectionCleanups.FirstOrDefault(item => ReferenceEquals(item.Runtime, runtime));
            if (cleanup == null)
            {
                cleanup = new UnfinishedConnectionCleanup(runtime, history);
                _unfinishedConnectionCleanups.Add(cleanup);
            }
            return cleanup;
        }

        /// <summary>只共享原始 Runtime 停止任务；不得保存需要回到 UI 才能完成的包装任务。</summary>
        private Task GetConnectionMonitorStopTask(UnfinishedConnectionCleanup cleanup)
        {
            if (cleanup.StopTask == null || cleanup.StopTask.IsFaulted || cleanup.StopTask.IsCanceled)
                cleanup.StopTask = cleanup.Runtime.StopControlInputMonitoringAsync();
            return cleanup.StopTask;
        }

        /// <summary>只有停止成功才移除记录并结束原历史；失败时保留对象供下次同步或退出重试。</summary>
        private async Task FinishUnpublishedConnectionAsync(UnfinishedConnectionCleanup cleanup)
        {
            await GetConnectionMonitorStopTask(cleanup);
            if (_unfinishedConnectionCleanups.Remove(cleanup)) CloseOwnedAlarmHistory(cleanup.History);
        }

        /// <summary>新连接发布前或正常关闭前，重试尚未结束的具体监控。</summary>
        private async Task FinishUnpublishedConnectionsAsync()
        {
            if (!HasUnfinishedConnectionCleanup) return;
            foreach (UnfinishedConnectionCleanup cleanup in _unfinishedConnectionCleanups.ToArray())
                await FinishUnpublishedConnectionAsync(cleanup);
        }

        /// <summary>强制退出不等待 UI 收尾续体，只等待使用 ConfigureAwait(false) 的原始运行时停止任务。</summary>
        private void FinishUnpublishedConnectionsAtExit()
        {
            if (!HasUnfinishedConnectionCleanup) return;
            foreach (UnfinishedConnectionCleanup cleanup in _unfinishedConnectionCleanups.ToArray())
            {
                try
                {
                    GetConnectionMonitorStopTask(cleanup).GetAwaiter().GetResult();
                    if (_unfinishedConnectionCleanups.Remove(cleanup)) CloseOwnedAlarmHistory(cleanup.History);
                }
                catch (Exception error)
                {
                    // 继续尝试其他独立对象，但原失败对象与历史仍保持未完成状态。
                    RecordConnectionSynchronizationError(_alarmContinuity, error);
                }
            }
        }

        /// <summary>判断当前字段是否仍归待收尾对象所有，避免退出路径重复停止或提前关闭历史。</summary>
        private bool IsUnfinishedConnectionRuntime(MachineRuntime runtime)
        {
            return runtime != null && HasUnfinishedConnectionCleanup
                && _unfinishedConnectionCleanups.Any(item => ReferenceEquals(item.Runtime, runtime));
        }

        /// <summary>未确认监控结束的历史不能交给已经关闭订阅的历史队列。</summary>
        private bool IsUnfinishedConnectionHistory(AlarmHistoryJournal history)
        {
            return history != null && HasUnfinishedConnectionCleanup
                && _unfinishedConnectionCleanups.Any(item => ReferenceEquals(item.History, history));
        }

        /// <summary>只撤下仍属于指定 Runtime 的页面；调用方已核对连接来源，不能替新运行时清空绑定。</summary>
        private void ClearConnectionPages(MachineRuntime expectedRuntime)
        {
            if (!ReferenceEquals(expectedRuntime, _machineRuntime)) return;
            _machineRuntime = null;
            Volatile.Write(ref _currentAlarmService, null);
            _machineSafetyStatus = null;
            _publishedCardConnection = null;
            _publishedLogicalIo = null;
            _manualMotionDebugPage = null;
            _flowStatusPage = null;
            _mainWindow.SetMachineRuntime(null);
            _deviceVariablePage.SetMachineRuntime(null);
            _mainWindow.SetFlowStatusPage(null);
            _mainWindow.SetManualMotionDebugPage(null);
            RefreshAxisMonitorPage(null);
            RefreshIoMonitorPage();
        }

        /// <summary>保留原始失败和跨连接报警门禁；旧收尾失败不能被后来成功连接掩盖。</summary>
        private void RecordConnectionSynchronizationError(AlarmContinuityStore continuity, Exception error)
        {
            Trace.TraceError("连接上下文初始化或收尾失败：" + error);
            string reason = "连接上下文初始化或收尾未完成：" + error.Message;
            continuity?.MarkUnconfirmed(reason);
            if (!ReferenceEquals(continuity, _alarmContinuity)) return;
            _machineRuntime?.MarkAlarmContinuityUnconfirmed(reason);
            MachineAlarmService alarms = Volatile.Read(ref _currentAlarmService);
            if (alarms != null) ReportUnconfirmedAlarm(alarms, reason);
        }
    }
}
