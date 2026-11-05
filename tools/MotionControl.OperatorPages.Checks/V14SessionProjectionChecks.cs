using System;
using MotionControl.TaskEngine.Configuration;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Wpf.Simulation;
namespace MotionControl.OperatorPages.Checks
{
    /// <summary>验证会话引用冻结且不会借用后来修改的草稿或文件。</summary>
    internal static class V14SessionProjectionChecks
    {
        /// <summary>核对共享任务的工位路径、源修改隔离及投影失败前不抢占设备。</summary>
        internal static void Run()
        {
            var catalog = new TaskResourceCatalog(new TaskAxisInfo[0], new string[0], new[] { "Output_A" },
                new Dictionary<string, DeviceVariableType>(), new TaskTeachPoint[0]);
            var device = new InMemoryTaskDevice(catalog);
            var node = new SetOutputNode { Id = "set", Name = "输出", OutputName = "Output_A", Value = true };
            var task = new TaskDefinition { TaskId = "Shared", Name = "共享任务", Root = new SequenceNode { Id = "root", Name = "顺序", Children = new List<TaskNode> { node } } };
            var config = new TaskMachineConfiguration { MachineId = "ProjectionTest", Name = "测试", Stations = new List<StationTaskConfiguration> {
                new StationTaskConfiguration { StationId = "a", Name = "A", TaskId = "Shared", Mode = StationTaskMode.Once },
                new StationTaskConfiguration { StationId = "b", Name = "B", TaskId = "Shared", Mode = StationTaskMode.Once } } };
            bool rejected = false;
            try { new TaskMonitorSession(null, new[] { task }, device, "test", false); }
            catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("缺清单投影未拒绝");
            var internalConstructor = typeof(TaskMonitorSession).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 8);
            var failedProjectionSession = (TaskMonitorSession)internalConstructor.Invoke(new object[] {
                config, new[] { task }, device, "test", false, null, null,
                (Action)(() => { throw new InvalidOperationException("注入投影失败"); }) });
            if (typeof(TaskMonitorSession).GetConstructors().Any(constructor => constructor.GetParameters().Length == 8))
                throw new InvalidOperationException("故障注入能力暴露为公开构造接口");
            if (failedProjectionSession.Projection != null || failedProjectionSession.ProjectionError == null)
                throw new InvalidOperationException("投影失败未保留错误或阻止了原会话加载");
            failedProjectionSession.Machine.CloseAsync().GetAwaiter().GetResult();
            var session = new TaskMonitorSession(config, new[] { task }, device, "test", false);
            try
            {
                var projection = session.Projection;
                if (projection.Source != "已加载会话" || projection.SourceSignature != null) throw new InvalidOperationException("伪造会话来源或签名");
                var paths = projection.References.Where(row => row.NodeId == "set" && row.Target == "Output_A").ToArray();
                if (!paths.Any(row => row.StationId == "a") || !paths.Any(row => row.StationId == "b")) throw new InvalidOperationException("共享节点混合工位路径");
                node.OutputName = "DraftChanged";
                config.Stations[0].StationId = "changed";
                string path = Path.GetTempFileName();
                try { File.WriteAllText(path, TaskDefinitionJson.Serialize(task)); }
                finally { File.Delete(path); }
                if (projection.References.Any(row => row.Target == "DraftChanged" || row.StationId == "changed")) throw new InvalidOperationException("后来草稿或文件污染旧投影");
                if (((SetOutputNode)((SequenceNode)session.Library["Shared"].Root).Children[0]).OutputName != "Output_A") throw new InvalidOperationException("显示库未冻结");
            }
            finally { session.Machine.CloseAsync().GetAwaiter().GetResult(); }
        }
    }
}
