# -*- coding: utf-8 -*-
"""仅用于本次独立分支的确定性迁移；成功验证后删除自身，不进入常规产品代码。"""
from pathlib import Path
import os
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
BASE = '14b775016eb37abf402b4bc9854bd3a458c4423a'
BRANCH = 'codex/architecture-clarity-20261110'
LAUNCHER = 'src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs'
OPERATIONS_PATH = 'src/MotionControl.TaskEngine.MachineDevice/TaskMachineOperations.cs'
CHECK_PATH = 'tools/Check-CommandBoundaries.py'
TEST_PATH = 'tools/MotionControl.VirtualAdapter.Checks/Program.Commands.cs'

CHECKER = r'''# -*- coding: utf-8 -*-
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


if __name__ == '__main__':
    unittest.main(verbosity=2)
'''

OPERATIONS = r'''using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.TaskEngine.MachineDevice
{
    /// <summary>
    /// 协调一个已绑定任务会话的复位、启动、暂停和终止。
    /// 不显示界面、不读配置、不实现另一套状态机，也不直接调用任何厂商 API。
    /// </summary>
    public sealed class TaskMachineOperations
    {
        /// <summary>本会话唯一的整机运行时。</summary>
        private readonly MachineRuntime _runtime;
        /// <summary>负责工步及动作收尾的原任务控制器。</summary>
        private readonly TaskMachineController _machine;
        /// <summary>实际持有原连接控制权的任务设备。</summary>
        private readonly MachineTaskDevice _device;
        /// <summary>创建操作入口时冻结的连接身份，不随重连更新。</summary>
        private readonly Guid _connection;
        /// <summary>同一操作入口的命令互斥；页面的忙碌标志只用于显示。</summary>
        private readonly SemaphoreSlim _commandGate = new SemaphoreSlim(1, 1);
        /// <summary>页面上下文撤销后永久置为 1；不能复活旧操作入口。</summary>
        private int _invalidated;

        /// <summary>只能由已经持权的实际任务设备创建，避免混搭不同会话。</summary>
        internal TaskMachineOperations(MachineRuntime runtime, MachineTaskDevice device,
            TaskMachineController machine, Guid connection)
        {
            _runtime = runtime;
            _device = device;
            _machine = machine;
            _connection = connection;
        }

        /// <summary>撤销后禁止复位和启动；此方法自身不发命令，也不表示设备已经停止。</summary>
        public void Invalidate()
        {
            Interlocked.Exchange(ref _invalidated, 1);
        }

        /// <summary>核对原页面许可、任务持权者及实际连接；UI 显示操作成功前也必须调用。</summary>
        public void EnsureCurrent()
        {
            if (Volatile.Read(ref _invalidated) != 0)
                throw new InvalidOperationException("任务操作上下文已撤销，请确认停稳后重新读取当前任务。");
            _device.RequireOperationOwner(_machine);
        }

        /// <summary>按原顺序校验任务条件、复位 Runtime、复位工步；每个异步阶段返回后复核原身份。</summary>
        public async Task ResetAsync(bool confirmedStoppedProduction = false)
        {
            EnterCommand();
            try
            {
                EnsureCurrent();
                await _machine.ValidateResetPrerequisitesAsync().ConfigureAwait(false);
                EnsureCurrent();
                if (_runtime.State != MachineRunState.Ready)
                    await _runtime.ResetAsync(CancellationToken.None, confirmedStoppedProduction).ConfigureAwait(false);
                EnsureCurrent();
                await _machine.ResetAsync().ConfigureAwait(false);
                EnsureCurrent();
            }
            finally { _commandGate.Release(); }
        }

        /// <summary>先建立原连接的整机安全许可，再启动任务；后续失败须尝试暂停原任务并锁存故障。</summary>
        public async Task StartAsync()
        {
            EnterCommand();
            try
            {
                EnsureCurrent();
                await _runtime.BeginTaskEngineRunAsync(_connection, _device, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    EnsureCurrent();
                    _machine.Start();
                    _runtime.ConfirmTaskEngineProductionStarted();
                    EnsureCurrent();
                }
                catch (Exception startError)
                {
                    // 任务启动可能已部分发生，不能只把 Runtime 改成报警而留下工步继续执行。
                    var errors = new List<Exception> { startError };
                    try { await _machine.PauseAsync().ConfigureAwait(false); }
                    catch (Exception stopError) { errors.Add(stopError); }
                    try { await _runtime.FinishTaskEngineRunAsync(_connection, _device, true, startError).ConfigureAwait(false); }
                    catch (Exception finishError) { errors.Add(finishError); }
                    if (errors.Count > 1)
                        throw new AggregateException("任务启动失败，且原会话停止或状态收尾未确认。", errors);
                    throw;
                }
            }
            finally { _commandGate.Release(); }
        }

        /// <summary>即使上下文已失效也先尝试暂停原任务；只有原身份和停稳收尾均可信时才能报告成功。</summary>
        public async Task PauseAsync()
        {
            EnterCommand();
            try
            {
                try
                {
                    await _machine.PauseAsync().ConfigureAwait(false);
                    EnsureCurrent();
                    if (!_machine.IsAutomaticPauseConfirmed)
                        throw new InvalidOperationException("任务未确认暂停及全部动作收尾。");
                    await _runtime.FinishTaskEngineRunAsync(_connection, _device, false, null).ConfigureAwait(false);
                    EnsureCurrent();
                }
                catch (Exception pauseError)
                {
                    await RecordFailureAsync("任务暂停", pauseError).ConfigureAwait(false);
                    throw;
                }
            }
            finally { _commandGate.Release(); }
        }

        /// <summary>终止原工步并等待收尾，不替页面销毁会话或清除资源核查证据。</summary>
        public async Task AbortAsync()
        {
            EnterCommand();
            try
            {
                try
                {
                    await _machine.AbortAsync().ConfigureAwait(false);
                    EnsureCurrent();
                    if (_runtime.State == MachineRunState.Running || _runtime.State == MachineRunState.Pausing)
                        await _runtime.FinishTaskEngineRunAsync(_connection, _device, false, null).ConfigureAwait(false);
                    EnsureCurrent();
                }
                catch (Exception abortError)
                {
                    await RecordFailureAsync("任务终止", abortError).ConfigureAwait(false);
                    throw;
                }
            }
            finally { _commandGate.Release(); }
        }

        /// <summary>并发操作立即明确拒绝，不把后到的启动排队到另一个设备状态之后。</summary>
        private void EnterCommand()
        {
            if (!_commandGate.Wait(0))
                throw new InvalidOperationException("任务命令正在执行，请等待原操作及收尾完成。");
        }

        /// <summary>只向原连接、原持权设备提交故障；提交也失败时保留两个真实异常。</summary>
        private async Task RecordFailureAsync(string operation, Exception originalError)
        {
            try { await _runtime.FinishTaskEngineRunAsync(_connection, _device, true, originalError).ConfigureAwait(false); }
            catch (Exception finishError)
            {
                throw new AggregateException(operation + "失败，原连接的故障状态也未能确认。", originalError, finishError);
            }
        }
    }

    /// <summary>由实际持权设备创建任务操作入口，不增加通用工厂或反射装配。</summary>
    public sealed partial class MachineTaskDevice
    {
        /// <summary>使用本设备已经绑定的 Runtime 和任务控制器创建操作入口，不重新连接或启动设备。</summary>
        public TaskMachineOperations CreateOperations(TaskMachineController controller)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            RequireOperationOwner(controller);
            return new TaskMachineOperations(_runtime, this, controller, _expectedSession.Value);
        }

        /// <summary>锁内只检查本地持权者；锁外复核 Runtime 绑定，避免跨组件锁顺序反转。</summary>
        internal void RequireOperationOwner(TaskMachineController controller)
        {
            if (!_expectedSession.HasValue)
                throw new InvalidOperationException("正式任务操作必须绑定具体控制器连接，不能使用未绑定设备。");
            lock (_sync)
            {
                if (_changingOwner || _closing || !ReferenceEquals(_machineOwner, controller))
                    throw new InvalidOperationException("任务控制器不再持有本设备的稳定控制权。");
            }
            _runtime.EnsureTaskDeviceBinding(_expectedSession, this);
        }
    }
}
'''

