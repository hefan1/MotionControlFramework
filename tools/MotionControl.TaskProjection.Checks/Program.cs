using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Projection;

namespace MotionControl.TaskProjection.Checks
{
    /// <summary>在独立内存定义上验证投影，无 Host、设备服务或配置写入。</summary>
    internal static partial class Program
    {
        /// <summary>检查失败直接报告断言并以非零代码退出。</summary>
        private static void Main()
        {
            StationCounts();
            NestedReferences();
            FailedReferences();
            DraftAndVersions();
            StablePathsAndBudget();
            MalformedDraft();
            ActionFields();
            OptionalFieldsAndCycles();
            InitializationStation();
            Console.WriteLine("九组工程关联投影检查通过。");
        }

        /// <summary>给出能定位到具体预期的失败原因。</summary>
        private static void Check(bool ok, string expectation)
        {
            if (!ok) throw new InvalidOperationException(expectation);
        }

        /// <summary>创建按明确标识关联同一入口的测试设备。</summary>
        private static TaskMachineConfiguration Machine(params string[] ids)
        {
            var machine = new TaskMachineConfiguration { MachineId = "M", Name = "设备", Revision = 4 };
            foreach (string id in ids)
                machine.Stations.Add(new StationTaskConfiguration { StationId = id, Name = "同名工位", TaskId = "main" });
            return machine;
        }

        /// <summary>把各测试节点放入顺序根节点。</summary>
        private static TaskDefinition Task(string id, params TaskNode[] nodes)
        {
            return new TaskDefinition { TaskId = id, Name = id, Revision = 7,
                Root = new SequenceNode { Id = "root", Name = "根", Children = nodes.ToList() } };
        }

        /// <summary>提供两轴同编号示教点及显式配置的信号和资源。</summary>
        private static TaskResourceCatalog Catalog()
        {
            return new TaskResourceCatalog(new[] { new TaskAxisInfo("X", "card", "mm", 0, 100),
                new TaskAxisInfo("Y", "card", "mm", 0, 100) }, new[] { "sensor" }, new[] { "valve" },
                new Dictionary<string, DeviceVariableType> { { "flag", DeviceVariableType.Boolean } },
                new[] { new TaskTeachPoint("X", "P", "共同名称", 1), new TaskTeachPoint("Y", "P", "共同名称", 2) },
                new[] { "shared" });
        }

        /// <summary>零工位保留未分配任务，一工位及多工位使用相同定义，各自路径独立。</summary>
        private static void StationCounts()
        {
            var tasks = new[] { Task("main", new CallTaskNode { Id = "first", TaskId = "sub" },
                new CallTaskNode { Id = "second", TaskId = "sub" }),
                Task("sub", new SetOutputNode { Id = "set", OutputName = "valve" }) };
            foreach (string[] ids in new[] { new string[0], new[] { "S/1" }, new[] { "S/1", "S%2F1" } })
            {
                var projection = TaskProjectProjection.FromSaved(Machine(ids), tasks, Catalog());
                Check(projection.References.Count(row => row.Kind == "Station") == ids.Length, "工位数量必须来自清单。");
                var outputs = projection.FindReferences("Output", "VALVE").ToList();
                if (ids.Length == 0)
                    Check(outputs.Count > 0 && outputs.All(row => row.StationId == null), "无工位时不推断归属。");
                else
                {
                    Check(outputs.Count == ids.Length * 2 && outputs.All(row => row.TaskId == "sub" && row.NodeId == "set"
                        && row.TaskRevision == 7 && row.Status == "已定位"), "共享调用丢失或来源不正确。");
                    Check(outputs.Select(row => row.Path).Distinct().Count() == outputs.Count, "同名工位或重复调用路径合并。");
                }
            }
        }

        /// <summary>初始化快照的保留键只用于会话定义；保存和编辑预览仍是设备级路径。</summary>
        private static void InitializationStation()
        {
            var machine = Machine("A");
            machine.InitializationTaskId = "init";
            var tasks = new[] { Task("main"), Task("init", new SetOutputNode { Id = "ready", Name = "准备", OutputName = "valve" }) };
            var session = TaskProjectProjection.FromSession(machine, tasks, Catalog());
            Check(session.FindReferences("Output", "valve").Single().StationId == "__initialization", "初始化未关联真实快照键。");
            var saved = TaskProjectProjection.FromSaved(machine, tasks, Catalog());
            var document = new TaskWorkspaceDocument { MachineId = machine.MachineId, Machine = machine,
                Tasks = tasks.Select(task => new TaskWorkspaceTask { Definition = task }).ToList() };
            Check(saved.FindReferences("Output", "valve").Single().StationId == null &&
                TaskProjectProjection.FromDraft(document, Catalog()).FindReferences("Output", "valve").Single().StationId == null,
                "配置和草稿不能制造初始化执行工位。");
        }

