using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MotionControl.VirtualController
{
    /// <summary>维护测试控制器的轴和数字 IO；所有请求共享同一个状态。</summary>
    internal sealed class ControllerState
    {
        /// <summary>此控制器提供的轴数量，通道从零开始。</summary>
        private const int AxisCount = 64;
        /// <summary>此控制器提供的 DI 和 DO 通道数量。</summary>
        private const int IoCount = 4096;
        /// <summary>运动会话心跳超时时间，单位秒。</summary>
        private const double LeaseTimeoutSeconds = 2;
        /// <summary>跨连接保护轴、IO 和设备规则的锁。</summary>
        private readonly object _sync = new object();
        /// <summary>逻辑工程单位下的轴状态。</summary>
        private readonly VirtualAxis[] _axes = CreateAxes();
        /// <summary>输入电平，默认均为低电平。</summary>
        private readonly bool[] _inputs = new bool[IoCount];
        /// <summary>输出电平，默认均为低电平。</summary>
        private readonly bool[] _outputs = new bool[IoCount];
        /// <summary>由测试人员显式建立的输出到输入反馈规则。</summary>
        private readonly Dictionary<int, FeedbackRule> _feedback = new Dictionary<int, FeedbackRule>();
        /// <summary>当前上料循环规则；未启用时不自动改变供料输入。</summary>
        private readonly FeederRule _feeder = new FeederRule();
        /// <summary>租约计时使用的单调时钟，不受系统时间调整影响。</summary>
        private readonly Stopwatch _leaseClock = Stopwatch.StartNew();
        /// <summary>当前运动控制所有者的随机标识；为空表示没有所有者。</summary>
        private string _leaseId;
        /// <summary>当前租约超时的单调秒数。</summary>
        private double _leaseDeadline;
        /// <summary>主动检查心跳，避免无请求时轴继续运动。</summary>
        private readonly Timer _leaseWatchdog;

        /// <summary>创建控制器并启动租约看门狗。</summary>
        public ControllerState()
        {
            _leaseWatchdog = new Timer(CheckLease, null, 100, 100);
        }

        /// <summary>按命令读取或修改控制器状态；错误由调用方转换为失败响应。</summary>
        public JObject Execute(JObject request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            string command = RequireString(request, "command");
            lock (_sync)
            {
                ExpireLeaseIfNeeded();
                if (RequiresLease(command)) RequireLease(request);
                switch (command)
                {
                    case "ping": return new JObject
                    {
                        ["ok"] = true,
                        ["controllerType"] = "MotionControl.VirtualController",
                        ["protocolVersion"] = 2
                    };
                    case "acquireLease": return AcquireLease();
                    case "heartbeat": RenewLease(request); return Ok();
                    case "releaseLease": ReleaseLease(request); return Ok();
                    case "readInput": return BoolResult(_inputs[IoIndex(request, "channel")]);
                    case "readOutput": return BoolResult(_outputs[IoIndex(request, "channel")]);
                    case "readInputs": return ReadChannels(request, _inputs);
                    case "readOutputs": return ReadChannels(request, _outputs);
                    case "writeOutput":
                        WriteOutput(IoIndex(request, "channel"), RequireBool(request, "value"));
                        return Ok();
                    case "setInput":
                        _inputs[IoIndex(request, "channel")] = RequireBool(request, "value");
                        return Ok();
                    case "readAxis": return new JObject { ["ok"] = true, ["axisState"] = Axis(request).Snapshot() };
                    case "servo": Axis(request).SetServo(RequireBool(request, "value")); return Ok();
                    case "home": Axis(request).Home(); return Ok();
                    case "move": Axis(request).Move(RequireNumber(request, "position"), RequireNumber(request, "speed")); return Ok();
                    case "jog": Axis(request).Jog(RequireNumber(request, "speed")); return Ok();
                    case "stop": Axis(request).Stop(); return Ok();
                    case "resetAlarm": Axis(request).ResetAlarm(); return Ok();
                    case "setAxisAlarm": Axis(request).SetAlarm(RequireBool(request, "value")); return Ok();
                    case "configureFeedback": ConfigureFeedback(request); return Ok();
                    case "setFeedbackFault": SetFeedbackFault(request); return Ok();
                    case "readFeedback": return ReadFeedback(request);
                    case "removeFeedback": RemoveFeedback(request); return Ok();
                    case "configureFeeder": ConfigureFeeder(request); return Ok();
                    case "setFeederFault": SetFeederFault(request); return Ok();
                    case "readFeeder": return ReadFeeder();
                    default: throw new InvalidOperationException("不支持控制器命令：" + command);
                }
            }
        }

        /// <summary>判断会改变轴或输出的生产命令是否必须持有租约。</summary>
        private static bool RequiresLease(string command)
        {
            switch (command)
            {
                case "servo":
                case "home":
                case "move":
                case "jog":
                case "stop":
                case "resetAlarm":
                case "writeOutput": return true;
                default: return false;
            }
        }

        /// <summary>申请唯一会话；其他进程不能抢占未到期的会话。</summary>
        private JObject AcquireLease()
        {
            if (_leaseId != null) throw new InvalidOperationException("运动控制会话已被占用。");
            _leaseId = Guid.NewGuid().ToString("N");
            _leaseDeadline = _leaseClock.Elapsed.TotalSeconds + LeaseTimeoutSeconds;
            return new JObject { ["ok"] = true, ["leaseId"] = _leaseId, ["timeoutMs"] = (int)(LeaseTimeoutSeconds * 1000) };
        }

        /// <summary>检查请求是否来自当前会话，不自动续期。</summary>
        private void RequireLease(JObject request)
        {
            string leaseId = RequireString(request, "leaseId");
            if (_leaseId == null || !string.Equals(_leaseId, leaseId, StringComparison.Ordinal))
                throw new InvalidOperationException("运动控制会话无效或已过期，请重新连接。");
        }

        /// <summary>只允许当前所有者通过心跳延长会话。</summary>
        private void RenewLease(JObject request)
        {
            RequireLease(request);
            _leaseDeadline = _leaseClock.Elapsed.TotalSeconds + LeaseTimeoutSeconds;
        }

        /// <summary>释放当前会话时先停止全部轴，保留输出原值。</summary>
        private void ReleaseLease(JObject request)
        {
            RequireLease(request);
            StopAxesAndClearLease();
        }

        /// <summary>定期检查过期会话；即使客户端断开且不再发请求也会停止轴。</summary>
        private void CheckLease(object unused)
        {
            lock (_sync) ExpireLeaseIfNeeded();
        }

        /// <summary>超时后立即使会话失效并停止所有轴。</summary>
        private void ExpireLeaseIfNeeded()
        {
            if (_leaseId != null && _leaseClock.Elapsed.TotalSeconds >= _leaseDeadline)
                StopAxesAndClearLease();
        }

        /// <summary>安全收尾轴运动；DO 不变，避免猜测设备安全电平。</summary>
        private void StopAxesAndClearLease()
        {
            foreach (VirtualAxis axis in _axes) axis.Stop();
            _leaseId = null;
            _leaseDeadline = 0;
        }

        /// <summary>创建初始静止且未回零的全部轴。</summary>
        private static VirtualAxis[] CreateAxes()
        {
            var axes = new VirtualAxis[AxisCount];
            for (int i = 0; i < axes.Length; i++) axes[i] = new VirtualAxis();
            return axes;
        }

        /// <summary>根据命令中的轴号取得有效轴。</summary>
        private VirtualAxis Axis(JObject request)
        {
            int axis = RequireInt(request, "axis");
            if (axis < 0 || axis >= _axes.Length) throw new ArgumentOutOfRangeException("axis", "轴号必须在 0 到 63 之间。");
            return _axes[axis];
        }

        /// <summary>读取并检查数字 IO 通道号。</summary>
        private static int IoIndex(JObject request, string name)
        {
            int channel = RequireInt(request, name);
            if (channel < 0 || channel >= IoCount) throw new ArgumentOutOfRangeException(name, "IO 通道号必须在 0 到 4095 之间。");
            return channel;
        }

        /// <summary>批量读取通道，结果按请求顺序排列。</summary>
        private static JObject ReadChannels(JObject request, bool[] values)
        {
            var channels = request["channels"] as JArray;
            if (channels == null) throw new ArgumentException("缺少 channels 数组。");
            var result = new JArray();
            foreach (JToken token in channels)
            {
                if (token.Type != JTokenType.Integer) throw new ArgumentException("通道号必须是整数。");
                int channel = token.Value<int>();
                if (channel < 0 || channel >= IoCount) throw new ArgumentOutOfRangeException("channels", "IO 通道号必须在 0 到 4095 之间。");
                result.Add(values[channel]);
            }
            return new JObject { ["ok"] = true, ["values"] = result };
        }

        /// <summary>更新真实输出状态，并通知已显式配置的测试设备规则。</summary>
        private void WriteOutput(int channel, bool value)
        {
            bool previous = _outputs[channel];
            _outputs[channel] = value;
            if (previous == value) return;
            foreach (FeedbackRule rule in _feedback.Values)
            {
                if (rule.Output == channel) ScheduleFeedback(rule, value);
            }
            if (_feeder.Enabled && _feeder.Output == channel)
            {
                if (value && _inputs[_feeder.Input]) _feeder.Gripped = true;
                else if (_feeder.Gripped)
                {
                    _feeder.Gripped = false;
                    _inputs[_feeder.Input] = false;
                    ScheduleFeed(_feeder.EmptyDelayMs);
                }
            }
        }

        /// <summary>建立指定输出对应的输入反馈，并设置延迟与电平。</summary>
        private void ConfigureFeedback(JObject request)
        {
            int input = IoIndex(request, "inputChannel");
            if (_feeder.Enabled && _feeder.Input == input)
                throw new InvalidOperationException("该输入已由上料规则使用，不能同时配置输出反馈。");
            int output = IoIndex(request, "outputChannel");
            int delay = RequireInt(request, "delayMs");
            if (delay < 0 || delay > 600000) throw new ArgumentOutOfRangeException("delayMs", "延迟必须在 0 到 600000 毫秒之间。");
            FeedbackRule old;
            if (_feedback.TryGetValue(input, out old)) old.Version++;
            var rule = new FeedbackRule { Input = input, Output = output, DelayMs = delay, ActiveHigh = RequireBool(request, "activeHigh") };
            _feedback[input] = rule;
            ScheduleFeedback(rule, _outputs[output]);
        }

        /// <summary>设置输入反馈卡住故障；解除后按当前输出重新安排反馈。</summary>
        private void SetFeedbackFault(JObject request)
        {
            int input = IoIndex(request, "inputChannel");
            FeedbackRule rule;
            if (!_feedback.TryGetValue(input, out rule)) throw new InvalidOperationException("该输入没有配置输出反馈规则。");
            rule.Stuck = RequireBool(request, "value");
            rule.Version++;
            if (!rule.Stuck) ScheduleFeedback(rule, _outputs[rule.Output]);
        }

        /// <summary>读取一个输入反馈规则及其当前独立电平和故障状态。</summary>
        private JObject ReadFeedback(JObject request)
        {
            int input = IoIndex(request, "inputChannel");
            FeedbackRule rule;
            if (!_feedback.TryGetValue(input, out rule)) throw new InvalidOperationException("该输入没有配置输出反馈规则。");
            return new JObject
            {
                ["ok"] = true,
                ["feedbackState"] = new JObject
                {
                    ["inputChannel"] = rule.Input,
                    ["outputChannel"] = rule.Output,
                    ["inputValue"] = _inputs[rule.Input],
                    ["outputValue"] = _outputs[rule.Output],
                    ["delayMs"] = rule.DelayMs,
                    ["activeHigh"] = rule.ActiveHigh,
                    ["stuck"] = rule.Stuck
                }
            };
        }

        /// <summary>取消反馈规则并使尚未到期的反馈失效，不擅自改变输入电平。</summary>
        private void RemoveFeedback(JObject request)
        {
            int input = IoIndex(request, "inputChannel");
            FeedbackRule rule;
            if (!_feedback.TryGetValue(input, out rule)) throw new InvalidOperationException("该输入没有配置输出反馈规则。");
            rule.Version++;
            _feedback.Remove(input);
        }

        /// <summary>只在延迟到期且规则仍有效时更新实际输入，避免过期任务覆盖新输出。</summary>
        private void ScheduleFeedback(FeedbackRule rule, bool output)
        {
            int version = ++rule.Version;
            Task.Run(async () =>
            {
                await Task.Delay(rule.DelayMs).ConfigureAwait(false);
                lock (_sync)
                {
                    FeedbackRule current;
                    if (_feedback.TryGetValue(rule.Input, out current) && ReferenceEquals(current, rule)
                        && version == rule.Version && !rule.Stuck)
                        _inputs[rule.Input] = output == rule.ActiveHigh;
                }
            });
        }

        /// <summary>启用或停用上料设备规则，供两种上位机流程复用。</summary>
        private void ConfigureFeeder(JObject request)
        {
            bool enabled = RequireBool(request, "enabled");
            if (!enabled)
            {
                _feeder.Version++;
                _feeder.Enabled = false;
                _feeder.Gripped = false;
                return;
            }
            int input = IoIndex(request, "inputChannel");
            if (_feedback.ContainsKey(input))
                throw new InvalidOperationException("该输入已由输出反馈规则使用，不能同时配置上料规则。");
            int output = IoIndex(request, "outputChannel");
            int feedDelay = RequireInt(request, "feedDelayMs");
            int emptyDelay = RequireInt(request, "emptyDelayMs");
            if (feedDelay < 0 || feedDelay > 600000 || emptyDelay < 0 || emptyDelay > 600000)
                throw new ArgumentOutOfRangeException("feedDelayMs", "供料延迟必须在 0 到 600000 毫秒之间。");
            if (_outputs[output]) throw new InvalidOperationException("夹具输出仍开启，请先关闭后再启用上料规则。");
            _feeder.Version++;
            _feeder.Enabled = enabled;
            _feeder.Input = input;
            _feeder.Output = output;
            _feeder.FeedDelayMs = feedDelay;
            _feeder.EmptyDelayMs = emptyDelay;
            _feeder.Gripped = false;
            _feeder.Fault = false;
            if (enabled)
            {
                _inputs[input] = false;
                ScheduleFeed(feedDelay);
            }
        }

        /// <summary>注入缺料故障；解除故障后重新按延迟供料。</summary>
        private void SetFeederFault(JObject request)
        {
            if (!_feeder.Enabled) throw new InvalidOperationException("上料规则尚未启用。");
            _feeder.Fault = RequireBool(request, "value");
            _feeder.Version++;
            if (!_feeder.Fault && !_inputs[_feeder.Input]) ScheduleFeed(_feeder.FeedDelayMs);
        }

        /// <summary>报告上料规则、缺料故障和当前输入输出，便于确认测试状态。</summary>
        private JObject ReadFeeder()
        {
            return new JObject
            {
                ["ok"] = true,
                ["feederState"] = new JObject
                {
                    ["enabled"] = _feeder.Enabled,
                    ["inputChannel"] = _feeder.Input,
                    ["outputChannel"] = _feeder.Output,
                    ["inputValue"] = _inputs[_feeder.Input],
                    ["outputValue"] = _outputs[_feeder.Output],
                    ["feedDelayMs"] = _feeder.FeedDelayMs,
                    ["emptyDelayMs"] = _feeder.EmptyDelayMs,
                    ["fault"] = _feeder.Fault,
                    ["gripped"] = _feeder.Gripped
                }
            };
        }

        /// <summary>延迟供料，停用规则或注入故障后旧延迟不能再置位。</summary>
        private void ScheduleFeed(int delay)
        {
            int version = ++_feeder.Version;
            Task.Run(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_feeder.Enabled && !_feeder.Fault && version == _feeder.Version)
                        _inputs[_feeder.Input] = true;
                }
            });
        }

        /// <summary>构造无返回值的成功结果。</summary>
        private static JObject Ok() { return new JObject { ["ok"] = true }; }
        /// <summary>构造数字电平读取结果。</summary>
        private static JObject BoolResult(bool value) { return new JObject { ["ok"] = true, ["value"] = value }; }

        /// <summary>读取必需字符串参数。</summary>
        private static string RequireString(JObject request, string name)
        {
            JToken token = request[name];
            if (token == null || token.Type != JTokenType.String) throw new ArgumentException("缺少字符串参数：" + name);
            return token.Value<string>();
        }

        /// <summary>读取必需布尔参数。</summary>
        private static bool RequireBool(JObject request, string name)
        {
            JToken token = request[name];
            if (token == null || token.Type != JTokenType.Boolean) throw new ArgumentException("缺少布尔参数：" + name);
            return token.Value<bool>();
        }

        /// <summary>读取必需整数参数。</summary>
        private static int RequireInt(JObject request, string name)
        {
            JToken token = request[name];
            if (token == null || token.Type != JTokenType.Integer) throw new ArgumentException("缺少整数参数：" + name);
            return token.Value<int>();
        }

        /// <summary>读取有限的数字参数，拒绝无穷和非数字。</summary>
        private static double RequireNumber(JObject request, string name)
        {
            JToken token = request[name];
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) throw new ArgumentException("缺少数字参数：" + name);
            double value = token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("参数必须是有限数值：" + name);
            return value;
        }
    }
}
