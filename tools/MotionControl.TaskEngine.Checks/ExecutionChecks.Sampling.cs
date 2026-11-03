using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>锁存数据和轴位置等待的设备隔离检查。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>检查设备提供的编码、零值、未知输入及设备成功信号。</summary>
        private static async Task InputWordSampling()
        {
            string[] bits = Enumerable.Range(0, 16).Select(index => "Bit" + index).ToArray();
            var catalog = new TaskResourceCatalog(new[] { new TaskAxisInfo("X", "Card01", "mm", -100, 100) },
                bits.Concat(new[] { "Valid", "Success" }), new[] { "Vacuum" },
                new Dictionary<string, DeviceVariableType> { { "Code", DeviceVariableType.String } },
                new TaskTeachPoint[0], new string[0], alarmDefinitions: new[] { new TaskAlarmInfo("DEMO_SENSOR_TIMEOUT", "等待传感器超时", MachineAlarmKind.StopMachine) });
            var device = new InMemoryTaskDevice(catalog);
            foreach (string bit in bits) device.SetInput(bit, false);
            device.SetInput("Bit0", true); device.SetInput("Bit3", true);
            device.SetInput("Valid", true); device.SetInput("Success", true);
            var node = new ReadInputWordNode { Id = "word", Name = "读取编码", InputNames = bits.ToList(),
                ValidInputName = "Valid", SuccessInputName = "Success", TargetVariable = "Code", Prefix = "SCAN-", TimeoutMs = 300,
                TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } };
            Success(await Complete(Start(TaskFixtures.Task(node), device)));
            CheckAssert.True(Value(device, "Code") == "SCAN-00009", "未使用设备提供的位值构造编码。");
            device.SetInput("Success", false);
            Failure(await Complete(Start(TaskFixtures.Task(node), device)), "完成或成功输入无效");
            CheckAssert.True(Value(device, "Code") == "SCAN-00009", "失败反馈覆盖了原编码。");
            device.SetInput("Success", true); device.SetInput("Bit0", null);
            Failure(await Complete(Start(TaskFixtures.Task(node), device)), "数据输入未知");
            CheckAssert.True(Value(device, "Code") == "SCAN-00009", "未知输入覆盖了原编码。");
            device.SetInput("Bit0", false); device.SetInput("Bit3", false);
            Failure(await Complete(Start(TaskFixtures.Task(node), device)), "设备数据为零");
        }

        /// <summary>位置不符或未回零不能放行；条件恢复后按反馈通过。</summary>
        private static async Task AxisPositionWait()
        {
            var device = Device();
            device.ConfigureAxis("X", 1, true, false);
            var node = new WaitAxisPositionNode { Id = "axisWait", Name = "等待轴让位", AxisName = "X", Position = 0,
                Tolerance = 0.01, TimeoutMs = 5000, TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } };
            TaskRun run = Start(TaskFixtures.Task(node, Output()), device);
            await Until(() => HasNode(run, "axisWait", TaskNodeState.Waiting), "未等待轴让位。");
            CheckAssert.True(device.ReadOutput("Vacuum") == null, "未回零且位置不符时错误放行。");
            device.ConfigureAxis("X", 0, true, false);
            await Task.Delay(80);
            CheckAssert.True(device.ReadOutput("Vacuum") == null, "未回零时错误放行。");
            device.ConfigureAxis("X", 0, true, true);
            Success(await Complete(run));
            CheckAssert.True(device.ReadOutput("Vacuum") == true, "正确位置和状态未放行。");
        }
    }
}
