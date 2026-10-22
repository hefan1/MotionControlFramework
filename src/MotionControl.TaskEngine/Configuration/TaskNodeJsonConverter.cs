using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Configuration
{
    /// <summary>只按明确登记的 kind 创建普通节点，不允许 JSON 指定任意 CLR 类型。</summary>
    internal sealed class TaskNodeJsonConverter : JsonConverter
    {
        /// <summary>写入交给普通属性序列化，避免递归调用本转换器。</summary>
        public override bool CanWrite => false;

        /// <summary>仅处理树中声明为 TaskNode 的入口。</summary>
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(TaskNode);
        }

        /// <summary>读取固定类型并检查必填标识和容易误用默认值的字段。</summary>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject data = JObject.Load(reader);
            Require(data, "kind", JTokenType.String);
            Require(data, "id", JTokenType.String);
            Require(data, "name", JTokenType.String);
            Require(data, "timeoutMs", JTokenType.Integer);
            string kind = (string)data["kind"];
            TaskNode node;
            switch (kind)
            {
                case "Sequence": node = new SequenceNode(); Require(data, "children", JTokenType.Array); break;
                case "ParallelGroup": node = new ParallelGroupNode(); Require(data, "branches", JTokenType.Array); break;
                case "ConditionBranch": node = new ConditionBranchNode(); break;
                case "MoveAxis":
                    node = new MoveAxisNode();
                    Require(data, "mode", JTokenType.String);
                    break;
                case "CoordinatedMove": node = new CoordinatedMoveNode(); break;
                case "AxisCommand":
                    node = new AxisCommandNode();
                    Require(data, "command", JTokenType.String);
                    break;
                case "SetOutput":
                    node = new SetOutputNode();
                    Require(data, "value", JTokenType.Boolean);
                    break;
                case "WaitInput":
                    node = new WaitInputNode();
                    Require(data, "expected", JTokenType.Boolean);
                    break;
                case "WaitCondition": node = new WaitConditionNode(); break;
                case "Delay": node = new DelayNode(); break;
                case "SetVariable": node = new SetVariableNode(); break;
                case "CallTask": node = new CallTaskNode(); break;
                case "ResourceScope": node = new ResourceScopeNode(); break;
                default: throw new JsonSerializationException("节点 " + data["id"] + " 使用了未支持的 kind：" + kind);
            }
            using (JsonReader objectReader = data.CreateReader())
                serializer.Populate(objectReader, node);
            return node;
        }

        /// <summary>本转换器不参与写入；意外直接调用时明确报错。</summary>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotSupportedException("节点写入使用标准 JSON 属性序列化。");
        }

        /// <summary>必填字段必须存在且具有指定 JSON 类型，不用默认值掩盖遗漏。</summary>
        private static void Require(JObject data, string name, JTokenType type)
        {
            if (data[name] == null || data[name].Type != type)
                throw new JsonSerializationException("节点 " + data["id"] + " 的字段 " + name + " 缺失或类型错误。");
        }
    }
}
