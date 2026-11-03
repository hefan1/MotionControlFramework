using System;
using Newtonsoft.Json.Linq;

namespace MotionControl.VirtualController
{
    internal sealed partial class ControllerState
    {
        /// <summary>核对当前会话和上料轴状态；这不是设备的生产许可或交接互锁。</summary>
        private bool FeederSafetySatisfied()
        {
            if (!_feeder.Enabled || _feeder.SafeAxis < 0 || _feeder.Fault || _leaseId == null || _outputs[_feeder.Output]) return false;
            if (!FeederAxisAtSafePosition(_feeder.SafeAxis)) return false;
            foreach (int axisNumber in _feeder.AdditionalSafeAxes)
                if (!FeederAxisAtSafePosition(axisNumber)) return false;
            return true;
        }

        /// <summary>要求指定轴有有效参考、无报警和限位、停稳并处于配置位置。</summary>
        private bool FeederAxisAtSafePosition(int axisNumber)
        {
            JObject axis = _axes[axisNumber].Snapshot();
            return axis.Value<bool>("servoEnabled") && axis.Value<bool>("homed") && !axis.Value<bool>("moving")
                && !axis.Value<bool>("alarmActive") && !axis.Value<bool>("positiveLimitActive") && !axis.Value<bool>("negativeLimitActive")
                && !axis.Value<bool>("positiveSoftLimitActive") && !axis.Value<bool>("negativeSoftLimitActive")
                && Math.Abs(axis.Value<double>("position") - _feeder.SafePosition) <= _feeder.SafeTolerance;
        }

        /// <summary>只在安全条件连续成立期间计时；离位、运动、报警或会话失效立即丢弃计时。</summary>
        private void RefreshFeederSafetyGate()
        {
            if (!_feeder.Enabled || _feeder.SafeAxis < 0 || !_feeder.FeedPending) return;
            if (!FeederSafetySatisfied())
            {
                _feeder.SafeSinceMilliseconds = null;
                return;
            }
            double now = _leaseClock.Elapsed.TotalMilliseconds;
            if (!_feeder.SafeSinceMilliseconds.HasValue) _feeder.SafeSinceMilliseconds = now;
            if (now - _feeder.SafeSinceMilliseconds.Value < _feeder.PendingDelayMs) return;
            _inputs[_feeder.Input] = true;
            _feeder.FeedPending = false;
            _feeder.SafeSinceMilliseconds = null;
        }
    }
}
