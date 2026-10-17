using System;

namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>逻辑 DI/DO 到正运动仿真器全局 IO 位号的映射。</summary>
    public sealed class ZMotionIoPointConfiguration
    {
        /// <summary>设备硬件配置中所属的控制卡标识。</summary>
        public string ControllerId { get; set; }

        public string Name { get; set; }

        /// <summary>从 0 开始的正运动 IO 点号；映射到 IN/OP 的单个物理点位。</summary>
        public int Channel { get; set; }

        /// <summary>true 表示高电平为逻辑有效，false 表示低电平为逻辑有效。</summary>
        public bool ActiveHigh { get; set; } = true;

        /// <summary>检查逻辑名称、控制卡标识和物理 IO 点号。</summary>
        internal void Validate(string direction)
        {
            if (string.IsNullOrWhiteSpace(ControllerId) || ControllerId != ControllerId.Trim())
            {
                throw new InvalidOperationException(
                    "正运动逻辑" + direction + " ControllerId 不能为空或包含首尾空格。");
            }

            if (string.IsNullOrWhiteSpace(Name) || Name != Name.Trim())
            {
                throw new InvalidOperationException(
                    "正运动逻辑" + direction + "名称不能为空或包含首尾空格。");
            }

            if (Channel < 0)
            {
                throw new InvalidOperationException(
                    "逻辑" + direction + " '" + Name + "' 的物理位号不能小于 0。");
            }
        }
    }
}
