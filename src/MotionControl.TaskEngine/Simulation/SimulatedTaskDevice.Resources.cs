using System;
using System.Collections.Generic;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Simulation
{
    /// <summary>资源监控及异常后的显式恢复；不修改轴位置、输出，也不重启旧任务。</summary>
    public sealed partial class SimulatedTaskDevice
    {
        /// <summary>取得所有等待、占用、待确认记录的只读副本。</summary>
        public IReadOnlyList<TaskResourceSnapshot> ReadResources()
        {
            return Resources.Read();
        }

        /// <summary>取得占用与场景配置共用设备锁，防止检查空闲后恰好被新申请占用。</summary>
        internal bool TryAcquireResources(TaskResourceRequest request, out string reason)
        {
            lock (_sync) return Resources.TryAcquire(request, out reason);
        }

        /// <summary>确认区域已检查后尝试放行；仍须通过停止、报警及原退出条件复核。</summary>
        public bool TryRecoverResources(string requestId, string confirmation, out string reason)
        {
            lock (_sync)
            {
                // 锁顺序固定为设备再协调器；协调器绝不反向回调设备，避免互相等待。
                if (string.IsNullOrWhiteSpace(confirmation))
                {
                    reason = "请填写已核实区域状态的确认说明。";
                    return false;
                }
                TaskResourceRequest request = Resources.FindBlocked(requestId);
                if (request == null)
                {
                    reason = "待确认记录不存在、已恢复或仍在执行；不能使用过期的申请编号。";
                    return false;
                }
                foreach (string name in request.Names)
                {
                    if (!name.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase)) continue;
                    string axisName = name.Substring(5);
                    SimulatedAxis axis = FindAxis(axisName);
                    if (axis.Operation != null || !string.IsNullOrWhiteSpace(axis.Alarm))
                    {
                        reason = "轴尚未停止或仍有报警：" + axisName;
                        return false;
                    }
                }
                if (!CheckInterlock(request.ExitInterlock, request.ExitCondition, out reason)) return false;
                if (!Resources.Recover(request))
                {
                    reason = "申请状态已经改变，请重新读取资源状态。";
                    return false;
                }
                reason = "已解除待确认状态；确认说明：" + confirmation + "。旧任务不会自动重启。";
                return true;
            }
        }
    }
}
