using System;
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
                bool taskStartAttempted = false;
                try
                {
                    EnsureCurrent();
                    // 调用开始后即按可能部分启动处理，不能漏掉失败收尾。
                    taskStartAttempted = true;
                    _machine.Start();
                    _runtime.ConfirmTaskEngineProductionStarted();
                    EnsureCurrent();
                }
                catch (Exception startError)
                {
                    // 任务启动可能已部分发生，不能只把 Runtime 改成报警而留下工步继续执行。
                    var errors = new List<Exception> { startError };
                    try { if (taskStartAttempted) await _machine.PauseAsync().ConfigureAwait(false); }
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
