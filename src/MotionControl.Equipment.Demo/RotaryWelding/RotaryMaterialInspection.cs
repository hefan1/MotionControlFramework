using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.IO;

namespace MotionControl.Equipment.Demo.RotaryWelding
{
    /// <summary>双夹位示例的独立物料反馈判定；结果只用于核查，不能替代动作互锁。</summary>
    public static class RotaryMaterialInspection
    {
        /// <summary>顺序读取四处独立反馈；任一读取失败时直接报告，不把失败当作无料。</summary>
        public static async Task<IReadOnlyList<RotaryMaterialState>> ReadAsync(ILogicalIo io, CancellationToken cancellationToken)
        {
            if (io == null) throw new ArgumentNullException(nameof(io));
            var result = new List<RotaryMaterialState>();
            foreach (string position in new[] { "A夹位", "B夹位", "缓存位", "下料位" })
            {
                bool present = await io.ReadInputAsync(position + "有料", cancellationToken).ConfigureAwait(false);
                bool empty = await io.ReadInputAsync(position + "无料", cancellationToken).ConfigureAwait(false);
                bool presentAgain = await io.ReadInputAsync(position + "有料", cancellationToken).ConfigureAwait(false);
                bool emptyAgain = await io.ReadInputAsync(position + "无料", cancellationToken).ConfigureAwait(false);
                RotaryMaterialCondition condition = present != presentAgain || empty != emptyAgain
                    ? RotaryMaterialCondition.Unstable
                    : present && empty ? RotaryMaterialCondition.Conflict
                    : present ? RotaryMaterialCondition.Present
                    : empty ? RotaryMaterialCondition.Empty
                    : RotaryMaterialCondition.Unknown;
                result.Add(new RotaryMaterialState(position, condition));
            }
            return result.AsReadOnly();
        }
    }

    /// <summary>两路有料反馈的只读判定；未知、冲突和变化均不能当作空料。</summary>
    public enum RotaryMaterialCondition
    {
        /// <summary>两次读取均只有有料输入有效。</summary>
        Present,
        /// <summary>两次读取均只有无料输入有效。</summary>
        Empty,
        /// <summary>两次读取均无有效反馈。</summary>
        Unknown,
        /// <summary>两次读取均同时出现有料和无料反馈。</summary>
        Conflict,
        /// <summary>两次读取的任一反馈发生变化。</summary>
        Unstable
    }

    /// <summary>某一设备位置的一次只读状态；不代表位置仍持续安全。</summary>
    public sealed class RotaryMaterialState
    {
        /// <summary>保存位置和两次采样的判定。</summary>
        internal RotaryMaterialState(string position, RotaryMaterialCondition condition)
        { Position = position; Condition = condition; }

        /// <summary>样例设备中的物料位置名称。</summary>
        public string Position { get; private set; }
        /// <summary>两路反馈的判定结果。</summary>
        public RotaryMaterialCondition Condition { get; private set; }
    }
}
