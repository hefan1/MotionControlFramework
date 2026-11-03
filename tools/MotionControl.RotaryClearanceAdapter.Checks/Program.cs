using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.Virtual;
using MotionControl.Equipment.Demo.RotaryWelding;
using MotionControl.Framework.Contracts.Clearance;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.RotaryClearanceAdapter.Checks
{
    /// <summary>经真实虚拟卡适配器和独立软件控制器验证 A 夹位清料，不代表正式 Host 或实机验收。</summary>
    internal static class Program
    {
        /// <summary>连接由测试脚本准备的独立控制器并通过运动卡契约执行固定设备计划。</summary>
        private static int Main(string[] args)
        {
            try { RunAsync(int.Parse(args[0])).GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        /// <summary>按逻辑名称映射十三轴及独立 IO，核查执行后的反馈和全部轴。</summary>
        private static async Task RunAsync(int port)
        {
            string[] axisNames = { "Unload_X", "Load_X", "Press_Z", "Transfer_Y", "Camera_X", "Camera_Z", "Feeder_X", "Feeder_Z", "Buffer_X", "Buffer_Y", "Reject_Z", "Conveyor_R", "Weld_Z" };
            var axes = axisNames.Select((name, index) => new AxisHardwareDefinition {
                Name = name, ControllerId = "Test", AxisNumber = index, PositionTolerance = 0.01,
                SoftLimitEnabled = true, MinimumPosition = index == 11 ? -100 : -10, MaximumPosition = index == 11 ? 100 : 10 }).ToArray();
            var inputChannels = new Dictionary<string, int> {
                ["上料到位"] = 4, ["取料夹紧到位"] = 40, ["取料松开到位"] = 60, ["A夹紧到位"] = 41, ["A松开到位"] = 61, ["B夹紧到位"] = 42, ["B松开到位"] = 62,
                ["下料夹紧到位"] = 51, ["下料松开到位"] = 75, ["剔除完成"] = 48, ["缓存载具到位"] = 49,
                ["不良出口允许"] = 67, ["A夹位有料"] = 69, ["A夹位无料"] = 70, ["B夹位有料"] = 71, ["B夹位无料"] = 72,
                ["缓存位有料"] = 73, ["缓存位无料"] = 74, ["下料位有料"] = 76, ["下料位无料"] = 77,
                ["取料夹爪有料"] = 78, ["取料夹爪无料"] = 79, ["上料滑台有料"] = 80, ["上料滑台无料"] = 81,
                ["旋转区清料允许"] = 82, ["缓存区清料允许"] = 83, ["焊接模组空闲"] = 84, ["压合模组空闲"] = 85,
                ["上料模组空闲"] = 86, ["视觉模组空闲"] = 87, ["出料模组空闲"] = 88 };
            var outputChannels = new Dictionary<string, int> { ["A夹紧"] = 41, ["B夹紧"] = 42, ["取料夹紧"] = 40,
                ["下料夹紧"] = 51, ["缓存交接"] = 49, ["剔除启动"] = 48, ["焊接启动"] = 16, ["压装启动"] = 45,
                ["相机触发"] = 6, ["相机结果复位"] = 15, ["合格出料"] = 50 };
            var inputs = inputChannels.Select(item => new DigitalIoHardwareDefinition { Name = item.Key, Channel = item.Value, ActiveHigh = true }).ToArray();
            var outputs = outputChannels.Select(item => new DigitalIoHardwareDefinition { Name = item.Key, Channel = item.Value, ActiveHigh = true }).ToArray();
            string diagnostics = Path.Combine(Path.GetTempPath(), "RotaryClearanceAdapter_" + Guid.NewGuid().ToString("N"));
            var card = new VirtualMotionCard("Test", axes, inputs, outputs, port, diagnostics);
            var token = CancellationToken.None;
            await card.ConnectAsync(token).ConfigureAwait(false);
            try
            {
                await RotaryMaterialInspection.VerifySingleAClearanceAsync(card, token).ConfigureAwait(false);
                ControlledClearanceProfile profile = RotarySingleAClearance.CreateProfile();
                var executor = new ControlledClearanceExecutor(card.Axes.ToDictionary(axis => axis.Name, StringComparer.OrdinalIgnoreCase), card);
                await executor.ExecuteAsync(profile.Plan, token).ConfigureAwait(false);
                if (executor.HasUnsettledAction) throw new InvalidOperationException("清料动作仍待确认。");
                foreach (var condition in profile.Recovery.Inputs)
                    if (await card.ReadInputAsync(condition.Name, token).ConfigureAwait(false) != condition.Expected)
                        throw new InvalidOperationException("清料后输入不符：" + condition.Name);
                foreach (var condition in profile.Recovery.Outputs)
                    if (await card.ReadOutputAsync(condition.Name, token).ConfigureAwait(false) != condition.Value)
                        throw new InvalidOperationException("清料后输出不符：" + condition.Name);
                foreach (var condition in profile.Recovery.Axes)
                {
                    AxisStatus state = await card.Axes.Single(axis => axis.Name == condition.Name).ReadStatusAsync(token).ConfigureAwait(false);
                    if (state == null || state.Moving || double.IsNaN(state.Velocity) || double.IsInfinity(state.Velocity)
                        || Math.Abs(state.Velocity) > 0.001 || state.Position < condition.Minimum || state.Position > condition.Maximum)
                        throw new InvalidOperationException("清料后轴未在预期位置停稳：" + condition.Name);
                }
                Console.WriteLine("十三轴 A 夹位单件清料：通过 VirtualMotionCard API 完成交接、缓存和剔除，恢复条件反馈通过。");
            }
            finally { await card.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
        }
    }
}
