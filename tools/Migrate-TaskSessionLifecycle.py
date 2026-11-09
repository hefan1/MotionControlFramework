"""仅本次隔离分支使用的精确迁移；最终交付删除此脚本。"""
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
TEST = r'''using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.Host.Wpf;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>复用既有无轴设施检查 Host 装载生命周期；不代表运动控制器或屏幕验收。</summary>
    internal static partial class BoundTaskLoadChecks
    {
        /// <summary>分别检查普通关闭和切换为空上下文，二者均须等待原装载退出。</summary>
        internal static async Task RunPendingLoadChecksAsync()
        {
            await CheckPendingLoadCloseAsync(false);
            await CheckPendingLoadCloseAsync(true);
        }

        /// <summary>挂起原有连接读取，发起关闭，释放读取后核对未发布、未泄漏持权及准备范围。</summary>
        private static async Task CheckPendingLoadCloseAsync(bool changeContext)
        {
            string root = Path.Combine(Path.GetTempPath(), "PendingLoad-" + Guid.NewGuid().ToString("N"));
            const string id = "PendingLoad";
            var store = new MachineConfigurationStore(root);
            var host = new MainWindow();
            var card = new LoadCard();
            var cards = new MotionCardGroup(new[] { card });
            var runtime = new MachineRuntime(cards, new IStationFlow[0], new MachineAlarmService(), new SafeCheckSafety());
            var variables = new DeviceVariableStore(new DeviceVariableDefinition[0]);
            var preparation = new CheckPreparation("original");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type type = typeof(MainWindow).Assembly.GetType("MotionControl.Host.Wpf.Tasks.TaskMonitorLauncher", true);
            object page = Activator.CreateInstance(type, flags, null, new object[] { store, id, host, null }, null);
            Task Invoke(string name, params object[] args) => (Task)type.GetMethod(name, flags).Invoke(page, args);
            Task loading = null;
            Task closing = null;
            var errors = new List<Exception>();
            try
            {
                WriteMachine(store, id);
                await runtime.ConnectAsync(CancellationToken.None);
                runtime.ConfigurePreparations(new[] { preparation });
                await Invoke("SetMachineContextAsync", runtime, cards, new NoIo(), variables, new[] { preparation });
                card.BlockWorkerRead = true;
                loading = Invoke("LoadTaskSessionAsync");
                if (await Task.WhenAny(card.WorkerEntered.Task, Task.Delay(4000)) != card.WorkerEntered.Task)
                    throw new InvalidOperationException("后台读取没有进入指定窗口，不能判定本次关闭检查有效。");
                closing = changeContext
                    ? Invoke("SetMachineContextAsync", null, null, null, null, null)
                    : Invoke("CloseTaskSessionAsync", false);
                bool returnedBeforeReadSettled = closing.IsCompleted;
                card.Resume.Set();
                Exception loadError = null;
                try { await loading; }
                catch (Exception error) { loadError = error; }
                await closing;
                Require(!returnedBeforeReadSettled, "PENDING_LOAD_CLOSE_RETURNED_EARLY: 原任务仍在读取，关闭或上下文切换却提前成功。");
                Require(loadError is InvalidOperationException
                    && (loadError.Message.Contains("撤销") || loadError.Message.Contains("连接")),
                    "关闭后的读取未被准确拒绝，或错误类型不属于上下文撤销。");
                Require(type.GetField("_session", flags).GetValue(page) == null
                    && !runtime.IsTaskEngineControlClaimed, "关闭返回后迟到装载仍发布了会话或遗留控制权。");
                Require(runtime.PreparationStations.Count == 1 && runtime.PreparationStations[0].StationId == "original",
                    "关闭在途装载错误修改了设备原准备范围。");
                if (changeContext)
                    Require(type.GetField("_runtime", flags).GetValue(page) == null, "装载清理后没有完成所请求的上下文切换。");
            }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                card.Resume.Set();
                // 读取失败是场景预期；仍等待其退出，再通过正式入口清理实际持权。
                if (loading != null) { try { await loading; } catch { } }
                if (closing != null) { try { await closing; } catch (Exception error) { errors.Add(error); } }
                try { await Invoke("CloseTaskSessionAsync", false); }
                catch (Exception error) { errors.Add(error); }
                try { await runtime.DisconnectAsync(CancellationToken.None); }
                catch (Exception error) { errors.Add(error); }
                host.Close();
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (Exception error) { errors.Add(error); }
            }
            if (errors.Count != 0) throw new AggregateException("在途装载关闭检查失败。", errors);
            Console.WriteLine("[通过] 在途装载先撤销并收尾再关闭：" + (changeContext ? "上下文切换" : "普通关闭"));
        }
    }
}
'''


def replace_once(text, before, after):
    """不匹配就终止，避免覆盖其他会话已变更的代码。"""
    if text.count(before) != 1:
        raise RuntimeError('迁移定位不是唯一匹配: ' + before[:100])
    return text.replace(before, after, 1)


def write(path, text):
    """统一写入 UTF-8，生产源码由编译与完整差异复核确认。"""
    target = ROOT / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding='utf-8')


def add_tests():
    """只增加测试和独立运行入口，完全不修改生产行为。"""
    path = 'tools/MotionControl.OperatorPages.Checks/BoundTaskLoadChecks.cs'
    text = (ROOT / path).read_text(encoding='utf-8-sig')
    text = replace_once(text, 'internal static class BoundTaskLoadChecks', 'internal static partial class BoundTaskLoadChecks')
    write(path, text)
    write('tools/MotionControl.OperatorPages.Checks/PendingTaskLoadCloseChecks.cs', TEST)
    path = 'tools/MotionControl.OperatorPages.Checks/Program.cs'
    text = (ROOT / path).read_text(encoding='utf-8-sig')
    text = replace_once(text, 'private static int Main()', 'private static int Main(string[] args)')
    anchor = '            int failures = 0;'
    text = replace_once(text, anchor, anchor + '''
            // 专项入口仅用于快速复现同一正式页面的装载/关闭行为，完整检查也始终执行它。
            if (args.Length == 1 && args[0] == "--pending-task-load")
            {
                Check("在途任务装载关闭", () => RunUiAsync(BoundTaskLoadChecks.RunPendingLoadChecksAsync), ref failures);
                app.Shutdown();
                return failures == 0 ? 0 : 1;
            }
            Check("在途任务装载关闭", () => RunUiAsync(BoundTaskLoadChecks.RunPendingLoadChecksAsync), ref failures);''')
    text = replace_once(text, '操作页面内部检查：64 组', '操作页面内部检查：65 组')
    write(path, text)


if __name__ == '__main__':
    if sys.argv[1:] == ['--tests']:
        add_tests()
    else:
        raise SystemExit('本阶段只允许 --tests；先验证原实现确实失败。')
