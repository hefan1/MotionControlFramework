using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Validation;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>检查输出读回等待，不向真实设备写信号。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>未知和相反的输出均不放行；暂停期间保持等待，恢复后采样当前读回。</summary>
        private static async Task OutputReadbackWait()
        {
            var device = Device();
            var wait = new WaitOutputNode { Id = "wait-do", Name = "等待输出关闭", OutputName = "Vacuum", Expected = false,
                TimeoutMs = 3000, TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } };
            var task = TaskFixtures.Task(wait, Assign("after-do", "Process.Ready", Constant(MotionControl.Framework.Contracts.Variables.DeviceVariableType.Boolean, "true")));
            string json = TaskDefinitionJson.Serialize(task);
            var restored = TaskDefinitionJson.Deserialize(json);
            CheckAssert.NoErrors(new TaskDefinitionValidator().Validate(restored, device.Catalog));
            var parsed = (WaitOutputNode)((SequenceNode)restored.Root).Children[0];
            CheckAssert.True(parsed.OutputName == "Vacuum" && !parsed.Expected, "输出等待定义往返错误。");
            JObject missing = JObject.Parse(json); ((JObject)missing["root"]["children"][0]).Remove("expected");
            CheckAssert.Throws(() => TaskDefinitionJson.Deserialize(missing.ToString()), "缺少期望状态被默认当成 false。");
            parsed.OutputName = "PartReady";
            CheckAssert.HasError(new TaskDefinitionValidator().Validate(restored, device.Catalog), "找不到逻辑 DO");
            TaskRun run = Start(task, device);
            await Until(() => HasNode(run, "wait-do", TaskNodeState.Waiting), "未知输出未进入等待。");
            await Task.Delay(100);
            CheckAssert.True(!run.GetSnapshot().Nodes.Any(item => item.NodeId == "after-do"), "未知输出被当作关闭。");
            device.WriteOutput("Vacuum", true);
            await Task.Delay(100);
            CheckAssert.True(!run.Completion.IsCompleted, "开启的输出错误满足关闭等待。");
            CheckAssert.True(run.Pause(), "输出等待不能暂停。");
            device.WriteOutput("Vacuum", false);
            await Task.Delay(100);
            CheckAssert.True(!run.Completion.IsCompleted, "暂停期间错误继续。");
            run.Resume(); Success(await Complete(run));
            CheckAssert.True(bool.Parse(Value(device, "Process.Ready")), "输出关闭后未继续任务。");
        }
    }
}
