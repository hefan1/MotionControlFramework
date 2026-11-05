using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Wpf.Monitoring;
using MotionControl.TaskEngine.Wpf.Simulation;
using MotionControl.TaskEngine.Wpf.Views;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>用实际内存任务及并行节点执行快照核对当前执行位置和只读查看位置。</summary>
    internal static class TaskNodeSummaryChecks
    {
        internal static void Run()
        {
            var catalog = new TaskResourceCatalog(Array.Empty<TaskAxisInfo>(), Array.Empty<string>(), Array.Empty<string>(),
                new Dictionary<string, DeviceVariableType>(), Array.Empty<TaskTeachPoint>());
            var device = new InMemoryTaskDevice(catalog);
            SequenceNode Sequence(string id, params TaskNode[] nodes) =>
                new SequenceNode { Id = id, Name = id, Children = nodes.ToList() };
            TaskDefinition TaskWith(string id, params TaskNode[] nodes) =>
                new TaskDefinition { TaskId = id, Name = id, Root = Sequence("root", nodes) };
            TaskDefinition Child(string id) => TaskWith(id, new DelayNode { Id = "same", Name = "等待", Milliseconds = 30000 });
            var main = TaskWith("Main", new DelayNode { Id = "previous", Name = "前一步", Milliseconds = 1 },
                new ParallelGroupNode { Id = "group", Name = "并行", Branches = new List<TaskNode> {
                    Sequence("left", new CallTaskNode { Id = "callLeft", Name = "调用左支", TaskId = "Left" }),
                    Sequence("right", new CallTaskNode { Id = "callRight", Name = "调用右支", TaskId = "Right" }) } });
            var config = new TaskMachineConfiguration { MachineId = "NodeSummary", Name = "节点检查",
                Stations = new List<StationTaskConfiguration> { new StationTaskConfiguration {
                    StationId = "station", Name = "工位", TaskId = "Main", Enabled = true, Mode = StationTaskMode.Once } } };
            var session = new TaskMonitorSession(config, new[] { main, Child("Left"), Child("Right") }, device, "内存检查", false);
            try
            {
                var view = new TaskMonitorView(session);
                var active = (TextBlock)view.FindName("ExecutingNodesText");
                var viewed = (TextBlock)view.FindName("ViewedNodeText");
                var tree = (TreeView)view.FindName("StepsTree");
                var follow = (CheckBox)view.FindName("FollowBox");
                view.Refresh();
                Require(active.Text == view.T("没有节点运行快照"), "尚未启动却显示了正在执行的节点。");
                session.Machine.Reset();
                session.Machine.Start();
                var clock = Stopwatch.StartNew();
                TaskNodeSnapshot[] leaves;
                do
                {
                    leaves = session.Machine.GetSnapshot().Stations.Single().Run?.Nodes
                        .Where(n => n.NodeId == "same" && n.State == TaskNodeState.Waiting).ToArray() ?? Array.Empty<TaskNodeSnapshot>();
                    if (leaves.Length == 2) break;
                    if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException("未等到两个并行分支进入等待。");
                    Thread.Sleep(10);
                } while (true);
                view.Refresh();
                follow.IsChecked = false;
                var previous = TaskStepTree.Flatten(((IEnumerable<TaskStepRow>)tree.ItemsSource).ToList())
                    .Single(row => row.Node.Id == "previous");
                tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null, previous, TreeView.SelectedItemChangedEvent));
                foreach (var language in new[] { "zh-CN", "en-US" })
                {
                    view.SetLanguage(language);
                    Require(leaves.All(n => active.Text.Contains(n.TaskId + " / same") && active.Text.Contains(n.ExecutionId))
                        && active.Text.Split(new[] { Environment.NewLine }, StringSplitOptions.None).Length == 2,
                        "并行活动分支遗漏、容器被当成末端，或跨任务同名节点混淆。");
                    Require(viewed.Text.Contains("Main / previous") && !active.Text.Contains("Main / previous")
                        && follow.IsChecked == false && session.Machine.GetSnapshot().State == TaskMachineState.Running,
                        "查看历史步骤改变了执行、选择或跟随设置。");
                }
                session.Machine.PauseAsync().GetAwaiter().GetResult();
                view.Refresh();
                Require(!leaves.Any(n => active.Text.Contains(n.ExecutionId)) && viewed.Text.Contains("Main / previous"),
                    "暂停后仍把保留的节点当作当前执行，或清除了操作员的选择。");
            }
            finally { session.Machine.CloseAsync().GetAwaiter().GetResult(); }
        }

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