        /// <summary>验证并行、真假分支及资源正文中的绑定、条件和互锁引用。</summary>
        private static void NestedReferences()
        {
            var variable = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "flag" };
            var condition = new TaskCondition { Kind = TaskConditionKind.All, Conditions = { new TaskCondition {
                Kind = TaskConditionKind.Compare, Left = variable, Right = variable, Comparison = TaskComparison.Equal } } };
            var move = new MoveAxisNode { Id = "move", Name = "定位", AxisName = "Y",
                Profile = new TaskMotionProfile { Speed = 5, Acceleration = 10, Deceleration = 10 },
                Target = new TaskValueBinding { Source = TaskValueSource.TeachPoint, AxisName = "Y", PointId = "P" } };
            var branch = new ConditionBranchNode { Id = "choose", Name = "检查", Condition = condition,
                WhenTrue = Sequence("yes", move),
                WhenFalse = Sequence("no", new SetOutputNode { Id = "set", Name = "输出", OutputName = "valve" }) };
            var scope = new ResourceScopeNode { Id = "scope", Name = "区域", Body = Sequence("body", branch),
                Resources = { "shared", "Axis:X", "Axis:Y", "Output:valve" },
                EntryInterlock = new TaskInterlock { Axes = { new TaskAxisInterlock { AxisName = "X", MinimumPosition = 1, MaximumPosition = 2 } } },
                ExitInterlock = new TaskInterlock { Inputs = { new TaskInputInterlock { InputName = "sensor", Expected = true } } } };
            var parallel = new ParallelGroupNode { Id = "para", Name = "并行", Branches = { Sequence("area", scope),
                Sequence("other", new DelayNode { Id = "delay", Name = "等待", Milliseconds = 1 }) } };
            var result = TaskProjectProjection.FromSaved(Machine("A"), new[] { Task("main", parallel) }, Catalog());
            Check(result.FindReferences("Variable", "flag").Count() == 2, "须分别保留比较两端。");
            Check(result.FindReferences("TeachPoint", "P", "Y").Single().Path.Contains("/WhenTrue/"), "稳定点位或真分支路径错误。");
            Check(!result.FindReferences("TeachPoint", "P", "X").Any(), "不能跨轴匹配同编号点。");
            Check(result.FindReferences("Output", "valve").Count() == 2 &&
                result.FindReferences("Output", "valve").Any(row => row.Path.Contains("/WhenFalse/")), "假分支或范围输出路径错误。");
            Check(result.FindReferences("Input", "sensor").Single().Path.Contains("/ExitInterlock/"), "遗漏退出互锁。");
            Check(result.FindReferences("Axis", "X").Count() == 2 &&
                result.FindReferences("Axis", "X").Any(row => row.Path.Contains("/EntryInterlock/")), "遗漏进入互锁或范围轴。");
            Check(result.FindReferences("Resource", "shared").Single().Path.Contains("/Branches"), "遗漏并行分支资源。");
            Check(!result.Diagnostics.Any(issue => issue.StartsWith("Error ")), string.Join("\n", result.Diagnostics));

            // 复用正式节点模型要求的顺序正文和分支格式。
            SequenceNode Sequence(string id, TaskNode child)
            {
                return new SequenceNode { Id = id, Name = id, Children = { child } };
            }
        }

        /// <summary>递归、未加载及目录引用失效分别定位；未知节点不会静默忽略。</summary>
        private static void FailedReferences()
        {
            var tasks = new[] { Task("main", new CallTaskNode { Id = "call", TaskId = "sub" }),
                Task("sub", new CallTaskNode { Id = "back", TaskId = "main" }, new CallTaskNode { Id = "lost", TaskId = "no-task" },
                new WaitInputNode { Id = "wrong-kind", InputName = "valve" }, new UnknownNode { Id = "new" }) };
            var result = TaskProjectProjection.FromSaved(Machine("A", "B"), tasks, Catalog());
            foreach (string id in new[] { "A", "B" })
            {
                Check(result.FindReferences("Task", "main").Any(r => r.StationId == id && r.Status == "递归调用"), "间接递归必须逐站标记。");
                Check(result.FindReferences("Input", "valve").Single(r => r.StationId == id).Status == "引用失效", "DO 不能充当 DI。");
                Check(result.FindReferences("Task", "no-task").Single(r => r.StationId == id).Status == "引用失效", "缺失子任务需保留原引用。");
            }
            Check(result.Diagnostics.Any(issue => issue.Contains("未支持的节点类型") && issue.Contains("UnknownNode")), "未报告新节点类型。");
            var missing = TaskProjectProjection.FromSaved(Machine("A"), tasks, null);
            Check(missing.FindReferences("Input", "valve").Single().Status == "目录未提供", "缺目录与无此对象需区分。");
            var ambiguous = TaskProjectProjection.FromSaved(Machine("A"), new[] { tasks[0], Task("MAIN") }, Catalog());
            Check(ambiguous.FindReferences("Task", "main").Single().Status == "任务标识有歧义", "不能任选一个同名任务。");
        }

