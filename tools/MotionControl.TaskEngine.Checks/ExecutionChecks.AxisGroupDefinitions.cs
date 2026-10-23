using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Validation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>定位组定义、持久化及分发样例的检查。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>数量、重复轴、同卡、参数及示教点归属复用统一校验，并核对范围声明。</summary>
        private static Task GroupDefinitionValidation()
        {
            AssertGroupError(group => group.Targets = null, "2 到 64");
            AssertGroupError(group => group.Targets.RemoveAt(1), "2 到 64");
            AssertGroupError(group => group.Targets = Enumerable.Range(0, 65).Select(index => GroupTarget("X", "0")).ToList(), "2 到 64");
            AssertGroupError(group => group.Targets[1] = null, "不能为空");
            AssertGroupError(group => group.Targets[1].AxisName = "x", "重复加入");
            AssertGroupError(group => group.Targets[1].AxisName = "Missing", "逻辑轴不存在");
            AssertGroupError(group => group.Targets[1].AxisName = "Z", "同一卡");
            AssertGroupError(group => group.Targets[1].Mode = (TaskMoveMode)99, "坐标方式无效");
            AssertGroupError(group => group.Targets[1].Target.Constant = "101", "超出配置行程");
            AssertGroupError(group => group.Targets[1].Profile = null, "工艺运动参数");
            foreach (double invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
                AssertGroupError(group => group.Targets[1].Profile.Speed = invalid, "速度必须");
            AssertGroupError(group => group.Targets[1].Profile.Acceleration = 0, "加速度必须");
            AssertGroupError(group => group.Targets[1].Profile.Deceleration = -1, "减速度必须");
            AssertGroupError(group => group.Targets[1].Target = Variable("Vision.Result"), "必须是数值");
            AssertGroupError(group => group.Targets[1].Target = new TaskValueBinding {
                Source = TaskValueSource.TeachPoint, AxisName = "X", PointId = "P001" }, "不属于本次动作");
            AssertGroupError(group => {
                group.Targets[1].Mode = TaskMoveMode.Relative;
                group.Targets[1].Target = new TaskValueBinding { Source = TaskValueSource.TeachPoint, AxisName = "Y", PointId = "P001" };
            }, "不能当作相对距离");

            var validator = new TaskDefinitionValidator();
            var groupNode = Group(GroupTarget("X", "0"), GroupTarget("R", "90", 180));
            CheckAssert.NoErrors(validator.Validate(TaskFixtures.Task(groupNode), TaskFixtures.Catalog()));
            groupNode = Group(GroupTarget("X", "10"), GroupTarget("Y", "20"));
            var child = TaskFixtures.Task(groupNode); child.TaskId = "Child";
            var scope = ResourceScope("zone", new[] { "Axis:X" }, new CallTaskNode { Id = "call", Name = "定位子任务", TaskId = "Child" });
            var definition = TaskFixtures.Task(scope);
            var issues = validator.Validate(definition, TaskFixtures.Catalog(), new[] { child });
            CheckAssert.HasError(issues, "Axis:Y");
            CheckAssert.True(issues.Any(issue => issue.TaskId == "Transfer" && issue.NodeId == "zone"
                && issue.Message.Contains("Child")), "子任务组资源缺少上下文。");
            var device = Device();
            try { Start(TaskFixtures.Task(Output(), scope), device, new[] { child }); throw new Exception("遗漏组资源未在启动前拒绝。"); }
            catch (ArgumentException error) { CheckAssert.True(error.Message.Contains("Axis:Y"), "启动拒绝原因不符。"); }
            CheckAssert.True(device.ReadOutput("Vacuum") == null, "校验失败前写了输出。");
            return Task.CompletedTask;
        }

        /// <summary>从一份合法定位组仅改变一个条件，检查准确错误，防止其他错误掩盖问题。</summary>
        private static void AssertGroupError(Action<MoveAxisGroupNode> change, string expected)
        {
            var group = Group(GroupTarget("X", "10"), GroupTarget("Y", "20"));
            change(group);
            var issues = new TaskDefinitionValidator().Validate(TaskFixtures.Task(group), TaskFixtures.Catalog());
            CheckAssert.HasError(issues, expected);
            CheckAssert.True(issues.Any(issue => issue.NodeId == "group" && issue.Field.Contains("targets")), "定义错误未定位到组目标。");
        }

        /// <summary>完整保存定位组和修订备份，严格拒绝遗漏坐标方式、未知字段及错误 JSON 类型。</summary>
        private static Task GroupJsonPersistence()
        {
            var group = Group(GroupTarget("X", "10"), GroupTarget("Y", "20"));
            group.Targets[1].Target = new TaskValueBinding { Source = TaskValueSource.TeachPoint, AxisName = "Y", PointId = "P001" };
            var definition = TaskFixtures.Task(group);
            string json = TaskDefinitionJson.Serialize(definition);
            var restored = TaskDefinitionJson.Deserialize(json);
            CheckAssert.NoErrors(new TaskDefinitionValidator().Validate(restored, TaskFixtures.Catalog()));
            CheckAssert.True(JToken.DeepEquals(JObject.Parse(json), JObject.Parse(TaskDefinitionJson.Serialize(restored))), "定位组往返丢失字段。");
            foreach (string field in new[] { "axisName", "mode", "target", "profile" })
            {
                var document = JObject.Parse(json);
                ((JObject)document["root"]["children"][0]["targets"][1]).Remove(field);
                ExpectGroupJsonFailure(document, field);
                try { TaskDefinitionJson.Deserialize(document.ToString()); }
                catch (JsonSerializationException error)
                {
                    CheckAssert.True(error.Message.Contains("group") && error.Message.Contains("targets[1]"), "遗漏字段没有给出所属节点和目标行。");
                }
            }
            foreach (JToken mode in new JToken[] { new JValue(0), new JValue("Unknown") })
            {
                var document = JObject.Parse(json);
                document["root"]["children"][0]["targets"][1]["mode"] = mode;
                ExpectGroupJsonFailure(document, "mode");
            }
            var unknown = JObject.Parse(json);
            unknown["root"]["children"][0]["targets"][1]["guessSafeZ"] = true;
            ExpectGroupJsonFailure(unknown, "guessSafeZ");
            using (var workspace = new TestWorkspace())
            {
                var store = new TaskDefinitionStore(workspace.Store);
                store.Save("Test01", "Group.json", definition, TaskFixtures.Catalog());
                definition.Revision++;
                group.Targets[0].Profile.Speed = 25;
                string backup = store.Save("Test01", "Group.json", definition, TaskFixtures.Catalog());
                CheckAssert.True(File.Exists(backup) && TaskDefinitionJson.Deserialize(File.ReadAllText(backup)).Revision == 1
                    && store.Load("Test01", "Group.json", TaskFixtures.Catalog()).Revision == 2, "组修订和旧文件备份错误。");
            }
            return Task.CompletedTask;
        }

        /// <summary>确认 JSON 被明确的字段错误拒绝，而不是任意异常都算通过。</summary>
        private static void ExpectGroupJsonFailure(JObject document, string expectedField)
        {
            try { TaskDefinitionJson.Deserialize(document.ToString()); }
            catch (JsonSerializationException error)
            {
                CheckAssert.True(error.Message.IndexOf(expectedField, StringComparison.OrdinalIgnoreCase) >= 0, "JSON 拒绝原因未指向字段：" + error.Message);
                return;
            }
            throw new InvalidOperationException("定位组错误字段被读取：" + expectedField);
        }

        /// <summary>加载随项目提供的中文样例并执行往返，确认全部到位后才写输出并最终让出区域。</summary>
        private static async Task GroupSample()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples", "AxisGroupPositioning.json");
            var device = Device(); device.SetInput("PartReady", true);
            Success(await Complete(Start(TaskDefinitionJson.Deserialize(File.ReadAllText(path)), device)));
            CheckAssert.True(device.ReadAxis("X").Position == 0 && device.ReadAxis("Y").Position == 0
                && device.ReadAxis("X").AcceptedCommands == 2 && device.ReadAxis("Y").AcceptedCommands == 2
                && device.ReadOutput("Vacuum") == false && device.ReadResources().Count == 0, "轴组样例未完成往返或没有让出区域。");
        }
    }
}
