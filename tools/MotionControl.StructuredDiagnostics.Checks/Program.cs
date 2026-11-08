using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MotionControl.StructuredDiagnostics.Checks
{
    /// <summary>结构化诊断离线回归入口；只使用内存设备，不宣称完成正式 Host 屏幕验证。</summary>
    internal static class Program
    {
        /// <summary>逐组执行全部检查，单组失败后继续收集其他失败，便于 CI 一次给出完整结果。</summary>
        [STAThread]
        private static int Main()
        {
            var checks = new Dictionary<string, Func<Task>> {
                { "说明文案不决定分类", () => Run(DiagnosticChecks.TextDoesNotChooseKind) },
                { "缺少诊断保留原文", () => Run(DiagnosticChecks.MissingDiagnosticStaysMissing) },
                { "标点对象与多项导航", () => Run(DiagnosticChecks.PunctuationAndMultipleWaits) },
                { "采样质量与快照冻结", () => Run(DiagnosticChecks.QualityAndImmutableSnapshot) },
                { "数值时限与失败原因", () => Run(DiagnosticChecks.NumericTimeoutAndReason) },
                { "C# 可选工位契约", () => Run(DiagnosticChecks.OptionalCSharpStatus) },
                { "列表工位活动末端集合", () => Run(DiagnosticChecks.ParallelStationProjection) },
                { "组合条件未读取项", () => Run(DiagnosticChecks.UnreadConditionReferences) },
                { "真实执行器输入等待", RuntimeChecks.InputWait },
                { "真实执行器父级超时", RuntimeChecks.ParentTimeout },
                { "真实执行器并行等待", RuntimeChecks.ParallelWaits }
            };
            int failures = 0;
            foreach (var check in checks)
            {
                try
                {
                    check.Value().GetAwaiter().GetResult();
                    Console.WriteLine("通过：" + check.Key);
                }
                catch (Exception error)
                {
                    failures++;
                    Console.Error.WriteLine("失败：" + check.Key + Environment.NewLine + error);
                }
            }
            Console.WriteLine("结构化诊断离线检查：共 " + checks.Count + " 组，失败 " + failures + " 组。未连接控制器，未验证正式 Host 屏幕。");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>把同步快照检查适配到同一个检查清单，不启动额外线程。</summary>
        private static Task Run(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