TESTS = r'''using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.Virtual;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Safety;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.MachineDevice;

namespace MotionControl.VirtualAdapter.Checks
{
    /// <summary>实际软件控制器上的任务操作协调回归，不创建轴替身。</summary>
    internal static partial class Program
    {
        /// <summary>连接实际软件控制器，检查正常操作、并发拒绝、旧会话隔离及异步启动撤销。</summary>
        private static async Task CheckTaskCommandOperationsAsync(int port)
        {
            var axisDefinition = new AxisHardwareDefinition
            {
                Name = "Command_X", ControllerId = "CommandCard", AxisNumber = 1,
                SoftLimitEnabled = false, PositionTolerance = 0.01
            };
            var input = new DigitalIoHardwareDefinition
            {
                Name = "CommandEmergencyStop", ControllerId = "CommandCard", Channel = 43, ActiveHigh = true
            };
            var card = new VirtualMotionCard("CommandCard", new[] { axisDefinition },
                new[] { input }, new DigitalIoHardwareDefinition[0], port, Path.Combine(DiagnosticsRoot, "commands"));
            var group = new MotionCardGroup(new IMotionCard[] { card });
            var alarms = new MachineAlarmService();
            var runtime = new MachineRuntime(group, new IStationFlow[0], alarms,
                new LogicalIoEmergencyStopStatus(card, "CommandEmergencyStop"));
            TaskMachineController machine = null;
            var errors = new List<Exception>();
            try
            {
                await runtime.ConnectAsync(CancellationToken.None);
                IAxis axis = group.GetAxis("Command_X");
                await axis.EnableServoAsync(CancellationToken.None);
                await axis.HomeAsync(CancellationToken.None);
                var device = CreateCommandDevice(group, card, runtime);
                machine = CreateCommandMachine(device, alarms);
                var operations = device.CreateOperations(machine);

                // 使用控制器已有的单次延迟反馈，不用线程替身模拟运动卡。
                RequireCommandFault(port, "delayReply", 300);
                Task resetting = operations.ResetAsync();
                await RequireCommandRejectedAsync(() => operations.StartAsync(), "并发启动");
                await resetting;
                RequireCommandState(runtime.State == MachineRunState.Ready
                    && machine.GetSnapshot().State == TaskMachineState.Ready, "复位未同时使两个执行层就绪");

                await operations.StartAsync();
                RequireCommandState(runtime.State == MachineRunState.Running
                    && machine.GetSnapshot().State == TaskMachineState.Running, "启动没有经过两个执行层");
                await operations.PauseAsync();
                RequireCommandState(runtime.State == MachineRunState.Paused && machine.IsAutomaticPauseConfirmed,
                    "暂停未确认任务收尾及 Runtime 状态");
                await operations.AbortAsync();
                RequireCommandState(machine.GetSnapshot().State == TaskMachineState.Stopped, "终止没有结束原任务");
                await machine.CloseAsync();
                machine = null;
                await runtime.DisconnectAsync(CancellationToken.None);

                // 同一 Runtime 重连后，旧操作实例绝不能复位新设备。
                await runtime.ConnectAsync(CancellationToken.None);
                MachineRunState beforeOldCommand = runtime.State;
                await RequireCommandRejectedAsync(() => operations.ResetAsync(), "旧会话复位");
                RequireCommandState(runtime.State == beforeOldCommand, "旧操作改写了新连接状态");
                device = CreateCommandDevice(group, card, runtime);
                machine = CreateCommandMachine(device, alarms);
                var current = device.CreateOperations(machine);
                await current.ResetAsync();

                // Runtime 建立许可期间撤销页面上下文：迟到结果必须拒绝，工步不能继续运行。
                RequireCommandFault(port, "delayReply", 300);
                Task starting = current.StartAsync();
                current.Invalidate();
                await RequireCommandRejectedAsync(() => starting, "异步启动完成前撤销上下文");
                RequireCommandState(machine.GetSnapshot().State != TaskMachineState.Running
                    && runtime.State != MachineRunState.Running, "撤销的启动仍保留运行许可");
                await RequireCommandRejectedAsync(() => current.StartAsync(), "撤销后重新启动");
                // 停止优先：撤销不能让原会话失去尝试停步的途径，但不得返回假成功。
                await RequireCommandRejectedAsync(() => current.AbortAsync(), "撤销后的终止结果提交");
                RequireCommandState(machine.GetSnapshot().State == TaskMachineState.Stopped,
                    "撤销后没有先终止原工步");
                await machine.CloseAsync();
                machine = null;
                await axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None);
                RequireCommandState((await axis.ReadStatusAsync(CancellationToken.None)).HasStoppedMotionFeedback(),
                    "结束时实际软件控制器没有确认停稳");
                await runtime.DisconnectAsync(CancellationToken.None);
            }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                if (machine != null)
                {
                    try { await machine.AbortAsync(); await machine.CloseAsync(); }
                    catch (Exception error) { errors.Add(error); }
                }
                if (card.IsConnected)
                {
                    try
                    {
                        await group.GetAxis("Command_X").StopAsync(AxisStopMode.Controlled, CancellationToken.None);
                        await runtime.DisconnectAsync(CancellationToken.None);
                    }
                    catch (Exception error) { errors.Add(error); }
                }
            }
            if (errors.Count != 0) throw new AggregateException("任务操作协调 API 检查或退出收尾失败。", errors);
            Console.WriteLine("[通过] 任务操作协调：复位/启动/暂停/终止、并发拒绝、旧连接拒绝、异步撤销与停止优先");
        }

        /// <summary>使用实际卡、逻辑 IO、空变量表和当前连接创建正式任务设备。</summary>
        private static MachineTaskDevice CreateCommandDevice(MotionCardGroup group, VirtualMotionCard card, MachineRuntime runtime)
        {
            var catalog = new TaskResourceCatalog(new[] { new TaskAxisInfo("Command_X", "CommandCard", "mm", -100, 100) },
                new[] { "CommandEmergencyStop" }, new string[0], new Dictionary<string, DeviceVariableType>(), new TaskTeachPoint[0]);
            return new MachineTaskDevice(catalog, group, card,
                new DeviceVariableStore(new DeviceVariableDefinition[0]), runtime, runtime.ConnectionSessionId.Value);
        }

        /// <summary>建立保持等待的工步，专门观察协调状态；实际轴命令由现有单/双控制器测试覆盖。</summary>
        private static TaskMachineController CreateCommandMachine(MachineTaskDevice device, MachineAlarmService alarms)
        {
            var definition = new TaskDefinition
            {
                TaskId = "CommandCycle", Name = "操作协调检查",
                Root = new SequenceNode { Id = "root", Name = "流程", Children = new List<TaskNode>
                {
                    new DelayNode { Id = "wait", Name = "等待操作员暂停", Milliseconds = 30000, TimeoutMs = 60000 }
                } }
            };
            var configuration = new TaskMachineConfiguration
            {
                MachineId = "CommandMachine", Name = "操作协调设备",
                Stations = new List<StationTaskConfiguration>
                {
                    new StationTaskConfiguration { StationId = "station", Name = "验证工位", TaskId = definition.TaskId, Enabled = true, Mode = StationTaskMode.Once }
                }
            };
            return new TaskMachineController(configuration, new[] { definition }, device, alarms);
        }

        /// <summary>只向本轮隔离软件控制器注入一次 readAxis 回读延迟。</summary>
        private static void RequireCommandFault(int port, string mode, int delay)
        {
            string response = SendRaw(port, "{\"command\":\"setCommandFault\",\"targetCommand\":\"readAxis\",\"targetIndex\":1,\"mode\":\""
                + mode + "\",\"delayMs\":" + delay + "}");
            RequireCommandState(response.Contains("\"ok\":true"), "控制器未接受延迟反馈配置");
        }

        /// <summary>操作确实被明确拒绝才算成功，不能吞掉不相关异常。</summary>
        private static async Task RequireCommandRejectedAsync(Func<Task> command, string scenario)
        {
            bool rejected = false;
            try { await command(); }
            catch (InvalidOperationException) { rejected = true; }
            RequireCommandState(rejected, scenario + "未被拒绝");
        }

        /// <summary>明确报告失败场景，不把未知反馈当作成功。</summary>
        private static void RequireCommandState(bool success, string message)
        {
            if (!success) throw new InvalidOperationException(message);
        }
    }
}
'''


