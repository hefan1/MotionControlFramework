using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Device.InMemory;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>设备级持续运行检查，仅创建内存设备和独立临时文件。</summary>
    internal static partial class MachineChecks
    {
        /// <summary>每组结束后必须关闭的控制器。</summary>
        private static readonly List<TaskMachineController> Machines = new List<TaskMachineController>();

        /// <summary>逐项验证整机链路，失败时仍等待全部工位收尾。</summary>
        internal static int RunAll()
        {
            var checks = new Dictionary<string, Func<Task>> {
                { "设备清单校验和持久化", Configuration },
                { "初始化先后与定义冻结", Initialization },
                { "快慢工位独立循环及统一暂停", IndependentCycles },
                { "旧完成信号及未知信号处理", OldAndUnknownSignals },
                { "混合级别超时报警批量上报并等待整机复位", LinkedTimeoutAlarms },
                { "非停机超时保留运行并允许其他工位生产", NonStopTimeoutContinuesProduction },
                { "并行条件共享报警时分别恢复", ParallelConditionsShareAlarm },
                { "上层超时可能先于等待步骤报警", ParentDeadlineCanPreemptWaitAlarm },
                { "三工位三十周期连续交接", ProductionPipeline },
                { "故障暂停整机并保留已完成步骤", FaultAndStepRecovery },
                { "相对单轴及轴组故障终点", RelativeTargetRecovery },
                { "节点超时与区域退出恢复", TimeoutAndExitRecovery },
                { "父步骤超时不重放及规模超限终止", ParentTimeoutAndExpansionLimit },
                { "外部停机条件锁存", StopAlarm },
                { "请求提前撤销不重做动作", EarlyRequestWithdrawal },
                { "整机所有权及终止关闭", OwnershipAndAbort }
            };
            int failures = 0;
            foreach (var check in checks)
            {
                try { check.Value().GetAwaiter().GetResult(); Console.WriteLine("[通过] 整机运行：" + check.Key); }
                catch (Exception error) { failures++; Console.Error.WriteLine("[失败] 整机运行：" + check.Key + "\n" + error); }
                finally
                {
                    foreach (var machine in Machines)
                    {
                        try { Wait(machine.CloseAsync()).GetAwaiter().GetResult(); }
                        catch (Exception error) { failures++; Console.Error.WriteLine("[失败] 整机收尾：" + error); }
                    }
                    Machines.Clear();
                }
            }
            Console.WriteLine("设备级内存运行检查完成：共 " + checks.Count + " 组，失败 " + failures + " 组。未连接控制器。");
            return failures;
        }

        /// <summary>提供各工位交接及产品编号变量，复用原轴/IO/示教目录。</summary>
        private static InMemoryTaskDevice Device()
        {
            var source = TaskFixtures.Catalog();
            var variables = source.Variables.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (string name in new[] { "Feed.Request", "Feed.Complete", "Inspect.Request", "Inspect.Complete", "Unload.Request", "Unload.Complete" })
                variables.Add(name, DeviceVariableType.Boolean);
            foreach (string name in new[] { "Source.Id", "Load.Id", "Inspect.Id", "Unload.Id" }) variables.Add(name, DeviceVariableType.UInt32);
            var device = new InMemoryTaskDevice(new TaskResourceCatalog(source.Axes.Values, source.Inputs, source.Outputs,
                variables, source.TeachPoints, new[] { "Zone:Camera" },
                alarmCodes: source.AlarmCodes, alarmDefinitions: source.AlarmDefinitions));
            foreach (string axis in device.Catalog.Axes.Keys) device.ConfigureAxis(axis, 0, true, true);
            return device;
        }

        /// <summary>构造一个明确命名的任务。</summary>
        private static TaskDefinition TaskDefinition(string id, params TaskNode[] nodes)
        {
            var task = TaskFixtures.Task(nodes); task.TaskId = id; task.Name = id; return task;
        }

        /// <summary>构造具体工位配置。</summary>
        private static StationTaskConfiguration Station(string id, string taskId, StationTaskMode mode)
        {
            return new StationTaskConfiguration { StationId = id, Name = id, TaskId = taskId, Mode = mode, CycleIntervalMs = 1 };
        }

        /// <summary>构造需要复位才能启动的设备控制器，并登记收尾。</summary>
        private static TaskMachineController Machine(InMemoryTaskDevice device, IEnumerable<TaskDefinition> tasks,
            params StationTaskConfiguration[] stations)
        {
            var machine = new TaskMachineController(new TaskMachineConfiguration { MachineId = "Test01", Name = "内存检查设备",
                Stations = stations.ToList() }, tasks, device);
            Machines.Add(machine); return machine;
        }

        /// <summary>按工位标识读取快照。</summary>
        private static StationTaskSnapshot State(TaskMachineController machine, string id)
        {
            return machine.GetSnapshot().Stations.Single(station => station.StationId == id);
        }

        /// <summary>最多等待五秒观察状态，不靠固定长延时猜测动作。</summary>
        private static async Task Until(Func<bool> condition, string reason)
        {
            var timer = Stopwatch.StartNew();
            while (!condition())
            {
                if (timer.ElapsedMilliseconds > 5000) throw new TimeoutException(reason);
                await Task.Delay(5).ConfigureAwait(false);
            }
        }

        /// <summary>关闭本身限时，防止检查器被泄漏的工位卡住。</summary>
        private static async Task Wait(Task operation)
        {
            if (await Task.WhenAny(operation, Task.Delay(10000)).ConfigureAwait(false) != operation)
                throw new TimeoutException("整机操作超过十秒未收尾。");
            await operation.ConfigureAwait(false);
        }

        /// <summary>按现有变量类型读取文本。</summary>
        private static string Value(InMemoryTaskDevice device, string name)
        {
            return device.ReadVariables().Single(value => value.Name == name).CurrentValue;
        }

        /// <summary>按 bool 语义核对交接，不把变量存储的 True/False 大小写当作逻辑差异。</summary>
        private static bool? BooleanValue(InMemoryTaskDevice device, string name)
        {
            var value = device.ReadVariables().Single(item => item.Name == name);
            return value.HasValue ? (bool?)bool.Parse(value.CurrentValue) : null;
        }

        /// <summary>构造一次变量复制，不通过 C# 代码替代任务引擎执行。</summary>
        private static SetVariableNode CopyValue(string id, string target, string source)
        {
            return new SetVariableNode { Id = id, Name = id, VariableName = target,
                Value = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = source } };
        }

        /// <summary>构造清楚的普通顺序节点。</summary>
        private static SequenceNode Sequence(string id, params TaskNode[] nodes)
        {
            return new SequenceNode { Id = id, Name = id, Children = nodes.ToList() };
        }
    }
}
