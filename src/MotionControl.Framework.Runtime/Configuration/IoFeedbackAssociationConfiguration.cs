using System.Collections.Generic;
using Newtonsoft.Json;

namespace MotionControl.Framework.Runtime.Configuration
{
    /// <summary>设备独立保存的输出与反馈输入关系；只用于显示，不改变 IO 电平或运动控制。</summary>
    public sealed class IoFeedbackAssociationConfiguration
    {
        /// <summary>配置格式版本；当前只支持版本 1。</summary>
        [JsonProperty(Required = Required.Always)]
        public int SchemaVersion { get; set; } = 1;

        /// <summary>各输出与其反馈输入的逻辑名称。</summary>
        [JsonProperty(Required = Required.Always)]
        public List<IoFeedbackAssociation> Associations { get; set; } = new List<IoFeedbackAssociation>();
    }

    /// <summary>一个逻辑输出及其零个或多个反馈输入。</summary>
    public sealed class IoFeedbackAssociation
    {
        /// <summary>已映射数字输出的逻辑名称。</summary>
        [JsonProperty(Required = Required.Always)]
        public string OutputName { get; set; }

        /// <summary>已映射数字输入的逻辑名称；按配置顺序展示。</summary>
        [JsonProperty(Required = Required.Always)]
        public List<string> InputNames { get; set; } = new List<string>();
    }
}
