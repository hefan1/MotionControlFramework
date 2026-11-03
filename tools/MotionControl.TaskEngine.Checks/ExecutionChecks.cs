using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Device.InMemory;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>执行及资源检查组织与小型夹具；仅创建隔离内存设备。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>当前检查创建的运行，失败时也统一取消收尾。</summary>
        private static readonly List<TaskRun> Runs = new List<TaskRun>();

        /// <summary>逐项运行执行检查，并在每项结束后确认没有遗留运行。</summary>
        internal static int RunAll()
        {
            var checks = new Dictionary<string, Func<Task>>
            {
                { "诊断未知输入与不可变采样", DiagnosticInputSamples },
                { "诊断分支固定判断证据", DiagnosticBranchDecision },
                { "诊断相对运动接受终点", DiagnosticAcceptedTarget },
                { "诊断暂停冻结延时", DiagnosticDelayPause },
                { "顺序、示教与动态目标", SequenceAndTargets },
                { "分支业务 NG 与组合条件", BranchConditions },
                { "全部变量类型赋值和比较", AllValueTypes },
                { "未知 DI 和无效变量等待", UnknownValues },
                { "输入采样和未知数据拦截", InputSampling },
                { "锁存编码读取与无效信号拦截", InputWordSampling },
                { "轴等待位置及状态拦截", AxisPositionWait },
                { "互斥输入等待、暂停和原子变量发布", ExclusiveInputs },
                { "输出读回等待未知值及暂停", OutputReadbackWait },
                { "无效分支值拒绝执行", InvalidBranch },
                { "独立分支并行和汇合", ParallelJoin },
                { "独立轴同时运动", IndependentAxes },
                { "并行失败取消兄弟动作", ParallelFailure },
                { "节点超时与父级时限", Timeouts },
                { "暂停恢复保留相对终点", PauseRelativeMove },
                { "暂停排除超时和延时计时", PauseTimeBudget },
                { "暂停期间取消并停止写值", CancelWhilePaused },
                { "立即取消和外部令牌", ImmediateCancellation },
                { "同轴排队和隔离运行", AxisConflicts },
                { "未使能、未回零和报警拦截", AxisPreconditions },
                { "运行报警和动态行程", DynamicMotionErrors },
                { "内存回零和停止语义", HomeAndStop },
                { "子任务重复调用与定义冻结", CallsAndFrozenDefinitions },
                { "不支持能力在动作前拒绝", UnsupportedBeforeStart },
                { "运行状态隔离和不可变快照", IsolatedSnapshots },
                { "子任务展开数量保护", ExpansionLimit },
                { "资源整组申请和独立组并行", ResourceAtomicGroups },
                { "冲突顺位与暂停等待者", ResourceQueueOrder },
                { "取消和超时清理等待队列", ResourceWaitingCleanup },
                { "排队后读取目标及回零排队", ResourceWaitingTarget },
                { "输入位置变量进入与退出条件", ResourceInterlocks },
                { "进入失败不隔离和轴状态拦截", ResourceEntryFailure },
                { "异常隔离与显式恢复", ResourceFailureRecovery },
                { "退出超时保留占用", ResourceExitTimeout },
                { "取消运动范围停止后仍待确认", ResourceMotionCancellation },
                { "并行范围失败连带隔离", ResourceParallelFailure },
                { "子任务继承和连续范围", ResourceSubtasks },
                { "设备隔离和资源快照", ResourceDeviceIsolation },
                { "动作资源遗漏和隐藏嵌套校验", ResourceUsageValidation },
                { "互锁字段和位置边界校验", ResourceInterlockValidation },
                { "互锁 JSON 保存兼容与备份", ResourcePersistence },
                { "资源样例完整执行", ResourceSample },
                { "轴组定义及范围声明", GroupDefinitionValidation },
                { "轴组 JSON 与修订备份", GroupJsonPersistence },
                { "轴组独立到位与汇合", GroupIndependentArrival },
                { "轴组排队后读取目标", GroupWaitingTargets },
                { "重叠定位组完整排队", GroupOverlappingResources },
                { "轴组零距离与独立工程单位", GroupMixedUnitsAndZeroMove },
                { "轴组整组前置检查", GroupPreflight },
                { "轴组暂停保留相对终点", GroupPauseRelative },
                { "轴组取消与父子时限", GroupCancellationAndTimeout },
                { "轴组运行与先到位轴报警", GroupRuntimeAlarm },
                { "轴组子任务继承和区域恢复", GroupSubtaskAndScope },
                { "轴组样例完整往返", GroupSample }
            };
            int failures = 0;
            foreach (var check in checks)
            {
                try
                {
                    check.Value().GetAwaiter().GetResult();
                    Console.WriteLine("[通过] 仿真执行：" + check.Key);
                }
                catch (Exception error) { failures++; Console.Error.WriteLine("[失败] 仿真执行：" + check.Key + "\n" + error); }
                finally
                {
                    foreach (TaskRun run in Runs) run.Cancel();
                    try { Task.WhenAll(Runs.Select(run => Complete(run))).GetAwaiter().GetResult(); }
                    catch (Exception error) { failures++; Console.Error.WriteLine("[失败] 仿真清理：" + error); }
                    Runs.Clear();
                }
            }
            Console.WriteLine("任务执行纯软件仿真检查完成：共 " + checks.Count + " 组，失败 " + failures + " 组。未连接控制器。");
            return failures;
        }

        /// <summary>创建明确就绪的内存轴；这些设置不调用伺服或真实卡。</summary>
        private static InMemoryTaskDevice Device()
        {
            var device = new InMemoryTaskDevice(TaskFixtures.Catalog());
            foreach (string axis in device.Catalog.Axes.Keys) device.ConfigureAxis(axis, 0, true, true);
            return device;
        }

        /// <summary>登记运行以便失败时清理。</summary>
        private static TaskRun Start(TaskDefinition definition, InMemoryTaskDevice device,
            IEnumerable<TaskDefinition> dependencies = null, CancellationToken token = default(CancellationToken))
        {
            TaskRun run = new TaskExecutionEngine().Start(definition, device, dependencies, token);
            Runs.Add(run);
            return run;
        }

        /// <summary>等待终态但限制测试本身的时间，避免错误实现让检查器无限等待。</summary>
        private static async Task<TaskRunSnapshot> Complete(TaskRun run)
        {
            if (await Task.WhenAny(run.Completion, Task.Delay(10000)).ConfigureAwait(false) != run.Completion)
                throw new TimeoutException("测试运行超过 10 秒没有收尾：" + run.RunId);
            return await run.Completion.ConfigureAwait(false);
        }

        /// <summary>轮询可观察状态，不用固定延时猜测动作是否开始。</summary>
        private static async Task Until(Func<bool> condition, string message)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException(message);
                await Task.Delay(5).ConfigureAwait(false);
            }
        }

        /// <summary>按节点定义编号检查某次运行是否达到状态。</summary>
        private static bool HasNode(TaskRun run, string id, TaskNodeState state)
        {
            return run.GetSnapshot().Nodes.Any(node => node.NodeId == id && node.State == state);
        }

        /// <summary>断言成功，并把失败节点的原始上下文输出。</summary>
        private static void Success(TaskRunSnapshot snapshot)
        {
            CheckAssert.True(snapshot.State == TaskRunState.Completed, snapshot.Error?.ToString() ?? "运行未成功：" + snapshot.State);
        }

        /// <summary>验证失败原因和终态，而不是仅验证发生过异常。</summary>
        private static void Failure(TaskRunSnapshot snapshot, string expected)
        {
            CheckAssert.True(snapshot.State == TaskRunState.Failed && snapshot.Error != null
                && snapshot.Error.Message.Contains(expected), "未得到预期故障：" + expected + "，实际=" + snapshot.Error);
        }

        /// <summary>构造顺序分支。</summary>
        private static SequenceNode Sequence(string id, params TaskNode[] children)
        {
            return new SequenceNode { Id = id, Name = id, Children = new List<TaskNode>(children) };
        }

        /// <summary>构造两个独立的顺序分支。</summary>
        private static ParallelGroupNode Parallel(TaskNode first, TaskNode second)
        {
            return new ParallelGroupNode { Id = "parallel", Name = "并行组",
                Branches = new List<TaskNode> { Sequence("branchA", first), Sequence("branchB", second) } };
        }

        /// <summary>构造内存输出节点，检查是否错误进入后续步骤。</summary>
        private static SetOutputNode Output(string id = "after", bool value = true)
        {
            return new SetOutputNode { Id = id, Name = "写仿真输出", OutputName = "Vacuum", Value = value };
        }

        /// <summary>构造逻辑输入等待。</summary>
        private static WaitInputNode Input(string id = "input", int timeout = 5000)
        {
            return new WaitInputNode { Id = id, Name = "等待来料", InputName = "PartReady", Expected = true, TimeoutMs = timeout,
                TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } };
        }

        /// <summary>构造类型明确的常量。</summary>
        private static TaskValueBinding Constant(DeviceVariableType type, string text)
        {
            return new TaskValueBinding { Source = TaskValueSource.Constant, DataType = type, Constant = text };
        }

        /// <summary>构造现有变量引用。</summary>
        private static TaskValueBinding Variable(string name)
        {
            return new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = name };
        }

        /// <summary>构造赋值动作。</summary>
        private static SetVariableNode Assign(string id, string name, TaskValueBinding value)
        {
            return new SetVariableNode { Id = id, Name = "赋值 " + name, VariableName = name, Value = value };
        }

        /// <summary>取得变量文本以核对执行结果。</summary>
        private static string Value(InMemoryTaskDevice device, string name)
        {
            return device.ReadVariables().Single(item => item.Name == name).CurrentValue;
        }
    }
}
