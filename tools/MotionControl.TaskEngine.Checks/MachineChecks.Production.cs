using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>真实任务定义驱动的上料、检测、下料连续交接场景。</summary>
    internal static partial class MachineChecks
    {
        /// <summary>旧完成或未知值不能触发动作；请求保持有效时也只能执行一次。</summary>
        private static async Task OldAndUnknownSignals()
        {
            var device = Device();
            var task = TaskDefinition("Work", new DelayNode { Id = "work", Name = "触发动作", Milliseconds = 20 }); task.Root.TimeoutMs = 80;
            var station = Station("work", "Work", StationTaskMode.OnRequest);
            station.RequestVariable = "feed.request"; station.CompletedVariable = "feed.complete";
            var machine = Machine(device, new[] { task }, station);
            machine.Reset(); machine.Start(); await Task.Delay(140);
            CheckAssert.True(State(machine, "work").Run == null && machine.GetSnapshot().State == TaskMachineState.Running, "等待未知请求也消耗了动作时限。");
            device.SetVariable("Feed.Request", "true"); device.SetVariable("Feed.Complete", "true");
            await Task.Delay(50);
            CheckAssert.True(State(machine, "work").CompletedCycles == 0, "旧完成信号触发了新任务。");
            device.SetVariable("Feed.Request", "false");
            await Until(() => BooleanValue(device, "Feed.Complete") == false, "旧交接没有复归。");
            device.SetVariable("Feed.Request", "true");
            await Until(() => BooleanValue(device, "Feed.Complete") == true, "新请求未执行。");
            await Task.Delay(80);
            CheckAssert.True(State(machine, "work").CompletedCycles == 1, "保持请求 true 重复执行了任务。");
            device.SetVariable("Feed.Request", "false");
            await Until(() => BooleanValue(device, "Feed.Complete") == false, "完成后没有复归。");
        }

        /// <summary>按样例任务执行三十个产品，每个编号必须完整传过三个工位。</summary>
        private static Task ProductionPipeline()
        {
            return RunPipelineAsync(false);
        }

        /// <summary>可独立运行的内存演示命令，成功与失败均关闭设备会话。</summary>
        internal static int RunProductionDemo()
        {
            try { RunPipelineAsync(true).GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally
            {
                foreach (TaskMachineController machine in Machines) Wait(machine.CloseAsync()).GetAwaiter().GetResult();
                Machines.Clear();
            }
        }

        /// <summary>读取随项目提供的 JSON 清单和正文，通过上游请求实际驱动后续工位。</summary>
        private static async Task RunPipelineAsync(bool verbose)
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples", "ProductionDemo");
            var definitions = new[] { "Load", "Inspect", "Unload" }
                .Select(name => TaskDefinitionJson.Deserialize(File.ReadAllText(Path.Combine(folder, name + ".json")))).ToArray();
            var device = Device();
            foreach (string name in new[] { "Feed.Request", "Feed.Complete", "Inspect.Request", "Inspect.Complete", "Unload.Request", "Unload.Complete" })
                device.SetVariable(name, "false");
            TaskMachineConfiguration configuration;
            using (var workspace = new TestWorkspace())
            {
                string target = Path.Combine(workspace.Store.GetMachineDirectory("ProductionDemo"), "Parameters");
                Directory.CreateDirectory(target);
                File.Copy(Path.Combine(folder, "TaskMachine.json"), Path.Combine(target, "TaskMachine.json"));
                configuration = new TaskMachineConfigurationStore(workspace.Store).Load("ProductionDemo", definitions, device.Catalog);
            }
            var machine = new TaskMachineController(configuration, definitions, device); Machines.Add(machine);
            machine.Reset(); machine.Start();
            for (int product = 1; product <= 30; product++)
            {
                device.SetVariable("Source.Id", product.ToString(System.Globalization.CultureInfo.InvariantCulture));
                device.SetVariable("Feed.Request", "true");
                await Until(() => BooleanValue(device, "Feed.Complete") == true || machine.GetSnapshot().State == TaskMachineState.Faulted, "产线未完成当前产品。");
                CheckAssert.True(machine.GetSnapshot().State == TaskMachineState.Running,
                    string.Join("；", machine.GetSnapshot().Faults.Select(fault => fault.Message)));
                CheckAssert.True(Value(device, "Load.Id") == product.ToString() && Value(device, "Inspect.Id") == product.ToString()
                    && Value(device, "Unload.Id") == product.ToString(), "工位交接沿用了上一周期产品编号。");
                device.SetVariable("Feed.Request", "false");
                await Until(() => BooleanValue(device, "Feed.Complete") == false, "上料交接未复归。");
                if (verbose && product % 10 == 0) Console.WriteLine("已完成产品 " + product + "，三个工位编号均已核对。");
            }
            machine.Pause();
            var snapshot = machine.GetSnapshot();
            CheckAssert.True(snapshot.Stations.All(station => station.CompletedCycles == 30)
                && device.ReadAxis("X").Position == 0 && device.ReadAxis("Y").Position == 0
                && device.ReadAxis("R").Position == 30 && snapshot.Resources.Count == 0, "连续生产周期、最终位置或资源状态不正确。");
            CheckAssert.True(device.ReadAxis("X").AcceptedCommands == 60 && device.ReadAxis("Y").AcceptedCommands == 60
                && device.ReadAxis("R").AcceptedCommands == 30, "出现重复或漏执行的产品动作。");
            if (verbose)
            {
                foreach (StationTaskSnapshot station in snapshot.Stations) Console.WriteLine(station.Name + "：成功周期=" + station.CompletedCycles + "；" + station.Detail);
                Console.WriteLine("纯内存连续生产验证通过：X=0，Y=0，R=30，共享资源=0。未连接控制器。");
            }
        }
    }
}