def git(*args):
    """执行本工作区 Git 命令，失败立即停止，不改写其他分支。"""
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True, encoding='utf-8').strip()


def write(path, content, bom=False):
    """按原文件的 BOM 约定写入 UTF-8，统一使用 LF，避免无关换行差异。"""
    target = ROOT / path
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open('w', encoding='utf-8-sig' if bom else 'utf-8', newline='\n') as stream:
        stream.write(content)


def replace_once(text, old, new):
    """迁移只接受唯一命中；不猜测已变化的源代码。"""
    count = text.count(old)
    if count != 1:
        raise RuntimeError('迁移锚点数量不是 1：' + str(count) + ' / ' + old[:100])
    return text.replace(old, new, 1)


def replace_method(text, name, replacement):
    """成员结束括号必须位于类成员缩进；方法内容不使用任意全局正则替换。"""
    import re
    match = re.search(r'^        (?:private|internal).*?\b' + name + r'\(', text, re.M)
    if match is None:
        raise RuntimeError('缺少原方法：' + name)
    end = text.find('\n        }', match.end())
    if end < 0:
        raise RuntimeError('无法定位原方法结尾：' + name)
    return text[:match.start()] + replacement + text[end + len('\n        }'):]


def apply():
    """先运行结构红灯检查，再机械迁移，最后运行绿灯与保护文件核对。"""
    if git('branch', '--show-current') != BRANCH:
        raise RuntimeError('只允许在明确的独立重构分支运行')
    if git('rev-parse', 'HEAD:' + LAUNCHER) != '50990f52bfddb5cb21512bee410793c050f12b71':
        raise RuntimeError('页面源码已改变，停止自动迁移，不能覆盖其他会话改动')
    write(CHECK_PATH, CHECKER)
    red = subprocess.run([sys.executable, str(ROOT / CHECK_PATH)], cwd=ROOT)
    if red.returncode == 0:
        raise RuntimeError('原源码未触发结构回归失败，检查缺乏有效性')
    print('[RED] 原页面编排职责与缺失协调类已被检查检出', flush=True)

    text = (ROOT / LAUNCHER).read_text(encoding='utf-8-sig')
    text = replace_once(text, 'using MotionControl.TaskEngine.Machine;\n', 'using MotionControl.TaskEngine.Machine;\nusing MotionControl.TaskEngine.MachineDevice;\n')
    text = replace_once(text, '        private TaskMonitorSession _session;\n', '        private TaskMonitorSession _session;\n        /// <summary>当前已发布任务的设备操作入口；撤销后不复用到新连接。</summary>\n        private TaskMachineOperations _taskOperations;\n')
    if text.count('            _machineContextVersion++;') != 2:
        raise RuntimeError('上下文失效入口数量已变化，需重新审查')
    text = text.replace('            _machineContextVersion++;', '            _machineContextVersion++;\n            _taskOperations?.Invalidate();')
    text = replace_once(text, '                long installedVersion = 0;\n', '                long installedVersion = 0;\n                TaskMachineOperations operations = null;\n')
    text = replace_once(text, '                    loaded.Publish(_machineId);', '                    loaded.Publish(_machineId);\n                    operations = ((MachineTaskDevice)loaded.TaskDevice).CreateOperations(loaded.Machine);')
    text = replace_once(text, '                _session = loaded;', '                _session = loaded;\n                _taskOperations = operations;')
    text = replace_once(text, '            _closingTaskSession = true;', '            _closingTaskSession = true;\n            _taskOperations?.Invalidate();')
    text = replace_once(text, '            _session = null;', '            _session = null;\n            _taskOperations = null;')
    text = replace_method(text, 'ResetTaskCoreAsync', '''        private async Task ResetTaskCoreAsync(bool confirmedStoppedProduction)
        {
            var operations = RequireTaskOperations();
            await operations.ResetAsync(confirmedStoppedProduction).ConfigureAwait(true);
            operations.EnsureCurrent();
        }''')
    text = replace_method(text, 'StartTaskCoreAsync', '''        private async Task StartTaskCoreAsync()
        {
            var operations = RequireTaskOperations();
            await operations.StartAsync().ConfigureAwait(true);
            operations.EnsureCurrent();
            SetSuccessText("任务执行已启动");
        }''')
    text = replace_method(text, 'PauseTaskCoreAsync', '''        private async Task PauseTaskCoreAsync()
        {
            var operations = RequireTaskOperations();
            await operations.PauseAsync().ConfigureAwait(true);
            operations.EnsureCurrent();
            SetSuccessText("任务执行已暂停");
        }''')
    text = replace_method(text, 'StopTaskSessionCoreAsync', '''        private async Task StopTaskSessionCoreAsync(bool allowBlockedHandoff = false)
        {
            TaskMonitorSession session = _session;
            if (session == null) return;
            var operations = RequireTaskOperations();
            await WaitForShutdownSettlementAsync(operations.AbortAsync(), "任务终止").ConfigureAwait(true);
            operations.EnsureCurrent();
            bool preserveBlocked = allowBlockedHandoff && session.TaskDevice.ReadResources()
                .Any(resource => resource.State == TaskResourceState.Blocked);
            await CloseTaskSessionAsync(preserveBlocked).ConfigureAwait(true);
        }''')
    text = replace_method(text, 'EnsureCurrentTaskCommandContext', '''        private TaskMachineOperations RequireTaskOperations()
        {
            RequireTaskSession();
            if (_taskOperations == null)
                throw new InvalidOperationException("当前任务尚未建立设备操作入口，请重新读取任务。");
            // 不在这里拒绝失效实例：Pause/Abort 仍需优先尝试停止其原任务。
            return _taskOperations;
        }''')
    text = text.replace('核对异步命令仍属于发起时的设备、连接和任务；失败时不操作新的会话。', '取得当前已发布任务的操作入口，不在页面内编排设备运行状态。')
    write(LAUNCHER, text, bom=True)
    write(OPERATIONS_PATH, OPERATIONS)
    write(TEST_PATH, TESTS)

    program_path = 'tools/MotionControl.VirtualAdapter.Checks/Program.cs'
    program = (ROOT / program_path).read_text(encoding='utf-8-sig')
    program = replace_once(program, '                    CheckRuntimeTaskSession(port);', '                    CheckRuntimeTaskSession(port);\n                    CheckTaskCommandOperationsAsync(port).GetAwaiter().GetResult();')
    write(program_path, program)
    project_path = 'tools/MotionControl.VirtualAdapter.Checks/MotionControl.VirtualAdapter.Checks.csproj'
    project = (ROOT / project_path).read_text(encoding='utf-8-sig')
    project = replace_once(project, '  </ItemGroup>', '    <ProjectReference Include="..\\..\\src\\MotionControl.TaskEngine.MachineDevice\\MotionControl.TaskEngine.MachineDevice.csproj" />\n  </ItemGroup>')
    write(project_path, project)
    verify_path = 'tools/Verify-V18.ps1'
    verify = (ROOT / verify_path).read_text(encoding='utf-8-sig')
    marker = '# 先构建正式主程序，发现 XAML、页面事件和跨程序集调用的编译错误。'
    verify = replace_once(verify, marker, 'python tools/Check-CommandBoundaries.py\nif ($LASTEXITCODE -ne 0) { throw "任务命令职责边界检查失败。" }\n\n' + marker)
    write(verify_path, verify)
    subprocess.run([sys.executable, str(ROOT / CHECK_PATH)], cwd=ROOT, check=True)
    print('[GREEN] 三项结构约束通过；运动行为仍须下面的实际控制器检查', flush=True)
    protected = [
        'src/MotionControl.Framework.Wpf/Configuration/AxisMappingConfigurationView',
        'src/MotionControl.Framework.Wpf/Configuration/IoMappingConfigurationView',
        'src/MotionControl.Framework.Wpf/ManualDebug/ManualAxisControl',
        'src/MotionControl.Framework.Wpf/ManualDebug/IoMonitorView',
        'src/MotionControl.Host.Wpf/MainWindow']
    for prefix in protected:
        for extension in ['.xaml', '.xaml.cs']:
            path = prefix + extension
            expected = git('rev-parse', BASE + ':' + path)
            actual = git('hash-object', '--path=' + path, '--', path)
            if actual != expected:
                raise RuntimeError('受保护页面或后台发生变化：' + path)
    print('[通过] 五个受保护页面的布局和后台共十个文件保持原基线', flush=True)
    subprocess.run(['git', 'diff', '--check'], cwd=ROOT, check=True)


