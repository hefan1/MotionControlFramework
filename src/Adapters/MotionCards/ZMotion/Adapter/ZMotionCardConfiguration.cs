using System;

namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>
    /// 正运动仿真器的连接配置。轴参数和 IO 映射由设备项目的硬件配置文件单独提供。
    /// </summary>
    public sealed class ZMotionCardConfiguration
    {
        /// <summary>设置本机仿真器的默认地址和运动超时。</summary>
        public ZMotionCardConfiguration()
        {
            SchemaVersion = 1;
            Address = "127.0.0.1";
            MotionTimeoutMilliseconds = 120000;
        }

        public int SchemaVersion { get; set; }

        public string Address { get; set; }

        public int MotionTimeoutMilliseconds { get; set; }

        /// <summary>检查连接参数是否符合本机 ZDevelop 仿真器的支持范围。</summary>
        public void Validate()
        {
            if (SchemaVersion != 1)
            {
                throw new InvalidOperationException("正运动仿真配置只支持 schemaVersion 1。");
            }

            if (!string.Equals(Address, "127.0.0.1", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "当前适配器只连接 ZDevelop 本机仿真器，请将 Address 配置为 127.0.0.1。");
            }

            if (MotionTimeoutMilliseconds <= 0)
            {
                throw new InvalidOperationException("MotionTimeoutMilliseconds 必须大于 0。");
            }
        }
    }
}
