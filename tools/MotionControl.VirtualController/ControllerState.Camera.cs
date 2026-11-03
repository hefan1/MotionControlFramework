using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MotionControl.VirtualController
{
    /// <summary>可选的相机触发、处理、完成和复位测试规则。</summary>
    internal sealed class CameraRule
    {
        /// <summary>触发输出的物理通道。</summary>
        public int TriggerOutput;
        /// <summary>相机接受触发的确认输入物理通道。</summary>
        public int AcknowledgeInput;
        /// <summary>处理完成后锁存的输入物理通道。</summary>
        public int CompleteInput;
        /// <summary>可选的合格结果输入，-1 表示未配置结果。</summary>
        public int OkInput;
        /// <summary>可选的不合格结果输入，-1 表示未配置结果。</summary>
        public int NgInput;
        /// <summary>下一次检测完成时返回 OK、NG 或 Invalid（无有效结果）。</summary>
        public string NextResult;
        /// <summary>可选复位输出物理通道；-1 表示没有配置。</summary>
        public int ResetOutput;
        /// <summary>接受触发到处理完成的延迟，单位毫秒。</summary>
        public int ProcessingDelayMs;
        /// <summary>已接受触发并正在处理；故障时保持至复位。</summary>
        public bool Processing;
        /// <summary>已完成并等待复位。</summary>
        public bool Completed;
        /// <summary>注入不完成故障；解除后须复位并重新触发。</summary>
        public bool NoCompletion;
        /// <summary>已接受的工作因故障中断，需明确继续或复位。</summary>
        public bool AwaitingResume;
        /// <summary>复位或卸载规则时，使旧延迟任务失效。</summary>
        public int Version;
    }

    internal sealed partial class ControllerState
    {
        /// <summary>当前相机测试规则；为空时不影响任何输入输出。</summary>
        private CameraRule _camera;

        /// <summary>判断输入是否已由启用的相机规则占用。</summary>
        private bool CameraUsesInput(int input)
        {
            return _camera != null && (_camera.AcknowledgeInput == input || _camera.CompleteInput == input
                || _camera.OkInput == input || _camera.NgInput == input);
        }

        /// <summary>判断输出是否被相机的触发或复位规则占用。</summary>
        private bool CameraUsesOutput(int output)
        {
            return _camera != null && (_camera.TriggerOutput == output || _camera.ResetOutput == output);
        }

        /// <summary>显式安装一条相机规则，不覆盖已有设备规则或改变现有 IO。</summary>
        private void ConfigureCamera(JObject request)
        {
            if (_camera != null) throw new InvalidOperationException("已有相机规则，请先检查并移除。");
            int trigger = IoIndex(request, "triggerOutputChannel");
            int acknowledge = IoIndex(request, "acknowledgeInputChannel");
            int complete = IoIndex(request, "completeInputChannel");
            int reset = request["resetOutputChannel"] == null ? -1 : RequireInt(request, "resetOutputChannel");
            int ok = request["okInputChannel"] == null ? -1 : RequireInt(request, "okInputChannel");
            int ng = request["ngInputChannel"] == null ? -1 : RequireInt(request, "ngInputChannel");
            int delay = RequireInt(request, "processingDelayMs");
            if (acknowledge == complete) throw new InvalidOperationException("相机触发反馈和检测完成不能使用同一输入。");
            if ((ok == -1) != (ng == -1) || ok < -1 || ng < -1 || ok >= IoCount || ng >= IoCount)
                throw new ArgumentOutOfRangeException("okInputChannel", "合格和不合格输入必须同时配置为有效通道或均不配置。");
            if (ok >= 0 && (ok == ng || ok == acknowledge || ok == complete || ng == acknowledge || ng == complete))
                throw new InvalidOperationException("相机的反馈、完成及结果输入必须互不相同。");
            if (reset < -1 || reset >= IoCount || reset == trigger) throw new ArgumentOutOfRangeException("resetOutputChannel", "复位输出须为不同于触发的有效通道或 -1。");
            if (ActuatorUsesOutput(trigger) || ScannerUsesOutput(trigger) || (reset >= 0 && (ActuatorUsesOutput(reset) || ScannerUsesOutput(reset))))
                throw new InvalidOperationException("相机触发或复位输出已被夹具规则占用。");
            if (_feeder.Enabled && (_feeder.Output == trigger || _feeder.Output == reset))
                throw new InvalidOperationException("相机触发或复位输出已被供料规则占用。");
            if (delay < 0 || delay > 600000) throw new ArgumentOutOfRangeException("processingDelayMs", "处理延迟必须在 0 到 600000 毫秒之间。");
            string result = ok >= 0 ? RequireString(request, "result") : "Invalid";
            ValidateCameraResult(result);
            foreach (int input in new[] { acknowledge, complete, ok, ng })
            {
                if (input < 0) continue;
                if (_feedback.ContainsKey(input) || ActuatorUsesInput(input) || AxisSensorUsesInput(input) || ScannerUsesInput(input) || (_feeder.Enabled && _feeder.Input == input))
                    throw new InvalidOperationException("相机输入已被其他设备规则占用。");
                if (_inputs[input]) throw new InvalidOperationException("安装相机规则前，相机输入须均为低电平。");
            }
            if (_outputs[trigger] || (reset >= 0 && _outputs[reset]))
                throw new InvalidOperationException("安装相机规则前，触发、复位和相机输入须均为低电平。");
            _camera = new CameraRule { TriggerOutput = trigger, AcknowledgeInput = acknowledge,
                CompleteInput = complete, OkInput = ok, NgInput = ng, NextResult = result,
                ResetOutput = reset, ProcessingDelayMs = delay };
        }

        /// <summary>根据触发或复位输出的边沿处理相机状态；触发下降不取消图像处理。</summary>
        private void CameraOutputChanged(int channel, bool value)
        {
            CameraRule camera = _camera;
            if (camera == null) return;
            if (channel == camera.ResetOutput && value && !_outputs[camera.TriggerOutput])
            {
                ResetCamera();
                return;
            }
            if (channel != camera.TriggerOutput) return;
            if (!value)
            {
                _inputs[camera.AcknowledgeInput] = false;
                return;
            }
            // 忙碌、完成未复位或复位输出仍有效时忽略触发，不伪造接受反馈。
            if (camera.Processing || camera.Completed || (camera.ResetOutput >= 0 && _outputs[camera.ResetOutput])) return;
            _inputs[camera.AcknowledgeInput] = true;
            camera.Processing = true;
            if (camera.NoCompletion) { camera.AwaitingResume = true; return; }
            ScheduleCameraCompletion(camera);
        }

        /// <summary>为当前已接受工作安排一次完成；故障或复位使旧结果失效。</summary>
        private void ScheduleCameraCompletion(CameraRule camera)
        {
            camera.AwaitingResume = false;
            int version = ++camera.Version;
            Task.Run(async () =>
            {
                await Task.Delay(camera.ProcessingDelayMs).ConfigureAwait(false);
                lock (_sync)
                {
                    if (!ReferenceEquals(_camera, camera) || version != camera.Version || camera.NoCompletion) return;
                    camera.Processing = false;
                    camera.Completed = true;
                    _inputs[camera.CompleteInput] = true;
                    if (camera.OkInput >= 0)
                    {
                        _inputs[camera.OkInput] = camera.NextResult == "OK";
                        _inputs[camera.NgInput] = camera.NextResult == "NG";
                    }
                }
            });
        }

        /// <summary>设置不完成故障；故障使在途处理失效，解除后仍需复位重触发。</summary>
        private void SetCameraFault(JObject request)
        {
            if (_camera == null) throw new InvalidOperationException("尚未配置相机规则。");
            bool fault = RequireBool(request, "value");
            if (fault != _camera.NoCompletion && _camera.Processing)
            {
                _camera.Version++;
                if (fault) _camera.AwaitingResume = true;
            }
            _camera.NoCompletion = fault;
        }

        /// <summary>明确恢复已接受但因故障中断的工作，不生成新的触发或复位结果。</summary>
        private void ResumeCamera()
        {
            CameraRule camera = _camera;
            if (camera == null || !camera.Processing || !camera.AwaitingResume || camera.NoCompletion || camera.Completed
                || (camera.ResetOutput >= 0 && _outputs[camera.ResetOutput]))
                throw new InvalidOperationException("没有可继续的相机工作，或故障/复位输出尚未解除。");
            ScheduleCameraCompletion(camera);
        }

        /// <summary>校验软件检测结果；Invalid 表示没有有效的 OK/NG 结果。</summary>
        private static void ValidateCameraResult(string result)
        {
            if (result != "OK" && result != "NG" && result != "Invalid")
                throw new ArgumentException("相机结果仅支持 OK、NG 或 Invalid。");
        }

        /// <summary>空闲且结果已复位时，选择后续检测周期产生的测试结果。</summary>
        private void SetCameraResult(JObject request)
        {
            if (_camera == null || _camera.OkInput < 0) throw new InvalidOperationException("尚未配置相机结果输入。");
            if (_camera.Processing || _camera.Completed || _outputs[_camera.TriggerOutput])
                throw new InvalidOperationException("相机忙碌或结果未复位，不能改变下一次检测结果。");
            string result = RequireString(request, "result");
            ValidateCameraResult(result);
            _camera.NextResult = result;
        }

        /// <summary>仅对已完成但无有效结果的测试周期，人工指定一次 OK 或 NG；不得覆盖已锁存结果。</summary>
        private void ResolveCameraResult(JObject request)
        {
            CameraRule camera = _camera;
            if (camera == null || camera.OkInput < 0) throw new InvalidOperationException("尚未配置相机结果输入。");
            string result = RequireString(request, "result");
            if (result != "OK" && result != "NG") throw new ArgumentException("人工确认结果只能是 OK 或 NG。");
            if (!camera.Completed || camera.Processing || camera.NextResult != "Invalid" || !_inputs[camera.CompleteInput]
                || _inputs[camera.OkInput] || _inputs[camera.NgInput] || _outputs[camera.TriggerOutput]
                || (camera.ResetOutput >= 0 && _outputs[camera.ResetOutput]))
                throw new InvalidOperationException("当前周期不是待人工确认的无效结果，不能覆盖已有结果或正在进行的动作。");
            _inputs[camera.OkInput] = result == "OK";
            _inputs[camera.NgInput] = result == "NG";
        }

        /// <summary>触发已释放时复位处理和完成信号，使过期延迟任务失效。</summary>
        private void ResetCamera()
        {
            CameraRule camera = _camera;
            if (camera == null) throw new InvalidOperationException("尚未配置相机规则。");
            if (_outputs[camera.TriggerOutput]) throw new InvalidOperationException("相机触发输出仍有效，不能复位。");
            camera.Version++;
            camera.Processing = false;
            camera.Completed = false;
            camera.AwaitingResume = false;
            _inputs[camera.AcknowledgeInput] = false;
            _inputs[camera.CompleteInput] = false;
            if (camera.OkInput >= 0) { _inputs[camera.OkInput] = false; _inputs[camera.NgInput] = false; }
        }

        /// <summary>读取规则和当前 IO，不修改相机状态。</summary>
        private JObject ReadCamera()
        {
            CameraRule camera = _camera;
            if (camera == null) return new JObject { ["ok"] = true, ["configured"] = false };
            return new JObject { ["ok"] = true, ["configured"] = true,
                ["cameraState"] = new JObject {
                    ["triggerOutputChannel"] = camera.TriggerOutput, ["triggerOutputValue"] = _outputs[camera.TriggerOutput],
                    ["acknowledgeInputChannel"] = camera.AcknowledgeInput, ["acknowledgeInputValue"] = _inputs[camera.AcknowledgeInput],
                    ["completeInputChannel"] = camera.CompleteInput, ["completeInputValue"] = _inputs[camera.CompleteInput],
                    ["okInputChannel"] = camera.OkInput, ["okInputValue"] = camera.OkInput >= 0 && _inputs[camera.OkInput],
                    ["ngInputChannel"] = camera.NgInput, ["ngInputValue"] = camera.NgInput >= 0 && _inputs[camera.NgInput],
                    ["nextResult"] = camera.NextResult,
                    ["resetOutputChannel"] = camera.ResetOutput, ["resetOutputValue"] = camera.ResetOutput >= 0 && _outputs[camera.ResetOutput],
                    ["processingDelayMs"] = camera.ProcessingDelayMs,
                    ["processing"] = camera.Processing, ["completed"] = camera.Completed,
                    ["awaitingResume"] = camera.AwaitingResume, ["noCompletion"] = camera.NoCompletion } };
        }

        /// <summary>移除规则并取消在途处理，保留已有输入电平供调用者检查。</summary>
        private void RemoveCamera()
        {
            if (_camera == null) throw new InvalidOperationException("尚未配置相机规则。");
            if (_outputs[_camera.TriggerOutput] || (_camera.ResetOutput >= 0 && _outputs[_camera.ResetOutput]))
                throw new InvalidOperationException("相机触发或复位输出仍有效，不能移除规则。");
            _camera.Version++;
            _camera = null;
        }
    }
}
