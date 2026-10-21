using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>在物理轴集合不变且轴全部停稳时在线更新映射和轴参数，不重建卡实例。</summary>
        public Task<bool> ApplyAxisMappingsAsync(
            IReadOnlyCollection<ZMotionAxisConfiguration> axisConfigurations,
            CancellationToken cancellationToken)
        {
            if (axisConfigurations == null)
            {
                throw new ArgumentNullException(nameof(axisConfigurations));
            }

            return Task.Run(
                () => ApplyAxisMappings(axisConfigurations, cancellationToken),
                cancellationToken);
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
                    ApplyAxisParameters(handle, axis.Configuration, cancellationToken);
                }

                _axisParametersApplied = true;
            }
        }

        /// <summary>核对物理轴集合后在线更新运行时轴配置，并只下发发生变化的卡参数。</summary>
        private bool ApplyAxisMappings(
            IReadOnlyCollection<ZMotionAxisConfiguration> axisConfigurations,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configurationsByNumber = new Dictionary<int, ZMotionAxisConfiguration>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ZMotionAxisConfiguration configuration in axisConfigurations)
            {
                if (configuration == null)
                {
                    throw new ArgumentException("在线轴映射中包含空配置。", nameof(axisConfigurations));
                }

                configuration.Validate();
                configuration.ValidateHomeParameters();
                if (!string.Equals(configuration.ControllerId, _controllerId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "逻辑轴 '" + configuration.Name + "' 属于其他控制卡，不能在线应用到当前卡。");
                }

                if (!names.Add(configuration.Name))
                {
                    throw new InvalidOperationException("在线轴映射中存在重复逻辑名称：" + configuration.Name + "。");
                }

                if (configurationsByNumber.ContainsKey(configuration.AxisNumber))
                {
                    throw new InvalidOperationException("在线轴映射中存在重复物理轴号：" + configuration.AxisNumber + "。");
                }

                configurationsByNumber.Add(configuration.AxisNumber, configuration);
            }

            if (configurationsByNumber.Count != _axesByNumber.Count
                || _axesByNumber.Keys.Any(axisNumber => !configurationsByNumber.ContainsKey(axisNumber)))
            {
                throw new InvalidOperationException(
                    "在线更新要求已映射的物理轴集合保持不变；新增或移除轴时必须重新连接控制卡。");
            }

            lock (_nativeLock)
            {
                IntPtr handle = GetConnectedHandle();
                bool configurationChanged = false;
                bool controllerParametersChanged = !_axisParametersApplied;

                foreach (KeyValuePair<int, ZMotionAxis> item in _axesByNumber)
                {
                    ZMotionAxisConfiguration current = item.Value.Configuration;
                    ZMotionAxisConfiguration updated = configurationsByNumber[item.Key];
                    configurationChanged |= !AxisConfigurationsEqual(current, updated);
                    controllerParametersChanged |= !ControllerParametersEqual(current, updated);
                }

                if (!configurationChanged && _axisParametersApplied)
                {
                    return false;
                }

                foreach (KeyValuePair<int, ZMotionAxis> item in _axesByNumber)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureAxisIdle(handle, item.Key, configurationsByNumber[item.Key].Name);
                }

                if (controllerParametersChanged)
                {
                    bool applyAllAxisParameters = !_axisParametersApplied;
                    _axisParametersApplied = false;
                    foreach (KeyValuePair<int, ZMotionAxis> item in _axesByNumber)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ZMotionAxisConfiguration current = item.Value.Configuration;
                        ZMotionAxisConfiguration updated = configurationsByNumber[item.Key];
                        if (applyAllAxisParameters || !ControllerParametersEqual(current, updated))
                        {
                            ApplyAxisParameters(handle, updated, cancellationToken);
                        }
                    }
                }

                foreach (KeyValuePair<int, ZMotionAxis> item in _axesByNumber)
                {
                    item.Value.UpdateConfiguration(configurationsByNumber[item.Key]);
                }

                _axisParametersApplied = true;
                return configurationChanged;
            }
        }

        /// <summary>按候选配置调用正运动 API 写入一个轴的单位、软限位和回零运动参数。</summary>
        private void ApplyAxisParameters(
            IntPtr handle,
            ZMotionAxisConfiguration configuration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double minimumPosition = configuration.MinimumPosition;
            double maximumPosition = configuration.MaximumPosition;
            if (!configuration.SoftLimitEnabled)
            {
                GetDisabledSoftLimitPositions(
                    handle,
                    configuration.AxisNumber,
                    configuration.Name,
                    out minimumPosition,
                    out maximumPosition);
            }

            CheckResult(
                zmcaux.ZAux_Direct_SetUnits(
                    handle,
                    configuration.AxisNumber,
                    (float)configuration.UnitsPerEngineeringUnit),
                "应用工程单位换算",
                configuration.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetRsLimit(
                    handle,
                    configuration.AxisNumber,
                    (float)minimumPosition),
                "应用负向软件限位",
                configuration.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetFsLimit(
                    handle,
                    configuration.AxisNumber,
                    (float)maximumPosition),
                "应用正向软件限位",
                configuration.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetSpeed(
                    handle,
                    configuration.AxisNumber,
                    (float)configuration.HomeSearchSpeed),
                "应用回零搜索速度",
                configuration.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetCreep(
                    handle,
                    configuration.AxisNumber,
                    (float)configuration.HomeCreepSpeed),
                "应用回零爬行速度",
                configuration.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetAccel(
                    handle,
                    configuration.AxisNumber,
                    (float)configuration.HomeAcceleration),
                "应用回零加速度",
                configuration.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetDecel(
                    handle,
                    configuration.AxisNumber,
                    (float)configuration.HomeDeceleration),
                "应用回零减速度",
                configuration.Name);
        }

        /// <summary>比较运行时轴映射中的全部正运动参数和逻辑名称。</summary>
        private static bool AxisConfigurationsEqual(
            ZMotionAxisConfiguration first,
            ZMotionAxisConfiguration second)
        {
            return string.Equals(first.ControllerId, second.ControllerId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(first.Name, second.Name, StringComparison.Ordinal)
                && string.Equals(first.EngineeringUnit, second.EngineeringUnit, StringComparison.Ordinal)
                && first.AxisNumber == second.AxisNumber
                && first.UnitsPerEngineeringUnit == second.UnitsPerEngineeringUnit
                && first.MinimumPosition == second.MinimumPosition
                && first.MaximumPosition == second.MaximumPosition
                && first.SoftLimitEnabled == second.SoftLimitEnabled
                && first.ManualSpeed == second.ManualSpeed
                && first.PositionTolerance == second.PositionTolerance
                && first.HomeMode == second.HomeMode
                && first.HomeSearchSpeed == second.HomeSearchSpeed
                && first.HomeCreepSpeed == second.HomeCreepSpeed
                && first.HomeAcceleration == second.HomeAcceleration
                && first.HomeDeceleration == second.HomeDeceleration;
        }

        /// <summary>比较会写入正运动控制器的轴参数。</summary>
        private static bool ControllerParametersEqual(
            ZMotionAxisConfiguration first,
            ZMotionAxisConfiguration second)
        {
            return first.UnitsPerEngineeringUnit == second.UnitsPerEngineeringUnit
                && first.MinimumPosition == second.MinimumPosition
                && first.MaximumPosition == second.MaximumPosition
                && first.SoftLimitEnabled == second.SoftLimitEnabled
                && first.HomeSearchSpeed == second.HomeSearchSpeed
                && first.HomeCreepSpeed == second.HomeCreepSpeed
                && first.HomeAcceleration == second.HomeAcceleration
                && first.HomeDeceleration == second.HomeDeceleration;
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
            if (axisNumber < 0 || axisNumber >= ZMotionAxisConfiguration.SupportedPhysicalAxisCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(axisNumber),
                    "正运动物理轴号必须在 0 到 "
                    + (ZMotionAxisConfiguration.SupportedPhysicalAxisCount - 1) + " 之间。");
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
