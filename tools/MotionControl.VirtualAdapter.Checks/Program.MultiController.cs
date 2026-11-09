using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MotionControl.Adapter.MotionCard.Virtual;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.VirtualAdapter.Checks
{
    /// <summary>通过两份已有软件控制器进程，验证通用多控制卡接口和连接隔离。</summary>
    internal static partial class Program
    {
        /// <summary>
        /// 两张卡各自连接不同的独立软件控制器，验证逻辑轴路由、IO 隔离和停稳后断卡。
        /// 这里只验证多卡通用契约；两张卡仍属于 Virtual 后端，不冒充跨厂商 SDK 验证。
        /// </summary>
        private static void RunMultiControllerApi(int firstPort, int secondPort)
        {
            if (firstPort == secondPort)
                throw new ArgumentException("多控制器验证必须指定两个不同的软件控制器端口。");

            var firstAxis = new AxisHardwareDefinition
            {
                Name = "Transfer_X", ControllerId = "ControllerA",
                AxisNumber = 3, PositionTolerance = 0.01, SoftLimitEnabled = false
            };
            var secondAxis = new AxisHardwareDefinition
            {
                Name = "Clamp_Z", ControllerId = "ControllerB",
                AxisNumber = 4, PositionTolerance = 0.01, SoftLimitEnabled = false
            };
            // 两张独立控制器可以具有相同物理通道号，IO 必须根据所属控制器隔离。
            var firstOutput = new DigitalIoHardwareDefinition
            {
                Name = "TransferOutput", ControllerId = "ControllerA",
                Channel = 8, ActiveHigh = true
            };
            var secondOutput = new DigitalIoHardwareDefinition
            {
                Name = "ClampOutput", ControllerId = "ControllerB",
                Channel = 8, ActiveHigh = true
            };

            var cardA = new VirtualMotionCard("ControllerA", new[] { firstAxis },
                new DigitalIoHardwareDefinition[0], new[] { firstOutput },
                firstPort, Path.Combine(DiagnosticsRoot, "controllerA"));
            var cardB = new VirtualMotionCard("ControllerB", new[] { secondAxis },
                new DigitalIoHardwareDefinition[0], new[] { secondOutput },
                secondPort, Path.Combine(DiagnosticsRoot, "controllerB"));
            var group = new MotionCardGroup(new IMotionCard[] { cardA, cardB });
            var token = CancellationToken.None;
            var profile = new AxisMotionProfile(10, 20, 20);
            IAxis transfer = group.GetAxis("Transfer_X");
            IAxis clamp = group.GetAxis("Clamp_Z");

            // 在真正连接前就拒绝重名卡、重名轴和旧设备逻辑轴。
            AssertInvalidCardMappings(cardA, secondPort);
            bool oldAxisRejected = false;
            try { group.GetAxis("PreviousMachineAxis"); }
            catch (KeyNotFoundException) { oldAxisRejected = true; }
            if (!oldAxisRejected)
                throw new Exception("当前多卡配置错误接受了未配置的旧设备逻辑轴。");

            try
            {
                group.ConnectAsync(token).GetAwaiter().GetResult();
                if (!group.IsConnected || group.Cards.Count != 2 || group.Axes.Count != 2
                    || !ReferenceEquals(transfer, cardA.Axes.Single())
                    || !ReferenceEquals(clamp, cardB.Axes.Single())
                    || !ReferenceEquals(group.GetAxis("transfer_x"), transfer))
                    throw new Exception("多卡连接或不区分大小写的逻辑轴路由错误。");

                transfer.EnableServoAsync(token).GetAwaiter().GetResult();
                clamp.EnableServoAsync(token).GetAwaiter().GetResult();
                transfer.HomeAsync(token).GetAwaiter().GetResult();
                clamp.HomeAsync(token).GetAwaiter().GetResult();
                transfer.MoveAbsoluteAsync(1.5, profile, token).GetAwaiter().GetResult();
                clamp.MoveAbsoluteAsync(-2, profile, token).GetAwaiter().GetResult();

                AxisStatus transferStatus = transfer.ReadStatusAsync(token).GetAwaiter().GetResult();
                AxisStatus clampStatus = clamp.ReadStatusAsync(token).GetAwaiter().GetResult();
                if (!transferStatus.HasStoppedMotionFeedback() || !clampStatus.HasStoppedMotionFeedback()
                    || Math.Abs(transferStatus.Position - 1.5) > firstAxis.PositionTolerance
                    || Math.Abs(clampStatus.Position + 2) > secondAxis.PositionTolerance)
                    throw new Exception("两张控制卡的独立轴定位反馈被混用或没有确认停稳。");

                cardA.WriteOutputAsync("TransferOutput", true, token).GetAwaiter().GetResult();
                if (!cardA.ReadOutputAsync("TransferOutput", token).GetAwaiter().GetResult()
                    || cardB.ReadOutputAsync("ClampOutput", token).GetAwaiter().GetResult())
                    throw new Exception("两台软件控制器的相同物理 DO 通道被错误串联。");
                cardB.WriteOutputAsync("ClampOutput", true, token).GetAwaiter().GetResult();
                if (!cardA.ReadOutputAsync("TransferOutput", token).GetAwaiter().GetResult()
                    || !cardB.ReadOutputAsync("ClampOutput", token).GetAwaiter().GetResult())
                    throw new Exception("第二张卡的输出错误改变了第一张卡的状态。");
                cardA.WriteOutputAsync("TransferOutput", false, token).GetAwaiter().GetResult();
                cardB.WriteOutputAsync("ClampOutput", false, token).GetAwaiter().GetResult();

                // 只要任一张卡仍在运动，整组都不能先断开另一张卡。
                clamp.StartJogAsync(MotionDirection.Positive, profile, token).GetAwaiter().GetResult();
                bool movingDisconnectRejected = false;
                try { group.DisconnectAsync(token).GetAwaiter().GetResult(); }
                catch (InvalidOperationException error)
                {
                    movingDisconnectRejected = error.Message.Contains("仍在运动");
                }
                if (!movingDisconnectRejected || !group.IsConnected)
                    throw new Exception("某张卡运动期间，整组断开没有被阻止或已经发生部分断卡。");
                clamp.StopAsync(AxisStopMode.Controlled, token).GetAwaiter().GetResult();

                // 独立断开一张卡，整组状态必须立即变为不可用；重连后重新核对轴反馈。
                cardB.DisconnectAsync(token).GetAwaiter().GetResult();
                if (group.IsConnected || !cardA.IsConnected)
                    throw new Exception("一张卡离线后整组仍误报已连接，或影响了另一张卡。");
                group.ConnectAsync(token).GetAwaiter().GetResult();
                if (!group.IsConnected
                    || !clamp.ReadStatusAsync(token).GetAwaiter().GetResult().HasStoppedMotionFeedback())
                    throw new Exception("独立控制卡重连后无法重新确认连接及停止反馈。");

                transfer.DisableServoAsync(token).GetAwaiter().GetResult();
                clamp.DisableServoAsync(token).GetAwaiter().GetResult();
                group.DisconnectAsync(token).GetAwaiter().GetResult();
                if (group.IsConnected || cardA.IsConnected || cardB.IsConnected)
                    throw new Exception("多控制卡断开后仍报告连接成功。");
                Console.WriteLine("[通过] 多控制器统一契约：独立轴路由、相同通道 IO 隔离、运动时禁止断卡、离线与重连");
            }
            finally
            {
                // 测试中途失败，也应先分别向真实软件控制器发停止指令，再尝试断卡。
                // 只要任一轴停稳无法确认，就不把清理记录写作通过。
                var cleanupErrors = new List<Exception>();
                if (cardA.IsConnected)
                {
                    try { transfer.StopAsync(AxisStopMode.Controlled, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (Exception error) { cleanupErrors.Add(error); }
                }
                if (cardB.IsConnected)
                {
                    try { clamp.StopAsync(AxisStopMode.Controlled, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (Exception error) { cleanupErrors.Add(error); }
                }
                if (cleanupErrors.Count == 0 && (cardA.IsConnected || cardB.IsConnected))
                {
                    try { group.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (Exception error) { cleanupErrors.Add(error); }
                }
                if (cleanupErrors.Count != 0)
                    throw new AggregateException("多控制器联调退出时，停轴或断卡未能确认。", cleanupErrors);
            }
        }

        /// <summary>不连接控制器就核对配置冲突，避免新板卡接管时混入重名映射。</summary>
        private static void AssertInvalidCardMappings(VirtualMotionCard existingCard, int secondPort)
        {
            var conflictingAxis = new AxisHardwareDefinition
            {
                Name = "Transfer_X", ControllerId = "ControllerC",
                AxisNumber = 5, SoftLimitEnabled = false
            };
            var conflictingCard = new VirtualMotionCard("ControllerC", new[] { conflictingAxis },
                new DigitalIoHardwareDefinition[0], new DigitalIoHardwareDefinition[0],
                secondPort, Path.Combine(DiagnosticsRoot, "duplicate-axis"));
            bool axisRejected = false;
            try { new MotionCardGroup(new IMotionCard[] { existingCard, conflictingCard }); }
            catch (ArgumentException error) { axisRejected = error.Message.Contains("逻辑轴名称重复"); }
            if (!axisRejected) throw new Exception("两张卡使用相同逻辑轴名时没有明确拒绝。");

            var otherAxis = new AxisHardwareDefinition
            {
                Name = "AnotherAxis", ControllerId = "ControllerA",
                AxisNumber = 5, SoftLimitEnabled = false
            };
            var conflictingId = new VirtualMotionCard("ControllerA", new[] { otherAxis },
                new DigitalIoHardwareDefinition[0], new DigitalIoHardwareDefinition[0],
                secondPort, Path.Combine(DiagnosticsRoot, "duplicate-controller"));
            bool controllerRejected = false;
            try { new MotionCardGroup(new IMotionCard[] { existingCard, conflictingId }); }
            catch (ArgumentException error) { controllerRejected = error.Message.Contains("运动控制卡标识重复"); }
            if (!controllerRejected) throw new Exception("两张卡使用相同 ControllerId 时没有明确拒绝。");
        }
    }
}
