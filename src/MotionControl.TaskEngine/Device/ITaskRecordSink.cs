using System;
using System.Collections.Generic;
namespace MotionControl.TaskEngine.Device
{
    /// <summary>单条完整记录的幂等提交端口；失败必须抛错。</summary>
    public interface ITaskRecordSink
    {
        /// <summary>同一编号重试须保留首次采样时间和全部字段；时间必须为 UTC。</summary>
        void CommitRecord(string executionId, string taskId, string nodeId, string category, DateTimeOffset recordedAtUtc, IReadOnlyDictionary<string, string> fields);
    }
}
