using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>互斥结果输入及变量成对发布的独立执行检查。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>验证无效和未知结果不放行，暂停期间不采样，恢复后分别接收 OK/NG。</summary>
        private static async Task ExclusiveInputs()
        {
            var catalog = new TaskResourceCatalog(new TaskAxisInfo[0], new[] { "OK", "NG" }, new[] { "Vacuum" },
                new Dictionary<string, DeviceVariableType> { { "OkResult", DeviceVariableType.Boolean }, { "NgResult", DeviceVariableType.Boolean } },
                new TaskTeachPoint[0], alarmCodes: new[] { "DEMO_SENSOR_TIMEOUT" });
            var node = new WaitExclusiveInputsNode { Id = "result", Name = "等待有效结果", TimeoutMs = 3000,
                FirstInputName = "OK", SecondInputName = "NG", FirstVariableName = "OkResult", SecondVariableName = "NgResult",
                TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } };
            var task = TaskFixtures.Task(node, Output());
            string json = TaskDefinitionJson.Serialize(task);
            var copy = TaskDefinitionJson.Deserialize(json);
            CheckAssert.NoErrors(new TaskDefinitionValidator().Validate(copy, catalog));
            var restored = (WaitExclusiveInputsNode)((SequenceNode)copy.Root).Children[0];
            CheckAssert.True(restored.FirstInputName == "OK" && restored.SecondVariableName == "NgResult", "互斥输入定义往返丢失字段。");
            restored.SecondInputName = "OK";
            CheckAssert.HasError(new TaskDefinitionValidator().Validate(copy, catalog), "两路输入不能相同");
            restored.SecondInputName = "NG";
            restored.SecondVariableName = "OkResult";
            CheckAssert.HasError(new TaskDefinitionValidator().Validate(copy, catalog), "两个目标变量不能相同");
            node.RequiredFirstValue = false;
            var requiredCopy = (WaitExclusiveInputsNode)((SequenceNode)TaskDefinitionJson.Deserialize(TaskDefinitionJson.Serialize(task)).Root).Children[0];
            CheckAssert.True(requiredCopy.RequiredFirstValue == false, "指定第二路有效未在 JSON 中保留。");
            node.RequiredFirstValue = null;

            var device = new InMemoryTaskDevice(catalog);
            device.SetVariable("OkResult", "false"); device.SetVariable("NgResult", "false");
            device.SetInput("OK", false); device.SetInput("NG", false);
            TaskRun run = Start(task, device);
            await Until(() => HasNode(run, "result", TaskNodeState.Waiting), "未进入互斥结果等待。");
            await Task.Delay(150);
            CheckAssert.True(device.ReadOutput("Vacuum") == null && Value(device, "OkResult") == "False", "两路无效时错误放行。");
            CheckAssert.True(run.Pause(), "等待期间不能暂停。");
            device.SetInput("OK", true);
            await Task.Delay(150);
            CheckAssert.True(device.ReadOutput("Vacuum") == null && Value(device, "OkResult") == "False", "暂停时错误写入结果。");
            CheckAssert.True(run.Resume(), "等待期间不能恢复。");
            Success(await Complete(run));
            CheckAssert.True(Value(device, "OkResult") == "True" && Value(device, "NgResult") == "False", "OK 采样结果错误。");

            device.SetInput("OK", true); device.SetInput("NG", true); device.WriteOutput("Vacuum", false);
            run = Start(task, device);
            await Until(() => HasNode(run, "result", TaskNodeState.Waiting), "两路同时有效未进入等待。");
            await Task.Delay(150);
            CheckAssert.True(device.ReadOutput("Vacuum") == false, "两路同时有效时错误放行。");
            device.SetInput("OK", false);
            Success(await Complete(run));
            CheckAssert.True(Value(device, "OkResult") == "False" && Value(device, "NgResult") == "True", "NG 采样结果错误。");

            device.SetInput("OK", null); device.SetInput("NG", false); device.WriteOutput("Vacuum", false);
            run = Start(task, device);
            await Until(() => HasNode(run, "result", TaskNodeState.Waiting), "未知输入未进入等待。");
            await Task.Delay(150);
            CheckAssert.True(device.ReadOutput("Vacuum") == false && Value(device, "NgResult") == "True", "未知输入覆盖结果或放行。");
            run.Cancel(); await Complete(run);

            node.RequiredFirstValue = false;
            device.SetInput("OK", true); device.SetInput("NG", false); device.WriteOutput("Vacuum", false);
            run = Start(task, device);
            await Until(() => HasNode(run, "result", TaskNodeState.Waiting), "指定第二路时未等待相反状态。");
            await Task.Delay(150);
            CheckAssert.True(device.ReadOutput("Vacuum") == false, "第一路有效错误满足第二路要求。");
            device.SetInput("OK", false); device.SetInput("NG", true);
            Success(await Complete(run));
            CheckAssert.True(Value(device, "OkResult") == "False" && Value(device, "NgResult") == "True", "指定第二路结果错误。");
            node.RequiredFirstValue = true;
            device.SetInput("OK", false); device.SetInput("NG", true); device.WriteOutput("Vacuum", false);
            run = Start(task, device);
            await Until(() => HasNode(run, "result", TaskNodeState.Waiting), "指定第一路时未等待相反状态。");
            await Task.Delay(150);
            CheckAssert.True(device.ReadOutput("Vacuum") == false, "第二路有效错误满足第一路要求。");
            device.SetInput("NG", false); device.SetInput("OK", true);
            Success(await Complete(run));

            var before = device.ReadVariables().ToArray();
            try { device.WriteBooleanPair("OkResult", true, "不存在", false); throw new System.InvalidOperationException("错误目标未拒绝。"); }
            catch (KeyNotFoundException) { }
            var after = device.ReadVariables().ToArray();
            CheckAssert.True(before.Single(x => x.Name == "OkResult").CurrentValue == after.Single(x => x.Name == "OkResult").CurrentValue,
                "变量对写入失败时发生了部分写入。");
        }
    }
}
