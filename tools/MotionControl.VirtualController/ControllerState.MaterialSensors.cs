using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace MotionControl.VirtualController
{
    /// <summary>单个位置独立的有料和空料输入；状态由测试场景明确注入，不从输出推断。</summary>
    internal sealed class MaterialSensorRule
    {
        /// <summary>测试位置名称。</summary>
        internal string Name;
        /// <summary>有料反馈通道。</summary>
        internal int PresentInput;
        /// <summary>无料反馈通道。</summary>
        internal int EmptyInput;
    }

    internal sealed partial class ControllerState
    {
        /// <summary>独立配置的物料传感器；未配置时不占用通道。</summary>
        private readonly Dictionary<string, MaterialSensorRule> _materialSensors = new Dictionary<string, MaterialSensorRule>(StringComparer.OrdinalIgnoreCase);

        /// <summary>判断输入是否属于独立物料传感器。</summary>
        private bool MaterialSensorUsesInput(int input)
        {
            foreach (MaterialSensorRule item in _materialSensors.Values)
                if (item.PresentInput == input || item.EmptyInput == input) return true;
            return false;
        }

        /// <summary>创建一对初始未知的独立反馈，不关联任何输出。</summary>
        private void ConfigureMaterialSensors(JObject request)
        {
            string name = RequireString(request, "name");
            int present = IoIndex(request, "presentInputChannel");
            int empty = IoIndex(request, "emptyInputChannel");
            if (_materialSensors.ContainsKey(name)) throw new InvalidOperationException("物料传感器位置已存在：" + name);
            if (present == empty) throw new InvalidOperationException("有料与空料输入不能使用同一通道。");
            foreach (int input in new[] { present, empty })
            {
                if (_inputs[input] || MaterialSensorUsesInput(input) || AxisSensorUsesInput(input) || ActuatorUsesInput(input)
                    || CameraUsesInput(input) || ScannerUsesInput(input) || _feedback.ContainsKey(input)
                    || (_feeder.Enabled && _feeder.Input == input))
                    throw new InvalidOperationException("物料传感器输入已占用或未复位：DI" + input);
            }
            _materialSensors.Add(name, new MaterialSensorRule { Name = name, PresentInput = present, EmptyInput = empty });
        }

        /// <summary>显式注入有料、空料、未知或两输入冲突，不改变设备输出。</summary>
        private void SetMaterialSensorState(JObject request)
        {
            MaterialSensorRule sensor = FindMaterialSensors(request);
            string state = RequireString(request, "state");
            if (state != "present" && state != "empty" && state != "unknown" && state != "conflict")
                throw new ArgumentException("物料反馈状态须为 present、empty、unknown 或 conflict。");
            bool present = state == "present" || state == "conflict", empty = state == "empty" || state == "conflict";
            if (_inputs[sensor.PresentInput] != present || _inputs[sensor.EmptyInput] != empty) MarkRotaryPlantExternalChange();
            _inputs[sensor.PresentInput] = present;
            _inputs[sensor.EmptyInput] = empty;
        }

        /// <summary>读取该位置两路独立电平；未知或冲突不能解释为无料。</summary>
        private JObject ReadMaterialSensors(JObject request)
        {
            MaterialSensorRule sensor = FindMaterialSensors(request);
            return new JObject { ["ok"] = true, ["materialState"] = new JObject {
                ["name"] = sensor.Name, ["presentInputChannel"] = sensor.PresentInput, ["emptyInputChannel"] = sensor.EmptyInput,
                ["present"] = _inputs[sensor.PresentInput], ["empty"] = _inputs[sensor.EmptyInput] } };
        }

        /// <summary>按显式名称查找设备测试传感器。</summary>
        private MaterialSensorRule FindMaterialSensors(JObject request)
        {
            string name = RequireString(request, "name");
            MaterialSensorRule result;
            if (!_materialSensors.TryGetValue(name, out result)) throw new InvalidOperationException("物料传感器位置不存在：" + name);
            return result;
        }
    }
}
