# -*- coding: utf-8 -*-
"""隔离 Windows 工作区的精确迁移；验证后删除，不是生产依赖。"""
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def replace_once(text, before, after):
    """只修改唯一且完全匹配的原代码，避免覆盖其他会话改动。"""
    if text.count(before) != 1:
        raise RuntimeError('迁移原文不唯一：' + before[:120])
    return text.replace(before, after)


launcher = ROOT / 'src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs'
text = launcher.read_text(encoding='utf-8-sig')
text = replace_once(text,
    '        internal bool HasTaskSession { get { return _session != null; } }',
    '''        internal bool HasTaskSession { get { return _session != null; } }

        /// <summary>供退出与报警交接判断：包含在途装载、已发布会话和仍未结束的关闭。</summary>
        internal bool HasUnsettledTaskSession
        {
            get
            {
                return _session != null || _pendingTaskLoad != null
                    || (_sessionCloseTask != null && !_sessionCloseTask.IsCompleted);
            }
        }''')
text = replace_once(text,
    '''            if (_session == null) return;
            if (_commandInProgress) throw new InvalidOperationException("任务命令正在执行，请等待结束后重试关闭。");''',
    '''            if (_session == null || _closingTaskSession)
            {
                // 尚未发布也可能已经持权；关闭失败后沿用原模式重试，不能重入已撤销的 Abort 入口。
                await CloseTaskSessionAsync(_sessionClosePreservesBlocked).ConfigureAwait(true);
                return;
            }
            if (_commandInProgress) throw new InvalidOperationException("任务命令正在执行，请等待结束后重试关闭。");''')
text = replace_once(text,
    '        /// <summary>应用退出时沿用任务页的终止及收尾，不跳过设备控制权和资源检查。</summary>',
    '        /// <summary>应用退出先处理未发布或关闭中的会话；正常任务才执行终止，不跳过持权与资源检查。</summary>')
launcher.write_text(text, encoding='utf-8')

app = ROOT / 'src/MotionControl.Host.Wpf/App.xaml.cs'
text = app.read_text(encoding='utf-8-sig')
start = text.index('                _shutdownDisconnectInProgress = true;\n                bool shutdownBegun = false;')
end = text.index('                // 等待期间操作员可能编辑配置；重新检查未保存内容。', start)
text = text[:start] + r'''                _shutdownDisconnectInProgress = true;
                var runtime = _machineRuntime;
                var connection = _cardConnection;
                var tasks = _taskMonitorLauncher;
                var recovery = _machineRecoveryCoordinator;
                bool preserveRecoveryFiles = _equipmentProjectRestartRequired;
                // 只核对 App 组合对象归属；设备停止、输出核查和保存由具体退出对象负责。
                void CheckShutdownContext()
                {
                    if (!ReferenceEquals(runtime, _machineRuntime) || !ReferenceEquals(connection, _cardConnection)
                        || !ReferenceEquals(tasks, _taskMonitorLauncher) || !ReferenceEquals(recovery, _machineRecoveryCoordinator)
                        || preserveRecoveryFiles != _equipmentProjectRestartRequired)
                        throw new InvalidOperationException("退出期间设备上下文已变化，不能用旧退出结果操作当前设备。");
                }
                HostShutdownOperation shutdown = null;
                try
                {
                    shutdown = new HostShutdownOperation(runtime, connection, tasks, recovery,
                        preserveRecoveryFiles, CheckShutdownContext);
                    await shutdown.StopAsync();
                    await AcquireShutdownProjectGateAsync();
                    await shutdown.VerifyAndSaveAsync();
                    await DisconnectMachineBeforeCardAsync(CancellationToken.None);
                    CheckShutdownContext();
                    if (connection != null) await connection.DisconnectAsync(CancellationToken.None);
                    await SynchronizeCardConnectionAsync();
                }
                catch (Exception error)
                {
                    // 仅同步本次连接仍为当前连接时的断卡结果，旧退出不得替新连接清理状态。
                    if (ReferenceEquals(connection, _cardConnection) && connection != null && !connection.IsConnected)
                    {
                        try { await SynchronizeCardConnectionAsync(); }
                        catch (Exception syncError) { Trace.TraceError("关窗断卡后同步失败：" + syncError); }
                    }
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

''' + text[end:]
text = replace_once(text,
    '(_taskMonitorLauncher.HasTaskSession || _taskMonitorLauncher.IsCommandInProgress)',
    '(_taskMonitorLauncher.HasUnsettledTaskSession || _taskMonitorLauncher.IsCommandInProgress)')
text = replace_once(text,
    'if (_taskMonitorLauncher != null && _taskMonitorLauncher.HasTaskSession)\n                Trace.TraceWarning',
    'if (_taskMonitorLauncher != null && _taskMonitorLauncher.HasUnsettledTaskSession)\n                Trace.TraceWarning')
text = replace_once(text,
    '(!_taskMonitorLauncher.HasTaskSession && !_taskMonitorLauncher.IsCommandInProgress)',
    '(!_taskMonitorLauncher.HasUnsettledTaskSession && !_taskMonitorLauncher.IsCommandInProgress)')
