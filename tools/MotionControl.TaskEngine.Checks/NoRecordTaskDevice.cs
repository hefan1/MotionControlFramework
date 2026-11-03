using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>只转发任务必需设备能力，不实现逐件记录接口，用于验证记录可选。</summary>
    internal sealed class NoRecordTaskDevice : ITaskDevice, ITaskInterlockSupport
    {
        /// <summary>实际提供内存运动和 IO 的设备。</summary>
        private readonly InMemoryTaskDevice _device;
        /// <summary>绑定可观察的内存设备。</summary>
        internal NoRecordTaskDevice(InMemoryTaskDevice device) { _device = device; }
        /// <summary>取得设备目录。</summary>
        public TaskResourceCatalog Catalog => _device.Catalog;
        /// <summary>取得设备资源协调器。</summary>
        public TaskResourceCoordinator Resources => _device.Resources;
        /// <summary>报告内存设备的互锁能力。</summary>
        public bool SupportsInterlockConditions => _device.SupportsInterlockConditions;
        /// <summary>读取输入。</summary>
        public bool? ReadInput(string name) => _device.ReadInput(name);
        /// <summary>读取输出。</summary>
        public bool? ReadOutput(string name) => _device.ReadOutput(name);
        /// <summary>写入输出。</summary>
        public void WriteOutput(string name, bool value) => _device.WriteOutput(name, value);
        /// <summary>写入变量。</summary>
        public void WriteVariable(string name, object value) => _device.WriteVariable(name, value);
        /// <summary>原子写入两个布尔变量。</summary>
        public void WriteBooleanPair(string firstName, bool firstValue, string secondName, bool secondValue) => _device.WriteBooleanPair(firstName, firstValue, secondName, secondValue);
        /// <summary>读取变量快照。</summary>
        public IReadOnlyList<DeviceVariableSnapshot> ReadVariables() => _device.ReadVariables();
        /// <summary>读取轴状态。</summary>
        public TaskAxisState ReadAxis(string name) => _device.ReadAxis(name);
        /// <summary>校验互锁条件。</summary>
        public bool CheckInterlock(TaskInterlock interlock, TaskCondition legacyCondition, out string reason) => _device.CheckInterlock(interlock, legacyCondition, out reason);
        /// <summary>读取当前资源。</summary>
        public IReadOnlyList<TaskResourceSnapshot> ReadResources() => _device.ReadResources();
        /// <summary>显式确认待恢复资源。</summary>
        public bool TryRecoverResources(string requestId, string confirmation, out string reason) => _device.TryRecoverResources(requestId, confirmation, out reason);
        /// <summary>接受单轴运动。</summary>
        public ITaskAxisMove BeginMove(string name, double target, bool relative, bool home, AxisMotionProfile profile) => _device.BeginMove(name, target, relative, home, profile);
        /// <summary>推进内存单轴运动。</summary>
        public void AdvanceMove(ITaskAxisMove move, double progress) => _device.AdvanceMove(move, progress);
        /// <summary>检查单轴到位。</summary>
        public bool IsMoveComplete(ITaskAxisMove move, double progress) => _device.IsMoveComplete(move, progress);
        /// <summary>结束单轴运动。</summary>
        public Task EndMoveAsync(ITaskAxisMove move, bool completed, CancellationToken token) => _device.EndMoveAsync(move, completed, token);
        /// <summary>接受一组轴动作。</summary>
        public IReadOnlyList<ITaskAxisGroupMove> BeginGroupMove(IReadOnlyList<TaskAxisGroupTarget> targets) => _device.BeginGroupMove(targets);
        /// <summary>推进内存轴组动作。</summary>
        public bool AdvanceGroupMove(IReadOnlyList<ITaskAxisGroupMove> moves, double elapsedMilliseconds, out string detail) => _device.AdvanceGroupMove(moves, elapsedMilliseconds, out detail);
        /// <summary>结束轴组动作。</summary>
        public Task EndGroupMoveAsync(IReadOnlyList<ITaskAxisGroupMove> moves, CancellationToken token) => _device.EndGroupMoveAsync(moves, token);
        /// <summary>确认轴已停止。</summary>
        public Task ConfirmStoppedAsync(string name, CancellationToken token) => _device.ConfirmStoppedAsync(name, token);
        /// <summary>读取握手变量对。</summary>
        public void ReadHandshake(string requestName, string completedName, out bool? request, out bool? completed) => _device.ReadHandshake(requestName, completedName, out request, out completed);
        /// <summary>条件满足时发布完成握手。</summary>
        public bool TryPublishCompletion(string requestName, string completedName) => _device.TryPublishCompletion(requestName, completedName);
        /// <summary>条件满足时清除完成握手。</summary>
        public bool TryClearCompletion(string requestName, string completedName) => _device.TryClearCompletion(requestName, completedName);
        /// <summary>申请整机控制权。</summary>
        public void ClaimMachine(object owner) => ((ITaskDevice)_device).ClaimMachine(owner);
        /// <summary>释放整机控制权。</summary>
        public void ReleaseMachine(object owner) => ((ITaskDevice)_device).ReleaseMachine(owner);
        /// <summary>登记运行。</summary>
        public void RegisterRun(string runId, object owner) => ((ITaskDevice)_device).RegisterRun(runId, owner);
        /// <summary>解除运行登记。</summary>
        public void UnregisterRun(string runId) => ((ITaskDevice)_device).UnregisterRun(runId);
    }
}
