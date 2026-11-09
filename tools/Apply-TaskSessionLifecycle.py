"""本次迁移工具：只改已确认的页面职责，最终交付删除本文件。"""
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
BASE = '18ab5a9857ab3542c86ff02b835ca30da0530e89'
PAGE = 'src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs'


def replace_once(text, before, after):
    """锚点不唯一就拒绝迁移，不能猜测覆盖。"""
    if text.count(before) != 1:
        raise RuntimeError('源码锚点不唯一：' + before[:100])
    return text.replace(before, after, 1)


def replace_method(text, name, replacement):
    """按已检查的成员缩进替换完整方法，不移动其他职责。"""
    match = re.search(r'^        (?:private|internal).*?\b' + name + r'\(', text, re.M)
    if match is None:
        raise RuntimeError('找不到方法：' + name)
    end = text.find('\n        }', match.end())
    if end < 0:
        raise RuntimeError('找不到方法结束：' + name)
    return text[:match.start()] + replacement.rstrip() + text[end + len('\n        }'):]


LOAD = r'''        private async Task LoadTaskSessionAsync()
        {
            long requestedContext = _machineContextVersion;
            await RetryUnpublishedCleanupAsync().ConfigureAwait(true);
            if (requestedContext != _machineContextVersion)
                throw new InvalidOperationException("清理期间设备连接已变化，请在当前连接中重新读取任务。");
            if (_closingTaskSession || (_sessionCloseTask != null && !_sessionCloseTask.IsCompleted))
                throw new InvalidOperationException("任务会话正在关闭，请等待原会话收尾后重新读取。");
            if (!IsControllerConnected() || _logicalIo == null || _variables == null)
                throw new InvalidOperationException("请先连接控制器，再读取设备准备配置。");
            if (_session != null) return;

            var runtime = _runtime;
            var motionCards = _motionCards;
            var logicalIo = _logicalIo;
            var variables = _variables;
            Guid? sessionId = runtime.ConnectionSessionId;
            if (!runtime.IsConnectionSessionCurrent(sessionId))
                throw new InvalidOperationException("当前控制器连接会话未确认，请重新连接后读取任务。");
            long contextVersion = _machineContextVersion;
            // 此回调只核对页面仍属于原上下文；装载、准备安装和回滚由具体装载对象负责。
            void CheckContext()
            {
                if (contextVersion != _machineContextVersion || !ReferenceEquals(runtime, _runtime)
                    || !ReferenceEquals(motionCards, _motionCards) || !ReferenceEquals(logicalIo, _logicalIo)
                    || !ReferenceEquals(variables, _variables) || !IsControllerConnected()
                    || !runtime.IsConnectionSessionCurrent(sessionId))
                    throw new InvalidOperationException("设备连接在任务读取期间发生变化，本次读取已取消；请在当前连接中重新读取任务。");
            }
            var pending = new HostTaskSessionLoadOperation(runtime, _equipmentPreparations, CheckContext);
            // 必须在第一次后台读取前登记，关闭入口才能等到它归还实际设备控制权。
            _pendingTaskLoad = pending;
            try
            {
                await pending.LoadAsync(_store, _machineId, motionCards, logicalIo, variables, sessionId.Value).ConfigureAwait(true);
                pending.RequireCurrent();
            }
            catch (Exception loadError)
            {
                try { await RetryUnpublishedCleanupAsync(pending).ConfigureAwait(true); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("任务装载未发布且收尾未完成；请重试关闭或读取。", loadError, cleanupError);
                }
                throw;
            }

            TaskMonitorSession loaded = pending.Session;
            TaskMachineOperations operations = pending.Operations;
            _publishedPreparationVersion = pending.InstalledPreparationVersion;
            _session = loaded;
            _taskOperations = operations;
            _pendingTaskLoad = null;
            _catalog = loaded.TaskDevice.Catalog;
            _catalogFromLoadedSession = true;
            _monitor = new TaskMonitorView(loaded);
            _monitor.SetSessionValidityProvider(() => ReferenceEquals(CurrentPublishedTaskSession, loaded));
            _monitor.HideSourcePath();
            _taskSource = loaded.Source;
            _monitor.ReferenceRequested += Monitor_ReferenceRequested;
            _monitor.SetLanguage(_host.CurrentLanguage);
            _monitorContent.Content = _monitor;
            _stateTimer.Start();
            ClearError();
            SetSuccessText("已读取当前任务");
            _emptyState.Visibility = Visibility.Collapsed;
            RefreshAlarms();
            RefreshPageText();
            UpdateRunStatus();
            UpdateCommandAvailability();
            _host.RefreshMachineStateDisplay();
        }'''

RETRY = r'''        private async Task RetryUnpublishedCleanupAsync(HostTaskSessionLoadOperation pending = null)
        {
            // 装载失败方传入自己的对象，不能清理后来另一次装载的会话。
            pending = pending ?? _pendingTaskLoad;
            if (pending == null) return;
            await WaitForShutdownSettlementAsync(pending.RollbackAsync(), "未发布任务清理").ConfigureAwait(true);
            if (ReferenceEquals(_pendingTaskLoad, pending)) _pendingTaskLoad = null;
        }'''

