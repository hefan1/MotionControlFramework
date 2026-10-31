using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>检查在线修改 DO 映射时不会丢失仍有效的旧输出。</summary>
    internal static class OutputMappingSafetyChecks
    {
        /// <summary>覆盖原映射不变、改通道、改极性、删除及读取失败。</summary>
        internal static async Task RunAsync()
        {
            var io = new ReadOnlyOutput();
            var oldPoint = Point("夹具电磁阀", 3, true);
            var old = new[] { oldPoint };
            io.Active = true;
            await IoOutputMappingSafety.EnsureChangedOutputsInactiveAsync(io, old,
                new[] { Point("夹具电磁阀", 3, true) }, CancellationToken.None).ConfigureAwait(false);
            Ensure(io.ReadCount == 0, "未改变的输出不需要阻止其他 IO 映射维护。");

            await MustRejectAsync(io, old, new[] { Point("夹具电磁阀", 7, true) }).ConfigureAwait(false);
            await MustRejectAsync(io, old, new[] { Point("夹具电磁阀", 3, false) }).ConfigureAwait(false);
            await MustRejectAsync(io, old, new[] { Point("新的输出名", 3, true) }).ConfigureAwait(false);
            await MustRejectAsync(io, old, new DigitalIoHardwareDefinition[0]).ConfigureAwait(false);
            io.Active = false;
            await IoOutputMappingSafety.EnsureChangedOutputsInactiveAsync(io, old,
                new[] { Point("夹具电磁阀", 7, true) }, CancellationToken.None).ConfigureAwait(false);
            io.ReadError = new InvalidOperationException("测试读回失败");
            try
            {
                await IoOutputMappingSafety.EnsureChangedOutputsInactiveAsync(io, old,
                    new DigitalIoHardwareDefinition[0], CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException("旧输出不可读时错误允许删除映射。");
            }
            catch (InvalidOperationException error)
            {
                Ensure(error.Message.Contains("无法确认原输出") && error.InnerException == io.ReadError,
                    "读取失败没有保留原输出名称和诊断原因。");
            }
            Ensure(io.WriteCount == 0, "映射检查不应写入任何输出。");
        }

        /// <summary>创建当前控制卡的一个逻辑输出定义。</summary>
        private static DigitalIoHardwareDefinition Point(string name, int channel, bool activeHigh)
        {
            return new DigitalIoHardwareDefinition { Name = name, ControllerId = "CardA", Channel = channel, ActiveHigh = activeHigh };
        }

        /// <summary>确认有效输出不会被允许换映射或移除。</summary>
        private static async Task MustRejectAsync(ReadOnlyOutput io, IEnumerable<DigitalIoHardwareDefinition> old,
            IEnumerable<DigitalIoHardwareDefinition> next)
        {
            try
            {
                await IoOutputMappingSafety.EnsureChangedOutputsInactiveAsync(io, old, next, CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException("有效输出仍允许修改映射。");
            }
            catch (InvalidOperationException error)
            {
                Ensure(error.Message.Contains("夹具电磁阀") && error.Message.Contains("当前有效"), "拒绝原因没有指出有效输出。");
            }
        }

        /// <summary>断言失败时保留具体原因。</summary>
        private static void Ensure(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        /// <summary>只读输出替身；验证过程不会连接或修改控制器。</summary>
        private sealed class ReadOnlyOutput : ILogicalIo
        {
            /// <summary>当前旧映射的逻辑有效状态。</summary>
            public bool Active { get; set; }
            /// <summary>指定时模拟输出读回失败。</summary>
            public Exception ReadError { get; set; }
            /// <summary>累计读取次数。</summary>
            public int ReadCount { get; private set; }
            /// <summary>累计写入次数，检查期间必须为零。</summary>
            public int WriteCount { get; private set; }
            /// <summary>此检查不读取输入。</summary>
            public Task<bool> ReadInputAsync(string inputName, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>返回旧映射的逻辑状态或实际读取错误。</summary>
            public Task<bool> ReadOutputAsync(string outputName, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); ReadCount++;
                if (ReadError != null) return Task.FromException<bool>(ReadError);
                return Task.FromResult(Active);
            }
            /// <summary>若检查意外调用写入则记录并失败。</summary>
            public Task WriteOutputAsync(string outputName, bool value, CancellationToken token)
            {
                WriteCount++; throw new InvalidOperationException("禁止写入测试输出。");
            }
        }
    }
}
