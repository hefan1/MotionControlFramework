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

        /// <summary>连接配置文件格式版本；当前适配器支持版本 1。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>正运动 ZDevelop 仿真器地址；当前仅支持本机地址 127.0.0.1。</summary>
        public string Address { get; set; }

        /// <summary>轴运动等待控制器完成的超时时间，单位为毫秒。</summary>
        public int MotionTimeoutMilliseconds { get; set; }

        /// <summary>检查连接参数是否符合本机 ZDevelop 仿真器的支持范围。</summary>
        public void Validate()
        {
            if (SchemaVersion != 1)
            {
                throw new InvalidOperationException("正运动控制器配置只支持 schemaVersion 1。");
            }

            if (!string.Equals(Address, "127.0.0.1", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "当前适配器只连接本机 VPLC5xx-Simu，请将 Address 配置为 127.0.0.1；不支持实体控制卡。");
            }

            if (MotionTimeoutMilliseconds <= 0)
            {
                throw new InvalidOperationException("MotionTimeoutMilliseconds 必须大于 0。");
            }
        }
    }
}
