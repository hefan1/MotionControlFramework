using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.TaskEngine.MachineDevice;
using MotionControl.TaskEngine.Wpf.Simulation;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>
    /// 保存一次正式任务装载、准备配置安装和失败回滚的全部引用。
    /// Host 在同一调度上下文调用本对象；仅文件和设备读取进入后台，不持有任何界面控件。
    /// </summary>
    internal sealed class HostTaskSessionLoadOperation
    {
        /// <summary>本次装载开始时的运行时，回滚不使用页面后来切换的设备。</summary>
        private readonly MachineRuntime _runtime;
        /// <summary>任务接管前的设备准备定义，仅在本次安装代次仍有效时恢复。</summary>
        private readonly IReadOnlyList<IStationPreparation> _equipmentPreparations;
        /// <summary>调用方提供的纯身份核对；检查页面上下文、卡组和连接代次，不执行设备动作。</summary>
        private readonly Action _requireCurrentContext;
        /// <summary>本次装载是否已经开始；一个对象不能被用于第二次读取。</summary>
        private bool _loadStarted;
        /// <summary>读取和发布阶段全部退出的屏障；失败原因仍由 LoadAsync 原样交给调用方。</summary>
        private readonly TaskCompletionSource<bool> _loadFinished =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>请求回滚后永久禁止发布；读取本身仍须等待结束，不能丢失其返回的持权会话。</summary>
        private bool _rollbackRequested;
        /// <summary>并发清理共用的任务；清理失败后允许显式重试，成功后不重复释放。</summary>
        private Task _rollbackTask;
        /// <summary>在通知准备订阅者前写入的安装代次；通知抛错也能准确撤回本次安装。</summary>
        private long _installedPreparationVersion;

        /// <summary>固定原运行时和准备定义；此时不读取配置、不连接控制卡。</summary>
        internal HostTaskSessionLoadOperation(MachineRuntime runtime,
            IReadOnlyList<IStationPreparation> equipmentPreparations, Action requireCurrentContext)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _equipmentPreparations = equipmentPreparations ?? new IStationPreparation[0];
            _requireCurrentContext = requireCurrentContext ?? throw new ArgumentNullException(nameof(requireCurrentContext));
        }

        /// <summary>实际读取到的会话；即使尚未发布或回滚失败，也保留引用以完成安全清理。</summary>
        internal TaskMonitorSession Session { get; private set; }
        /// <summary>装载成功后创建的唯一操作入口，撤销装载时一并失效。</summary>
        internal TaskMachineOperations Operations { get; private set; }
        /// <summary>本次安装的准备代次，供页面发布和后续关闭时核对。</summary>
        internal long InstalledPreparationVersion { get { return _installedPreparationVersion; } }

        /// <summary>核对未撤销且仍属于原连接；页面挂接读取结果之前也必须执行。</summary>
        internal void RequireCurrent()
        {
            if (_rollbackRequested)
                throw new InvalidOperationException("任务装载已撤销，不能发布迟到的任务会话；请在当前连接中重新读取。");
            _requireCurrentContext();
        }

        /// <summary>读取任务、安装准备定义并生成发布身份；不启动工步，也不向轴或输出发送命令。</summary>
        internal async Task LoadAsync(MachineConfigurationStore store, string machineId,
            MotionCardGroup motionCards, ILogicalIo logicalIo, DeviceVariableStore variables, Guid connection)
        {
            if (_loadStarted) throw new InvalidOperationException("同一个任务装载操作不能重复执行。");
            _loadStarted = true;
            try
            {
                RequireCurrent();
                Session = await Task.Run(() => HostTaskSessionLoader.Load(
                    store, machineId, motionCards, logicalIo, variables, _runtime, connection)).ConfigureAwait(true);
                RequireCurrent();
                var preparations = await Task.Run(() => HostTaskSessionLoader.LoadPreparations(
                    store, machineId, Session)).ConfigureAwait(true);
                RequireCurrent();
                _runtime.TryConfigurePreparations(preparations, null, out _installedPreparationVersion);
                RequireCurrent();
                if (_runtime.PreparationConfigurationVersion != _installedPreparationVersion)
                    throw new InvalidOperationException("任务准备配置在读取期间发生变化，请核对当前配置后重新读取。");
                Session.Publish(machineId);
                Operations = ((MachineTaskDevice)Session.TaskDevice).CreateOperations(Session.Machine);
                RequireCurrent();
            }
            finally
            {
                // 关闭必须先等到这里；不能仅以页面尚无 _session 判断没有实际持权设备。
                _loadFinished.TrySetResult(true);
            }
        }

        /// <summary>先撤销发布资格，再共用一次清理；失败保留会话和安装代次，供下次重试。</summary>
        internal Task RollbackAsync()
        {
            _rollbackRequested = true;
            Operations?.Invalidate();
            if (_rollbackTask != null && !_rollbackTask.IsFaulted) return _rollbackTask;
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 在准备通知和关闭代码运行之前发布任务，防止通知重入造成重复回滚。
            _rollbackTask = completed.Task;
            _ = CompleteRollbackAsync(completed);
            return completed.Task;
        }

        /// <summary>等待原装载退出后，仅撤回本次安装并关闭其会话；所有失败通过共享任务返回。</summary>
        private async Task CompleteRollbackAsync(TaskCompletionSource<bool> completed)
        {
            try
            {
                if (_loadStarted) await _loadFinished.Task.ConfigureAwait(true);
                if (_installedPreparationVersion != 0)
                    _runtime.TryConfigurePreparations(_equipmentPreparations, _installedPreparationVersion, out _);
                if (Session != null) await Session.Machine.CloseAsync().ConfigureAwait(true);
                completed.TrySetResult(true);
            }
            catch (Exception error)
            {
                // 不丢弃 Session；调用方仍能重试同一个失败装载的实际控制权清理。
                completed.TrySetException(error);
            }
        }
    }
}