def finalize():
    """只有工作流全部验证成功才调用；记录证据并删除本次迁移工具。"""
    run_id = os.environ.get('GITHUB_RUN_ID', '')
    evidence = 'https://github.com/hefan1/MotionControlFramework/actions/runs/' + run_id
    plan_path = ROOT / 'docs/verification/2026-11-10-architecture-closure-plan.md'
    plan = plan_path.read_text(encoding='utf-8-sig').replace('- [ ]', '- [x]')
    plan += '\n## 执行记录\n\n本次隔离 Windows 工作区已完成结构红绿检查、保护文件核对、正式 Host 构建、全部内部检查和软件控制器 API 回归。证据：' + evidence + '\n\n仅关闭下方矩阵已列明的项目，不代表全仓库所有问题或实物设备验收完成。\n'
    write(str(plan_path.relative_to(ROOT)), plan)
    write('docs/verification/2026-11-10-architecture-closure.md', '''# 职责归位：问题关闭矩阵

## 本批已验证关闭

| 问题 | 处理与验证依据 |
| --- | --- |
| 启动、暂停、复位、终止在 WPF 页面直接协调两套执行状态 | 迁移到已有 MachineDevice 桥接项目的具体 TaskMachineOperations；页面四个入口只调用并显示结果；结构回归通过 |
| 页面、顶部及未来其他入口容易重复实现操作顺序 | 共用同一个已绑定会话的协调实例；显式方法，没有反射、事件总线或通用命令管线 |
| 上下文撤销与迟到启动结果缺乏集中处理 | 协调实例永久失效、阶段性复核原持权者与连接；实际软件控制器延迟反馈验证旧启动被拒绝 |
| 许可已建立但任务启动阶段失败可能留下任务推进 | 失败先尝试暂停原任务，再向原 Runtime 交接报警，完整保留多重异常 |
| 多入口并发可能交错命令 | 协调类明确拒绝并发命令；实际控制器延迟复位期间的启动拒绝检查通过 |
| 参考外部项目可能误引入空驱动或未授权源码 | 未复制 Sophon 代码，未新增厂商占位驱动、FakeAxis 或另一个状态机 |
| 受保护 UI 被重构顺带改变 | 四页和 MainWindow 的 XAML / code-behind 共十个文件与原基线相同 |

## 仍未关闭，不能以本批通过代替

- Host 逐卡混合品牌装配：当前工程级后端选择仍需专项实现与配置迁移。
- ZMotion 原厂 SDK 软件调用链：需相应原厂软件控制器环境；双 Virtual 通过不是该项通过。
- 雷赛、固高等未接入品牌：没有生成空适配器宣称兼容。
- App 启动/连接/退出职责整理、任务页面其余会话装载与展示代码：本批没有重写整套生命周期。
- 正式 Host 屏幕操作及实物限位、制动、安全回路：本批未执行。

## 新人阅读入口

- 页面按钮：`src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs`，只找对应的 `*TaskCoreAsync`。
- 设备操作顺序：`src/MotionControl.TaskEngine.MachineDevice/TaskMachineOperations.cs`。
- 工位及动作收尾：`src/MotionControl.TaskEngine/Machine/TaskMachineController.*`。
- 整机控制权及连接身份：`src/MotionControl.Framework.Runtime/Machine/MachineRuntime.*`。
- 原厂 API：`src/Adapters/MotionCards/<厂商>/Adapter`，不从工艺或 UI 直接调用。
- 结构回归：`tools/Check-CommandBoundaries.py`；真实软件控制器回归：`tools/Verify-ControllerApi.ps1`。

## 证据

''' + evidence + '\n\n此验证对应本分支的迁移后工作树；未合并 master。\n')
    (ROOT / 'tools/Refactor-CommandBoundary.py').unlink()
    print('[完成] 关闭矩阵与运行证据已记录，临时迁移脚本已移除', flush=True)


if __name__ == '__main__':
    if sys.argv[1:] == ['--apply']:
        apply()
    elif sys.argv[1:] == ['--finalize']:
        finalize()
    else:
        raise SystemExit('只能使用 --apply 或 --finalize')
