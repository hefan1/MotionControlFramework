using System.Collections.Generic;
using MotionControl.TaskEngine.Catalog;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Configuration
{
    public sealed partial class MachineProjectPackageService
    {
        /// <summary>在预览和真正写入前核对点位迁移；只读取当前/候选快照，不更改原冻结页面。</summary>
        private static void ValidateTeachPointMigration(Dictionary<string, string> current, Dictionary<string, string> merged,
            Dictionary<string, string> imported, ICollection<string> notices)
        {
            const string axesPath = "HardwareConfig/AxisParameters.json";
            const string pointsPath = "Parameters/ManualAxisPositions.json";
            string oldAxes, oldPoints, newAxes, newPoints;
            current.TryGetValue(axesPath, out oldAxes);
            current.TryGetValue(pointsPath, out oldPoints);
            merged.TryGetValue(axesPath, out newAxes);
            merged.TryGetValue(pointsPath, out newPoints);
            TaskTeachPointOwnershipValidator.ValidateMigration(
                oldAxes == null ? null : ReadStrictObject(oldAxes, axesPath),
                oldPoints == null ? null : ReadStrictObject(oldPoints, pointsPath),
                newAxes == null ? null : ReadStrictObject(newAxes, axesPath),
                newPoints == null ? null : ReadStrictObject(newPoints, pointsPath),
                imported.ContainsKey(pointsPath), notices);
        }
    }
}
