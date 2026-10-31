using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Machine
{
    /// <summary>一台内存设备的多工位生产入口；统一许可，各工位独立推进，不接实际运动卡。</summary>
    public sealed partial class TaskMachineController
    {
        /// <summary>保护整机状态和工位记录，锁内不等待任务完成。</summary>
        private readonly object _sync = new object();
        /// <summary>本会话独占的内存设备。</summary>
        private readonly ITaskDevice _device;
        /// <summary>当前设备报警服务；可由 Host 注入正式 Runtime 共用的实例。</summary>
        public IMachineAlarmService AlarmService { get; }
        /// <summary>冻结的设备清单。</summary>
        private readonly TaskMachineConfiguration _configuration;
        /// <summary>冻结的执行任务库，不引用编辑中的定义。</summary>
        private readonly Dictionary<string, TaskDefinition> _library;
        /// <summary>全部工位的具体运行记录。</summary>
        private readonly List<StationExecutionControl> _stations;
        /// <summary>可选初始化运行记录。</summary>
        private readonly StationExecutionControl _initialization;
        /// <summary>只在显式终止时取消，普通暂停不取消动作。</summary>
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        /// <summary>启动时创建的工位后台任务。</summary>
        private readonly List<Task> _workers = new List<Task>();
        /// <summary>整机当前状态。</summary>
        private TaskMachineState _state = TaskMachineState.NeedsReset;
        /// <summary>暂停时未完成，启动后异步放行。</summary>
        private TaskCompletionSource<bool> _runningPermit = NewSignal();
        /// <summary>初始化通过后放行工位，普通暂停不重置。</summary>
        private readonly TaskCompletionSource<bool> _initializationPermit = NewSignal();
        /// <summary>是否已完成本次初始化。</summary>
        private bool _initialized;
        /// <summary>后台工位只创建一次，防止重复启动。</summary>
        private bool _workersStarted;
        /// <summary>等待复位的故障，最多保留最近 100 条。</summary>
        private readonly List<TaskMachineFault> _faults = new List<TaskMachineFault>();
        /// <summary>外部仍有效的停机条件；条件恢复也不自动清除锁存。</summary>
        private readonly Dictionary<string, string> _activeStopAlarms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>终止的唯一收尾任务。</summary>
        private Task _abortTask;
        /// <summary>关闭和归还所有权的唯一任务。</summary>
        private Task _closeTask;

        /// <summary>先校验并冻结清单和任务库，再取得设备控制权；构造不会运行任务或修改场景值。</summary>
        public TaskMachineController(TaskMachineConfiguration configuration, IEnumerable<TaskDefinition> definitions,
            ITaskDevice device, IMachineAlarmService alarmService = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            AlarmService = alarmService ?? new MachineAlarmService();
            var original = new List<TaskDefinition>();
            foreach (TaskDefinition task in definitions ?? new TaskDefinition[0])
            {
                if (original.Count >= 128) throw new ArgumentException("设备任务库最多 128 份定义。");
                original.Add(task);
            }
            var errors = TaskMachineConfigurationValidator.Validate(configuration, original, device.Catalog);
            if (errors.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
            _configuration = TaskMachineConfigurationStore.Copy(configuration);
            _library = original.Select(task => TaskDefinitionJson.Deserialize(TaskDefinitionJson.Serialize(task)))
                .ToDictionary(task => task.TaskId, StringComparer.OrdinalIgnoreCase);
            // 传入的是本次执行库；未实现能力在任何工位启动前明确拒绝。
            foreach (TaskDefinition task in _library.Values) TaskExecutionEngine.RequireSupported(task.TaskId, task.Root);
            _stations = _configuration.Stations.Select(station => new StationExecutionControl(this, station)).ToList();
            if (_configuration.InitializationTaskId != null)
                _initialization = new StationExecutionControl(this, new StationTaskConfiguration {
                    StationId = "__initialization", Name = "设备初始化", TaskId = _configuration.InitializationTaskId, Mode = StationTaskMode.Once });
            else { _initialized = true; _initializationPermit.TrySetResult(true); }
            _device.ClaimMachine(this);
        }

        /// <summary>整机、工位及资源快照；不把可变运行句柄交给 UI。</summary>
        public TaskMachineSnapshot GetSnapshot()
        {
            lock (_sync) return new TaskMachineSnapshot(_configuration.MachineId, _state, _initialized,
                _initialization == null ? null : new StationTaskSnapshot(_initialization),
                _stations.Select(station => new StationTaskSnapshot(station)).ToList(),
                new List<TaskMachineFault>(_faults), _device.ReadResources());
        }

        /// <summary>生成异步信号，避免在控制锁内执行等待方回调。</summary>
        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>包含初始化的所有运行记录，调用方已持有整机锁或尚未发布控制器。</summary>
        private IEnumerable<StationExecutionControl> AllStations()
        {
            if (_initialization != null) yield return _initialization;
            foreach (StationExecutionControl station in _stations) yield return station;
        }

        /// <summary>停止/关闭是会话终态，旧会话不能重新执行生产周期。</summary>
        private void RequireOpen()
        {
            if (_state == TaskMachineState.Stopped || _state == TaskMachineState.Closed)
                throw new InvalidOperationException("生产会话已终止或关闭；不能恢复旧任务。");
        }
    }
}
