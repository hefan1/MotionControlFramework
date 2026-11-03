using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace MotionControl.VirtualController
{
    /// <summary>十三轴测试设备的物料交接状态；只在显式启用后根据多项反馈推进。</summary>
    internal sealed class RotaryClearancePlant
    {
        /// <summary>交接阶段：空闲、共同夹持或下料搬运。</summary>
        internal string Stage = "Idle";
        /// <summary>此次被共同夹持的夹位。</summary>
        internal string Source;
        /// <summary>确认夹持时旋转台所在位置。</summary>
        internal double TablePosition;
        /// <summary>异常锁存后禁止自动改变物料状态。</summary>
        internal string Fault;
        /// <summary>缓存接收完成曾复位，防止旧完成信号重复使用。</summary>
        internal bool BufferReadyArmed;
        /// <summary>剔除完成曾复位，防止旧完成信号重复使用。</summary>
        internal bool RejectReadyArmed;
    }

    internal sealed partial class ControllerState
    {
        /// <summary>默认未启用，不干预旧生产模型和其他设备。</summary>
        private RotaryClearancePlant _rotaryClearancePlant;

        /// <summary>仅允许四组物料传感器、三个夹具和独立完成反馈均已安装后启用。</summary>
        private void ConfigureRotaryClearancePlant()
        {
            if (_rotaryClearancePlant != null) throw new InvalidOperationException("清料交接模型已启用。");
            foreach (string name in new[] { "A夹位", "B夹位", "缓存位", "下料位" })
                if (!_materialSensors.ContainsKey(name)) throw new InvalidOperationException("缺少独立物料传感器：" + name);
            RequireActuator(41, 41, 61); RequireActuator(42, 42, 62); RequireActuator(51, 51, 75);
            RequireCompletionFeedback(49); RequireCompletionFeedback(48);
            _rotaryClearancePlant = new RotaryClearancePlant();
        }

        /// <summary>启用后冻结依赖模型配置，防止更换反馈通道导致虚假交接。</summary>
        private void EnsureClearanceModelsMutable()
        {
            if (_rotaryClearancePlant != null) throw new InvalidOperationException("交接模型已启用，不可更改依赖模型；请重启隔离测试控制器。");
        }

        /// <summary>完成信号必须使用约定的输出及有效电平。</summary>
        private void RequireCompletionFeedback(int channel)
        {
            FeedbackRule rule;
            if (!_feedback.TryGetValue(channel, out rule) || rule.Output != channel || !rule.ActiveHigh)
                throw new InvalidOperationException("缺少对应的完成反馈：DI" + channel);
        }

        /// <summary>检查示例夹具的输出与两路到位输入确实使用约定通道。</summary>
        private void RequireActuator(int output, int closed, int open)
        {
            ActuatorRule rule;
            if (!_actuators.TryGetValue(output, out rule) || rule.ClampedInput != closed || rule.ReleasedInput != open)
                throw new InvalidOperationException("缺少对应夹具及独立到位反馈：DO" + output);
        }

        /// <summary>读取当前交接阶段和锁存故障，不改变轴、输出或物料。</summary>
        private JObject ReadRotaryClearancePlant()
        {
            if (_rotaryClearancePlant == null) return new JObject { ["ok"] = true, ["configured"] = false };
            return new JObject { ["ok"] = true, ["configured"] = true, ["clearancePlant"] = new JObject {
                ["stage"] = _rotaryClearancePlant.Stage, ["source"] = _rotaryClearancePlant.Source,
                ["fault"] = _rotaryClearancePlant.Fault } };
        }

        /// <summary>交接中外部注入改变物料状态时锁存不确定，不能自动恢复。</summary>
        private void MarkRotaryPlantExternalChange()
        {
            if (_rotaryClearancePlant != null && _rotaryClearancePlant.Stage != "Idle")
                _rotaryClearancePlant.Fault = _rotaryClearancePlant.Fault ?? "交接中物料反馈被测试场景更改，位置需要核查。";
        }

        /// <summary>根据轴位置、独立夹具和物料反馈更新示例交接；任一条件未知均不推进。</summary>
        private void RefreshRotaryClearancePlant()
        {
            RotaryClearancePlant plant = _rotaryClearancePlant;
            if (plant == null || plant.Fault != null) return;
            if (HasClearanceReadFault()) { plant.Fault = "清料相关输入或轴读取故障，需要核查。"; return; }
            if (!_inputs[49]) plant.BufferReadyArmed = true;
            if (!_inputs[48]) plant.RejectReadyArmed = true;
            MaterialSensorRule a = _materialSensors["A夹位"], b = _materialSensors["B夹位"];
            MaterialSensorRule buffer = _materialSensors["缓存位"], unload = _materialSensors["下料位"];
            if (!MaterialKnown(a) || !MaterialKnown(b) || !MaterialKnown(buffer) || !MaterialKnown(unload)) return;

            if (plant.Stage == "Idle")
            {
                if (IsPresent(buffer) && IsEmpty(unload) && plant.RejectReadyArmed && _inputs[67] && _inputs[48]
                    && At(8, 2) && At(9, 2) && At(5, 0) && At(10, 1))
                {
                    SetMaterial(buffer, false); plant.RejectReadyArmed = false;
                    return;
                }
                if (!IsEmpty(unload) || !IsEmpty(buffer) || !UnloadAreaSafeAtPick()) return;
                bool minus = At(11, -90), plus = At(11, 90);
                MaterialSensorRule source = minus ? b : plus ? a : null;
                if (source == null || !IsPresent(source) || !ActuatorClosed(source == a ? 41 : 42, source == a ? 61 : 62)
                    || !ActuatorClosed(51, 75)) return;
                SetMaterial(unload, true);
                plant.Source = source.Name; plant.TablePosition = minus ? -90 : 90; plant.Stage = "Shared";
                return;
            }

            if (plant.Stage == "Shared")
            {
                MaterialSensorRule source = _materialSensors[plant.Source];
                if (!UnloadAreaSafeAtPick() || !At(11, plant.TablePosition) || !ActuatorClosed(51, 75) || !IsPresent(unload) || !IsPresent(source))
                { plant.Fault = "共同夹持期间机构或物料状态变化，需要核查。"; return; }
                int closed = plant.Source == "A夹位" ? 41 : 42, open = plant.Source == "A夹位" ? 61 : 62;
                if (!ActuatorOpen(closed, open)) return;
                SetMaterial(source, false); plant.Stage = "Transit";
                return;
            }

            if (plant.Stage == "Transit")
            {
                if (!IsPresent(unload) || !At(11, plant.TablePosition))
                { plant.Fault = "下料搬运期间物料或旋转台状态变化，需要核查。"; return; }
                if (ActuatorOpen(51, 75))
                {
                    if (!At(0, 0) || !At(8, 0) || !At(9, 0) || !IsEmpty(buffer) || !plant.BufferReadyArmed || !_inputs[49])
                    { plant.Fault = "下料夹具未在缓存接收条件满足时松开，需要核查。"; return; }
                    SetMaterial(unload, false); SetMaterial(buffer, true);
                    plant.Source = null; plant.Stage = "Idle"; plant.BufferReadyArmed = false;
                }
            }
        }

        /// <summary>等待轴及共享区域位于本测试布局的接料位置；它不是连续安全监控。</summary>
        private bool UnloadAreaSafeAtPick()
        {
            return At(0, 1) && At(1, 0) && At(2, 0) && At(3, 0) && At(5, 0)
                && At(6, 0) && At(7, 0) && At(8, 0) && At(9, 0) && At(10, 0) && At(12, 0);
        }

        /// <summary>验证测试轴已建立参考、停稳、无报警与限位且位置符合目标。</summary>
        private bool At(int axisNumber, double position)
        {
            JObject state = _axes[axisNumber].Snapshot();
            double value = state.Value<double>("position"), velocity = state.Value<double>("velocity");
            return state["originInputActive"] != null && state["originInputActive"].Type != JTokenType.Null
                && state.Value<bool>("servoEnabled") && state.Value<bool>("homed") && !state.Value<bool>("moving")
                && !state.Value<bool>("alarmActive") && !state.Value<bool>("requiresHomingAfterLimitAlarm")
                && !state.Value<bool>("positiveLimitActive") && !state.Value<bool>("negativeLimitActive")
                && !double.IsNaN(value) && !double.IsInfinity(value) && !double.IsNaN(velocity) && !double.IsInfinity(velocity)
                && Math.Abs(value - position) <= 0.01 && Math.Abs(velocity) <= 0.001;
        }

        /// <summary>仅把两路互斥的真实输入当作已知状态。</summary>
        private bool MaterialKnown(MaterialSensorRule sensor) { return _inputs[sensor.PresentInput] != _inputs[sensor.EmptyInput]; }
        /// <summary>确认有料且没有无料冲突。</summary>
        private bool IsPresent(MaterialSensorRule sensor) { return _inputs[sensor.PresentInput] && !_inputs[sensor.EmptyInput]; }
        /// <summary>确认无料且没有有料冲突。</summary>
        private bool IsEmpty(MaterialSensorRule sensor) { return !_inputs[sensor.PresentInput] && _inputs[sensor.EmptyInput]; }
        /// <summary>根据本测试设备的物理交接改变两路独立传感器。</summary>
        private void SetMaterial(MaterialSensorRule sensor, bool present)
        { _inputs[sensor.PresentInput] = present; _inputs[sensor.EmptyInput] = !present; }
        /// <summary>确认夹紧端到位且松开端未触发。</summary>
        private bool ActuatorClosed(int closed, int open) { return _inputs[closed] && !_inputs[open]; }
        /// <summary>确认松开端到位且夹紧端未触发。</summary>
        private bool ActuatorOpen(int closed, int open) { return !_inputs[closed] && _inputs[open]; }

        /// <summary>任何相关读输入或读轴故障注入均锁存，不能让故障前后状态自动迁移。</summary>
        private bool HasClearanceReadFault()
        {
            foreach (int axis in new[] { 0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12 })
                if (_commandFaults.ContainsKey(FaultKey("readAxis", axis))) return true;
            var inputs = new List<int> { 41, 42, 51, 61, 62, 75, 49, 48, 67 };
            foreach (MaterialSensorRule sensor in _materialSensors.Values) { inputs.Add(sensor.PresentInput); inputs.Add(sensor.EmptyInput); }
            foreach (int input in inputs) if (_commandFaults.ContainsKey(FaultKey("readInput", input))) return true;
            return false;
        }
    }
}
