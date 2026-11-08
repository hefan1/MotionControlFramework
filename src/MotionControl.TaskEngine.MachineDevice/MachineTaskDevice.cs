using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.MachineDevice
{
    /// <summary>
    /// 用真实运动控制卡驱动任务执行的设备实现：轴动作走 IAxis、DI/DO 走 ILogicalIo、
    /// 变量走 IDeviceVariableStore，整机控制权通过 MachineRuntime 统一仲裁。
    /// 多轴分别定位、轴组插补和有条件的资源范围互锁尚未接入，任务准入时明确拒绝，不退化执行。
    /// </summary>
    public sealed partial class MachineTaskDevice : ITaskDevice, ITaskInterlockSupport, ITaskAxisGroupSupport, ITaskResourceRecoveryState, ITaskResourceJournalCleanup, ITaskRecordSink, ITaskShutdownHandoff, IAutomaticProductionPause
    {
        /// <summary>正式设备尚未实现组定位的完整接受和收尾，装载前必须拒绝相关步骤。</summary>
        public bool SupportsAxisGroupMove { get { return false; } }

        /// <summary>Host 离线编辑和工程预检沿用正式任务设备的能力边界，不由工程配置改变。</summary>
        public static TaskDeviceCapabilities ExecutionCapabilities
        {
            get { return new TaskDeviceCapabilities(nameof(MachineTaskDevice), false, false); }
        }

        /// <summary>未指定所有者时使用的匿名所有权标记，保证同一实例内的所有权比对稳定。</summary>
        private static readonly object AnonymousOwner = new object();

        /// <summary>本设备的资源协调器，按目录中的资源名建立。</summary>
        private readonly TaskResourceCoordinator _resources;
        /// <summary>正式设备的会话资源记录，内存测试可不启用。</summary>
        private readonly TaskResourceJournal _journal;
        /// <summary>可选的独立任务记录存储。</summary>
        private readonly ITaskRecordSink _recordSink;
        /// <summary>开始安全关闭后不再接受新任务登记。</summary>
        private bool _closing;
        /// <summary>当前设备会话的资源记录已完整清理。</summary>
        private bool _journalCleared;

        /// <summary>已连接的控制卡组，按逻辑轴名称取轴。</summary>
        private readonly MotionCardGroup _motionCards;

        /// <summary>按逻辑名称读写 DI/DO 的入口。</summary>
        private readonly ILogicalIo _logicalIo;

        /// <summary>设备变量存储，任务流程写入的变量值保存在这里。</summary>
        private readonly IDeviceVariableStore _variables;

        /// <summary>整机运行时，用于占用和归还设备命令权。</summary>
        private readonly MachineRuntime _runtime;
        /// <summary>正式 Host 在后台装载任务前捕获的连接；兼容构造的设备没有绑定。</summary>
        private readonly Guid? _expectedSession;
        /// <summary>正式设备实际绑定的控制器连接标识。兼容构造未指定时为空。</summary>
        public Guid? BoundConnectionSessionId => _expectedSession;
        /// <summary>正式设备首次成功接管的控制器身份，归还命令权后也不允许把该设备实例转交另一控制器。</summary>
        private object _boundController;
        /// <summary>正式设备正在锁外申请或归还 Runtime 控制权，期间不发放新动作。</summary>
        private bool _changingOwner;
        /// <summary>正式设备提交 DO 至驱动登记完成之间的占用，避免控制权提前归还。</summary>
        private int _issuingBoundOutputs;

        /// <summary>保护所有权、运行登记和命令计数。</summary>
        private readonly object _sync = new object();

        /// <summary>每根轴已接受的动作次数，供重复执行和漏执行检查使用。</summary>
        private readonly Dictionary<string, int> _acceptedCommands =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>尚未收尾的任务运行编号。</summary>
        private readonly HashSet<string> _activeRuns = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>已进入驱动但尚未结束的输出写入；退出时不能以队列空闲代替完成。</summary>
        private readonly HashSet<Task> _pendingOutputWrites = new HashSet<Task>();
        /// <summary>写入结果不确定的输出，须保留对应资源或现场复核。</summary>
        private readonly HashSet<string> _unconfirmedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>已发出或可能发出、但原轴动作尚未结束的凭证；防止旧异步停止影响新动作。</summary>
        private readonly HashSet<MachineAxisMove> _unsettledMoves = new HashSet<MachineAxisMove>();

        /// <summary>已发出但尚未返回的停止调用；同一动作并发收尾时分别计数。</summary>
        private readonly Dictionary<MachineAxisMove, int> _pendingStops = new Dictionary<MachineAxisMove, int>();

        /// <summary>已预留但仍在读取前置状态的轴，禁止并发下发及归还控制权。</summary>
        private readonly HashSet<string> _beginningAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前独占本设备的对象；为空表示没有整机控制者。</summary>
        private object _machineOwner;

        /// <summary>向 Runtime 提供当前任务控制器的暂停收尾状态；没有控制器时不确认。</summary>
        public bool IsAutomaticPauseConfirmed
        {
            get
            {
                IAutomaticProductionPause participant;
                lock (_sync) participant = _machineOwner as IAutomaticProductionPause;
                return participant != null && participant.IsAutomaticPauseConfirmed;
            }
        }

        /// <summary>把整机暂停请求转交给持有本设备的任务控制器，不能只改变 Runtime 状态。</summary>
        public Task PauseAutomaticProductionAsync()
        {
            IAutomaticProductionPause participant;
            lock (_sync) participant = _machineOwner as IAutomaticProductionPause;
            if (participant == null)
                throw new InvalidOperationException("任务会话未提供受控暂停能力，不能确认生产已暂停。");
            return participant.PauseAutomaticProductionAsync();
        }

        /// <summary>用已连接的控制卡、逻辑 IO、变量存储和整机运行时建立任务设备。</summary>
        public MachineTaskDevice(
            TaskResourceCatalog catalog,
            MotionCardGroup motionCards,
            ILogicalIo logicalIo,
            IDeviceVariableStore variables,
            MachineRuntime runtime, TaskResourceJournal journal = null, ITaskRecordSink recordSink = null)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _motionCards = motionCards ?? throw new ArgumentNullException(nameof(motionCards));
            _logicalIo = logicalIo ?? throw new ArgumentNullException(nameof(logicalIo));
            _variables = variables ?? throw new ArgumentNullException(nameof(variables));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _journal = journal;
            _recordSink = recordSink;
            _resources = new TaskResourceCoordinator(catalog.Resources, journal);
        }

        /// <summary>正式 Host 设备必须沿用调用方在异步装载前捕获的连接。</summary>
        public MachineTaskDevice(TaskResourceCatalog catalog, MotionCardGroup motionCards, ILogicalIo logicalIo,
            IDeviceVariableStore variables, MachineRuntime runtime, Guid expectedSession,
            TaskResourceJournal journal = null, ITaskRecordSink recordSink = null)
            : this(catalog, motionCards, logicalIo, variables, runtime, journal, recordSink)
        {
            if (!runtime.IsConnectionSessionCurrent(expectedSession))
                throw new InvalidOperationException("装载任务期间控制器连接会话已变化。");
            _expectedSession = expectedSession;
        }

        /// <summary>调用方持设备锁，以本地占用保护正式命令登记直至 Runtime 实际派发。</summary>
        private void EnsureBoundDeviceAvailable()
        {
            if (_expectedSession.HasValue && (_changingOwner || _machineOwner == null || _closing))
                throw new InvalidOperationException("正式任务设备未持有稳定控制权，不能发放新命令。");
        }

        /// <summary>提交完整记录，未配置存储时拒绝继续。</summary>
        public void CommitRecord(string executionId, string taskId, string nodeId, string category, DateTimeOffset recordedAtUtc, IReadOnlyDictionary<string, string> fields)
        {
            if (_recordSink == null) throw new InvalidOperationException("任务记录存储未配置。");
            _recordSink.CommitRecord(executionId, taskId, nodeId, category, recordedAtUtc, fields);
        }

        /// <summary>本设备的只读资源目录。</summary>
        public TaskResourceCatalog Catalog { get; }

        /// <summary>本设备共用的资源协调器。</summary>
        public TaskResourceCoordinator Resources
        {
            get { return _resources; }
        }

        /// <summary>读取逻辑 DI；读取失败时抛出带逻辑名称的驱动错误。</summary>
        public bool? ReadInput(string name)
        {
            return Wait(_logicalIo.ReadInputAsync(name, CancellationToken.None));
        }

        /// <summary>读取逻辑 DO 当前状态；读取失败时抛出带逻辑名称的驱动错误。</summary>
        public bool? ReadOutput(string name)
        {
            return Wait(_logicalIo.ReadOutputAsync(name, CancellationToken.None));
        }

        /// <summary>写入逻辑 DO；写入失败时抛出错误，不静默忽略。</summary>
        public void WriteOutput(string name, bool value)
        {
            if (!_expectedSession.HasValue) { IssueOutput(name, value); return; }
            lock (_sync)
            {
                EnsureBoundDeviceAvailable();
                _issuingBoundOutputs++;
            }
            try { IssueOutput(name, value); }
            finally { lock (_sync) _issuingBoundOutputs--; }
        }

        /// <summary>在任务命令门内核对绑定、发出驱动写入并登记；锁外等待真实驱动完成。</summary>
        private void IssueOutput(string name, bool value)
        {
            Task write = _runtime.BeginTaskDeviceCommand(() =>
            {
                try
                {
                    Task issued = _logicalIo.WriteOutputAsync(name, value, CancellationToken.None);
                    if (issued == null) throw new InvalidOperationException("输出写入没有返回任务：" + name);
                    lock (_sync) _pendingOutputWrites.Add(issued);
                    return issued;
                }
                catch
                {
                    lock (_sync) _unconfirmedOutputs.Add(name);
                    throw;
                }
            }, _expectedSession, this);
            try { Wait(write); }
            catch
            {
                lock (_sync) _unconfirmedOutputs.Add(name);
                throw;
            }
            finally { lock (_sync) if (write.IsCompleted) _pendingOutputWrites.Remove(write); }
        }

        /// <summary>按目录中的变量类型写入流程值；类型不支持时明确报错。</summary>
        public void WriteVariable(string name, object value)
        {
            DeviceVariableType type;
            if (!Catalog.Variables.TryGetValue(name, out type))
            {
                throw new ArgumentException("设备变量未配置：" + name);
            }

            switch (type)
            {
                case DeviceVariableType.Boolean:
                    _variables.SetBoolean(name, Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture));
                    return;
                case DeviceVariableType.Integer:
                    _variables.SetInteger(name, Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
                    return;
                case DeviceVariableType.Number:
                    _variables.SetNumber(name, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                    return;
                case DeviceVariableType.String:
                    _variables.SetString(name, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                    return;
                case DeviceVariableType.Byte:
                    _variables.SetByte(name, Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture));
                    return;
                case DeviceVariableType.Character:
                    _variables.SetCharacter(name, Convert.ToChar(value, System.Globalization.CultureInfo.InvariantCulture));
                    return;
                case DeviceVariableType.DateTime:
                    _variables.SetDateTime(name, Convert.ToDateTime(value, System.Globalization.CultureInfo.InvariantCulture));
                    return;
                default:
                    throw new NotSupportedException("任务设备不支持写入该类型的变量：" + name + "，类型=" + type + "。");
            }
        }

        /// <summary>复制全部设备变量当前值，供一次条件判断使用一致快照。</summary>
        public IReadOnlyList<DeviceVariableSnapshot> ReadVariables()
        {
            return _variables.GetSnapshot();
        }

        /// <summary>将两路布尔采样一次写入同一个设备变量表。</summary>
        public void WriteBooleanPair(string firstName, bool firstValue, string secondName, bool secondValue)
        {
            _variables.SetBooleanPair(firstName, firstValue, secondName, secondValue);
        }

        /// <summary>读取一根逻辑轴的当前状态，位置和速度使用该轴工程单位。</summary>
        public TaskAxisState ReadAxis(string name)
        {
            IAxis axis = _motionCards.GetAxis(name);
            AxisStatus status = Wait(axis.ReadStatusAsync(CancellationToken.None));
            if (status == null) throw new InvalidOperationException("轴状态读取为空，不能确认设备状态：" + name + "。");
            return new TaskAxisState(
                name,
                status.Position,
                status.ServoEnabled,
                status.Homed && !status.RequiresHomingAfterLimitAlarm,
                status.Moving || double.IsNaN(status.Velocity) || double.IsInfinity(status.Velocity) || Math.Abs(status.Velocity) > 0.001,
                status.AlarmActive ? (string.IsNullOrWhiteSpace(status.AlarmMessage) ? "轴报警，未提供详细说明。" : status.AlarmMessage) : null,
                AcceptedCommands(name));
        }

        /// <summary>设备侧有条件互锁尚未实现，任务加载时须提前拒绝。</summary>
        public bool SupportsInterlockConditions => false;

        /// <summary>无条件时允许继续；有条件时明确拒绝，避免同步读设备阻塞任务运行锁。</summary>
        public bool CheckInterlock(TaskInterlock interlock, TaskCondition legacyCondition, out string reason)
        {
            if (interlock == null && legacyCondition == null)
            {
                reason = "未配置条件。";
                return true;
            }
            throw new NotSupportedException("设备任务暂不支持配置进入或退出互锁；请勿运行该资源范围。");
        }

        /// <summary>读取当前资源占用与待确认区域。</summary>
        public IReadOnlyList<TaskResourceSnapshot> ReadResources()
        {
            return _resources.Read();
        }

        /// <summary>只查询本会话的资源队列，不访问控制器或磁盘。</summary>
        public TaskResourceRecoveryState GetResourceRecoveryState()
        {
            // 在队列锁内同时检查占用与持久化结果，避免写失败回调与状态读取交错。
            TaskResourceRecoveryState resourceState = _resources.RecoveryState;
            if (resourceState != TaskResourceRecoveryState.Clear) return resourceState;
            lock (_sync)
            {
                RemoveSettledMoves();
                if (_unsettledMoves.Count != 0 || _pendingStops.Count != 0 || _beginningAxes.Count != 0
                    || _issuingBoundOutputs != 0 || _pendingOutputWrites.Count != 0 || _unconfirmedOutputs.Count != 0) return TaskResourceRecoveryState.Held;
            }
            return TaskResourceRecoveryState.Clear;
        }

        /// <summary>仅在任务已退出、人工核实且退出条件通过两次现场采样后解除隔离；不提供连续安全监控。</summary>
        public bool TryRecoverResources(string requestId, string confirmation, out string reason)
        {
            lock (_sync)
            {
                TaskResourceRequest request = _resources.FindBlocked(requestId);
                if (request == null) { reason = "找不到待确认的资源区域：" + requestId + "。"; return false; }
                if (string.IsNullOrWhiteSpace(confirmation)) { reason = "恢复资源区域必须提供现场确认说明。"; return false; }
                if (_changingOwner || _machineOwner == null || !_runtime.IsTaskEngineControlClaimed || _runtime.ControlOwner != MachineControlOwner.TaskEngine)
                { reason = "任务会话未持有设备控制权，不能复核或解除资源隔离。"; return false; }
                if (!_motionCards.IsConnected)
                { reason = "控制器未连接，不能确认资源退出条件。"; return false; }
                RemoveSettledMoves();
                if (_unsettledMoves.Any(move => !move.IsQuiescent) || _pendingStops.Count != 0 || _beginningAxes.Count != 0)
                { reason = "轴动作或取消收尾仍未结束，不能解除资源隔离。"; return false; }
                if (_issuingBoundOutputs != 0 || _pendingOutputWrites.Count != 0)
                { reason = "输出写入仍在执行，不能解除资源隔离。"; return false; }
                if (_activeRuns.Count != 0 || _runtime.IsPreparing || _runtime.State == MachineRunState.Running || _runtime.State == MachineRunState.Pausing)
                { reason = "任务或设备仍在运行或收尾，不能解除资源隔离。"; return false; }
                try
                {
                    // 设备状态可能变化；两次检查均成功才尝试移除原凭证。仍不能代替连续硬件互锁。
                    Dictionary<string, bool> firstOutputs, secondOutputs;
                    if (!ReadRecoveryResourceOutputs(request, out firstOutputs, out reason)
                        || !CheckResourceRecovery(request, out reason)
                        || !ReadRecoveryResourceOutputs(request, out secondOutputs, out reason)
                        || !CheckResourceRecovery(request, out reason)) return false;
                    if (!RecoveryOutputsMatch(firstOutputs, secondOutputs, out reason)) return false;
                    if (!_resources.Recover(request)) { reason = "资源状态已改变，请重新读取后核实。"; return false; }
                    // 仅在现场条件复核成功后退休该范围内已退出但曾收尾失败的动作。
                    _unsettledMoves.RemoveWhere(move => move.IsQuiescent
                        && request.Names.Contains("Axis:" + move.AxisName, StringComparer.OrdinalIgnoreCase));
                    foreach (string name in firstOutputs.Keys) _unconfirmedOutputs.Remove(name);
                    reason = "已解除待确认状态；确认说明：" + confirmation + "。旧任务不会自动重启。";
                    return true;
                }
                catch (Exception error)
                {
                    reason = "无法确认资源退出条件，保留隔离：" + error.Message;
                    return false;
                }
            }
        }

        /// <summary>读取全部资源轴和配置的退出条件；任何读取失败或未知值都不得放行。</summary>
        private bool CheckResourceRecovery(TaskResourceRequest request, out string reason)
        {
            foreach (string name in request.Names)
            {
                if (!name.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase)) continue;
                string axisName = name.Substring(5);
                AxisStatus state = Wait(_motionCards.GetAxis(axisName).ReadStatusAsync(CancellationToken.None));
                if (state == null || state.Moving || double.IsNaN(state.Position) || double.IsInfinity(state.Position)
                    || double.IsNaN(state.Velocity) || double.IsInfinity(state.Velocity)
                    || Math.Abs(state.Velocity) > 0.001 || state.AlarmActive)
                { reason = "轴尚未确认停稳或仍有报警：" + axisName; return false; }
            }
            TaskInterlock interlock = request.ExitInterlock;
            if (interlock != null)
            {
                if (interlock.Axes == null || interlock.Inputs == null) { reason = "退出条件配置不完整。"; return false; }
                foreach (TaskAxisInterlock item in interlock.Axes)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.AxisName) || !item.MinimumPosition.HasValue || !item.MaximumPosition.HasValue
                        || double.IsNaN(item.MinimumPosition.Value) || double.IsInfinity(item.MinimumPosition.Value)
                        || double.IsNaN(item.MaximumPosition.Value) || double.IsInfinity(item.MaximumPosition.Value)
                        || item.MinimumPosition.Value > item.MaximumPosition.Value
                        || !request.Names.Contains("Axis:" + item.AxisName, StringComparer.OrdinalIgnoreCase))
                    { reason = "退出轴条件配置不完整。"; return false; }
                    AxisStatus state = Wait(_motionCards.GetAxis(item.AxisName).ReadStatusAsync(CancellationToken.None));
                    if (state == null || state.Moving || state.AlarmActive || !state.ServoEnabled
                        || double.IsNaN(state.Position) || double.IsInfinity(state.Position)
                        || double.IsNaN(state.Velocity) || double.IsInfinity(state.Velocity) || Math.Abs(state.Velocity) > 0.001
                        || (item.RequireHomed && (!state.Homed || state.RequiresHomingAfterLimitAlarm))
                        || state.Position < item.MinimumPosition.Value || state.Position > item.MaximumPosition.Value)
                    { reason = "退出轴状态或位置不满足：" + item.AxisName; return false; }
                }
                foreach (TaskInputInterlock input in interlock.Inputs)
                {
                    if (input == null || !input.Expected.HasValue || string.IsNullOrWhiteSpace(input.InputName))
                    { reason = "退出输入条件配置不完整。"; return false; }
                    bool? value = ReadInput(input.InputName);
                    if (!value.HasValue || value.Value != input.Expected.Value)
                    { reason = "退出输入不满足：" + input.InputName + "，当前=" + (value.HasValue ? value.Value.ToString() : "未知"); return false; }
                }
            }
            TaskCondition condition = interlock?.Condition ?? request.ExitCondition;
            if (condition != null)
            {
                bool result;
                if (!TaskConditionEvaluator.TryEvaluate(condition, Catalog, _variables.GetSnapshot(), out result, out reason) || !result) return false;
            }
            reason = "退出条件已采样确认。";
            return true;
        }

        /// <summary>在同一快照中读取请求和完成值；未知值不当作 false，变量未配置时明确报错。</summary>
        public void ReadHandshake(string requestName, string completedName, out bool? request, out bool? completed)
        {
            RequireBooleanVariable(requestName);
            RequireBooleanVariable(completedName);
            var snapshots = _variables.GetSnapshot();
            DeviceVariableSnapshot requestValue = snapshots.SingleOrDefault(value => string.Equals(value.Name, requestName, StringComparison.OrdinalIgnoreCase));
            DeviceVariableSnapshot completedValue = snapshots.SingleOrDefault(value => string.Equals(value.Name, completedName, StringComparison.OrdinalIgnoreCase));
            if (requestValue == null || completedValue == null)
                throw new InvalidOperationException("握手变量在当前变量表中不存在：" + requestName + " / " + completedName + "。");
            if (!string.Equals(requestValue.DataType, DeviceVariableType.Boolean.ToString(), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(completedValue.DataType, DeviceVariableType.Boolean.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("握手变量必须是布尔类型：" + requestName + " / " + completedName + "。");
            request = requestValue.HasValue ? (bool?)bool.Parse(requestValue.CurrentValue) : null;
            completed = completedValue.HasValue ? (bool?)bool.Parse(completedValue.CurrentValue) : null;
        }

        /// <summary>请求为真且完成为假时原子发布完成；未知或条件不满足时不发布。</summary>
        public bool TryPublishCompletion(string requestName, string completedName)
        {
            RequireBooleanVariable(requestName);
            RequireBooleanVariable(completedName);
            return _variables.TryTransitionBooleanPair(requestName, true, completedName, false, true);
        }

        /// <summary>请求已清除且完成为真时原子复归，避免丢失完成信号。</summary>
        public bool TryClearCompletion(string requestName, string completedName)
        {
            RequireBooleanVariable(requestName);
            RequireBooleanVariable(completedName);
            return _variables.TryTransitionBooleanPair(requestName, false, completedName, true, false);
        }

        /// <summary>取得设备命令权；工位流程运行中或已有其他任务会话时由运行时拒绝。</summary>
        public void ClaimMachine(object owner)
        {
            object token = owner ?? AnonymousOwner;
            if (_expectedSession.HasValue)
            {
                if (owner == null) throw new ArgumentNullException(nameof(owner), "正式任务设备必须提供控制器身份。");
                lock (_sync)
                {
                    if (_changingOwner || _closing || (_machineOwner != null && !ReferenceEquals(_machineOwner, token))
                        || (_machineOwner == null && _activeRuns.Count != 0))
                        throw new InvalidOperationException("任务设备正在切换控制权、已占用或仍有任务未收尾。");
                    if (_boundController != null && !ReferenceEquals(_boundController, token))
                        throw new InvalidOperationException("该正式任务设备已绑定其他控制器；新的控制器须创建自己的设备实例。");
                    _changingOwner = true;
                }
                try
                {
                    Wait(_runtime.AcquireTaskEngineControlAsync(this, CancellationToken.None, _expectedSession));
                    lock (_sync)
                    {
                        _boundController = token;
                        _machineOwner = token;
                    }
                }
                finally { lock (_sync) _changingOwner = false; }
                return;
            }
            lock (_sync)
            {
                if (_machineOwner != null)
                {
                    if (ReferenceEquals(_machineOwner, token)) return;
                    throw new InvalidOperationException("任务设备已被其他整机控制器占用。");
                }
                if (_activeRuns.Count != 0)
                    throw new InvalidOperationException("仍有任务运行未收尾，不能接管设备。");
                Wait(_runtime.AcquireTaskEngineControlAsync(this, CancellationToken.None));
                _machineOwner = token;
            }
        }

        /// <summary>归还设备命令权；运行中或所有权不匹配时明确报错。</summary>
        public void ReleaseMachine(object owner)
        {
            object token = owner ?? AnonymousOwner;
            lock (_sync)
            {
                if (_changingOwner) throw new InvalidOperationException("任务设备正在切换控制权。");
                if (!ReferenceEquals(_machineOwner, token))
                    throw new InvalidOperationException("任务设备所有权不匹配，不能由当前对象归还。");
                if (_activeRuns.Count != 0)
                    throw new InvalidOperationException("仍有任务运行未收尾，不能归还设备命令权。");
                RemoveSettledMoves();
                if (_unsettledMoves.Count != 0 || _pendingStops.Count != 0 || _beginningAxes.Count != 0)
                    throw new InvalidOperationException("轴动作或取消收尾仍未结束，不能归还设备命令权。");
                if (_resources.Read().Any(resource => resource.State == TaskResourceState.Held || resource.State == TaskResourceState.Blocked))
                    throw new InvalidOperationException("存在仍被占用或待确认的资源区域，不能关闭任务会话或归还设备控制权；请先核实并解除隔离。");
                if (_issuingBoundOutputs != 0 || _pendingOutputWrites.Count != 0 || _unconfirmedOutputs.Count != 0)
                    throw new InvalidOperationException("输出写入仍在执行或结果未确认，不能归还设备命令权。");
                if (_journal != null && !_journalCleared)
                    throw new InvalidOperationException("资源记录尚未安全清理，不能归还设备控制权。");
                if (!_expectedSession.HasValue)
                {
                    _runtime.ReleaseTaskEngineControl(this);
                    _machineOwner = null;
                    return;
                }
                _changingOwner = true;
            }
            try
            {
                _runtime.ReleaseTaskEngineControl(this, throughBoundDevice: true);
                lock (_sync) _machineOwner = null;
            }
            finally { lock (_sync) _changingOwner = false; }
        }

        /// <summary>登记即将启动的任务运行；受整机控制时拒绝独立入口混用。</summary>
        public void RegisterRun(string runId, object owner)
        {
            if (_expectedSession.HasValue)
            {
                _runtime.BeginTaskDeviceCommand(() => { RegisterRunCore(runId, owner); return true; }, _expectedSession, this);
                return;
            }
            RegisterRunCore(runId, owner);
        }

        /// <summary>在设备锁内确认本地所有者并登记运行；登记后禁止提前归还设备。</summary>
        private void RegisterRunCore(string runId, object owner)
        {
            object token = owner ?? AnonymousOwner;
            lock (_sync)
            {
                EnsureBoundDeviceAvailable();
                if (_closing) throw new InvalidOperationException("任务会话正在关闭，不能登记新运行。");
                if (_runtime.IsShutdownInProgress) throw new InvalidOperationException("设备正在安全退出，不能登记新任务运行。");
                if (_machineOwner == null || !ReferenceEquals(_machineOwner, token))
                {
                    throw new InvalidOperationException("任务必须先取得整机控制权，不能使用独立任务入口。");
                }

                _activeRuns.Add(runId);
            }
        }

        /// <summary>核实工作者、轴动作与所有资源均已退出，再在后台清理当前会话记录。</summary>
        public async Task CompleteResourceJournalAsync()
        {
            if (_journal == null) return;
            lock (_sync)
            {
                RemoveSettledMoves();
                if (_changingOwner || _issuingBoundOutputs != 0 || _activeRuns.Count != 0 || _unsettledMoves.Count != 0 || _pendingStops.Count != 0 || _beginningAxes.Count != 0
                    || _pendingOutputWrites.Count != 0 || _unconfirmedOutputs.Count != 0)
                    throw new InvalidOperationException("任务、轴动作或资源尚未全部收尾，不能清理资源记录。");
                _closing = true;
            }
            // 被取消的排队申请可能正在落盘；异步等它收尾后再判断是否可清理。
            await _resources.SettleJournalWritesAsync().ConfigureAwait(false);
            if (_resources.Read().Count != 0)
                throw new InvalidOperationException("任务、轴动作或资源尚未全部收尾，不能清理资源记录。");
            await Task.Run(() => _journal.ClearSafeSession()).ConfigureAwait(false);
            lock (_sync) _journalCleared = true;
        }

        /// <summary>动作全部退出后移除运行登记。</summary>
        public void UnregisterRun(string runId)
        {
            lock (_sync)
            {
                _activeRuns.Remove(runId);
            }
        }

        /// <summary>在设备锁内清理原动作及设备收尾均已结束的凭证；未知动作继续保留。</summary>
        private void RemoveSettledMoves()
        {
            _unsettledMoves.RemoveWhere(move => move.IsSettled);
        }

        /// <summary>读取一根轴已接受的动作次数；未记录时返回零。</summary>
        private int AcceptedCommands(string axisName)
        {
            lock (_sync)
            {
                int count;
                return _acceptedCommands.TryGetValue(axisName, out count) ? count : 0;
            }
        }

        /// <summary>记录一次已接受的动作。</summary>
        private void RecordAcceptedCommand(string axisName)
        {
            lock (_sync)
            {
                int count;
                _acceptedCommands.TryGetValue(axisName, out count);
                _acceptedCommands[axisName] = count + 1;
            }
        }

        /// <summary>确认握手变量是布尔类型；类型不符时明确报错。</summary>
        private void RequireBooleanVariable(string name)
        {
            DeviceVariableType type;
            if (!Catalog.Variables.TryGetValue(name, out type) || type != DeviceVariableType.Boolean)
            {
                throw new InvalidOperationException("握手变量必须是已配置的布尔变量：" + name + "。");
            }
        }

        /// <summary>
        /// 同步等待设备异步调用完成。任务执行器在后台线程按固定节奏轮询，
        /// 这里的等待发生在执行线程而不是界面线程；不要在界面线程调用本设备的同步方法。
        /// </summary>
        private static void Wait(Task task)
        {
            task.GetAwaiter().GetResult();
        }

        /// <summary>同步等待设备异步调用并返回结果，语义同 <see cref="Wait(Task)"/>。</summary>
        private static T Wait<T>(Task<T> task)
        {
            return task.GetAwaiter().GetResult();
        }
    }
}
