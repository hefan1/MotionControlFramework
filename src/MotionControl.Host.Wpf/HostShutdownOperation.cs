using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf.Recovery;
using MotionControl.Host.Wpf.Tasks;

namespace MotionControl.Host.Wpf
{
    /// <summary>协调本次退出的设备停止、输出核验和恢复保存；窗口提示与工程门仍由 App 管理。</summary>
    internal sealed class HostShutdownOperation
    {
        /// <summary>发起退出时的整机，不能在异步返回后替换成新设备。</summary>
        private readonly MachineRuntime _runtime;
        /// <summary>发起退出时的连接服务；缺失时仍可尝试停止，但不能确认断卡。</summary>
        private readonly IMotionCardHostConnection _connection;
        /// <summary>原连接服务持有的实际卡对象，防止服务内部更换卡实例。</summary>
        private readonly IMotionCard _card;
        /// <summary>原 Runtime 连接代次，用于拒绝旧退出处理新连接。</summary>
        private readonly Guid? _connectionSession;
        /// <summary>本次退出需要收尾的任务入口，不访问其界面控件。</summary>
        private readonly TaskMonitorLauncher _tasks;
        /// <summary>原设备的恢复记录服务。</summary>
        private readonly MachineRecoveryCoordinator _recovery;
        /// <summary>工程已应用等待重启时保留原恢复文件，不能用旧运行上下文覆盖。</summary>
        private readonly bool _preserveRecoveryFiles;
        /// <summary>由 App 核对原对象仍属于当前上下文；不在此类访问可变 App 字段。</summary>
        private readonly Action _checkContext;
        /// <summary>同一次退出只发起一次停止，失败后由 App 创建新的退出尝试。</summary>
        private bool _stopRequested;
        /// <summary>只有完整停步收尾后才能进入输出核验和保存。</summary>
        private bool _stopCompleted;

        /// <summary>捕获一次退出所使用的具体依赖；不发命令或写文件。</summary>
        internal HostShutdownOperation(MachineRuntime runtime, IMotionCardHostConnection connection,
            TaskMonitorLauncher tasks, MachineRecoveryCoordinator recovery, bool preserveRecoveryFiles,
            Action checkContext)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _connection = connection;
            _card = connection?.ConnectedMotionCard;
            _connectionSession = runtime.ConnectionSessionId;
            _tasks = tasks;
            _recovery = recovery;
            _preserveRecoveryFiles = preserveRecoveryFiles;
            _checkContext = checkContext ?? throw new ArgumentNullException(nameof(checkContext));
        }

        /// <summary>原 Runtime 已登记退出意图；不能据此认为设备已经停稳。</summary>
        internal bool HasBegun { get; private set; }
        /// <summary>原任务已持久交接待核查资源时，后续空上下文不得覆盖原恢复记录。</summary>
        internal bool RetainsBlockedEvidence { get { return _runtime.IsShutdownSealed; } }

        /// <summary>先登记退出门禁，再顺序停止准备、任务和工位；每次等待后核对原身份。</summary>
        internal async Task StopAsync()
        {
            RequireOriginalContext();
            if (_stopRequested) throw new InvalidOperationException("本次退出停止已请求，请等待结果或重新发起退出。");
            _stopRequested = true;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                await _runtime.BeginShutdownAsync(timeout.Token).ConfigureAwait(true);
            HasBegun = true;
            RequireOriginalContext();
            if (_tasks != null && _tasks.IsCommandInProgress)
                throw new InvalidOperationException("任务命令正在执行，请等待结束后重试关闭。");
            if (_runtime.IsPreparing)
                await _runtime.StopPreparationAsync(CancellationToken.None).ConfigureAwait(true);
            RequireOriginalContext();
            if (_tasks != null)
                await _tasks.StopTaskSessionForShutdownAsync().ConfigureAwait(true);
            RequireOriginalContext();
            await _runtime.StopStationFlowsForShutdownAsync(CancellationToken.None).ConfigureAwait(true);
            RequireOriginalContext();
            _stopCompleted = true;
        }

        /// <summary>App 持有工程门后调用：先核验原控制器输出，再保存原设备恢复记录，不执行断卡。</summary>
        internal async Task VerifyAndSaveAsync()
        {
            if (!_stopCompleted || !_runtime.IsShutdownInProgress)
                throw new InvalidOperationException("设备停止收尾尚未完成，不能核验并保存退出记录。");
            RequireConnectedContext();
            if (_tasks != null && _tasks.HasUnsettledTaskSession)
                throw new InvalidOperationException("任务仍有装载或关闭未完成，不能保存退出记录。");
            if (_connection.DigitalOutputs == null)
                throw new InvalidOperationException("控制卡输出映射未确认，不能保存退出记录。");
            string[] outputs = _connection.DigitalOutputs.Select(point => point.Name).ToArray();
            await _runtime.VerifyShutdownOutputsAsync(outputs, CancellationToken.None).ConfigureAwait(true);
            RequireConnectedContext();
            if (!_preserveRecoveryFiles)
            {
                if (_recovery == null) throw new InvalidOperationException("设备恢复保存服务未就绪，退出记录没有保存。");
                _recovery.SaveForShutdown(_runtime, _runtime.State == MachineRunState.Paused);
            }
        }

        /// <summary>失败时只请求原 Runtime 解除门禁；换代后保留保护，绝不操作后来安装的新 Runtime。</summary>
        internal async Task<bool> TryCancelAsync()
        {
            if (!HasBegun) return true;
            if (_runtime.ConnectionSessionId != _connectionSession) return false;
            bool released = await _runtime.TryCancelShutdownAsync().ConfigureAwait(true);
            if (released) _stopCompleted = false;
            return released;
        }

        /// <summary>断线允许原任务尝试停止，但上下文或卡对象已被替换时拒绝继续。</summary>
        private void RequireOriginalContext()
        {
            _checkContext();
            Guid? currentSession = _runtime.ConnectionSessionId;
            if (currentSession.HasValue && currentSession != _connectionSession)
                throw new InvalidOperationException("退出期间控制器连接会话已变化，不能使用旧退出结果。");
            IMotionCard currentCard = _connection?.ConnectedMotionCard;
            if (currentCard != null && !ReferenceEquals(currentCard, _card))
                throw new InvalidOperationException("退出期间控制卡实例已变化，不能操作新的控制卡。");
        }

        /// <summary>输出核验和保存只能使用仍有效的原连接；未知连接不能当作安全断卡。</summary>
        private void RequireConnectedContext()
        {
            RequireOriginalContext();
            if (_connection == null || !_connection.IsConnected || _card == null
                || !ReferenceEquals(_connection.ConnectedMotionCard, _card)
                || !_runtime.IsConnectionSessionCurrent(_connectionSession))
                throw new InvalidOperationException("控制卡连接状态未知，无法核对断卡后的输出保持状态。");
        }
    }
}
