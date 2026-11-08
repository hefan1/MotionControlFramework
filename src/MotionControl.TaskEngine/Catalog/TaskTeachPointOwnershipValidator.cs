using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MotionControl.Framework.Contracts.Variables;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Catalog
{
    /// <summary>检查示教组的逻辑归属和工程迁移，防止旧物理通道坐标静默挂到新机构。</summary>
    public static class TaskTeachPointOwnershipValidator
    {
        /// <summary>按当前轴定义核对显式归属；旧文件缺声明时只保留兼容提示，不能声称归属已确认。</summary>
        public static void ValidateCurrent(JObject axisDocument, JObject pointDocument, ICollection<string> notices)
        {
            if (pointDocument == null) return;
            Dictionary<string, AxisBinding> axes = ReadAxes(axisDocument);
            Dictionary<string, JObject> groups = ReadGroups(pointDocument);
            foreach (var pair in groups)
            {
                AxisBinding axis;
                if (!axes.TryGetValue(pair.Key, out axis)) continue; // 未映射组由原目录加载器保留为历史提示。
                JObject group = pair.Value;
                string owner = ReadOptionalText(group, "logicalAxisName");
                string unit = ReadOptionalText(group, "engineeringUnit");
                if (owner != null && !string.Equals(owner, axis.Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("示教点逻辑归属不一致：" + pair.Key + " 原声明为 " + owner
                        + "，当前映射为 " + axis.Name + "；须显式迁移点位，不能按物理通道自动继承。");
                if (unit != null && !string.Equals(NumericValueMetadata.NormalizeUnit(unit),
                    NumericValueMetadata.NormalizeUnit(axis.Unit), StringComparison.Ordinal))
                    throw new InvalidDataException("示教点工程单位不一致：" + pair.Key + " 声明为 " + unit
                        + "，当前轴单位为 " + (axis.Unit ?? "未定义") + "；须核对并迁移坐标。");
                if (owner == null && HasPositions(group))
                    notices?.Add("示教位置组未声明 logicalAxisName，归属未确认：" + pair.Key + " → " + axis.Name
                        + "；暂按现有映射兼容，控制器、通道或逻辑键变化前须显式迁移。");
                if (unit == null && HasPositions(group))
                    notices?.Add("示教位置组工程单位未定义：" + pair.Key + "；不能证明旧坐标与变更后的轴单位一致。");
            }
        }

        /// <summary>比较应用前后映射；换绑后的坐标必须随候选文件明确声明新归属及单位。</summary>
        public static void ValidateMigration(JObject oldAxisDocument, JObject oldPointDocument,
            JObject newAxisDocument, JObject newPointDocument, bool pointFileIncluded, ICollection<string> notices)
        {
            ValidateCurrent(newAxisDocument, newPointDocument, notices);
            if (newPointDocument == null || oldPointDocument == null || oldAxisDocument == null) return;
            Dictionary<string, AxisBinding> oldAxes = ReadAxes(oldAxisDocument);
            Dictionary<string, AxisBinding> newAxes = ReadAxes(newAxisDocument);
            Dictionary<string, JObject> oldGroups = ReadGroups(oldPointDocument);
            Dictionary<string, JObject> newGroups = ReadGroups(newPointDocument);

            foreach (var pair in newGroups)
            {
                AxisBinding newAxis;
                if (!HasPositions(pair.Value) || !newAxes.TryGetValue(pair.Key, out newAxis)) continue;
                bool changed = false;
                string reason = null;
                JObject previousGroup;
                AxisBinding previousAxis;
                if (oldGroups.TryGetValue(pair.Key, out previousGroup) && HasPositions(previousGroup))
                {
                    bool previouslyMapped = oldAxes.TryGetValue(pair.Key, out previousAxis);
                    if (!previouslyMapped || !string.Equals(previousAxis.Name, newAxis.Name, StringComparison.OrdinalIgnoreCase)
                        || !SameUnit(previousAxis.Unit, newAxis.Unit))
                    {
                        changed = true;
                        reason = pair.Key + "：" + (previouslyMapped ? previousAxis.Name : "原未映射") + " → " + newAxis.Name;
                    }
                }
                foreach (var oldAxis in oldAxes)
                {
                    if (string.Equals(oldAxis.Value.Name, newAxis.Name, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(oldAxis.Key, pair.Key, StringComparison.OrdinalIgnoreCase)
                        && oldGroups.TryGetValue(oldAxis.Key, out previousGroup) && HasPositions(previousGroup))
                    {
                        changed = true;
                        reason = newAxis.Name + "：" + oldAxis.Key + " → " + pair.Key;
                        break;
                    }
                }
                if (!changed) continue;

                string owner = ReadOptionalText(pair.Value, "logicalAxisName");
                string unit = ReadOptionalText(pair.Value, "engineeringUnit");
                bool explicitOwner = string.Equals(owner, newAxis.Name, StringComparison.OrdinalIgnoreCase);
                bool explicitUnit = !string.IsNullOrWhiteSpace(newAxis.Unit) && unit != null && SameUnit(unit, newAxis.Unit);
                if (!pointFileIncluded || !explicitOwner || !explicitUnit)
                    throw new InvalidDataException("示教点归属变更需显式迁移：" + reason
                        + "。请在候选 Parameters/ManualAxisPositions.json 中核对坐标，并为该组填写 logicalAxisName 和 engineeringUnit；"
                        + "旧坐标不能随控制器、物理通道、逻辑键或单位变化自动生效。");
                notices?.Add("示教迁移已按显式归属检查：" + reason + "；坐标与真实机构仍需现场复核。");
            }
        }

        /// <summary>从轴配置取得物理通道到稳定逻辑键的映射，不使用显示名。</summary>
        private static Dictionary<string, AxisBinding> ReadAxes(JObject document)
        {
            var result = new Dictionary<string, AxisBinding>(StringComparer.OrdinalIgnoreCase);
            if (document == null) return result;
            var rows = document["axes"] as JArray;
            if (rows == null) throw new InvalidDataException("轴配置缺少 axes 数组。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken row in rows)
            {
                var axis = row as JObject;
                if (axis == null) throw new InvalidDataException("轴配置包含非对象行。");
                string name = (string)axis["name"];
                if (string.IsNullOrWhiteSpace(name)) continue;
                string controller = (string)axis["controllerId"];
                if (string.IsNullOrWhiteSpace(controller) || axis["axisNumber"]?.Type != JTokenType.Integer || (int)axis["axisNumber"] < 0)
                    throw new InvalidDataException("示教归属检查需要有效的控制器和轴号：" + name);
                string key = controller + ".Axis" + ((int)axis["axisNumber"]).ToString(CultureInfo.InvariantCulture);
                if (result.ContainsKey(key) || !names.Add(name)) throw new InvalidDataException("轴映射物理通道或逻辑名称重复：" + name);
                result.Add(key, new AxisBinding { Name = name, Unit = (string)axis["engineeringUnit"] });
            }
            return result;
        }

        /// <summary>读取物理组并拒绝重复键；同一 PointId 属于不同逻辑轴时仍由原复合键规则处理。</summary>
        private static Dictionary<string, JObject> ReadGroups(JObject document)
        {
            if (document["schemaVersion"]?.Type != JTokenType.Integer || (int)document["schemaVersion"] != 1)
                throw new InvalidDataException("示教位置文件版本不受支持；只接受 schemaVersion 1。");
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var groups = document["axes"] as JArray;
            if (groups == null) throw new InvalidDataException("示教位置文件缺少 axes 数组。");
            foreach (JToken row in groups)
            {
                var group = row as JObject;
                if (group == null) throw new InvalidDataException("示教位置组必须是对象。");
                string key = (string)group["axisKey"];
                if (string.IsNullOrWhiteSpace(key) || result.ContainsKey(key))
                    throw new InvalidDataException("示教位置组 axisKey 为空或重复：" + key);
                if (!(group["positions"] is JArray)) throw new InvalidDataException("示教位置组缺少 positions 数组：" + key);
                result.Add(key, group);
            }
            return result;
        }

        /// <summary>只有实际保存过点位的组才需要迁移坐标归属。</summary>
        private static bool HasPositions(JObject group)
        {
            return ((JArray)group["positions"]).Count != 0;
        }

        /// <summary>可选身份字段必须是非空且无首尾空格的字符串；null 表示旧文件未声明。</summary>
        private static string ReadOptionalText(JObject group, string property)
        {
            JToken value = group[property];
            if (value == null || value.Type == JTokenType.Null) return null;
            if (value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)value) || (string)value != ((string)value).Trim())
                throw new InvalidDataException("示教位置组 " + group["axisKey"] + " 的 " + property + " 无效。");
            return (string)value;
        }

        /// <summary>只接受相同单位符号或等价别名，不自动换算坐标。</summary>
        private static bool SameUnit(string left, string right)
        {
            return string.Equals(NumericValueMetadata.NormalizeUnit(left), NumericValueMetadata.NormalizeUnit(right), StringComparison.Ordinal);
        }

        /// <summary>仅用于本次归属核对的轴映射信息。</summary>
        private sealed class AxisBinding
        {
            /// <summary>稳定逻辑键。</summary>
            internal string Name;
            /// <summary>当前工程单位。</summary>
            internal string Unit;
        }
    }
}
