using System;
using System.Linq;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.TaskEngine.Configuration;

namespace MotionControl.Host.Wpf
{
    /// <summary>用当前 Host 实际支持的连接选项检查工程卡型，不连接控制器。</summary>
    internal static class HostEquipmentProjectCardValidation
    {
        /// <summary>按目标后端核查控制卡数量、厂商、卡型、适配器及可用轴数。</summary>
        internal static string Validate(MachineProjectCardSelection selection, MachineConfigurationStore store, string machineId)
        {
            if (selection == null || store == null) return "卡型或设备配置不可用。";
            if (selection.CardCount != 1) return "当前 Host 只支持一张运动控制卡。";
            IMotionCardHostConnection connection;
            bool virtualBackend = string.Equals(selection.Backend, "Virtual", StringComparison.OrdinalIgnoreCase);
            if (virtualBackend)
                connection = new VirtualAxisMappingCardConnection(store, machineId, 18520);
            else if (string.Equals(selection.Backend, "ZMotion", StringComparison.OrdinalIgnoreCase))
                connection = new ZMotionAxisMappingCardConnection(store, machineId);
            else return "未知控制器后端：" + selection.Backend;

            MotionCardSelectionOption card = connection.SupportedMotionCards.FirstOrDefault(item => item != null
                && string.Equals(item.VendorCode, selection.Vendor, StringComparison.Ordinal)
                && string.Equals(item.CardTypeCode, selection.CardType, StringComparison.Ordinal)
                && string.Equals(item.AdapterType, selection.AdapterType, StringComparison.Ordinal));
            if (card == null) return "厂商、卡型或适配器不在该后端的已安装列表中。";
            if (virtualBackend)
            {
                // 软件控制器连接器允许 1 至 64 轴；其卡型映射仍必须是已登记的硬件选项。
                if (selection.AxisCount < 1 || selection.AxisCount > 64) return "软件控制器支持 1 至 64 个轴通道。";
            }
            else if (card.SupportedAxisCounts == null || !card.SupportedAxisCounts.Contains(selection.AxisCount))
                return "轴通道数量不在该卡型支持列表中。";
            return null;
        }
    }
}
