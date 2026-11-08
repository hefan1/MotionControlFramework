using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.TaskEditing.Checks
{
    internal static partial class Program
    {
        /// <summary>完整工作区的合法组定位定义可保留编辑，但提供正式设备能力后必须在文件应用前拒绝。</summary>
        private static void V18WorkspaceCapabilityPreflight()
        {
            using (var test = new Workspace())
            {
                TaskWorkspaceDocument draft = test.Store.Load(Workspace.Id, false);
                var catalog = test.Store.LoadCatalog(draft);
                var axes = catalog.Axes.Values.GroupBy(axis => axis.ControllerId, StringComparer.OrdinalIgnoreCase)
                    .First(group => group.Count() >= 2).Take(2).ToArray();
                var groupMove = new MoveAxisGroupNode { Id = "workspace-group", Name = "工作区组定位" };
                foreach (var axis in axes)
                {
                    groupMove.Targets.Add(new AxisPositionTarget
                    {
                        AxisName = axis.Name, Mode = TaskMoveMode.Absolute,
                        Target = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Number,
                            Constant = axis.MinimumPosition.Value.ToString("R", CultureInfo.InvariantCulture) },
                        Profile = new TaskMotionProfile { Speed = 1, Acceleration = 10, Deceleration = 10 }
                    });
                }
                var task = new TaskDefinition
                {
                    TaskId = "WorkspaceCapability", Name = "能力准入定义",
                    Root = new SequenceNode { Id = "workspace-root", Name = "顺序",
                        Children = new List<TaskNode>
                        {
                            new SetOutputNode { Id = "before-group-output", Name = "前序输出", OutputName = catalog.Outputs.First(), Value = true },
                            groupMove
                        } }
                };
                draft.Tasks.Add(new TaskWorkspaceTask { FileName = "WorkspaceCapability.json", Definition = task });
                string activeBefore = test.ActiveBytes();
                var definitionIssues = test.Store.Check(draft);
                Assert(!definitionIssues.Any(issue => issue.Severity == TaskIssueSeverity.Error),
                    "未提供后端能力时，合法组定位定义应能完成定义检查：" + string.Join("\n", definitionIssues));

                var capabilities = new TaskDeviceCapabilities("MachineTaskDevice", false, false);
                var restricted = new TaskWorkspaceStore(test.Configuration, capabilities);
                var issues = restricted.Check(draft);
                Assert(issues.Any(issue => issue.Severity == TaskIssueSeverity.Error && issue.TaskId == task.TaskId
                    && issue.NodeId == "workspace-group" && issue.Field == "Capability.MoveAxisGroup"),
                    "工作区未把当前设备能力接入完整检查并定位组定位节点。");
                string warning;
                Throws(() => restricted.Apply(draft, out warning));
                Assert(activeBefore == test.ActiveBytes() && !restricted.HasPendingApply(Workspace.Id),
                    "后端缺组定位能力仍改写运行文件或创建应用标记。");
            }
        }
    }
}
