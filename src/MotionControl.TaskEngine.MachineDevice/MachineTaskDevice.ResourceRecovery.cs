using System;
using System.Collections.Generic;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.MachineDevice
{
    /// <summary>现场资源恢复时对明确声明的输出进行只读复核。</summary>
    public sealed partial class MachineTaskDevice
    {
        /// <summary>读取本次资源组内的输出；不可读时拒绝恢复，不改变任何输出。</summary>
        private bool ReadRecoveryResourceOutputs(TaskResourceRequest request, out Dictionary<string, bool> values, out string reason)
        {
            values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string resource in request.Names)
            {
                if (!resource.StartsWith("Output:", StringComparison.OrdinalIgnoreCase)) continue;
                string name = resource.Substring(7);
                bool? value = ReadOutput(name);
                if (!value.HasValue) { reason = "资源输出状态未知：" + name; return false; }
                if (value.Value) { reason = "资源输出仍有效，不能解除隔离：" + name + "。请按设备规程处理，系统不会自动切换输出。"; return false; }
                values.Add(name, value.Value);
            }
            reason = null;
            return true;
        }

        /// <summary>两次读数不同表示现场状态正在变化，不能按旧确认放行。</summary>
        private static bool RecoveryOutputsMatch(Dictionary<string, bool> first, Dictionary<string, bool> second, out string reason)
        {
            foreach (var pair in first)
            {
                bool current;
                if (!second.TryGetValue(pair.Key, out current) || current != pair.Value)
                { reason = "资源输出状态在复核期间发生变化：" + pair.Key; return false; }
            }
            reason = null;
            return true;
        }
    }
}
