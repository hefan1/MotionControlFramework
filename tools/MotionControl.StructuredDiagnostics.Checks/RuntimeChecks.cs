using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.StructuredDiagnostics.Checks
{
    /// <summary>让真实任务执行器运行在隔离内存设备上，验证诊断写入与快照边界。</summary>
    internal static class RuntimeChecks
    {
        /// <summary>未知输入保持未知；实际反馈变化后采样更新，但以前取得的快照不改变。</summary>
        internal static async Task InputWait()
        {
            var device = new InMemoryTaskDevice(Fixtures.Catalog());
            TaskRun run = new TaskExecutionEngine().Start(Fixtures.Definition(Fixtures.Wait("input", Fixtures.FirstInput)), device);
            try
            {
                await Until(() => Waiting(run, "input") != null, "输入等待没有发布诊断。").ConfigureAwait(false);
                TaskStepDiagnostics unknown = Waiting(run, "input").Diagnostics;
                Fixtures.Require(unknown.WaitKind == StationWaitKind.Input && unknown.WaitItems.Count == 1,
                    "输入等待没有显式类别和对象集合。");
                Fixtures.Require(unknown.WaitItems[0].ObjectId == Fixtures.FirstInput
                    && unknown.WaitItems[0].Quality == StationValueQuality.Unknown && unknown.WaitItems[0].Actual == null,
                    "未知输入被变成 false，或标点名称被改变。");
                device.SetInput(Fixtures.FirstInput, false);
                await Until(() => Waiting(run, "input")?.Diagnostics.WaitItems[0].Quality == StationValueQuality.Good,
                    "有效输入采样没有更新。").ConfigureAwait(false);
                Fixtures.Require(unknown.WaitItems[0].Actual == null && unknown.WaitItems[0].Quality == StationValueQuality.Unknown,
                    "后续采样修改了旧诊断快照。");
                device.SetInput(Fixtures.FirstInput, true);
                TaskRunSnapshot finished = await Complete(run).ConfigureAwait(false);
                Fixtures.Require(finished.State == TaskRunState.Completed, finished.Error?.ToString() ?? "有效输入没有完成等待。");
            }
            finally { await CancelAndFinish(run).ConfigureAwait(false); }
        }

        /// <summary>父级到期时节点保留父级数值时限及编号，不从异常说明提取数字。</summary>
        internal static async Task ParentTimeout()
        {
            var device = new InMemoryTaskDevice(Fixtures.Catalog());
            WaitInputNode input = Fixtures.Wait("input", Fixtures.FirstInput);
            input.TimeoutMs = 5000;
            TaskDefinition definition = Fixtures.Definition(input);
            definition.Root.TimeoutMs = 250;
            TaskRun run = new TaskExecutionEngine().Start(definition, device);
            try
            {
                TaskRunSnapshot finished = await Complete(run).ConfigureAwait(false);
                Fixtures.Require(finished.State == TaskRunState.Failed && finished.Error.FailureKind == TaskExecutionFailureKind.Timeout,
                    "父级到期没有形成明确的超时失败。");
                TaskStepDiagnostics diagnostic = finished.Nodes.Single(node => node.NodeId == "input").Diagnostics;
                Fixtures.Require(diagnostic.IsTimeout && diagnostic.TimeoutMilliseconds == 250 && diagnostic.TimeoutNodeId == "root",
                    "节点显示了自身 5000ms，而不是触发的父级 250ms。");
                Fixtures.Require(diagnostic.WaitItems.Count == 1 && diagnostic.WaitItems[0].ObjectId == Fixtures.FirstInput,
                    "父级超时丢失了被等待的实际输入。");
            }
            finally { await CancelAndFinish(run).ConfigureAwait(false); }
        }

        /// <summary>两个顺序分支并行等待各自输入，末端诊断同时存在，完成其中一项不覆盖另一项。</summary>
        internal static async Task ParallelWaits()
        {
            var device = new InMemoryTaskDevice(Fixtures.Catalog());
            var parallel = new ParallelGroupNode { Id = "parallel", Name = "双输入并行", Branches = new List<TaskNode> {
                new SequenceNode { Id = "first-branch", Name = "第一输入分支", Children = new List<TaskNode> {
                    Fixtures.Wait("first", Fixtures.FirstInput)
                } },
                new SequenceNode { Id = "second-branch", Name = "第二输入分支", Children = new List<TaskNode> {
                    Fixtures.Wait("second", Fixtures.SecondInput)
                } }
            } };
            TaskRun run = new TaskExecutionEngine().Start(Fixtures.Definition(parallel), device);
            try
            {
                await Until(() => Waiting(run, "first") != null && Waiting(run, "second") != null,
                    "两个输入没有同时发布等待诊断。").ConfigureAwait(false);
                TaskNodeSnapshot[] active = run.GetSnapshot().Nodes.Where(node => node.State == TaskNodeState.Waiting
                    && node.Diagnostics?.WaitKind == StationWaitKind.Input).ToArray();
                Fixtures.Require(active.Length == 2 && active.SelectMany(node => node.Diagnostics.WaitItems)
                    .Select(item => item.ObjectId).Contains(Fixtures.SecondInput), "并行等待只保留了第一输入。");
                device.SetInput(Fixtures.FirstInput, true);
                await Until(() => run.GetSnapshot().Nodes.Any(node => node.NodeId == "first" && node.State == TaskNodeState.Completed),
                    "第一输入满足后没有完成。").ConfigureAwait(false);
                Fixtures.Require(Waiting(run, "second") != null, "完成第一分支覆盖了第二分支的等待证据。");
                device.SetInput(Fixtures.SecondInput, true);
                Fixtures.Require((await Complete(run).ConfigureAwait(false)).State == TaskRunState.Completed, "两个输入均满足后并行任务没有完成。");
            }
            finally { await CancelAndFinish(run).ConfigureAwait(false); }
        }

        /// <summary>取得指定步骤当前等待快照；没有完整诊断时继续等待，避免检查半次发布。</summary>
        private static TaskNodeSnapshot Waiting(TaskRun run, string nodeId)
        {
            return run.GetSnapshot().Nodes.FirstOrDefault(node => node.NodeId == nodeId
                && node.State == TaskNodeState.Waiting && node.Diagnostics != null && node.Diagnostics.WaitItems.Count > 0);
        }

        /// <summary>按可观察条件轮询，给检查设置自身上限，避免 CI 无限等候。</summary>
        private static async Task Until(Func<bool> condition, string message)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException(message);
                await Task.Delay(5).ConfigureAwait(false);
            }
        }

        /// <summary>等待运行完成收尾；不能把先出现的工位故障锁存当作运行已终结。</summary>
        private static async Task<TaskRunSnapshot> Complete(TaskRun run)
        {
            if (await Task.WhenAny(run.Completion, Task.Delay(7000)).ConfigureAwait(false) != run.Completion)
                throw new TimeoutException("诊断检查中的运行没有完成收尾：" + run.RunId);
            return await run.Completion.ConfigureAwait(false);
        }

        /// <summary>检查失败也要停止内存动作并完成收尾，避免影响同进程后续检查。</summary>
        private static async Task CancelAndFinish(TaskRun run)
        {
            if (!run.Completion.IsCompleted) run.Cancel();
            await Complete(run).ConfigureAwait(false);
        }
    }
}
