using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Validation;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>资源定义必须在任何动作开始前检查清楚。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>实际加载并运行随文档分发的范围样例，结束时回到原位置且输出关闭。</summary>
        private static async Task ResourceSample()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples", "ResourceTransfer.json");
            var definition = TaskDefinitionJson.Deserialize(File.ReadAllText(path));
            var device = Device();
            device.SetInput("PartReady", true);
            Success(await Complete(Start(definition, device)));
            CheckAssert.True(device.ReadAxis("X").Position == 0 && device.ReadAxis("X").AcceptedCommands == 2
                && device.ReadOutput("Vacuum") == false && device.ReadResources().Count == 0, "样例没有完整退出。");
        }

        /// <summary>动作遗漏、子任务隐藏范围和区内并行都给出可定位错误。</summary>
        private static Task ResourceUsageValidation()
        {
            var validator = new TaskDefinitionValidator();
            var catalog = TaskFixtures.Catalog();
            foreach (string reserved in new[] { "Axis:Missing", "output:Missing" })
            {
                try
                {
                    new TaskResourceCatalog(catalog.Axes.Values, catalog.Inputs, catalog.Outputs,
                        catalog.Variables.ToDictionary(pair => pair.Key, pair => pair.Value), catalog.TeachPoints, new[] { reserved });
                    throw new Exception("自定义资源冒用保留前缀。");
                }
                catch (ArgumentException error) { CheckAssert.True(error.Message.Contains("保留前缀"), "目录拒绝原因不符。"); }
            }
            var scope = ResourceScope("scope", new[] { "Zone:Camera" }, TaskFixtures.Move(), Output());
            var definition = TaskFixtures.Task(scope);
            var issues = validator.Validate(definition, catalog);
            CheckAssert.HasError(issues, "Axis:X");
            CheckAssert.HasError(issues, "Output:Vacuum");
            CheckAssert.True(issues.All(item => item.NodeId == "scope" && item.TaskId == definition.TaskId), "范围错误缺少定位。");

            var child = TaskFixtures.Task(TaskFixtures.Move()); child.TaskId = "Child";
            scope.Body = Sequence("body", new CallTaskNode { Id = "call", Name = "调用", TaskId = "Child" });
            CheckAssert.HasError(validator.Validate(definition, catalog, new[] { child }), "引用位置：任务 Child");
            scope.Resources.Add("Axis:X");
            CheckAssert.NoErrors(validator.Validate(definition, catalog, new[] { child }));
            child.Root = Sequence("root", ResourceScope("nested", new[] { "Axis:X" }));
            CheckAssert.HasError(validator.Validate(definition, catalog, new[] { child }), "不能嵌套");
            child.Root = Sequence("root", Parallel(TaskFixtures.Delay("a"), TaskFixtures.Delay("b")));
            CheckAssert.HasError(validator.Validate(definition, catalog, new[] { child }), "不能使用 ParallelGroup");
            scope.Body = Sequence("body", new ConditionBranchNode { Id = "if", Name = "条件", Condition = TaskFixtures.Condition(),
                WhenTrue = Sequence("yes"), WhenFalse = Sequence("no", ResourceScope("nested", new[] { "Axis:X" })) });
            CheckAssert.HasError(validator.Validate(definition, catalog), "不能嵌套");

            var device = Device();
            try { Start(TaskFixtures.Task(Output(), scope), device); throw new Exception("未在启动前拒绝非法范围。"); }
            catch (ArgumentException error) { CheckAssert.True(error.Message.Contains("不能嵌套"), "启动失败原因不符。"); }
            CheckAssert.True(device.ReadOutput("Vacuum") == null, "校验失败前已写输出。");
            return Task.CompletedTask;
        }

        /// <summary>互锁名称、类型、范围、重复和遗漏要明确报错，不用缺省值猜测退出。</summary>
        private static Task ResourceInterlockValidation()
        {
            var validator = new TaskDefinitionValidator();
            var catalog = TaskFixtures.Catalog();
            var scope = ResourceScope("scope", new[] { "Zone:Camera" });
            var definition = TaskFixtures.Task(scope);
            scope.EntryInterlock = new TaskInterlock();
            CheckAssert.HasError(validator.Validate(definition, catalog), "至少需要一个");
            scope.EntryInterlock.Axes.Add(AxisInterlock(0, 10));
            CheckAssert.HasError(validator.Validate(definition, catalog), "纳入本范围");
            scope.Resources.Add("Axis:X");
            CheckAssert.NoErrors(validator.Validate(definition, catalog));
            var axis = scope.EntryInterlock.Axes[0];
            foreach (double? invalid in new double?[] { null, double.NaN, double.PositiveInfinity, 11 })
            {
                axis.MinimumPosition = invalid;
                CheckAssert.HasError(validator.Validate(definition, catalog), "有限数且下限");
            }
            axis.MinimumPosition = -101;
            CheckAssert.HasError(validator.Validate(definition, catalog), "超出轴配置行程");
            axis.MinimumPosition = 0;
            scope.EntryInterlock.Axes.Add(AxisInterlock(0, 10));
            CheckAssert.HasError(validator.Validate(definition, catalog), "轴条件重复");
            scope.EntryInterlock = InputInterlock(true);
            scope.EntryInterlock.Inputs[0].Expected = null;
            CheckAssert.HasError(validator.Validate(definition, catalog), "指定 DI 期望值");
            scope.EntryInterlock.Inputs[0].Expected = false;
            scope.EntryInterlock.Inputs[0].InputName = "Vacuum";
            CheckAssert.HasError(validator.Validate(definition, catalog), "DI 名称");
            scope.EntryInterlock = InputInterlock(true);
            scope.EntryInterlock.Inputs.Add(new TaskInputInterlock { InputName = "PartReady", Expected = true });
            CheckAssert.HasError(validator.Validate(definition, catalog), "DI 名称");
            scope.EntryInterlock = InputInterlock(true);
            scope.ExitInterlock = InputInterlock(false);
            scope.ExitCondition = TaskFixtures.Condition();
            CheckAssert.HasError(validator.Validate(definition, catalog), "只能选择一种");
            return Task.CompletedTask;
        }

        /// <summary>旧文件可读，新互锁按修订保存并保留备份，遗漏期望输入值必须拒绝。</summary>
        private static Task ResourcePersistence()
        {
            var scope = ResourceScope("zone", new[] { "Zone:Camera", "Axis:X" });
            scope.ExitCondition = TaskFixtures.Condition();
            var definition = TaskFixtures.Task(scope);
            var catalog = TaskFixtures.Catalog();
            using (var workspace = new TestWorkspace())
            {
                var store = new TaskDefinitionStore(workspace.Store);
                store.Save("Test01", "Zone.json", definition, catalog);
                var old = (ResourceScopeNode)((SequenceNode)store.Load("Test01", "Zone.json", catalog).Root).Children[0];
                CheckAssert.True(old.ExitCondition != null && old.ExitInterlock == null, "旧退出条件不能读取。");
                definition.Revision++;
                scope.ExitCondition = null;
                scope.EntryInterlock = InputInterlock(false);
                scope.EntryInterlock.Axes.Add(AxisInterlock(-1, 1));
                scope.ExitInterlock = InputInterlock(true);
                scope.ExitInterlock.Condition = TaskFixtures.Condition();
                string backup = store.Save("Test01", "Zone.json", definition, catalog);
                CheckAssert.True(File.Exists(backup) && TaskDefinitionJson.Deserialize(File.ReadAllText(backup)).Revision == 1, "没有保存旧定义备份。");
                var loaded = store.Load("Test01", "Zone.json", catalog);
                string json = TaskDefinitionJson.Serialize(loaded);
                CheckAssert.True(JToken.DeepEquals(JObject.Parse(json), JObject.Parse(TaskDefinitionJson.Serialize(definition))), "互锁 JSON 往返改变了定义。");
                var document = JObject.Parse(json);
                ((JObject)document["root"]["children"][0]["entryInterlock"]["inputs"][0]).Remove("expected");
                CheckAssert.HasError(new TaskDefinitionValidator().Validate(TaskDefinitionJson.Deserialize(document.ToString()), catalog), "指定 DI 期望值");
                document["root"]["children"][0]["entryInterlock"]["unknownSafety"] = true;
                try { TaskDefinitionJson.Deserialize(document.ToString()); throw new Exception("未知互锁字段被忽略。"); }
                catch (Newtonsoft.Json.JsonSerializationException error)
                {
                    CheckAssert.True(error.Message.Contains("unknownSafety"), "JSON 拒绝原因没有指出未知字段。");
                }
            }
            return Task.CompletedTask;
        }
    }
}
