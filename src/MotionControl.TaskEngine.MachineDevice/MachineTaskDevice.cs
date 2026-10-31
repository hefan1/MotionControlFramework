using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.MachineDevice
{
    /// <summary>
    /// 用真实运动控制卡驱动任务执行的设备实现：轴动作走 IAxis、DI/DO 走 ILogicalIo、
    /// 变量走 IDeviceVariableStore，整机控制权通过 MachineRuntime 统一仲裁。
    /// 轴组插补和互锁节点尚未接入，调用时明确报错，不退化执行。
    /// </summary>
    public sealed partial class MachineTaskDevice : ITaskDevice
    {
        /// <summary>未指定所有者时使用的匿名所有权标记，保证同一实例内的所有权比对稳定。</summary>
        private static readonly object AnonymousOwner = new object();

        /// <summary>本设备的资源协调器，按目录中的资源名建立。</summary>
        private readonly TaskResourceCoordinator _resources;

        /// <summary>已连接的控制卡组，按逻辑轴名称取轴。</summary>
        private readonly MotionCardGroup _motionCards;

        /// <summary>按逻辑名称读写 DI/DO 的入口。</summary>
        private readonly ILogicalIo _logicalIo;

        /// <summary>设备变量存储，任务流程写入的变量值保存在这里。</summary>
        private readonly IDeviceVariableStore _variables;

        /// <summary>整机运行时，用于占用和归还设备命令权。</summary>
        private readonly MachineRuntime _runtime;

        /// <summary>保护所有权、运行登记和命令计数。</summary>
        private readonly object _sync = new object();

        /// <summary>每根轴已接受的动作次数，供重复执行和漏执行检查使用。</summary>
        private readonly Dictionary<string, int> _acceptedCommands =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>尚未收尾的任务运行编号。</summary>
        private readonly HashSet<string> _activeRuns = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>当前独占本设备的对象；为空表示没有整机控制者。</summary>
        private object _machineOwner;

        /// <summary>用已连接的控制卡、逻辑 IO、变量存储和整机运行时建立任务设备。</summary>
        public MachineTaskDevice(
            TaskResourceCatalog catalog,
            MotionCardGroup motionCards,
            ILogicalIo logicalIo,
            IDeviceVariableStore variables,
            MachineRuntime runtime)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _motionCards = motionCards ?? throw new ArgumentNullException(nameof(motionCards));
            _logicalIo = logicalIo ?? throw new ArgumentNullException(nameof(logicalIo));
            _variables = variables ?? throw new ArgumentNullException(nameof(variables));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _resources = new TaskResourceCoordinator(catalog.Resources);
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
            Wait(_logicalIo.WriteOutputAsync(name, value, CancellationToken.None));
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

        /// <summary>读取一根逻辑轴的当前状态，位置和速度使用该轴工程单位。</summary>
        public TaskAxisState ReadAxis(string name)
        {
            IAxis axis = _motionCards.GetAxis(name);
            AxisStatus status = Wait(axis.ReadStatusAsync(CancellationToken.None));
            return new TaskAxisState(
                name,
                status.Position,
                status.ServoEnabled,
                status.Homed,
                status.Moving,
                status.AlarmActive ? status.AlarmMessage : null,
                AcceptedCommands(name));
        }

        /// <summary>真机互锁节点尚未接入；明确拒绝，避免用未验证的判断放行动作。</summary>
        public bool CheckInterlock(TaskInterlock interlock, TaskCondition legacyCondition, out string reason)
        {
            throw new NotSupportedException(
                "真机任务暂不支持互锁节点：任务定义中的进入/退出互锁尚未接入设备侧判断。"
                + "请先在任务配置中移除互锁，或等待该能力接入后再运行。");
        }

        /// <summary>读取当前资源占用与待确认区域。</summary>
        public IReadOnlyList<TaskResourceSnapshot> ReadResources()
        {
            return _resources.Read();
        }

        /// <summary>人工核实后恢复一个待确认资源区域。</summary>
        public bool TryRecoverResources(string requestId, string confirmation, out string reason)
        {
            TaskResourceRequest request = _resources.FindBlocked(requestId);
            if (request == null)
            {
                reason = "找不到待确认的资源区域：" + requestId + "。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(confirmation))
            {
                reason = "恢复资源区域必须提供现场确认说明。";
                return false;
            }

            bool recovered = _resources.Recover(request);
            reason = recovered ? null : "资源区域仍被占用或确认不匹配。";
            return recovered;
        }

        /// <summary>读取请求/完成握手的两个变量；变量未配置时明确报错。</summary>
        public void ReadHandshake(string requestName, string completedName, out bool? request, out bool? completed)
        {
            RequireBooleanVariable(requestName);
            RequireBooleanVariable(completedName);
            request = _variables.GetBoolean(requestName);
            completed = _variables.GetBoolean(completedName);
        }

        /// <summary>请求仍有效时置位完成信号；请求已撤销或变量缺失时返回 false。</summary>
        public bool TryPublishCompletion(string requestName, string completedName)
        {
            try
            {
                if (!_variables.GetBoolean(requestName))
                {
                    return false;
                }

                _variables.SetBoolean(completedName, true);
                return true;
            }
            catch (KeyNotFoundException)
            {
                return false;
            }
        }

        /// <summary>清除完成信号；变量缺失时返回 false。</summary>
        public bool TryClearCompletion(string requestName, string completedName)
        {
            try
            {
                _variables.SetBoolean(completedName, false);
                return true;
            }
            catch (KeyNotFoundException)
            {
                return false;
            }
        }

        /// <summary>取得设备命令权；设备正在运行或已配置工位流程时由运行时拒绝。</summary>
        public void ClaimMachine(object owner)
        {
            object token = owner ?? AnonymousOwner;
            lock (_sync)
            {
                if (_machineOwner != null)
                {
                    if (ReferenceEquals(_machineOwner, token))
                    {
                        return;
                    }

                    throw new InvalidOperationException("任务设备已被其他整机控制器占用。");
                }
            }

            Wait(_runtime.AcquireTaskEngineControlAsync(CancellationToken.None));
            lock (_sync)
            {
                _machineOwner = token;
            }
        }

        /// <summary>归还设备命令权；运行中或所有权不匹配时明确报错。</summary>
        public void ReleaseMachine(object owner)
        {
            object token = owner ?? AnonymousOwner;
            lock (_sync)
            {
                if (!ReferenceEquals(_machineOwner, token))
                {
                    throw new InvalidOperationException("任务设备所有权不匹配，不能由当前对象归还。");
                }
            }

            if (_activeRuns.Count != 0)
            {
                throw new InvalidOperationException("仍有任务运行未收尾，不能归还设备命令权。");
            }

            _runtime.ReleaseTaskEngineControl();
            lock (_sync)
            {
                _machineOwner = null;
            }
        }

        /// <summary>登记即将启动的任务运行；受整机控制时拒绝独立入口混用。</summary>
        public void RegisterRun(string runId, object owner)
        {
            object token = owner ?? AnonymousOwner;
            lock (_sync)
            {
                if (_machineOwner != null && !ReferenceEquals(_machineOwner, token))
                {
                    throw new InvalidOperationException("设备受整机控制，不能混用独立任务入口。");
                }

                _activeRuns.Add(runId);
            }
        }

        /// <summary>动作全部退出后移除运行登记。</summary>
        public void UnregisterRun(string runId)
        {
            lock (_sync)
            {
                _activeRuns.Remove(runId);
            }
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
