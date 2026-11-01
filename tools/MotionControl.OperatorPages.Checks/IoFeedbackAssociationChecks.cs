using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.ManualDebug;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>检查反馈关系独立持久化、兼容旧设备和无效配置的处理。</summary>
    internal static class IoFeedbackAssociationChecks
    {
        /// <summary>在唯一临时目录中验证多个输入往返和错误配置不覆盖原文件。</summary>
        internal static void Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "motion-feedback-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var machineStore = new MachineConfigurationStore(directory);
                var store = new IoFeedbackAssociationStore(machineStore, "Machine01");
                Ensure(store.Load().Associations.Count == 0, "缺失关联文件时应返回空列表。");
                var configuration = new IoFeedbackAssociationConfiguration
                {
                    Associations = new List<IoFeedbackAssociation>
                    {
                        new IoFeedbackAssociation { OutputName = "夹具打开", InputNames = new List<string> { "打开到位", "关闭到位" } }
                    }
                };
                store.Save(configuration);
                var loaded = store.Load();
                Ensure(loaded.Associations.Count == 1 && loaded.Associations[0].InputNames.Count == 2
                    && loaded.Associations[0].InputNames[1] == "关闭到位", "多个反馈输入保存或读取失败。");
                string path = Path.Combine(directory, "Machine01", "Parameters", "IoFeedbackAssociations.json");
                string validFile = File.ReadAllText(path);
                configuration.Associations[0].InputNames.Add("打开到位");
                bool rejected = false;
                try { store.Save(configuration); }
                catch (InvalidOperationException) { rejected = true; }
                Ensure(rejected && File.ReadAllText(path) == validFile, "重复输入应拒绝并保留原文件。");
                File.WriteAllText(path, "{}");
                rejected = false;
                try { store.Load(); }
                catch (InvalidOperationException) { rejected = true; }
                Ensure(rejected, "缺少必需字段的文件不能当作空关联。");
                CheckBoundedReadsAsync().GetAwaiter().GetResult();
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        /// <summary>慢读超时后不重复发起；其他输入可读取，全部名额占满时明确繁忙，释放后可恢复。</summary>
        private static async Task CheckBoundedReadsAsync()
        {
            var fake = new SlowLogicalIo();
            Type coordinatorType = typeof(IoMonitorView).Assembly.GetType(
                "MotionControl.Framework.Wpf.ManualDebug.FeedbackInputReadCoordinator", true);
            MethodInfo create = coordinatorType.GetMethod("For", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo read = coordinatorType.GetMethod("ReadAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            object coordinator = create.Invoke(null, new object[] { fake });
            Ensure(ReferenceEquals(coordinator, create.Invoke(null, new object[] { fake })), "同一设备应共用读取上限。");
            try
            {
                Task<string> slow = ReadStatusAsync(coordinator, read, "慢1");
                Task<string> fast = ReadStatusAsync(coordinator, read, "快");
                Ensure(await fast.ConfigureAwait(false) == "Success", "慢输入不能阻塞其他正常输入。");
                Ensure(await slow.ConfigureAwait(false) == "Timeout", "慢输入应超时并显示未知。");
                Ensure(await ReadStatusAsync(coordinator, read, "慢1").ConfigureAwait(false) == "Timeout"
                    && fake.GetCalls("慢1") == 1, "超时后重复轮询不应重复调用设备。");
                string[] more = await Task.WhenAll(ReadStatusAsync(coordinator, read, "慢2"),
                    ReadStatusAsync(coordinator, read, "慢3"), ReadStatusAsync(coordinator, read, "慢4")).ConfigureAwait(false);
                Ensure(Array.TrueForAll(more, status => status == "Timeout"), "额外慢输入应分别超时。");
                Ensure(await ReadStatusAsync(coordinator, read, "快").ConfigureAwait(false) == "Busy",
                    "设备调用占满名额后不能无限启动新调用。");
                fake.Complete("慢1");
                string recovered = "Busy";
                for (int i = 0; i < 50 && recovered == "Busy"; i++)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                    recovered = await ReadStatusAsync(coordinator, read, "快").ConfigureAwait(false);
                }
                Ensure(recovered == "Success", "底层读取实际结束后应释放名额。");
                Ensure(await ReadStatusAsync(coordinator, read, "慢1").ConfigureAwait(false) == "Success"
                    && fake.GetCalls("慢1") == 2, "恢复后应重新采样，而不是采用超时任务的迟到值。");
            }
            finally { fake.CompleteAll(); }
        }

        /// <summary>通过反射调用内部采样器，返回状态名称供检查使用。</summary>
        private static async Task<string> ReadStatusAsync(object coordinator, MethodInfo read, string name)
        {
            var task = (Task)read.Invoke(coordinator, new object[] { name });
            await task.ConfigureAwait(false);
            object result = task.GetType().GetProperty("Result").GetValue(task, null);
            return result.GetType().GetProperty("Status", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(result, null).ToString();
        }

        /// <summary>可按输入保持读取未完成，用于模拟厂商接口无法取消的情况。</summary>
        private sealed class SlowLogicalIo : ILogicalIo
        {
            /// <summary>保护调用计数和未完成任务。</summary>
            private readonly object _sync = new object();
            /// <summary>各输入的读取调用次数。</summary>
            private readonly Dictionary<string, int> _calls = new Dictionary<string, int>();
            /// <summary>尚未完成的模拟输入读取。</summary>
            private readonly Dictionary<string, TaskCompletionSource<bool>> _held = new Dictionary<string, TaskCompletionSource<bool>>();
            /// <summary>已解除阻塞的输入，后续读取立即返回。</summary>
            private readonly HashSet<string> _released = new HashSet<string>();

            /// <summary>快输入立即完成，慢输入保持到测试显式释放。</summary>
            public Task<bool> ReadInputAsync(string name, CancellationToken cancellationToken)
            {
                lock (_sync)
                {
                    _calls.TryGetValue(name, out int count);
                    _calls[name] = count + 1;
                    if (!name.StartsWith("慢", StringComparison.Ordinal) || _released.Contains(name)) return Task.FromResult(true);
                    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _held[name] = completion;
                    return completion.Task;
                }
            }

            /// <summary>返回指定输入的真实调用次数。</summary>
            internal int GetCalls(string name) { lock (_sync) return _calls.TryGetValue(name, out int count) ? count : 0; }
            /// <summary>结束指定阻塞调用，并允许其后续读取正常返回。</summary>
            internal void Complete(string name)
            {
                lock (_sync) { _released.Add(name); if (_held.TryGetValue(name, out var completion)) completion.TrySetResult(true); }
            }
            /// <summary>清理所有阻塞调用，避免检查留下未完成任务。</summary>
            internal void CompleteAll() { lock (_sync) foreach (var completion in _held.Values) completion.TrySetResult(true); }
            /// <summary>本检查不读取输出。</summary>
            public Task<bool> ReadOutputAsync(string name, CancellationToken cancellationToken) { throw new NotSupportedException(); }
            /// <summary>本检查不写入输出。</summary>
            public Task WriteOutputAsync(string name, bool value, CancellationToken cancellationToken) { throw new NotSupportedException(); }
        }

        /// <summary>检查失败时抛出明确原因。</summary>
        private static void Ensure(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
