using System;
using System.Collections.Generic;
using System.Linq;

namespace MotionControl.Framework.Runtime.FlowScheduling
{
    /// <summary>指出自动工位要求回零的轴当前尚未建立坐标原点。</summary>
    internal sealed class StationAxesNotHomedException : InvalidOperationException
    {
        /// <summary>创建列出全部未回零轴及其受影响工位的启动拦截原因。</summary>
        internal StationAxesNotHomedException(
            IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> requirements,
            bool hasEquipmentInitializationFlow)
            : base(BuildMessage(requirements, hasEquipmentInitializationFlow))
        {
        }

        /// <summary>将未回零轴按现场可执行的格式列出，并说明需要完成的处理。</summary>
        private static string BuildMessage(
            IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> requirements,
            bool hasEquipmentInitializationFlow)
        {
            var lines = new List<string>();
            if (requirements != null)
            {
                foreach (KeyValuePair<string, IReadOnlyList<string>> requirement in requirements)
                {
                    string stationNames = requirement.Value == null
                        ? string.Empty
                        : string.Join("、", requirement.Value);
                    lines.Add("逻辑轴 '" + requirement.Key + "'（工位：" + stationNames + "）");
                }
            }

            string recoveryGuidance = hasEquipmentInitializationFlow
                ? "请检查设备初始化流程中的回零结果，或在轴手动页面完成上述逻辑轴回零后再启动。"
                : "请在轴手动页面逐个完成上述逻辑轴回零后再启动。";
            return "以下自动工位所需轴尚未回零：" + Environment.NewLine
                + string.Join(Environment.NewLine, lines.Select(line => "• " + line))
                + Environment.NewLine
                + recoveryGuidance;
        }
    }
}
