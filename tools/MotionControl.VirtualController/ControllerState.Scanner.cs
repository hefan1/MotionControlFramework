using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MotionControl.VirtualController
{
    /// <summary>独立扫码器的输出、输入及本次处理状态；只表示软件测试设备。</summary>
    internal sealed class ScannerRule
    {
        /// <summary>高电平维持扫码的触发输出。</summary>
        public int TriggerOutput;
        /// <summary>扫码处理完成输入。</summary>
        public int CompleteInput;
        /// <summary>本次读取成功输入。</summary>
        public int SuccessInput;
        /// <summary>低位在前的十六路连续数据输入起点。</summary>
        public int DataInputStart;
        /// <summary>读取完成的延迟，单位毫秒。</summary>
        public int DelayMs;
        /// <summary>normal 成功、fail 读码失败、noCompletion 保持未完成。</summary>
        public string FaultMode;
        /// <summary>取消或重触发时使旧的异步结果失效。</summary>
        public int Version;
        /// <summary>触发后的工作尚在进行。</summary>
        public bool Pending;
        /// <summary>本次锁存的无符号码；失败或尚未完成时为零。</summary>
        public int Value;
    }

    internal sealed partial class ControllerState
    {
        /// <summary>当前测试扫码器；未配置时不占用任何通道。</summary>
        private ScannerRule _scanner;
        /// <summary>控制器进程内已分配的最后一码，移除模型也不会重置；跨进程不保证唯一。</summary>
        private int _lastScanValue;

        /// <summary>判断扫码器是否占用指定输入。</summary>
        private bool ScannerUsesInput(int input)
        {
            return _scanner != null && (input == _scanner.CompleteInput || input == _scanner.SuccessInput
                || (input >= _scanner.DataInputStart && input < _scanner.DataInputStart + 16));
        }

        /// <summary>判断扫码器是否占用指定输出。</summary>
        private bool ScannerUsesOutput(int output)
        {
            return _scanner != null && output == _scanner.TriggerOutput;
        }

        /// <summary>安装扫码模型，逐项拒绝占用、重复或非低电平的输入。</summary>
        private void ConfigureScanner(JObject request)
        {
            if (_scanner != null) throw new InvalidOperationException("已有扫码器模型，请先检查并移除。");
            int trigger = IoIndex(request, "triggerOutputChannel");
            int complete = IoIndex(request, "completeInputChannel");
            int success = IoIndex(request, "successInputChannel");
            int start = IoIndex(request, "dataInputStart");
            int delay = RequireInt(request, "processingDelayMs");
            if (start > IoCount - 16) throw new ArgumentOutOfRangeException("dataInputStart", "16 位数据输入超出通道范围。");
            if (delay < 0 || delay > 600000) throw new ArgumentOutOfRangeException("processingDelayMs", "处理延迟须在 0 到 600000 毫秒之间。");
            var inputs = new HashSet<int> { complete, success };
            if (inputs.Count != 2) throw new InvalidOperationException("完成与成功输入不能相同。");
            for (int bit = 0; bit < 16; bit++)
                if (!inputs.Add(start + bit)) throw new InvalidOperationException("扫码数据位与完成或成功输入重复。");
            if (_outputs[trigger] || ActuatorUsesOutput(trigger) || CameraUsesOutput(trigger)
                || (_feeder.Enabled && _feeder.Output == trigger))
                throw new InvalidOperationException("扫码触发输出已占用或未关闭。");
            foreach (FeedbackRule feedback in _feedback.Values)
                if (feedback.Output == trigger) throw new InvalidOperationException("扫码触发输出已被其他反馈使用。");
            foreach (int input in inputs)
            {
                if (_inputs[input] || AxisSensorUsesInput(input) || ActuatorUsesInput(input) || CameraUsesInput(input)
                    || _feedback.ContainsKey(input) || (_feeder.Enabled && _feeder.Input == input))
                    throw new InvalidOperationException("扫码输入已占用或未复位：DI" + input);
            }
            _scanner = new ScannerRule { TriggerOutput = trigger, CompleteInput = complete, SuccessInput = success,
                DataInputStart = start, DelayMs = delay, FaultMode = "normal" };
        }

        /// <summary>高沿发起扫码；低沿清除反馈并取消尚未完成的请求。</summary>
        private void ScannerOutputChanged(int channel, bool value)
        {
            ScannerRule scanner = _scanner;
            if (scanner == null || channel != scanner.TriggerOutput) return;
            int version = ++scanner.Version;
            ClearScannerResult(scanner);
            scanner.Pending = value;
            if (!value || scanner.FaultMode == "noCompletion") return;
            Task.Run(async () =>
            {
                await Task.Delay(scanner.DelayMs).ConfigureAwait(false);
                lock (_sync)
                {
                    if (!ReferenceEquals(_scanner, scanner) || scanner.Version != version || !_outputs[channel]) return;
                    scanner.Pending = false;
                    bool success = scanner.FaultMode == "normal" && _lastScanValue < 65535;
                    scanner.Value = success ? ++_lastScanValue : 0;
                    for (int bit = 0; bit < 16; bit++) _inputs[scanner.DataInputStart + bit] = (scanner.Value & (1 << bit)) != 0;
                    _inputs[scanner.SuccessInput] = success;
                    _inputs[scanner.CompleteInput] = true;
                }
            });
        }

        /// <summary>清除上一次结果；调用者必须持有控制器状态锁。</summary>
        private void ClearScannerResult(ScannerRule scanner)
        {
            _inputs[scanner.CompleteInput] = false;
            _inputs[scanner.SuccessInput] = false;
            for (int bit = 0; bit < 16; bit++) _inputs[scanner.DataInputStart + bit] = false;
            scanner.Value = 0;
        }

        /// <summary>配置后续扫码故障；只允许在触发关闭后变更。</summary>
        private void SetScannerFault(JObject request)
        {
            if (_scanner == null) throw new InvalidOperationException("尚未配置扫码器。");
            if (_outputs[_scanner.TriggerOutput]) throw new InvalidOperationException("请先关闭扫码触发输出。");
            string mode = RequireString(request, "mode");
            if (mode != "normal" && mode != "fail" && mode != "noCompletion") throw new ArgumentException("扫码故障模式无效。");
            _scanner.FaultMode = mode;
        }

        /// <summary>只读显示当前扫码状态、故障与数据，不会分配新的编号。</summary>
        private JObject ReadScanner()
        {
            if (_scanner == null) return new JObject { ["ok"] = true, ["configured"] = false };
            return new JObject { ["ok"] = true, ["configured"] = true, ["scannerState"] = new JObject {
                ["triggerOutputChannel"] = _scanner.TriggerOutput, ["completeInputChannel"] = _scanner.CompleteInput,
                ["successInputChannel"] = _scanner.SuccessInput, ["dataInputStart"] = _scanner.DataInputStart,
                ["pending"] = _scanner.Pending, ["complete"] = _inputs[_scanner.CompleteInput],
                ["success"] = _inputs[_scanner.SuccessInput], ["value"] = _scanner.Value,
                ["faultMode"] = _scanner.FaultMode, ["exhausted"] = _lastScanValue >= 65535 } };
        }

        /// <summary>触发关闭后移除扫码模型；保留整个控制器进程内已分配码号。</summary>
        private void RemoveScanner()
        {
            if (_scanner == null) throw new InvalidOperationException("尚未配置扫码器。");
            if (_outputs[_scanner.TriggerOutput]) throw new InvalidOperationException("请先关闭扫码触发输出。");
            _scanner.Version++;
            ClearScannerResult(_scanner);
            _scanner = null;
        }
    }
}
