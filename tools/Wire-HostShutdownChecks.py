# -*- coding: utf-8 -*-
"""临时测试接线；只修改检查入口，实施结束删除。"""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
path = root / 'tools/MotionControl.OperatorPages.Checks/Program.cs'
text = path.read_text(encoding='utf-8-sig')
marker = '            if (args.Length == 1 && args[0] == "--pending-task-load")'
assert text.count(marker) == 1, '专项入口数量变化，停止接线'
extra = '''            if (args.Length == 1 && args[0] == "--shutdown-retry")
            {
                Check("退出重试原关闭", () => RunUiAsync(BoundTaskLoadChecks.RunShutdownRetryCheckAsync), ref failures);
                app.Shutdown();
                return failures == 0 ? 0 : 1;
            }
            if (args.Length == 1 && args[0] == "--host-shutdown")
            {
                Check("退出原上下文保护", () => RunUiAsync(BoundTaskLoadChecks.RunHostShutdownChecksAsync), ref failures);
                app.Shutdown();
                return failures == 0 ? 0 : 1;
            }
'''
text = text.replace(marker, extra + marker)
marker = '            Check("任务入口关闭超时与迟到完成", LauncherCloseTimeoutChecks.Run, ref failures);'
assert text.count(marker) == 1, '完整回归入口数量变化'
text = text.replace(marker, '''            Check("退出重试原关闭", () => RunUiAsync(BoundTaskLoadChecks.RunShutdownRetryCheckAsync), ref failures);
            Check("退出原上下文保护", () => RunUiAsync(BoundTaskLoadChecks.RunHostShutdownChecksAsync), ref failures);
''' + marker)
assert '操作页面内部检查：65 组' in text
text = text.replace('操作页面内部检查：65 组', '操作页面内部检查：67 组')
path.write_text(text, encoding='utf-8')
