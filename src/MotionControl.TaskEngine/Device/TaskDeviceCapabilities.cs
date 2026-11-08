using System;

namespace MotionControl.TaskEngine.Device
{
    /// <summary>由实际设备实现提供的任务能力说明；配置文件和界面不能勾选改变驱动能力。</summary>
    public sealed class TaskDeviceCapabilities
    {
        /// <summary>实际提供能力的设备实现名称，供问题列表说明来源。</summary>
        public string BackendName { get; private set; }

        /// <summary>是否支持多轴分别定位，不表示插补或同步到达。</summary>
        public bool SupportsAxisGroupMove { get; private set; }

        /// <summary>是否支持资源范围的进入和退出条件，不表示持续硬件安全监控。</summary>
        public bool SupportsInterlockConditions { get; private set; }

        /// <summary>由 Host 装配代码或设备实现声明当前实际能力，不读取用户可修改的支持开关。</summary>
        public TaskDeviceCapabilities(string backendName, bool supportsAxisGroupMove, bool supportsInterlockConditions)
        {
            if (string.IsNullOrWhiteSpace(backendName)) throw new ArgumentException("任务能力来源不能为空。", nameof(backendName));
            BackendName = backendName;
            SupportsAxisGroupMove = supportsAxisGroupMove;
            SupportsInterlockConditions = supportsInterlockConditions;
        }

        /// <summary>读取设备明确实现的可选能力；旧设备没有声明时保守报告不支持。</summary>
        public static TaskDeviceCapabilities FromDevice(ITaskDevice device)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            var group = device as ITaskAxisGroupSupport;
            var interlock = device as ITaskInterlockSupport;
            return new TaskDeviceCapabilities(device.GetType().Name,
                group != null && group.SupportsAxisGroupMove,
                interlock != null && interlock.SupportsInterlockConditions);
        }
    }
}