text = replace_once(text,
    '''            bool defaultProjectNeverConnected = _usingSavedSimulationProject && !_savedControllerConnectionAttempted
                && _machineRuntime == null''',
    '''            bool defaultProjectNeverConnected = _usingSavedSimulationProject && !_savedControllerConnectionAttempted
                && (_taskMonitorLauncher == null || (!_taskMonitorLauncher.HasUnsettledTaskSession && !_taskMonitorLauncher.IsCommandInProgress))
                && _machineRuntime == null''')
text = replace_once(text,
    '''            MachineRuntime runtime = _machineRuntime;
            if (_taskMonitorLauncher != null)
                await _taskMonitorLauncher.CloseTaskSessionAsync().ConfigureAwait(true);
            if (runtime != null && runtime.State != MachineRunState.Disconnected)
            {
                RetainUnconfirmedAlarms(runtime);
                await runtime.DisconnectAsync(cancellationToken).ConfigureAwait(true);
                _alarmHandoffSettled = runtime.IsAlarmHandoffSettled && _trackedAlarmSources;''',
    '''            MachineRuntime runtime = _machineRuntime;
            var tasks = _taskMonitorLauncher;
            var connection = _cardConnection;
            Guid? session = runtime?.ConnectionSessionId;
            if (tasks != null)
                await tasks.CloseTaskSessionAsync().ConfigureAwait(true);
            if (!ReferenceEquals(runtime, _machineRuntime) || !ReferenceEquals(tasks, _taskMonitorLauncher)
                || !ReferenceEquals(connection, _cardConnection) || session != runtime?.ConnectionSessionId)
                throw new InvalidOperationException("任务收尾期间连接上下文已变化，未断开新的设备。");
            if (runtime != null && runtime.State != MachineRunState.Disconnected)
            {
                RetainUnconfirmedAlarms(runtime);
                await runtime.DisconnectAsync(cancellationToken).ConfigureAwait(true);
                if (!ReferenceEquals(runtime, _machineRuntime) || !ReferenceEquals(connection, _cardConnection))
                    throw new InvalidOperationException("原设备已请求断卡，但当前连接上下文已变化，未提交旧交接结果。");
                _alarmHandoffSettled = runtime.IsAlarmHandoffSettled && _trackedAlarmSources;''')
app.write_text(text, encoding='utf-8')

checks = ROOT / 'tools/Check-CommandBoundaries.py'
text = checks.read_text(encoding='utf-8-sig')
text = text.replace('(?:private|internal)', '(?:private|internal|protected)')
marker = "\n\nif __name__ == '__main__':"
extra = '''
    def test_window_close_delegates_device_sequence(self):
        """关窗事件保留提示和工程门，不再编排设备停止、输出核验或撤回新 Runtime。"""
        text = (ROOT / 'src/MotionControl.Host.Wpf/App.xaml.cs').read_text(encoding='utf-8-sig')
        body = method_body(text, 'MainWindow_Closing')
        for forbidden in ['BeginShutdownAsync(', 'StopPreparationAsync(', 'StopStationFlowsForShutdownAsync(', 'VerifyShutdownOutputsAsync(', 'TryCancelShutdownAsync(']:
            self.assertNotIn(forbidden, body)
        for entry in ['shutdown.StopAsync()', 'shutdown.VerifyAndSaveAsync()', 'shutdown.TryCancelAsync()']:
            self.assertIn(entry, body)

    def test_shutdown_operation_owns_original_context(self):
        """退出协调使用具体依赖与原连接，不访问界面控件或厂商 SDK。"""
        text = (ROOT / 'src/MotionControl.Host.Wpf/HostShutdownOperation.cs').read_text(encoding='utf-8-sig')
        for member in ['readonly MachineRuntime _runtime', 'readonly IMotionCardHostConnection _connection', 'readonly Guid? _connectionSession', 'RequireOriginalContext', 'RequireConnectedContext']:
            self.assertIn(member, text)
        for forbidden in ['System.Windows', 'MessageBox', 'ZAux_', 'FakeAxis', 'InMemoryTaskDevice', 'Activator.CreateInstance']:
            self.assertNotIn(forbidden, text)
        self.assertIn('_runtime.TryCancelShutdownAsync()', method_body(text, 'TryCancelAsync'))

    def test_app_exit_tracks_unpublished_and_closing_tasks(self):
        """未发布装载和失败关闭均参与关窗与报警交接，专用退出复用原关闭模式。"""
        app = (ROOT / 'src/MotionControl.Host.Wpf/App.xaml.cs').read_text(encoding='utf-8-sig')
        self.assertIn('HasUnsettledTaskSession', method_body(app, 'MainWindow_Closing'))
        self.assertIn('HasUnsettledTaskSession', method_body(app, 'OnExit'))
        page = (ROOT / 'src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs').read_text(encoding='utf-8-sig')
        body = method_body(page, 'StopTaskSessionForShutdownAsync')
        self.assertIn('await CloseTaskSessionAsync(_sessionClosePreservesBlocked)', body)
        self.assertNotIn('if (_session == null) return;', body)
'''
text = replace_once(text, marker, '\n' + extra + marker)
checks.write_text(text, encoding='utf-8')
subprocess.run([sys.executable, str(checks)], check=True)
