using System;
using System.Collections.Generic;
using System.Linq;

namespace MotionControl.Equipment.Demo
{
    /// <summary>一台测试设备的并行工位配置；独占运动轴和输出，共享输入可被多个工位读取。</summary>
    public sealed class DemoStationSetSettings
    {
        /// <summary>创建默认关闭的工位组，避免新目录自动获得运动入口。</summary>
        public DemoStationSetSettings()
        {
            SchemaVersion = 1;
            Stations = new List<DemoStationSettings>();
        }

        /// <summary>配置格式版本，当前只接受 1。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>只有设为 true 时，Host 才按 Stations 创建并行工位。</summary>
        public bool Enabled { get; set; }

        /// <summary>本设备的工位配置；列表顺序用于总览和流程状态排序。</summary>
        public List<DemoStationSettings> Stations { get; set; }

        /// <summary>验证启用工位的名称和设备资源，拒绝并行流程暗中共用轴或 IO。</summary>
        public void Validate()
        {
            if (SchemaVersion != 1)
                throw new InvalidOperationException("并行工位配置只支持 schemaVersion 为 1。");
            if (Stations == null || Stations.Count == 0 || Stations.Count > 32)
                throw new InvalidOperationException("并行工位配置必须包含 1 到 32 个工位。");

            List<DemoStationSettings> enabledStations = Stations.Where(station => station != null && station.Enabled).ToList();
            if (enabledStations.Count == 0)
                throw new InvalidOperationException("工位组已启用，但没有启用任何工位。");

            var stationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var axisNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DemoStationSettings station in enabledStations)
            {
                station.Validate();
                if (!stationNames.Add(station.StationName))
                    throw new InvalidOperationException("并行工位名称重复：" + station.StationName + "。");
                if (!axisNames.Add(station.AxisName))
                    throw new InvalidOperationException("多个并行工位配置了同一逻辑轴：" + station.AxisName + "。");
                if (!outputNames.Add(station.OutputName))
                    throw new InvalidOperationException("多个并行工位配置了同一逻辑 DO：" + station.OutputName + "。");
            }
        }
    }
}
