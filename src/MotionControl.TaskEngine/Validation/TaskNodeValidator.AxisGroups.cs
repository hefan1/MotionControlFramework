using System;
using System.Collections.Generic;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Validation
{
    /// <summary>轴组定位复用单轴参数规则，并检查同卡和重复轴边界。</summary>
    internal sealed partial class TaskNodeValidator
    {
        /// <summary>逐项检查定位参数，不给轴组强加路径速度或隐含安全动作。</summary>
        private void CheckAxisGroup(MoveAxisGroupNode node, string path)
        {
            if (node.Targets == null || node.Targets.Count < 2 || node.Targets.Count > 64)
            {
                Error(node, path + ".targets", "轴组定位需要 2 到 64 根轴；上限仅限制定义规模，不代表卡能力。");
                return;
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string controller = null;
            for (int index = 0; index < node.Targets.Count; index++)
            {
                AxisPositionTarget target = node.Targets[index];
                string itemPath = path + ".targets[" + index + "]";
                if (target == null) { Error(node, itemPath, "轴定位目标不能为空。"); continue; }
                if (!names.Add(target.AxisName ?? string.Empty)) Error(node, itemPath, "同一逻辑轴不能重复加入定位组。");
                if (!Enum.IsDefined(typeof(TaskMoveMode), target.Mode)) Error(node, itemPath + ".mode", "坐标方式无效。");
                var axis = CheckAxisTarget(target.AxisName, target.Target, target.Mode == TaskMoveMode.Relative, node, itemPath);
                CheckProfile(target.Profile, node, itemPath + ".profile");
                if (axis == null) continue;
                if (controller == null) controller = axis.ControllerId;
                else if (!string.Equals(controller, axis.ControllerId, StringComparison.OrdinalIgnoreCase))
                    Error(node, itemPath, "当前轴组定位只支持同一卡，不能混用不同控制卡。");
            }
        }
    }
}
