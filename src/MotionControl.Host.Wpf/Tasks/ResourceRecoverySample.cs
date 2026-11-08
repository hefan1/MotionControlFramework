using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>一次只读的资源现场采样，不代表机构或载具已安全。</summary>
    internal sealed class ResourceRecoverySample
    {
        /// <summary>请求编号，不能用旧确认放行新申请。</summary>
        internal string RequestId { get; private set; }
        /// <summary>已配置逻辑轴的采样。</summary>
        private readonly Dictionary<string, TaskAxisState> _axes = new Dictionary<string, TaskAxisState>(StringComparer.OrdinalIgnoreCase);
        /// <summary>已配置逻辑输入的采样。</summary>
        private readonly Dictionary<string, bool> _inputs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        /// <summary>已配置逻辑输出的采样。</summary>
        private readonly Dictionary<string, bool> _outputs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        /// <summary>资源显示名与任务、步骤信息。</summary>
        internal string ResourceDescription { get; private set; }
        /// <summary>供操作者核对的完整采样说明。</summary>
        internal string Description { get; private set; }
        /// <summary>本次顺序读取结束的 UTC 时间；它不是所有轴和 IO 的共同采样时刻。</summary>
        internal DateTime CapturedAtUtc { get; private set; }
        /// <summary>采样中当前有效的逻辑输出；有有效输出时仍不得解除资源隔离。</summary>
        internal IReadOnlyList<string> ActiveOutputs { get { return _outputs.Where(item => item.Value).Select(item => item.Key).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(); } }

        /// <summary>复查被选资源仍待确认，并读取全部已配置轴与逻辑 IO；输出有效时保留采样供核对，不代表允许解除隔离。</summary>
        internal static ResourceRecoverySample Capture(ITaskDevice device, string requestId, bool english = false)
        {
            if (device == null || string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("资源或设备无效。");
            TaskResourceSnapshot resource = FindBlocked(device, requestId);
            var sample = new ResourceRecoverySample { RequestId = requestId };
            sample.ResourceDescription = "申请编号：" + resource.RequestId + "\n任务：" + resource.TaskName + "\n步骤：" + resource.NodeName
                + "\n资源：" + string.Join("、", resource.Resources) + "\n原因：" + resource.Detail;
            var text = new StringBuilder();
            text.AppendLine(sample.ResourceDescription).AppendLine();
            text.AppendLine("以下是读取时的设备采样；命名区域、夹具、载具和物料是否安全必须由现场人员另行确认。");
            text.AppendLine(english ? "Unknown / inspect on site: personnel, workpieces, fixture position and area safety. Output commands do not prove physical feedback." : "未知／待现场确认：人员、物料、夹持到位和区域安全；输出命令不能替代机构反馈。");
            text.AppendLine("轴状态：");
            foreach (string name in device.Catalog.Axes.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                TaskAxisState axis = device.ReadAxis(name);
                if (axis == null || axis.IsBusy || !string.IsNullOrWhiteSpace(axis.Alarm)
                    || double.IsNaN(axis.Position) || double.IsInfinity(axis.Position))
                    throw new InvalidOperationException("轴未确认停稳、状态不可读或存在报警：" + name + "。保留隔离。");
                sample._axes.Add(name, axis);
                text.AppendLine(name + "：位置=" + axis.Position.ToString("G17", CultureInfo.InvariantCulture)
                    + "；已停止；伺服=" + (axis.Enabled ? "已使能" : "未使能") + "；回零=" + (axis.Homed ? "已回零" : "未回零"));
            }
            if (sample._axes.Count == 0) text.AppendLine(english ? "No logical axes configured; this does not establish equipment safety." : "未配置逻辑轴；不能据此证明设备安全。");
            else text.AppendLine(english ? "Confirmed in this sample: configured axes stopped with no axis alarm. Verify position and homing flags under the equipment procedure." : "已确认：以上配置轴本次读取为停止且无轴报警；位置与回零标记仍须按设备规程核对。");
            text.AppendLine(english ? "Input feedback (configured input levels only; verify mechanism and polarity):" : "输入反馈（仅表示配置输入的当前电平，须核对对应机构及极性）：");
            foreach (string name in device.Catalog.Inputs.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                bool? value = device.ReadInput(name);
                if (!value.HasValue) throw new InvalidOperationException("输入状态未知：" + name + "。保留隔离。");
                sample._inputs.Add(name, value.Value);
                text.AppendLine(name + "：" + (value.Value ? "有效" : "无效"));
            }
            if (sample._inputs.Count == 0) text.AppendLine("未配置逻辑输入。");
            text.AppendLine(english ? "Output command state (does not prove fixture clamped or released):" : "输出命令状态（不能证明夹具已夹紧或松开）：");
            foreach (string name in device.Catalog.Outputs.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                bool? value = device.ReadOutput(name);
                if (!value.HasValue) throw new InvalidOperationException("输出状态未知：" + name + "。保留隔离。");
                sample._outputs.Add(name, value.Value);
                text.AppendLine(name + "：" + (value.Value ? "有效" : "无效"));
            }
            if (sample._outputs.Count == 0) text.AppendLine("未配置逻辑输出。");
            FindBlocked(device, requestId);
            sample.CapturedAtUtc = DateTime.UtcNow;
            sample.Description = "采样完成时间：" + sample.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                + "（轴与 IO 依次读取，并非同时采样）\n" + text;
            return sample;
        }

        /// <summary>确认前重新采样；轴位置按该轴工程单位允许 0.001 的差值，使能、回零及 IO 必须一致。</summary>
        internal bool SameAs(ResourceRecoverySample current)
        {
            if (current == null || !string.Equals(RequestId, current.RequestId, StringComparison.Ordinal)
                || _axes.Count != current._axes.Count || _inputs.Count != current._inputs.Count || _outputs.Count != current._outputs.Count) return false;
            foreach (var pair in _axes)
            {
                TaskAxisState axis;
                if (!current._axes.TryGetValue(pair.Key, out axis) || Math.Abs(pair.Value.Position - axis.Position) > 0.001
                    || pair.Value.Enabled != axis.Enabled || pair.Value.Homed != axis.Homed) return false;
            }
            return _inputs.All(pair => current._inputs.TryGetValue(pair.Key, out bool value) && value == pair.Value)
                && _outputs.All(pair => current._outputs.TryGetValue(pair.Key, out bool value) && value == pair.Value);
        }

        /// <summary>仅在明确人工确认、同一会话和复核采样不变时请求底层复查；失败保留隔离。</summary>
        internal bool TryConfirm(ITaskDevice device, bool confirmed, string confirmation, Func<bool> contextValid, out string reason)
        {
            if (!confirmed || string.IsNullOrWhiteSpace(confirmation)) { reason = "必须确认已核查现场并填写检查说明。"; return false; }
            if (contextValid == null || !contextValid()) { reason = "设备未连接、任务尚未终止或会话已变化；保留隔离。"; return false; }
            try
            {
                ResourceRecoverySample current = Capture(device, RequestId);
                if (current.ActiveOutputs.Count != 0) { reason = "输出仍有效：" + string.Join("、", current.ActiveOutputs) + "。保留隔离；请按设备规程处理，系统不会自动切换输出。"; return false; }
                if (ActiveOutputs.Count != 0) { reason = "上次采样中存在有效输出；请重新读取并核查现场。"; return false; }
                if (!SameAs(current)) { reason = "轴或 IO 状态发生变化；请重新读取并核查现场。"; return false; }
                if (!contextValid()) { reason = "复核期间设备或会话状态发生变化；保留隔离。"; return false; }
                return device.TryRecoverResources(RequestId, confirmation.Trim(), out reason);
            }
            catch (Exception error) { reason = "现场复核未通过，保留隔离：" + error.Message; return false; }
        }

        /// <summary>只允许同一会话内当前仍被隔离的资源申请。</summary>
        private static TaskResourceSnapshot FindBlocked(ITaskDevice device, string requestId)
        {
            TaskResourceSnapshot result = device.ReadResources().FirstOrDefault(item => item.RequestId == requestId && item.State == TaskResourceState.Blocked);
            if (result == null) throw new InvalidOperationException("资源状态已变化或不再待确认，请重新读取。 ");
            return result;
        }
    }
}