        /// <summary>编辑器修改不能改变旧投影，缺失的草稿实时版本不能借用文件签名。</summary>
        private static void DraftAndVersions()
        {
            var set = new SetOutputNode { Id = "set", OutputName = "valve" };
            var def = Task("main", set);
            var document = new TaskWorkspaceDocument { MachineId = "M", Machine = Machine("A"), DraftSignature = "旧文件",
                Tasks = { new TaskWorkspaceTask { Definition = def } } };
            var draft = TaskProjectProjection.FromDraft(document, Catalog());
            set.OutputName = "edited";
            Check(draft.Source == "编辑草稿" && draft.SourceSignature == null && draft.MachineRevision == 4, "草稿来源或来源版本错误。");
            Check(draft.FindReferences("Output", "valve").Count() == 1, "旧投影持有可编辑正文。");
            var rebuilt = TaskProjectProjection.FromDraft(document, Catalog());
            Check(rebuilt.FindReferences("Output", "edited").Single().Status == "引用失效", "重建后未反映改名失效。");
            var saved = TaskProjectProjection.FromSaved(document.Machine, new[] { def }, Catalog(), "保存集合签名");
            Check(saved.Source == "已保存定义" && saved.SourceSignature == "保存集合签名", "已保存定义缺少独立来源。");
        }

        /// <summary>模拟将来新增但尚未接入投影的动作。</summary>
        private sealed class UnknownNode : TaskNode
        {
            /// <summary>仅供未支持类型诊断检查。</summary>
            public override string Kind => "NewKind";
        }

        /// <summary>同级排序不改变有效路径，共享任务扇出超限时明确截断。</summary>
        private static void StablePathsAndBudget()
        {
            var task = Task("main", new DelayNode { Id = "a" }, new SetOutputNode { Id = "b", OutputName = "valve" });
            var machine = Machine("A");
            var before = TaskProjectProjection.FromSaved(machine, new[] { task }, Catalog());
            ((SequenceNode)task.Root).Children.Reverse();
            var after = TaskProjectProjection.FromSaved(machine, new[] { task }, Catalog());
            Check(before.FindReferences("Output", "valve").Single().Path == after.FindReferences("Output", "valve").Single().Path,
                "移动步骤顺序应保留稳定定位路径。");
            var tasks = new List<TaskDefinition>();
            for (int i = 0; i < 16; i++)
            {
                string next = "sub" + (i + 1);
                tasks.Add(Task(i == 0 ? "main" : "sub" + i,
                    new CallTaskNode { Id = "a", Name = "第一次调用", TaskId = next },
                    new CallTaskNode { Id = "b", Name = "第二次调用", TaskId = next }));
            }
            tasks.Add(Task("sub16", new DelayNode { Id = "end", Name = "等待", Milliseconds = 1 }));
            var limited = TaskProjectProjection.FromSaved(machine, tasks, Catalog());
            Check(limited.Diagnostics.Count(issue => issue.Contains("结果未完整展开")) == 1, "大扇出必须明确报告一次截断。");
        }

        /// <summary>非法草稿仍返回部分树及字段原因，不隐去空调用、坏结构和条件循环。</summary>
        private static void MalformedDraft()
        {
            var cycle = new TaskCondition { Kind = TaskConditionKind.Not };
            cycle.Conditions.Add(cycle);
            var definition = Task("main", new CallTaskNode { Id = "missing" },
                new WaitConditionNode { Id = "cycle", Condition = cycle },
                new ParallelGroupNode { Id = "broken", Branches = null }, null);
            var document = new TaskWorkspaceDocument { MachineId = "M", Machine = Machine("A"),
                Tasks = { new TaskWorkspaceTask { Definition = definition } } };
            var result = TaskProjectProjection.FromDraft(document, Catalog());
            Check(result.FindReferences("Task", null).Single().Path.Contains("missing"), "子任务空引用必须定位到调用点。");
            Check(result.Diagnostics.Any(issue => issue.Contains("broken") && issue.Contains("子节点集合缺失")), "丢失非法分支诊断。");
            Check(result.Diagnostics.Any(issue => issue.Contains("cycle") && issue.Contains("条件")), "丢失条件循环来源。");
            Check(result.Diagnostics.Any(issue => issue.Contains("节点缺失")), "未报告空节点。");
            Check(result.Diagnostics.Any(issue => issue.Contains("任务库") && issue.Contains("128")) == false, "小型草稿不应被误判超限。");
        }
    }
}
