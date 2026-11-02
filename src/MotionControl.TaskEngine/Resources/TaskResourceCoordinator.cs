using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Resources
{
    /// <summary>一个内存设备共用的资源队列；整组取得，冲突排队，独立组可同时运行。</summary>
    public sealed class TaskResourceCoordinator
    {
        /// <summary>只保护短小的队列操作；锁内不等待，不调用设备或运行对象。</summary>
        private readonly object _sync = new object();
        /// <summary>目录允许使用的名称。</summary>
        private readonly HashSet<string> _known;
        /// <summary>按申请先后保存等待、占用及待确认记录。</summary>
        private readonly List<TaskResourceRequest> _requests = new List<TaskResourceRequest>();
        /// <summary>真实设备可配置持久记录；内存设备仍可只使用队列。</summary>
        private readonly TaskResourceJournal _journal;

        /// <summary>复制设备目录，不使用静态共享队列。</summary>
        public TaskResourceCoordinator(IEnumerable<string> names, TaskResourceJournal journal = null)
        {
            _known = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            _journal = journal;
        }

        /// <summary>登记完整申请；调用方负责在结束时释放或标记待确认。</summary>
        internal TaskResourceRequest Enqueue(string runId, string taskId, string taskName, string nodeId,
            string nodeName, string executionId, IEnumerable<string> names,
            TaskInterlock exitInterlock = null, TaskCondition exitCondition = null)
        {
            var group = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            if (group.Count == 0 || group.Any(name => !_known.Contains(name)))
                throw new InvalidOperationException("申请包含空资源组或未登记的资源。");
            var request = new TaskResourceRequest(runId, taskId, taskName, nodeId, nodeName, executionId,
                group.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(), exitInterlock, exitCondition);
            lock (_sync) _requests.Add(request);
            return request;
        }

        /// <summary>尝试一次性取得全部资源；相冲突的较早等待者也不能被插队。</summary>
        internal bool TryAcquire(TaskResourceRequest request, out string reason)
        {
            lock (_sync)
            {
                RequirePresent(request);
                if (request.State != TaskResourceState.Waiting) throw new InvalidOperationException("申请不处于等待状态。");
                TaskResourceRequest blocker = FindBlocker(request);
                reason = blocker == null ? "已取得全部资源。" : DescribeBlocker(blocker);
                if (blocker != null) return false;
                if (_journal != null)
                {
                    if (request.JournalWrite == null)
                    {
                        request.JournalWrite = Task.Run(() => _journal.Save(request));
                        request.JournalWrite.ContinueWith(completed => FinishJournalWrite(request, completed), TaskScheduler.Default);
                    }
                    if (!request.JournalWrite.IsCompleted)
                    {
                        reason = "正在保存资源状态，完成后才能执行动作。";
                        return false;
                    }
                    if (request.JournalWrite.IsFaulted || request.JournalWrite.IsCanceled)
                    {
                        request.State = TaskResourceState.Blocked;
                        request.Detail = "资源记录写入失败，设备状态未知，请联系维护人员检查存储。";
                        throw new InvalidOperationException(request.Detail, request.JournalWrite.Exception);
                    }
                }
                request.State = TaskResourceState.Held;
                return true;
            }
        }

        /// <summary>释放等待或正常持有的申请；异常待确认记录永远不会被 finally 放行。</summary>
        internal void Release(TaskResourceRequest request)
        {
            lock (_sync)
            {
                if (request.State == TaskResourceState.Blocked) return;
                if (request.JournalWrite != null && !request.JournalWrite.IsCompleted)
                {
                    request.ReleaseRequested = true;
                    return;
                }
                if (request.JournalWrite != null && (request.JournalWrite.IsFaulted || request.JournalWrite.IsCanceled))
                {
                    request.State = TaskResourceState.Blocked;
                    request.Detail = "资源记录写入失败，设备状态未知，请联系维护人员检查存储。";
                    return;
                }
                _requests.Remove(request);
            }
        }

        /// <summary>取消等待后仍保留申请至磁盘写入完成；写失败保持隔离。</summary>
        private void FinishJournalWrite(TaskResourceRequest request, Task completed)
        {
            lock (_sync)
            {
                if (!_requests.Contains(request)) return;
                if (completed.IsFaulted || completed.IsCanceled)
                {
                    request.State = TaskResourceState.Blocked;
                    request.Detail = "资源记录写入失败，设备状态未知，请联系维护人员检查存储。";
                }
                else if (request.ReleaseRequested) _requests.Remove(request);
            }
        }

        /// <summary>异步等待取消前已开始的记录写入，并结算回调；失败仍保留隔离。</summary>
        internal async Task SettleJournalWritesAsync()
        {
            TaskResourceRequest[] pending;
            lock (_sync) pending = _requests.Where(item => item.JournalWrite != null).ToArray();
            try { await Task.WhenAll(pending.Select(item => item.JournalWrite)).ConfigureAwait(false); }
            catch { /* 写入失败由隔离状态和后续检查处理。 */ }
            foreach (var item in pending) FinishJournalWrite(item, item.JournalWrite);
        }

        /// <summary>写入未完成，或已失败但尚未完成隔离时，不能允许复位越过结算窗口。</summary>
        internal bool HasPendingJournalWrites
        {
            get
            {
                lock (_sync)
                    return _requests.Any(item => item.JournalWrite != null
                        && (!item.JournalWrite.IsCompleted
                            || ((item.JournalWrite.IsFaulted || item.JournalWrite.IsCanceled)
                                && item.State != TaskResourceState.Blocked)));
            }
        }

        /// <summary>等待申请已经开始持久写入时，不能将整个设备视为安全空闲。</summary>
        internal TaskResourceRecoveryState RecoveryState
        {
            get
            {
                lock (_sync)
                {
                    if (_requests.Any(item => item.State == TaskResourceState.Blocked
                        || item.JournalWrite != null && (item.JournalWrite.IsFaulted || item.JournalWrite.IsCanceled)))
                        return TaskResourceRecoveryState.Blocked;
                    if (_requests.Any(item => item.State == TaskResourceState.Held
                        || item.State == TaskResourceState.Waiting && item.JournalWrite != null))
                        return TaskResourceRecoveryState.Held;
                    return TaskResourceRecoveryState.Clear;
                }
            }
        }

        /// <summary>正文未完成退出确认时隔离整组；重复隔离保留首次原因。</summary>
        internal void Block(TaskResourceRequest request, string reason)
        {
            lock (_sync)
            {
                RequirePresent(request);
                if (request.State == TaskResourceState.Blocked) return;
                if (request.State != TaskResourceState.Held) throw new InvalidOperationException("只有已占用资源可以转为待确认。");
                request.State = TaskResourceState.Blocked;
                request.Detail = reason;
            }
        }

        /// <summary>读取副本，等待原因根据当前队列重新计算。</summary>
        public IReadOnlyList<TaskResourceSnapshot> Read()
        {
            lock (_sync)
                return _requests.Select(request => new TaskResourceSnapshot(request,
                    request.State == TaskResourceState.Waiting ? DescribeBlocker(FindBlocker(request))
                    : request.State == TaskResourceState.Held ? "持有全部资源。" : request.Detail)).ToList().AsReadOnly();
        }

        /// <summary>确认继承的占用仍有效且覆盖本次动作。</summary>
        internal void RequireHeld(TaskResourceRequest request, string name)
        {
            lock (_sync)
            {
                RequirePresent(request);
                if (request.State != TaskResourceState.Held || !request.Names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("当前范围未持有动作资源：" + name);
            }
        }

        /// <summary>供测试场景配置入口拒绝覆盖已持有或待确认的轴。</summary>
        internal bool IsReserved(string name)
        {
            lock (_sync) return _requests.Any(request => request.State != TaskResourceState.Waiting
                && request.Names.Contains(name, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>查找待确认记录；它只保存身份和冻结的退出条件。</summary>
        internal TaskResourceRequest FindBlocked(string id)
        {
            lock (_sync) return _requests.FirstOrDefault(request => request.Id == id && request.State == TaskResourceState.Blocked);
        }

        /// <summary>设备完成退出条件复核后移除同一条记录，旧凭证不能释放新申请。</summary>
        internal bool Recover(TaskResourceRequest request)
        {
            lock (_sync) return request.State == TaskResourceState.Blocked
                && (request.JournalWrite == null || request.JournalWrite.Status == TaskStatus.RanToCompletion)
                && _requests.Remove(request);
        }

        /// <summary>寻找实际占用者或较早的冲突等待者；调用时已持有队列锁。</summary>
        private TaskResourceRequest FindBlocker(TaskResourceRequest request)
        {
            int index = _requests.IndexOf(request);
            for (int current = 0; current < _requests.Count; current++)
            {
                TaskResourceRequest other = _requests[current];
                if (other == request || (other.State == TaskResourceState.Waiting && current > index)) continue;
                if (other.Names.Any(name => request.Names.Contains(name, StringComparer.OrdinalIgnoreCase))) return other;
            }
            return null;
        }

        /// <summary>用工位、步骤和资源名称说明等待原因；唯一编号保留在结构化快照中。</summary>
        private static string DescribeBlocker(TaskResourceRequest blocker)
        {
            if (blocker == null) return "等待本节点下一次取得执行许可。";
            string task = string.IsNullOrWhiteSpace(blocker.TaskName) ? blocker.TaskId : blocker.TaskName;
            string node = string.IsNullOrWhiteSpace(blocker.NodeName) ? blocker.NodeId : blocker.NodeName;
            string resources = string.Join(", ", blocker.Names.Select(DescribeResourceName));
            string owner = "“" + task + "”工位的“" + node + "”步骤";
            string reason = blocker.State == TaskResourceState.Blocked ? "资源占用待确认，关联" + owner + "。"
                : blocker.State == TaskResourceState.Held ? "当前由" + owner + "占用。"
                : "前序资源申请来自" + owner + "。";
            return "等待资源 [" + resources + "]；" + reason;
        }

        /// <summary>把控制器资源类别转换成操作界面使用的中文名称。</summary>
        private static string DescribeResourceName(string name)
        {
            if (name.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase)) return "轴：" + name.Substring(5);
            if (name.StartsWith("Output:", StringComparison.OrdinalIgnoreCase)) return "输出：" + name.Substring(7);
            return name;
        }

        /// <summary>拒绝跨协调器或已释放的凭证；调用时已持有队列锁。</summary>
        private void RequirePresent(TaskResourceRequest request)
        {
            if (!_requests.Contains(request)) throw new InvalidOperationException("资源申请已经失效。");
        }
    }
}
