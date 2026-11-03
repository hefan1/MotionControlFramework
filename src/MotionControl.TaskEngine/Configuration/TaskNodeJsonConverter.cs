using System;
using System.Linq;
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
                case "MoveAxisGroup":
                    node = new MoveAxisGroupNode();
                    Require(data, "targets", JTokenType.Array);
                    foreach (JToken target in (JArray)data["targets"])
                    {
                        if (!(target is JObject item)) throw new JsonSerializationException("轴组目标必须是对象：" + target.Path);
                        Require(item, "axisName", JTokenType.String, (string)data["id"]);
                        Require(item, "mode", JTokenType.String, (string)data["id"]);
                        Require(item, "target", JTokenType.Object, (string)data["id"]);
                        Require(item, "profile", JTokenType.Object, (string)data["id"]);
                    }
                    break;
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
                case "WaitOutput":
                    node = new WaitOutputNode();
                    Require(data, "outputName", JTokenType.String);
                    Require(data, "expected", JTokenType.Boolean);
                    break;
                case "ReadInput":
                    node = new ReadInputNode();
                    Require(data, "inputName", JTokenType.String);
                    Require(data, "variableName", JTokenType.String);
                    break;
                case "WaitExclusiveInputs":
                    node = new WaitExclusiveInputsNode();
                    Require(data, "firstInputName", JTokenType.String);
                    Require(data, "secondInputName", JTokenType.String);
                    Require(data, "firstVariableName", JTokenType.String);
                    Require(data, "secondVariableName", JTokenType.String);
                    if (data["requiredFirstValue"] != null && data["requiredFirstValue"].Type != JTokenType.Null)
                        Require(data, "requiredFirstValue", JTokenType.Boolean);
                    break;
                case "WaitCondition": node = new WaitConditionNode(); break;
                case "Delay": node = new DelayNode(); break;
                case "SetVariable": node = new SetVariableNode(); break;
                case "CallTask": node = new CallTaskNode(); break;
                case "ResourceScope": node = new ResourceScopeNode(); break;
                default: throw new JsonSerializationException("节点 " + data["id"] + " 使用了未支持的 kind：" + kind);
            }
            MigrateAlarmCodes(data, node);
            using (JsonReader objectReader = data.CreateReader())
                serializer.Populate(objectReader, node);
            return node;
        }

        /// <summary>把旧版单报警字段迁移到多报警数组，读取旧任务时不丢失报警引用。</summary>
        private static void MigrateAlarmCodes(JObject data, TaskNode node)
        {
            string legacyField = null;
            string multipleField = null;
            if (node is WaitInputNode)
            {
                legacyField = "timeoutAlarmCode";
                multipleField = "timeoutAlarmCodes";
            }
            else if (node is WaitConditionNode)
            {
                legacyField = "timeoutAlarmCode";
                multipleField = "timeoutAlarmCodes";
            }
            else if (node is ConditionBranchNode)
            {
                legacyField = "whenFalseAlarmCode";
                multipleField = "whenFalseAlarmCodes";
            }

            if (legacyField == null || data[legacyField] == null)
            {
                return;
            }

            JToken legacyValue = data[legacyField];
            if (legacyValue.Type != JTokenType.String)
            {
                throw new JsonSerializationException("节点 " + node.Id + " 的旧报警代码字段类型错误：" + legacyField + "。");
            }

            string legacyCode = (string)legacyValue;
            JArray codes = data[multipleField] as JArray;
            if (codes == null)
            {
                if (data[multipleField] != null)
                {
                    throw new JsonSerializationException("节点 " + node.Id + " 的报警代码列表必须是数组：" + multipleField + "。");
                }

                codes = new JArray();
                data[multipleField] = codes;
            }

            bool alreadyIncluded = codes.Values<string>().Any(
                code => string.Equals(code, legacyCode, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(legacyCode) && !alreadyIncluded)
            {
                codes.Add(legacyCode);
            }

            data.Remove(legacyField);
        }

        /// <summary>本转换器不参与写入；意外直接调用时明确报错。</summary>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotSupportedException("节点写入使用标准 JSON 属性序列化。");
        }

        /// <summary>必填字段必须有指定 JSON 类型；组内目标沿用所属节点编号并保留具体字段路径。</summary>
        private static void Require(JObject data, string name, JTokenType type, string ownerId = null)
        {
            if (data[name] == null || data[name].Type != type)
            {
                string path = string.IsNullOrEmpty(data.Path) ? name : data.Path + "." + name;
                throw new JsonSerializationException("节点 " + (ownerId ?? data["id"]?.ToString()) + " 的字段 " + path + " 缺失或类型错误。");
            }
        }
    }
}
