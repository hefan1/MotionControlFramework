using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MotionControl.VirtualController
{
    /// <summary>单电磁阀夹具的两个独立到位输入及软件动作状态。</summary>
    internal sealed class ActuatorRule
    {
        /// <summary>高电平驱动夹紧、低电平驱动松开的输出通道。</summary>
        public int Output;
        /// <summary>夹紧到位的输入通道，高电平有效。</summary>
        public int ClampedInput;
        /// <summary>松开到位的输入通道，高电平有效。</summary>
        public int ReleasedInput;
        /// <summary>从当前命令到达目标端的动作延迟，单位毫秒。</summary>
        public int DelayMs;
        /// <summary>目标位置尚未到达，包括故障造成的未完成状态。</summary>
        public bool Pending;
        /// <summary>故障时不产生新的到位反馈，当前静止到位状态保留。</summary>
        public bool Stuck;
        /// <summary>测试注入两个到位同时有效的传感器矛盾。</summary>
        public bool Conflict;
        /// <summary>使取消、反向动作或故障前的延迟任务失效。</summary>
        public int Version;
    }

    internal sealed partial class ControllerState
    {
        /// <summary>按输出通道保存显式创建的夹具规则，默认无规则。</summary>
        private readonly Dictionary<int, ActuatorRule> _actuators = new Dictionary<int, ActuatorRule>();

        /// <summary>判断输入是否已被任何夹具规则占用，供其他设备规则检查。</summary>
        private bool ActuatorUsesInput(int input)
        {
            foreach (ActuatorRule actuator in _actuators.Values)
                if (actuator.ClampedInput == input || actuator.ReleasedInput == input) return true;
            return false;
        }

        /// <summary>判断输出是否已被夹具规则占用，供相机规则检查。</summary>
        private bool ActuatorUsesOutput(int output)
        {
            return _actuators.ContainsKey(output);
        }

        /// <summary>安装单电磁阀规则；只接受未占用、低电平的输入和关闭的输出。</summary>
        private void ConfigureActuator(JObject request)
        {
            int output = IoIndex(request, "outputChannel");
            int clamped = IoIndex(request, "clampInputChannel");
            int released = IoIndex(request, "releaseInputChannel");
            int delay = RequireInt(request, "delayMs");
            if (clamped == released) throw new InvalidOperationException("夹紧到位与松开到位不能使用同一输入。");
            if (delay < 0 || delay > 600000) throw new ArgumentOutOfRangeException("delayMs", "动作延迟必须在 0 到 600000 毫秒之间。");
            if (_actuators.ContainsKey(output)) throw new InvalidOperationException("输出已配置夹具规则，请先检查并移除。");
            if (_camera != null && (_camera.TriggerOutput == output || _camera.ResetOutput == output))
                throw new InvalidOperationException("输出已被相机规则使用，不能同时驱动夹具。");
            if (ScannerUsesOutput(output)) throw new InvalidOperationException("输出已被扫码器规则使用。");
            if (AxisSensorUsesInput(clamped) || AxisSensorUsesInput(released)
                || ActuatorUsesInput(clamped) || ActuatorUsesInput(released) || CameraUsesInput(clamped) || CameraUsesInput(released) || ScannerUsesInput(clamped) || ScannerUsesInput(released)
                || MaterialSensorUsesInput(clamped) || MaterialSensorUsesInput(released)
                || _feedback.ContainsKey(clamped) || _feedback.ContainsKey(released)
                || (_feeder.Enabled && (_feeder.Input == clamped || _feeder.Input == released)))
                throw new InvalidOperationException("夹具到位输入已被其他设备规则占用。");
            if (_outputs[output] || _inputs[clamped] || _inputs[released])
                throw new InvalidOperationException("安装夹具规则前，夹具输出及两个到位输入须均为低电平。");
            var actuator = new ActuatorRule { Output = output, ClampedInput = clamped, ReleasedInput = released, DelayMs = delay };
            _actuators.Add(output, actuator);
            // 初始位置未知，按照关闭命令经过完整动作延时后产生松开到位。
            ScheduleActuatorMove(actuator);
        }

        /// <summary>输出边沿开始新动作；旧到位立即消失且过期动作不可写回。</summary>
        private void ActuatorOutputChanged(int channel)
        {
            ActuatorRule actuator;
            if (_actuators.TryGetValue(channel, out actuator)) ScheduleActuatorMove(actuator);
        }

        /// <summary>开始或重新开始动作，卡滞时保持两个到位无效。</summary>
        private void ScheduleActuatorMove(ActuatorRule actuator)
        {
            int version = ++actuator.Version;
            actuator.Pending = true;
            if (actuator.Conflict)
            {
                _inputs[actuator.ClampedInput] = true;
                _inputs[actuator.ReleasedInput] = true;
                return;
            }
            _inputs[actuator.ClampedInput] = false;
            _inputs[actuator.ReleasedInput] = false;
            if (actuator.Stuck) return;
            bool targetClamped = _outputs[actuator.Output];
            Task.Run(async () =>
            {
                await Task.Delay(actuator.DelayMs).ConfigureAwait(false);
                lock (_sync)
                {
                    ActuatorRule current;
                    if (!_actuators.TryGetValue(actuator.Output, out current) || !ReferenceEquals(current, actuator)
                        || version != actuator.Version || actuator.Stuck || actuator.Conflict) return;
                    _inputs[actuator.ClampedInput] = targetClamped;
                    _inputs[actuator.ReleasedInput] = !targetClamped;
                    actuator.Pending = false;
                }
            });
        }

        /// <summary>注入卡滞使在途到位失效；解除后按当前输出重新等待完整动作延时。</summary>
        private void SetActuatorFault(JObject request)
        {
            ActuatorRule actuator = FindActuator(request);
            bool stuck = RequireBool(request, "value");
            if (actuator.Stuck == stuck) return;
            actuator.Stuck = stuck;
            actuator.Version++;
            if (!stuck && actuator.Pending) ScheduleActuatorMove(actuator);
        }

        /// <summary>注入互相矛盾的到位反馈；解除后重新执行完整反馈延时。</summary>
        private void SetActuatorConflict(JObject request)
        {
            ActuatorRule actuator = FindActuator(request);
            bool conflict = RequireBool(request, "value");
            if (actuator.Conflict == conflict) return;
            actuator.Conflict = conflict;
            ScheduleActuatorMove(actuator);
        }

        /// <summary>只读返回通道、电平、待完成状态和卡滞故障。</summary>
        private JObject ReadActuator(JObject request)
        {
            ActuatorRule actuator = FindActuator(request);
            return new JObject { ["ok"] = true, ["actuatorState"] = new JObject {
                ["outputChannel"] = actuator.Output, ["outputValue"] = _outputs[actuator.Output],
                ["clampInputChannel"] = actuator.ClampedInput, ["clampInputValue"] = _inputs[actuator.ClampedInput],
                ["releaseInputChannel"] = actuator.ReleasedInput, ["releaseInputValue"] = _inputs[actuator.ReleasedInput],
                ["delayMs"] = actuator.DelayMs, ["pending"] = actuator.Pending, ["stuck"] = actuator.Stuck,
                ["conflict"] = actuator.Conflict } };
        }

        /// <summary>查找已配置的输出规则，未配置时明确报错。</summary>
        private ActuatorRule FindActuator(JObject request)
        {
            int output = IoIndex(request, "outputChannel");
            ActuatorRule actuator;
            if (!_actuators.TryGetValue(output, out actuator)) throw new InvalidOperationException("该输出尚未配置夹具规则。");
            return actuator;
        }

        /// <summary>关闭输出后移除规则并取消延迟动作；保留输入电平供调用者核对。</summary>
        private void RemoveActuator(JObject request)
        {
            ActuatorRule actuator = FindActuator(request);
            if (_outputs[actuator.Output]) throw new InvalidOperationException("夹具输出仍有效，不能移除规则。");
            actuator.Version++;
            _actuators.Remove(actuator.Output);
        }
    }
}
