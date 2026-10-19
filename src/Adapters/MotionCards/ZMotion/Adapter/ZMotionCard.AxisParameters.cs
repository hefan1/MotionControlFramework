using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.VendorApi.ZMotion.Native;

namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>正运动适配器中负责轴参数读写与软限位换算的实现部分。</summary>
    public sealed partial class ZMotionCard
    {
        /// <summary>把已映射轴的单位、软限位和回零运动参数应用到控制器。</summary>
        public Task ApplyAxisParametersAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() => ApplyAxisParameters(cancellationToken), cancellationToken);
        }

        /// <summary>读取控制器指定物理轴的单位、软限位和当前运动参数。</summary>
        public Task<ZMotionPhysicalAxisParameters> ReadPhysicalAxisParametersAsync(
            int axisNumber,
            CancellationToken cancellationToken)
        {
            ValidatePhysicalAxisNumber(axisNumber);
            return Task.Run(() =>
            {
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    var axisName = GetPhysicalAxisName(axisNumber);
                    var parameters = new ZMotionPhysicalAxisParameters
                    {
                        AxisNumber = axisNumber
                    };

                    ReadAxisParameter(
                        handle,
                        axisNumber,
                        axisName,
                        "UNITS",
                        zmcaux.ZAux_Direct_GetUnits,
                        value => parameters.UnitsPerEngineeringUnit = value);
                    ReadAxisParameter(
                        handle,
                        axisNumber,
                        axisName,
                        "RS_LIMIT",
                        zmcaux.ZAux_Direct_GetRsLimit,
                        value => parameters.MinimumPosition = value);
                    ReadAxisParameter(
                        handle,
                        axisNumber,
                        axisName,
                        "FS_LIMIT",
                        zmcaux.ZAux_Direct_GetFsLimit,
                        value => parameters.MaximumPosition = value);
                    ReadSoftLimitEffectiveState(handle, axisNumber, axisName, parameters);
                    ReadAxisParameter(
                        handle,
                        axisNumber,
                        axisName,
                        "SPEED",
                        zmcaux.ZAux_Direct_GetSpeed,
                        value => parameters.Speed = value);
                    ReadAxisParameter(
                        handle,
                        axisNumber,
                        axisName,
                        "CREEP",
                        zmcaux.ZAux_Direct_GetCreep,
                        value => parameters.CreepSpeed = value);
                    ReadAxisParameter(
                        handle,
                        axisNumber,
                        axisName,
                        "ACCEL",
                        zmcaux.ZAux_Direct_GetAccel,
                        value => parameters.Acceleration = value);
                    ReadAxisParameter(
                        handle,
                        axisNumber,
                        axisName,
                        "DECEL",
                        zmcaux.ZAux_Direct_GetDecel,
                        value => parameters.Deceleration = value);

                    return parameters;
                }
            }, cancellationToken);
        }

        /// <summary>在所有已映射轴空闲时应用映射页配置的轴参数。</summary>
        private void ApplyAxisParameters(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_nativeLock)
            {
                var handle = GetConnectedHandle();
                _axisParametersApplied = false;

                foreach (var axis in _axesByNumber.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureAxisIdle(handle, axis);
                    axis.Configuration.ValidateHomeParameters();
                    double minimumPosition = axis.Configuration.MinimumPosition;
                    double maximumPosition = axis.Configuration.MaximumPosition;
                    if (!axis.Configuration.SoftLimitEnabled)
                    {
                        GetDisabledSoftLimitPositions(
                            handle,
                            axis.AxisNumber,
                            axis.Name,
                            out minimumPosition,
                            out maximumPosition);
                    }
                    CheckResult(
                        zmcaux.ZAux_Direct_SetUnits(
                            handle,
                            axis.AxisNumber,
                            (float)axis.Configuration.UnitsPerEngineeringUnit),
                        "应用工程单位换算",
                        axis.Name);
                    CheckResult(
                        zmcaux.ZAux_Direct_SetRsLimit(
                            handle,
                            axis.AxisNumber,
                            (float)minimumPosition),
                        "应用负向软件限位",
                        axis.Name);
                    CheckResult(
                        zmcaux.ZAux_Direct_SetFsLimit(
                            handle,
                            axis.AxisNumber,
                            (float)maximumPosition),
                        "应用正向软件限位",
                        axis.Name);
                    CheckResult(
                        zmcaux.ZAux_Direct_SetSpeed(
                            handle,
                            axis.AxisNumber,
                            (float)axis.Configuration.HomeSearchSpeed),
                        "应用回零搜索速度",
                        axis.Name);
                    CheckResult(
                        zmcaux.ZAux_Direct_SetCreep(
                            handle,
                            axis.AxisNumber,
                            (float)axis.Configuration.HomeCreepSpeed),
                        "应用回零爬行速度",
                        axis.Name);
                    CheckResult(
                        zmcaux.ZAux_Direct_SetAccel(
                            handle,
                            axis.AxisNumber,
                            (float)axis.Configuration.HomeAcceleration),
                        "应用回零加速度",
                        axis.Name);
                    CheckResult(
                        zmcaux.ZAux_Direct_SetDecel(
                            handle,
                            axis.AxisNumber,
                            (float)axis.Configuration.HomeDeceleration),
                        "应用回零减速度",
                        axis.Name);
                }

                _axisParametersApplied = true;
            }
        }

        /// <summary>使用正运动 API 读取一个浮点轴参数，并保留参数名和轴号。</summary>
        private void ReadAxisParameter(
            IntPtr handle,
            int axisNumber,
            string axisName,
            string parameterName,
            AxisFloatParameterReader readParameter,
            Action<double> saveValue)
        {
            float value = 0;
            CheckResult(
                readParameter(handle, axisNumber, ref value),
                "读取 " + parameterName,
                axisName);
            saveValue(value);
        }

        /// <summary>读取 REP_DIST，并按正运动规则判断正负软限位参数是否生效。</summary>
        private void ReadSoftLimitEffectiveState(
            IntPtr handle,
            int axisNumber,
            string axisName,
            ZMotionPhysicalAxisParameters parameters)
        {
            float repeatDistance = 0;
            int result = zmcaux.ZAux_Direct_GetRepDist(handle, axisNumber, ref repeatDistance);
            if (result != 0)
            {
                parameters.PositiveSoftLimitEffective = null;
                parameters.NegativeSoftLimitEffective = null;
                parameters.SoftLimitEnabled = null;
                parameters.SoftLimitStatusDiagnostic =
                    "正运动物理轴 '" + axisName + "' 读取 REP_DIST 失败，厂商错误码：" + result + "。";
                return;
            }

            if (!IsFinite(repeatDistance) || repeatDistance <= 0)
            {
                parameters.PositiveSoftLimitEffective = null;
                parameters.NegativeSoftLimitEffective = null;
                parameters.SoftLimitEnabled = null;
                parameters.SoftLimitStatusDiagnostic =
                    "正运动物理轴 '" + axisName + "' 的 REP_DIST 不是大于零的有限数值，无法判断软限位状态。";
                return;
            }

            parameters.PositiveSoftLimitEffective = IsSoftLimitWithinRepeatDistance(
                parameters.MaximumPosition,
                repeatDistance);
            parameters.NegativeSoftLimitEffective = IsSoftLimitWithinRepeatDistance(
                parameters.MinimumPosition,
                repeatDistance);
            parameters.SoftLimitEnabled = parameters.PositiveSoftLimitEffective.HasValue
                && parameters.NegativeSoftLimitEffective.HasValue
                && parameters.PositiveSoftLimitEffective.Value == parameters.NegativeSoftLimitEffective.Value
                    ? parameters.PositiveSoftLimitEffective.Value
                    : (bool?)null;

            if (!parameters.PositiveSoftLimitEffective.HasValue || !parameters.NegativeSoftLimitEffective.HasValue)
            {
                parameters.SoftLimitStatusDiagnostic =
                    "正运动物理轴 '" + axisName + "' 的 FS_LIMIT 或 RS_LIMIT 读回值无效，无法判断对应方向。";
            }
        }

        /// <summary>读取 REP_DIST 并计算卡上无效范围，用于停用正负软限位但保留控制器设置。</summary>
        private void GetDisabledSoftLimitPositions(
            IntPtr handle,
            int axisNumber,
            string axisName,
            out double minimumPosition,
            out double maximumPosition)
        {
            float repeatDistance = 0;
            int result = zmcaux.ZAux_Direct_GetRepDist(handle, axisNumber, ref repeatDistance);
            CheckResult(result, "读取 REP_DIST 以停用软限位", axisName);

            float disabledLimit = repeatDistance * 2.0f;
            if (!IsFinite(repeatDistance)
                || repeatDistance <= 0
                || !IsFinite(disabledLimit)
                || disabledLimit <= repeatDistance)
            {
                throw new InvalidOperationException(
                    "正运动物理轴 '" + axisName
                    + "' 的 REP_DIST 无法生成大于有效区间的软限位停用值；未写入该轴参数。");
            }

            minimumPosition = -disabledLimit;
            maximumPosition = disabledLimit;
        }

        /// <summary>判断一个软限位位置是否处于正运动规定的 REP_DIST 有效范围内。</summary>
        private static bool? IsSoftLimitWithinRepeatDistance(double limitPosition, double repeatDistance)
        {
            if (!IsFinite(limitPosition) || !IsFinite(repeatDistance) || repeatDistance <= 0)
            {
                return null;
            }

            return limitPosition > -repeatDistance && limitPosition < repeatDistance;
        }

        /// <summary>检查物理轴通道范围，防止访问超过当前适配器支持范围的轴。</summary>
        private static void ValidatePhysicalAxisNumber(int axisNumber)
        {
            if (axisNumber < 0 || axisNumber >= 64)
            {
                throw new ArgumentOutOfRangeException(nameof(axisNumber), "正运动物理轴号必须在 0 到 63 之间。");
            }
        }

        /// <summary>判断数值是否为有限数值。</summary>
        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>把物理轴号转换为便于诊断的 AxisN 名称。</summary>
        private static string GetPhysicalAxisName(int axisNumber)
        {
            return "Axis" + axisNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
