using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>整机退出前的受控停止，不改变设备项目的工艺和输出策略。</summary>
    public sealed partial class MachineRuntime
    {
        /// <summary>开始关闭后禁止新动作；确认停止前不会因关闭失败自动解除。</summary>
        private volatile bool _shutdownIntent;
        /// <summary>待确认资源已完成持久交接后锁定本运行时；仅允许停止及断卡。</summary>
        private volatile bool _shutdownSealed;
        /// <summary>输出核验失败时锁定退出保护，直至完整复核成功。</summary>
        private bool _shutdownVerificationFailed;
        /// <summary>退出核验发出的原始 DO 读取；超时后仍须等待驱动退出才能断卡。</summary>
        private readonly List<Task> _shutdownOutputReads = new List<Task>();
        /// <summary>串行化关闭意图与任务设备命令的实际发出，避免检查后仍有新命令进入。</summary>
        private readonly SemaphoreSlim _taskCommandGate = new SemaphoreSlim(1, 1);

        /// <summary>资源已交接但控制权仍保持在原任务会话，直到控制卡实际断开。</summary>
        public bool IsShutdownSealed { get { return _shutdownSealed; } }

        /// <summary>表示退出命令已取得全局动作门禁；不代表现场已安全。</summary>
        public bool IsShutdownInProgress { get { return _shutdownIntent || _shutdownSealed; } }

        /// <summary>在统一命令锁中登记关闭意图，之后只允许停止、退出和只读检查。</summary>
        public async Task BeginShutdownAsync(CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _taskCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { _shutdownIntent = true; }
                finally { _taskCommandGate.Release(); }
            }
            finally { _commandLock.Release(); }
        }

        /// <summary>任务设备仅在关闭意图出现前登记并发出原始命令；等待完成仍由执行器负责。</summary>
        internal T BeginTaskDeviceCommand<T>(Func<T> issue)
        {
            if (issue == null) throw new ArgumentNullException(nameof(issue));
            _taskCommandGate.Wait();
            try
            {
                if (IsShutdownInProgress) throw new ShutdownCommandRejectedException();
                return issue();
            }
            finally { _taskCommandGate.Release(); }
        }

        /// <summary>关闭失败时，仅在设备已完整收尾且控制权空闲时解除退出门禁。</summary>
        public async Task<bool> TryCancelShutdownAsync()
        {
            if (!await _commandLock.WaitAsync(0).ConfigureAwait(false)) return false;
            try
            {
                if (_shutdownSealed || _shutdownVerificationFailed || IsPreparing || IsTaskEngineControlClaimed || State == MachineRunState.Running
                    || State == MachineRunState.Pausing || _flowScheduler.IsRunning || !_flowScheduler.IsSettlementConfirmed)
                    return false;
                if (_motionCards.Cards.Any(card => card.IsConnected) && !_motionCards.IsConnected) return false;
                try { EnsureNoManualOperationsInProgress("取消关闭"); EnsureShutdownOutputReadsSettled(); EnsureStopFeedbackReadSettled(); }
                catch (InvalidOperationException) { return false; }
                _shutdownIntent = false;
                return true;
            }
            finally { _commandLock.Release(); }
        }

        /// <summary>停止准备或 C# 工位并确认后台收尾；任务会话由其持有者先行关闭。</summary>
        public async Task StopStationFlowsForShutdownAsync(CancellationToken cancellationToken)
        {
            if (_shutdownSealed) return;
            if (IsPreparing) await StopPreparationAsync(cancellationToken).ConfigureAwait(false);
            if (IsTaskEngineControlClaimed)
                throw new InvalidOperationException("任务执行会话仍持有设备，请先等待任务停止和资源收尾。");

            MachineRunState state = State;
            if (state == MachineRunState.Running || state == MachineRunState.Pausing || _flowScheduler.IsRunning)
                await PauseAsync(cancellationToken).ConfigureAwait(false);

            if (_flowScheduler.IsRunning || !_flowScheduler.IsSettlementConfirmed)
                throw new InvalidOperationException("工位流程或停止收尾尚未确认，不能关闭设备；请检查工位、轴与报警状态。");
        }

        /// <summary>在整机命令锁内检查输出保持能力与两次读取；未知或变化不能断卡。</summary>
        public async Task VerifyShutdownOutputsAsync(IEnumerable<string> outputNames, CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await VerifyShutdownOutputsCoreAsync(outputNames, cancellationToken).ConfigureAwait(false); }
            finally { _commandLock.Release(); }
        }

        /// <summary>保留任务控制权并锁定运行时；退出交接失败时不得调用此方法。</summary>
        internal async Task SealTaskEngineShutdownAsync(object owner, IEnumerable<string> outputNames)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            await _commandLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!_shutdownIntent) throw new InvalidOperationException("尚未登记退出意图，不能交接任务控制权。");
                EnsureNoPreparationInProgress("结束设备会话");
                EnsureStopFeedbackReadSettled();
                EnsureNoManualOperationsInProgress("结束设备会话");
                lock (_controlOwnerLock)
                    if (!ReferenceEquals(_taskEngineOwner, owner))
                        throw new InvalidOperationException("任务设备控制权已变化，不能交接待确认资源。");
                if (State == MachineRunState.Running || State == MachineRunState.Pausing || _flowScheduler.IsRunning || !_flowScheduler.IsSettlementConfirmed)
                    throw new InvalidOperationException("设备仍在运行或工位停止尚未确认，不能交接资源。");
                // 两次确认轴停稳和输出稳定；只能声明控制器断开会话行为，不推断物理安全。
                await EnsurePreparationAxesStoppedAsync(CancellationToken.None, false).ConfigureAwait(false);
                await VerifyShutdownOutputsCoreAsync(outputNames, CancellationToken.None).ConfigureAwait(false);
                await EnsurePreparationAxesStoppedAsync(CancellationToken.None, false).ConfigureAwait(false);
                _shutdownSealed = true;
                RecoveryBlockReason = "旧任务资源已持久保留，设备必须核查现场并重新准备，禁止沿用旧工步。";
            }
            finally { _commandLock.Release(); }
        }

        /// <summary>仅连接状态明确、全部控制卡声明保持输出且已映射输出连续两次一致时通过。</summary>
        private async Task VerifyShutdownOutputsCoreAsync(IEnumerable<string> outputNames, CancellationToken cancellationToken)
        {
            _shutdownVerificationFailed = true;
            if (outputNames == null || !_motionCards.IsConnected || _controlInputIo == null)
                throw new InvalidOperationException("控制卡或输出状态未知，不能确认断卡后的保持状态。");
            string[] names = outputNames.ToArray();
            if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
                throw new InvalidOperationException("输出映射缺少名称或存在重复，不能确认断卡状态。");
            foreach (IMotionCard card in _motionCards.Cards)
            {
                var guarantee = card as IOutputStatePreservingDisconnect;
                if (guarantee == null || !guarantee.PreservesDigitalOutputsOnDisconnect)
                    throw new InvalidOperationException("控制卡“" + card.ControllerId + "”尚未验证断开会话后是否保持输出，请按设备规程处理夹具及吸附状态。");
            }
            EnsureShutdownOutputReadsSettled();
            var previous = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int pass = 0; pass < 2; pass++)
                foreach (string name in names)
                {
                    Task<bool> read;
                    try { read = _controlInputIo.ReadOutputAsync(name, cancellationToken); }
                    catch (Exception error) { throw new InvalidOperationException("读取输出“" + name + "”失败，不能确认断卡状态。", error); }
                    if (read == null) throw new InvalidOperationException("输出“" + name + "”未返回读取任务，不能确认断卡状态。");
                    _shutdownOutputReads.Add(read);
                    if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)).ConfigureAwait(false) != read)
                    {
                        _ = read.ContinueWith(task => { var ignored = task.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("读取输出“" + name + "”超时，不能确认断卡状态。");
                    }
                    bool value;
                    try { value = await read.ConfigureAwait(false); }
                    catch (Exception error) { throw new InvalidOperationException("读取输出“" + name + "”失败，不能确认断卡状态。", error); }
                    if (pass == 0) previous.Add(name, value);
                    else if (previous[name] != value) throw new InvalidOperationException("输出“" + name + "”状态发生变化，不能确认断卡状态。");
                }
            _shutdownVerificationFailed = false;
        }

        /// <summary>原始读取未结束时禁止断卡或解除门禁，避免迟到调用继续访问已释放的控制卡。</summary>
        private void EnsureShutdownOutputReadsSettled()
        {
            foreach (Task read in _shutdownOutputReads.Where(task => task.IsFaulted))
                System.Diagnostics.Trace.TraceWarning("退出核验读取已结束但发生异常：" + read.Exception);
            _shutdownOutputReads.RemoveAll(task => task.IsCompleted);
            if (_shutdownOutputReads.Count != 0)
                throw new InvalidOperationException("上次退出核验的输出读取仍在执行，不能断卡或解除保护。");
        }
    }

    /// <summary>退出门禁在命令发出前拒绝任务设备动作。</summary>
    internal sealed class ShutdownCommandRejectedException : InvalidOperationException
    {
        /// <summary>标明命令尚未下发，便于任务执行器区分未知结果。</summary>
        internal ShutdownCommandRejectedException() : base("设备正在安全退出，任务命令尚未下发。") { }
    }
}
