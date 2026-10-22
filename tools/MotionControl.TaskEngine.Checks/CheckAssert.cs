using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>离线检查的简单断言，不依赖 WPF 或厂商测试程序。</summary>
    internal static class CheckAssert
    {
        /// <summary>条件不成立时保留清楚的失败原因。</summary>
        internal static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        /// <summary>确认指定操作确实拒绝无效输入，而不是默认成功。</summary>
        internal static void Throws(Action action, string message)
        {
            try { action(); }
            catch (Exception) { return; }
            throw new InvalidOperationException(message);
        }

        /// <summary>定义可以有运行待核实提示，但不能含定义错误。</summary>
        internal static void NoErrors(IEnumerable<TaskValidationIssue> issues)
        {
            var errors = issues.Where(issue => issue.Severity == TaskIssueSeverity.Error).ToList();
            True(errors.Count == 0, string.Join("\n", errors.Select(issue => issue.ToString())));
        }

        /// <summary>确认错误包含预期中文原因，防止因无关异常而误报测试通过。</summary>
        internal static void HasError(IEnumerable<TaskValidationIssue> issues, string expected)
        {
            True(issues.Any(issue => issue.Severity == TaskIssueSeverity.Error && issue.Message.Contains(expected)),
                "没有发现预期错误：" + expected);
        }
    }
}