STRUCTURE = r'''
    def test_task_load_has_a_tracked_owner_before_background_read(self):
        """装载状态由独立具体对象保存；页面只核对身份和挂接展示。"""
        text = (ROOT / 'src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs').read_text(encoding='utf-8-sig')
        body = method_body(text, 'LoadTaskSessionAsync')
        for forbidden in ['HostTaskSessionLoader.Load', 'TryConfigurePreparations', 'loaded.Publish(', '_retryUnpublishedCleanup']:
            self.assertNotIn(forbidden, body)
        self.assertLess(body.index('_pendingTaskLoad = pending;'), body.index('await pending.LoadAsync('))
        self.assertIn('RetryUnpublishedCleanupAsync(pending)', body)
        self.assertIn('_pendingTaskLoad != null', method_body(text, 'SetMachineContextAsync'))
        self.assertIn('await RetryUnpublishedCleanupAsync()', method_body(text, 'CloseTaskSessionCoreAsync'))

    def test_task_load_rollback_keeps_original_runtime_and_versions(self):
        """不引入控件、厂商或测试设备，回滚只操作原会话和本次准备代次。"""
        text = (ROOT / 'src/MotionControl.Host.Wpf/Tasks/HostTaskSessionLoadOperation.cs').read_text(encoding='utf-8-sig')
        self.assertIn('internal sealed class HostTaskSessionLoadOperation', text)
        for forbidden in ['using System.Windows', 'Dispatcher', 'ZAux_', 'FakeAxis', 'InMemoryTaskDevice', 'Activator.CreateInstance']:
            self.assertNotIn(forbidden, text)
        self.assertIn('await _loadFinished.Task', text)
        self.assertIn('out _installedPreparationVersion', text)
        self.assertIn('TryConfigurePreparations(_equipmentPreparations, _installedPreparationVersion', text)
        self.assertIn('Session.Machine.CloseAsync()', text)
        page = (ROOT / 'src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs').read_text(encoding='utf-8-sig')
        close = method_body(page, 'CloseTaskSessionCoreAsync')
        self.assertIn('var runtime = _runtime;', close)
        self.assertIn('runtime.TryConfigurePreparations(equipmentPreparations, publishedPreparationVersion', close)

'''


def apply():
    """实施页面与结构检查迁移，保留现有业务安全语义。"""
    path = ROOT / PAGE
    base = subprocess.check_output(['git', 'show', BASE + ':' + PAGE]).decode('utf-8-sig').replace('\r\n', '\n')
    text = path.read_text(encoding='utf-8-sig')
    if text != base:
        raise RuntimeError('页面已偏离核对过的基线，拒绝自动覆盖。')
    text = replace_once(text, 'if (_session != null || _retryUnpublishedCleanup != null)', 'if (_session != null || _pendingTaskLoad != null)')
    text = replace_method(text, 'LoadTaskSessionAsync', LOAD)
    text = replace_once(text, '''        /// <summary>未发布任务若无法撤回定义或释放控制权，保留清理工作供关闭或重新读取时重试。</summary>
        private Func<Task> _retryUnpublishedCleanup;
        /// <summary>当前未发布会话的清理任务，防止重复关闭同一执行器。</summary>
        private Task _unpublishedCleanupInProgress;''', '''        /// <summary>从读取开始即登记的任务装载操作；失败或超时保留它，直到原持权会话完成回滚。</summary>
        private HostTaskSessionLoadOperation _pendingTaskLoad;''')
    text = replace_method(text, 'RetryUnpublishedCleanupAsync', RETRY)
    start = text.index('        private async Task CloseTaskSessionCoreAsync(')
    end = text.index('\n        }', start) + len('\n        }')
    body = text[start:end]
    body = replace_once(body, '''            if (session == null) return;
            TaskMachineState state''', '''            if (session == null) return;
            var runtime = _runtime;
            var equipmentPreparations = _equipmentPreparations;
            long publishedPreparationVersion = _publishedPreparationVersion;
            TaskMachineState state''')
    body = body.replace('_runtime?.State', 'runtime?.State').replace('_runtime?.IsPreparing', 'runtime?.IsPreparing')
    body = replace_once(body, 'if (!ReferenceEquals(_session, session))', 'if (!ReferenceEquals(_session, session) || !ReferenceEquals(_runtime, runtime))')
    body = replace_once(body, '''if (_runtime != null && !_runtime.IsShutdownInProgress
                    && !_runtime.TryConfigurePreparations(_equipmentPreparations, _publishedPreparationVersion, out _))''', '''if (runtime != null && !runtime.IsShutdownInProgress
                    && !runtime.TryConfigurePreparations(equipmentPreparations, publishedPreparationVersion, out _))''')
    text = text[:start] + body + text[end:]
    if '_retryUnpublishedCleanup' in text or '_unpublishedCleanupInProgress' in text:
        raise RuntimeError('旧清理状态没有完全移除。')
    path.write_text(text, encoding='utf-8')
    path = ROOT / 'tools/Check-CommandBoundaries.py'
    text = path.read_text(encoding='utf-8')
    text = replace_once(text, "\n\nif __name__ == '__main__':", '\n' + STRUCTURE + "\nif __name__ == '__main__':")
    path.write_text(text, encoding='utf-8')
    subprocess.run([sys.executable, str(path)], check=True)


if __name__ == '__main__':
    apply()
