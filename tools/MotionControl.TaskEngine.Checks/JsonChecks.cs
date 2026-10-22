using System;
using System.Linq;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>检查未知格式、类型元数据和容易误用默认值的字段。</summary>
    internal static class JsonChecks
    {
        /// <summary>只允许当前版本和固定节点类型，并拒绝拼错的字段。</summary>
        internal static void UnknownDefinitions()
        {
            CheckInvalid(document => document["schemaVersion"] = 2);
            CheckInvalid(document => document.Remove("schemaVersion"));
            CheckInvalid(document => document["unknown"] = true);
            CheckInvalid(document => document["$type"] = "System.IO.FileInfo, mscorlib");
            CheckInvalid(document => document["root"]["children"][0]["kind"] = "VendorMagic");
            CheckInvalid(document => document["root"]["children"][0]["typoSpeed"] = 123);
            CheckInvalid(document => document["root"]["children"][0]["mode"] = 0);
            CheckInvalid(document => ((JObject)document["root"]["children"][0]["target"]).Remove("source"));
            CheckInvalid(document => ((JObject)document["root"]["children"][0]).Remove("mode"));
        }

        /// <summary>字段重复、多个根对象和损坏文件均不能静默读取。</summary>
        internal static void MalformedFiles()
        {
            string json = TaskDefinitionJson.Serialize(TaskFixtures.Task(TaskFixtures.Move()));
            CheckAssert.Throws(() => TaskDefinitionJson.Deserialize(json + "{}"), "接受了多个根对象。");
            CheckAssert.Throws(() => TaskDefinitionJson.Deserialize("{"), "接受了损坏 JSON。");
            CheckAssert.Throws(() => TaskDefinitionJson.Deserialize(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1")), "接受了重复字段。");
            CheckAssert.Throws(() => TaskDefinitionJson.Deserialize(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"SchemaVersion\": 1")), "接受了大小写重复字段。");
        }

        /// <summary>输出目标值必须显式存在并且是 JSON 布尔值。</summary>
        internal static void RequiredOutputValue()
        {
            var definition = TaskFixtures.Task(new SetOutputNode { Id = "do", Name = "输出", OutputName = "Vacuum", Value = false });
            JObject json = JObject.Parse(TaskDefinitionJson.Serialize(definition));
            JObject node = (JObject)json["root"]["children"][0];
            node.Remove("value");
            CheckAssert.Throws(() => TaskDefinitionJson.Deserialize(json.ToString()), "缺少输出值仍使用默认 false。");
            node["value"] = "false";
            CheckAssert.Throws(() => TaskDefinitionJson.Deserialize(json.ToString()), "字符串被误认为输出布尔值。");
        }

        /// <summary>确认任务模块和本检查程序没有 UI、Host 或厂商运行库依赖。</summary>
        internal static void AssemblyBoundaries()
        {
            foreach (var assembly in new[] { typeof(TaskDefinition).Assembly, typeof(Program).Assembly })
                foreach (var reference in assembly.GetReferencedAssemblies())
                    CheckAssert.True(!reference.Name.Contains("VendorApi") && !reference.Name.Contains("ZMotion")
                        && !reference.Name.Contains("Host") && !reference.Name.Contains("Wpf")
                        && !reference.Name.StartsWith("Presentation", StringComparison.Ordinal),
                        "发现不应有的引用：" + reference.Name);
            string[] forbidden = { "zauxdll.dll", "zmotion.dll", "LTDMC.dll" };
            foreach (string name in forbidden)
                CheckAssert.True(!System.IO.File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name)),
                    "纯离线检查输出中含有厂商 DLL：" + name);
            CheckAssert.True(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                assembly.GetName().Name.Contains("VendorApi") || assembly.GetName().Name.Contains("ZMotion")),
                "进程加载了厂商程序集。");
        }

        /// <summary>从有效定义开始只修改一项，避免无关错误让负面检查误通过。</summary>
        private static void CheckInvalid(Action<JObject> change)
        {
            JObject document = JObject.Parse(TaskDefinitionJson.Serialize(TaskFixtures.Task(TaskFixtures.Move())));
            change(document);
            CheckAssert.Throws(() => TaskDefinitionJson.Deserialize(document.ToString()), "错误 JSON 被接受：" + document);
        }
    }
}
