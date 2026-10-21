using System;
using System.Collections.Generic;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.VendorApi.ZMotion.Native;

namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>正运动适配器中负责轴状态、限位和报警原因解析的实现部分。</summary>
    public sealed partial class ZMotionCard
    {
        /// <summary>调用正运动 API 读取轴位置、速度、伺服和状态字。</summary>
        private AxisStatus ReadAxisStatus(ZMotionAxis axis)
        {
            lock (_nativeLock)
            {
                var handle = GetConnectedHandle();
                float actualPosition = 0;
                float commandPosition = 0;
                float velocity = 0;
                int servoEnabled = 0;
                int idle = 1;

                CheckResult(
                    zmcaux.ZAux_Direct_GetMpos(handle, axis.AxisNumber, ref actualPosition),
                    "读取反馈位置",
                    axis.Name);
                CheckResult(
                    zmcaux.ZAux_Direct_GetDpos(handle, axis.AxisNumber, ref commandPosition),
                    "读取指令位置",
                    axis.Name);
                CheckResult(
                    zmcaux.ZAux_Direct_GetMspeed(handle, axis.AxisNumber, ref velocity),
                    "读取轴速度",
                    axis.Name);
                CheckResult(
                    zmcaux.ZAux_Direct_GetAxisEnable(handle, axis.AxisNumber, ref servoEnabled),
                    "读取伺服使能状态",
                    axis.Name);
                CheckResult(
                    zmcaux.ZAux_Direct_GetIfIdle(handle, axis.AxisNumber, ref idle),
                    "读取轴运动状态",
                    axis.Name);

                var statusBits = ReadAxisStatusBits(handle, axis, "读取轴状态字");
                bool? originInputActive = ReadOriginInputActive(handle, axis);
                var activeAlarmBits = statusBits & ActiveAlarmStatusMask;
                var alarmActive = activeAlarmBits != 0;
                var alarmMessage = alarmActive ? BuildAxisAlarmMessage(statusBits) : null;

                return new AxisStatus(
                    actualPosition,
                    velocity,
                    servoEnabled != 0,
                    idle == 0,
                    axis.IsHomed,
                    (statusBits & PositiveHardLimitMask) != 0,
                    (statusBits & NegativeHardLimitMask) != 0,
                    (statusBits & PositiveSoftLimitMask) != 0,
                    (statusBits & NegativeSoftLimitMask) != 0,
                    alarmActive,
                    alarmMessage,
                    axis.RequiresHomingAfterLimitAlarm,
                    commandPosition,
                    originInputActive,
                    "ZAux_Direct_GetAxisStatus / AXISSTATUS");
            }
        }

        /// <summary>读取 DATUM_IN 对应输入及其反相设置，得到控制器识别的原点信号状态。</summary>
        private bool? ReadOriginInputActive(IntPtr handle, ZMotionAxis axis)
        {
            int inputNumber = -1;
            CheckResult(
                zmcaux.ZAux_Direct_GetDatumIn(handle, axis.AxisNumber, ref inputNumber),
                "读取 DATUM_IN 原点输入通道",
                axis.Name);

            if (inputNumber < 0)
            {
                return null;
            }

            uint inputValue = 0;
            CheckResult(
                zmcaux.ZAux_Direct_GetIn(handle, inputNumber, ref inputValue),
                "读取原点输入通道 " + inputNumber,
                axis.Name);

            int inputInverted = 0;
            CheckResult(
                zmcaux.ZAux_Direct_GetInvertIn(handle, inputNumber, ref inputInverted),
                "读取原点输入通道 " + inputNumber + " 的反相设置",
                axis.Name);

            bool inputActive = inputValue != 0;
            return inputInverted != 0 ? !inputActive : inputActive;
        }

        /// <summary>读取正运动控制器的实时轴状态字。</summary>
        private int ReadAxisStatusBits(IntPtr handle, ZMotionAxis axis, string operation)
        {
            var statusBits = 0;
            CheckResult(
                zmcaux.ZAux_Direct_GetAxisStatus(handle, axis.AxisNumber, ref statusBits),
                operation,
                axis.Name);
            return statusBits;
        }

        /// <summary>读取正运动控制器锁存的历史停止原因。</summary>
        private int ReadAxisStopReason(IntPtr handle, ZMotionAxis axis, string operation)
        {
            var stopReasonCode = 0;
            CheckResult(
                zmcaux.ZAux_Direct_GetAxisStopReason(handle, axis.AxisNumber, ref stopReasonCode),
                operation,
                axis.Name);
            return stopReasonCode;
        }

        /// <summary>把正运动实时状态位转换为轴或驱动报警原因；限位原因单独显示。</summary>
        private static string BuildAxisAlarmMessage(int statusBits)
        {
            var messages = new List<string>();
            if ((statusBits & (1 << RemoteAxisCommunicationErrorBit)) != 0)
            {
                messages.Add("远程轴通讯异常");
            }

            if ((statusBits & BusDriveAlarmMask) != 0)
            {
                messages.Add("总线驱动器报警");
            }

            if ((statusBits & (1 << FollowingErrorBit)) != 0)
            {
                messages.Add("随动误差超限");
            }

            if ((statusBits & (1 << PowerAbnormalBit)) != 0)
            {
                messages.Add("控制器电源异常");
            }

            if ((statusBits & (1 << AxisSpeedProtectionBit)) != 0)
            {
                messages.Add("触发轴速度保护");
            }

            if ((statusBits & (1 << SpecialMotionCommandErrorBit)) != 0)
            {
                messages.Add("运动中触发特殊指令失败");
            }

            if ((statusBits & AlarmSignalInputMask) != 0)
            {
                messages.Add("报警信号输入有效");
            }

            return string.Join("；", messages);
        }

        /// <summary>整理当前有效的硬限位和软限位名称，供复位提示使用。</summary>
        private static string BuildActiveLimitMessage(int statusBits)
        {
            var messages = new List<string>();
            if ((statusBits & PositiveHardLimitMask) != 0)
            {
                messages.Add("正向硬限位");
            }

            if ((statusBits & NegativeHardLimitMask) != 0)
            {
                messages.Add("负向硬限位");
            }

            if ((statusBits & PositiveSoftLimitMask) != 0)
            {
                messages.Add("正向软限位");
            }

            if ((statusBits & NegativeSoftLimitMask) != 0)
            {
                messages.Add("负向软限位");
            }

            return string.Join("、", messages);
        }
    }
}
