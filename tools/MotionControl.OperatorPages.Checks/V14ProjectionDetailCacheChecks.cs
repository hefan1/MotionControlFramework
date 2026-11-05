using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MotionControl.TaskEngine.Projection;
using MotionControl.TaskEngine.Wpf.Monitoring;
using MotionControl.TaskEngine.Wpf.Views;
namespace MotionControl.OperatorPages.Checks
{
    /// <summary>大引用显示缓存的独立检查，不建立控制器或读取文件。</summary>
    internal static class V14ProjectionDetailCacheChecks
    {
        /// <summary>验证共享路径、显示上限和选择及语言切换的缓存身份。</summary>
        internal static void Run()
        {
            var references = new List<TaskProjectReference>();
            for (int i = 0; i < 151; i++)
            {
                var reference = new TaskProjectReference();
                foreach (var field in new Dictionary<string,string> { { "StationId", "a" }, { "TaskId", "shared" },
                    { "NodeId", "node" }, { "Kind", "Input" }, { "Target", "DI" }, { "Path", "call/" + i }, { "Status", "未解析" } })
                    typeof(TaskProjectReference).GetProperty(field.Key).SetValue(reference, field.Value);
                references.Add(reference);
            }
            var constructor = typeof(TaskProjectProjection).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var projection = (TaskProjectProjection)constructor.Invoke(new object[] { "已加载会话", "test", null, null, references,
                new List<string> { "展开截断：测试证据" } });
            var cacheType = typeof(TaskMonitorView).Assembly.GetType("MotionControl.TaskEngine.Wpf.Views.TaskProjectionDetailCache");
            var cache = Activator.CreateInstance(cacheType, true);
            var read = cacheType.GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic);
            object[] args = { projection, null, "a", "shared", "node", false };
            var rows = (IReadOnlyList<TaskDetailField>)read.Invoke(cache, args);
            if (!ReferenceEquals(rows, read.Invoke(cache, args))) throw new InvalidOperationException("同选择刷新重新扫描或重建字段");
            if (rows.Count(row => row.Name.StartsWith("Input")) != 100 || !rows.Any(row => row.Value.Contains("151"))
                || !rows.Any(row => row.Value.Contains("显示不全")) || !rows.Any(row => row.Value.Contains("展开截断")))
                throw new InvalidOperationException("大引用上限、总数或失败诊断丢失");
            if (!rows.Any(row => row.Value.Contains("call/0")) || !rows.Any(row => row.Value.Contains("call/99")))
                throw new InvalidOperationException("共享调用路径被混为单个实例");
            if (!rows.Any(row => row.Value.Contains("已投影项中匹配")) || !rows.Any(row => row.Value.Contains("完整性未提供")))
                throw new InvalidOperationException("截断投影错误声称完整总数");
            var unavailable = (IReadOnlyList<TaskDetailField>)read.Invoke(cache, new object[] { null, new InvalidOperationException("故障证据"), "a", "shared", "node", false });
            if (!unavailable.Any(row => row.Value.Contains("数量未知")) || !unavailable.Any(row => row.Value.Contains("故障证据"))
                || unavailable.Any(row => row.Value.Contains("匹配 0"))) throw new InvalidOperationException("投影失败被伪装成零引用");
            var missing = (IReadOnlyList<TaskDetailField>)read.Invoke(cache, new object[] { null, null, "a", "shared", "node", true });
            if (!missing.Any(row => row.Value.Contains("Count unknown"))) throw new InvalidOperationException("缺投影未显示数量未知");
            args[2] = "b";
            var otherStation = (IReadOnlyList<TaskDetailField>)read.Invoke(cache, args);
            if (ReferenceEquals(rows, otherStation) || otherStation.Any(row => row.Name.StartsWith("Input"))) throw new InvalidOperationException("跨工位复用了引用");
            args[2] = "a"; args[4] = "other";
            if (((IReadOnlyList<TaskDetailField>)read.Invoke(cache, args)).Any(row => row.Name.StartsWith("Input"))) throw new InvalidOperationException("跨节点复用了引用");
            args[4] = "node"; args[5] = true;
            if (!((IReadOnlyList<TaskDetailField>)read.Invoke(cache, args)).Any(row => row.Name == "Definition references")) throw new InvalidOperationException("语言切换没有失效缓存");
        }
    }
}
