using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace MotionControl.VirtualController
{
    internal sealed partial class ControllerState
    {
        /// <summary>按轴号保存显式配置的原点和硬限位模型；未配置时沿用旧轴行为。</summary>
        private readonly Dictionary<int, AxisSensorRule> _axisSensors = new Dictionary<int, AxisSensorRule>();

        /// <summary>检查输入是否已被任一轴的原点或限位模型占用。</summary>
        private bool AxisSensorUsesInput(int input)
        {
            foreach (AxisSensorRule rule in _axisSensors.Values)
                if (rule.OriginInput == input || rule.NegativeInput == input || rule.PositiveInput == input) return true;
            return false;
        }

        /// <summary>静止轴可建立独立位置感应规则；不能抢占其他模型的输入。</summary>
        private void ConfigureAxisSensors(JObject request)
        {
            int axisNumber = RequireInt(request, "axis");
            VirtualAxis axis = Axis(request);
            if (_axisSensors.ContainsKey(axisNumber)) throw new InvalidOperationException("该轴已有传感器模型，请先检查并移除。");
            int origin = IoIndex(request, "originInputChannel");
            int negative = IoIndex(request, "negativeInputChannel");
            int positive = IoIndex(request, "positiveInputChannel");
            double originPosition = RequireNumber(request, "originPosition");
            double halfWidth = RequireNumber(request, "originHalfWidth");
            double negativeLimit = RequireNumber(request, "negativeLimit");
            double positiveLimit = RequireNumber(request, "positiveLimit");
            if (origin == negative || origin == positive || negative == positive)
                throw new InvalidOperationException("原点及正负限位必须使用三个不同输入。");
            double originLow = originPosition - halfWidth;
            double originHigh = originPosition + halfWidth;
            if (halfWidth <= 0 || double.IsInfinity(originLow) || double.IsInfinity(originHigh)
                || double.IsInfinity(positiveLimit - negativeLimit) || negativeLimit >= originLow || positiveLimit <= originHigh)
                throw new ArgumentException("原点感应区必须位于正负限位之间，半宽必须大于零。");
            foreach (int input in new[] { origin, negative, positive })
            {
                if (AxisSensorUsesInput(input) || CameraUsesInput(input) || ActuatorUsesInput(input)
                    || _feedback.ContainsKey(input) || (_feeder.Enabled && _feeder.Input == input))
                    throw new InvalidOperationException("原点或限位输入已被其他设备规则占用：DI" + input);
                if (_inputs[input]) throw new InvalidOperationException("配置轴传感器前输入必须为低电平：DI" + input);
            }
            var rule = new AxisSensorRule { OriginInput = origin, NegativeInput = negative, PositiveInput = positive,
                OriginPosition = originPosition, OriginHalfWidth = halfWidth, NegativeLimit = negativeLimit, PositiveLimit = positiveLimit };
            axis.ConfigureSensors(rule);
            _axisSensors.Add(axisNumber, rule);
            RefreshAxisSensors();
        }

        /// <summary>更新单个传感器的故障模式，按当前物理位置刷新状态。</summary>
        private void SetAxisSensorFault(JObject request)
        {
            Axis(request).SetSensorFault(RequireString(request, "sensor"), RequireString(request, "mode"));
            RefreshAxisSensors();
        }

        /// <summary>只读取模型配置和当前电平，不改变设备配置。</summary>
        private JObject ReadAxisSensors(JObject request)
        {
            return new JObject { ["ok"] = true, ["axisSensors"] = Axis(request).SensorSnapshot() };
        }

        /// <summary>轴静止时移除感应规则；保留当前输入电平，供人员核对。</summary>
        private void RemoveAxisSensors(JObject request)
        {
            int axisNumber = RequireInt(request, "axis");
            VirtualAxis axis = Axis(request);
            axis.RemoveSensors();
            _axisSensors.Remove(axisNumber);
        }

        /// <summary>推进已配置轴并同步三路输入；调用方必须持有控制器状态锁。</summary>
        private void RefreshAxisSensors()
        {
            foreach (KeyValuePair<int, AxisSensorRule> item in _axisSensors)
            {
                bool origin, negative, positive;
                _axes[item.Key].ReadSensors(out origin, out negative, out positive);
                _inputs[item.Value.OriginInput] = origin;
                _inputs[item.Value.NegativeInput] = negative;
                _inputs[item.Value.PositiveInput] = positive;
            }
        }
    }
}
