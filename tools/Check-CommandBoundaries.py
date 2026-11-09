# -*- coding: utf-8 -*-
"""结构回归：页面只提交命令，设备协调不得引用 WPF 或厂商 SDK。不是运动行为测试。"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


def method_body(text, name):
    """按项目成员缩进取得方法体；用于可审阅的结构约束，不伪装成 C# 语法解析器。"""
    start = re.search(r'^        (?:private|internal).*?\b' + name + r'\(', text, re.M)
    if start is None:
        raise AssertionError('缺少明确操作入口：' + name)
    end = text.find('\n        }', start.end())
    if end < 0:
        raise AssertionError('方法边界不可确认：' + name)
    return text[start.start():end]


class CommandBoundaryChecks(unittest.TestCase):
    """维护页面、应用协调、运行时、厂商适配器之间的职责边界。"""
    def test_coordinator_is_a_concrete_non_ui_type(self):
        path = ROOT / 'src/MotionControl.TaskEngine.MachineDevice/TaskMachineOperations.cs'
        self.assertTrue(path.exists(), '缺少独立任务操作协调类，页面仍承担设备操作编排')
        text = path.read_text(encoding='utf-8-sig')
        self.assertIn('public sealed class TaskMachineOperations', text)
        for forbidden in ['System.Windows', 'ZAux_', 'VirtualMotionCard', 'InMemoryTaskDevice', 'FakeAxis', 'Activator.CreateInstance']:
            self.assertNotIn(forbidden, text)
        for member in ['ResetAsync', 'StartAsync', 'PauseAsync', 'AbortAsync', 'Invalidate', 'EnsureCurrent']:
            self.assertRegex(text, r'\b' + member + r'\(')

    def test_page_does_not_orchestrate_run_commands(self):
        text = (ROOT / 'src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs').read_text(encoding='utf-8-sig')
        for method in ['ResetTaskCoreAsync', 'StartTaskCoreAsync', 'PauseTaskCoreAsync', 'StopTaskSessionCoreAsync']:
            body = method_body(text, method)
            for forbidden in ['BeginTaskEngineRunAsync', 'FinishTaskEngineRunAsync', 'ValidateResetPrerequisitesAsync', '.Machine.Start(', '.Machine.PauseAsync(', '.Machine.AbortAsync(']:
                self.assertNotIn(forbidden, body, method + ' 仍直接协调设备状态')
            self.assertIn('operations.', body)

    def test_old_context_is_explicitly_invalidated(self):
        text = (ROOT / 'src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs').read_text(encoding='utf-8-sig')
        for method in ['SetMachineContextAsync', 'MarkControllerConnectionLost', 'CloseTaskSessionCoreAsync']:
            self.assertIn('_taskOperations?.Invalidate()', method_body(text, method))
        self.assertIn('_taskOperations = operations;', text)
        self.assertIn('_taskOperations = null;', text)


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


if __name__ == '__main__':
    unittest.main(verbosity=2)
