using System;
using System.Collections.Generic;

namespace MotionControl.Equipment.Demo.RotaryWelding
{
    /// <summary>双夹位测试设备的现场证据，只供人工核对，不能用于自动续跑。</summary>
    public sealed class RotaryEvidence
    {
        /// <summary>证据格式版本。</summary>
        public int SchemaVersion { get; set; }
        /// <summary>生成证据的进程内设备会话标识。</summary>
        public string SessionId { get; set; }
        /// <summary>证据最新写入时间，UTC。</summary>
        public DateTime UpdatedUtc { get; set; }
        /// <summary>本会话曾经进入生产，重启后需要人工核对。</summary>
        public bool ProductionStarted { get; set; }
        /// <summary>当前位于焊接侧的夹位索引；旋转中时该值不代表旋转完成。</summary>
        public int WeldNest { get; set; }
        /// <summary>A、B 两个夹位及缓存的最近确认产品；空值不证明现场无料。</summary>
        public RotaryProductEvidence NestA { get; set; }
        /// <summary>B 夹位最近确认产品。</summary>
        public RotaryProductEvidence NestB { get; set; }
        /// <summary>缓存最近确认产品。</summary>
        public RotaryProductEvidence Buffer { get; set; }
        /// <summary>正在上料的已扫码产品编号；扫码前为空。</summary>
        public string LoadingProduct { get; set; }
        /// <summary>相应操作正在进行，重启时产品或机构实际位置可能不确定。</summary>
        public bool Loading { get; set; }
        /// <summary>焊接动作进行中或状态未确认。</summary>
        public bool Welding { get; set; }
        /// <summary>压合动作进行中或状态未确认。</summary>
        public bool Pressing { get; set; }
        /// <summary>夹位到缓存交接进行中或状态未确认。</summary>
        public bool Unloading { get; set; }
        /// <summary>转台切换进行中或状态未确认。</summary>
        public bool Rotating { get; set; }
        /// <summary>检测或出料进行中，最终产品位置未确认。</summary>
        public bool Inspecting { get; set; }
        /// <summary>仍需现场核对的动作及不确定原因；空数组不代表自动续跑许可。</summary>
        public string[] UncertainOperations { get; set; }
        /// <summary>各工位最近一次确认进入的动作说明，供重启后人工核对。</summary>
        public Dictionary<string, string> StationActions { get; set; }
    }

    /// <summary>某个确认位置的产品编号、工艺阶段及结果。</summary>
    public sealed class RotaryProductEvidence
    {
        /// <summary>扫码模型实际返回的本会话编号。</summary>
        public string Id { get; set; }
        /// <summary>最近确认的加工阶段。</summary>
        public string Stage { get; set; }
        /// <summary>检测模型明确返回的结果；空值表示未知。</summary>
        public string Result { get; set; }
        /// <summary>产品装入的物理夹位。</summary>
        public string Nest { get; set; }
    }

    /// <summary>经独立反馈确认完成出料的一件产品记录；编号仅保证控制器当前会话内唯一。</summary>
    public sealed class RotaryCompletedProduct
    {
        /// <summary>设备流程会话标识。</summary>
        public string SessionId { get; set; }
        /// <summary>扫码设备返回的产品编号。</summary>
        public string Id { get; set; }
        /// <summary>装入的物理夹位。</summary>
        public string Nest { get; set; }
        /// <summary>完成出料的工位。</summary>
        public string Station { get; set; }
        /// <summary>记录确认时的工艺阶段。</summary>
        public string Stage { get; set; }
        /// <summary>检测结果。</summary>
        public string Result { get; set; }
        /// <summary>独立反馈确认的出料口。</summary>
        public string Outlet { get; set; }
        /// <summary>确认完成的 UTC 时间。</summary>
        public DateTime CompletedUtc { get; set; }
    }
}
