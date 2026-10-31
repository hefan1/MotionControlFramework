using System;
using System.Linq;

namespace MotionControl.TaskEngine.Device.InMemory
{
    /// <summary>交接信号使用已有变量存储，在同一设备锁内核对条件并提交完成值。</summary>
    public sealed partial class InMemoryTaskDevice
    {
        /// <summary>同一份快照读取请求和完成，保留未知数据。</summary>
        public void ReadHandshake(string requestName, string completedName, out bool? request, out bool? completed)
        {
            lock (_sync)
            {
                var values = _variables.GetSnapshot();
                var requestValue = values.Single(value => string.Equals(value.Name, requestName, StringComparison.OrdinalIgnoreCase));
                var completedValue = values.Single(value => string.Equals(value.Name, completedName, StringComparison.OrdinalIgnoreCase));
                request = requestValue.HasValue ? (bool?)bool.Parse(requestValue.CurrentValue) : null;
                completed = completedValue.HasValue ? (bool?)bool.Parse(completedValue.CurrentValue) : null;
            }
        }

        /// <summary>请求仍保持 true 才发布完成，避免动作已结束后把过期请求当作有效。</summary>
        public bool TryPublishCompletion(string requestName, string completedName)
        {
            lock (_sync)
            {
                bool? request, completed;
                ReadHandshake(requestName, completedName, out request, out completed);
                if (request != true || completed != false) return false;
                WriteVariable(completedName, true);
                return true;
            }
        }

        /// <summary>请求方先清请求，完成方才清完成；未知状态不能放行下一周期。</summary>
        public bool TryClearCompletion(string requestName, string completedName)
        {
            lock (_sync)
            {
                bool? request, completed;
                ReadHandshake(requestName, completedName, out request, out completed);
                if (request != false || completed != true) return false;
                WriteVariable(completedName, false);
                return true;
            }
        }
    }
}
