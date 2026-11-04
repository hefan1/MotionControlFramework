using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf;
using MotionControl.Host.Wpf.Operation;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Wpf.Editing;
using MotionControl.TaskEngine.Wpf.Simulation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>四页真实控件和内存服务检查；不连接厂商 SDK，不代替桌面验收。</summary>
    internal static class Program
    {
        /// <summary>逐项运行并保留错误堆栈和非零失败退出码。</summary>
        [STAThread]
        private static int Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MotionControl.Host.Wpf;component/Localization/Strings.zh-CN.xaml", UriKind.Relative) });
            int failures = 0;
            Check("任务入口关闭超时与迟到完成", LauncherCloseTimeoutChecks.Run, ref failures);
            Check("未知状态、真实Runtime来源和无硬件调用", RuntimeSource, ref failures);
            Check("暂停覆盖、任意数量工位和预览来源", OverviewStates, ref failures);
            Check("整机启动拦截原因跨页面一致", StartReadinessConsistency, ref failures);
            Check("任务入口的会话状态和导航", TaskOwnerDisplay, ref failures);
            Check("报警恢复待复位、搜索和稳定选择", AlarmConditions, ref failures);
            Check("报警状态与报警库恢复指导同时可见", AlarmGuidanceChecks.Run, ref failures);
            Check("任务页报警表格快照与语言切换", TaskAlarmGridChecks.Run, ref failures);
            Check("任务运行空状态与诊断", TaskRunPageChecks.Run, ref failures);
            Check("设备状态资源覆盖与中英文显示", MachineStateLocalization, ref failures);
            Check("IO 监控区分通道范围和当前读取失败", IoMonitorReadFailureChecks.Run, ref failures);
            Check("DO 切换前重读实际电平", IoMonitorToggleChecks.Run, ref failures);
            Check("IO 反馈关联持久化与无效配置", IoFeedbackAssociationChecks.Run, ref failures);
            Check("IO 关联反馈显示与独立读取", IoFeedbackMonitorChecks.Run, ref failures);
            Check("控制器后端选择与设备映射保护", HostBackendSelectionChecks.Run, ref failures);
            Check("Host导航和返回原页面", HostNavigation, ref failures);
            Check("运动控制集中导航及关联返回", MotionControlNavigationChecks.Run, ref failures);
            Check("轴手动断开时保留原页面并闭锁动作", DisconnectedManualMotionChecks.Run, ref failures);
            Check("操作员总览任务来源与报警直接定位", OperatorStationOverviewChecks.Run, ref failures);
            Check("设备工程页面和应用后重启保护", EquipmentProjectPageChecks.Run, ref failures);
            Check("工程导入等待映射回滚续体", ProjectGateContinuationChecks.Run, ref failures);
            Check("工程应用后退出保留原恢复记录", ShutdownEvidenceChecks.Run, ref failures);
            Check("暂停和停止的现场恢复提示", MachineCommandGuidance, ref failures);
            Check("恢复拦截只读诊断和设备目录", RecoveryEvidenceGuidance, ref failures);
            Check("设备准备页轴使能提示和只读导航", PreparationAxisGuidance, ref failures);
            Check("资源确认先核查现场并拒绝 IO 异常和变化", ResourceRecoveryUiChecks.Run, ref failures);
            Check("产品记录只读显示两种来源及错误", ProductRecordChecks.Run, ref failures);
            Check("工位版本、草稿与多轴参数独立保存", EditorIntegration, ref failures);
            Check("四页中英文和两种尺寸离屏布局", RenderWorkspaces, ref failures);
            Console.WriteLine("操作页面内部检查：29 组，失败 " + failures + "。未连接控制器，不代表屏幕验收。");
            app.Shutdown();
            return failures == 0 ? 0 : 1;
        }
        /// <summary>恢复阻断时提供当前配置目录下的原始证据路径，不操作设备或放行。</summary>
        private static void RecoveryEvidenceGuidance()
        {
            var card = new GuardCard();
            var runtime = CreateRuntime(card, new MachineAlarmService());
            runtime.SetRecoveryBlockReason("恢复文件未确认");
            var view = new EquipmentPreparationView(null, null);
            string directory = Path.Combine(Path.GetTempPath(), "RecoveryEvidenceExample", "Machine01");
            view.SetRecoveryEvidenceDirectory(directory);
            view.SetRuntime(runtime);
            var expander = (Expander)view.FindName("DiagnosticExpander");
            var diagnostic = (TextBox)view.FindName("DiagnosticText");
            Assert(expander.Visibility == Visibility.Visible, "恢复拦截时没有维护诊断入口。");
            Assert(diagnostic.Text.Contains(Path.Combine(directory, "Runtime", "RecoverySnapshot.json"))
                && diagnostic.Text.Contains(Path.Combine(directory, "Runtime", "AutomaticRun.pending"))
                && diagnostic.Text.Contains(Path.Combine(directory, "Runtime", "ControlledClearance.pending"))
                && diagnostic.Text.Contains(Path.Combine(directory, "Runtime", "ControlledClearance.actions-observed"))
                && diagnostic.Text.Contains(Path.Combine(directory, "Runtime", "ControlledClearanceInspections"))
                && diagnostic.Text.Contains("不会解除启动限制"), "未显示当前目录的证据或限制说明。");
            Assert(!((Button)view.FindName("PrepareAllButton")).IsEnabled && card.Calls == 0, "只读诊断触发设备动作或错误放行。");
            string longReason = new string('测', 260) + "完整原因末尾";
            runtime.SetRecoveryBlockReason(longReason);
            view.SetRuntime(runtime);
            Assert(((TextBlock)view.FindName("ErrorText")).Text.Length == 241 && diagnostic.Text.Contains("完整原因末尾"),
                "长错误未适当截断或完整原因丢失。");
            view.SetLanguage("en-US");
            Assert(diagnostic.Text.Contains("does not release") && diagnostic.Text.Contains(directory), "英文诊断或证据路径未更新。");
            view.SetRuntime(null);
            Assert(expander.Visibility == Visibility.Collapsed, "断开后仍展示旧设备拦截详情。");
        }
        /// <summary>准备页给出伺服前置提示；跳转轴手动不改变设备状态或发出控制命令。</summary>
        private static void PreparationAxisGuidance()
        {
            var card = new GuardCard();
            var runtime = CreateRuntime(card, new MachineAlarmService());
            string requestedPage = null;
            int configurationCalls = 0;
            var view = new EquipmentPreparationView(() => { configurationCalls++; return Task.CompletedTask; }, page => requestedPage = page);
            view.SetRuntime(runtime);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert(configurationCalls == 0 && card.Calls == 0, "进入准备页面时错误读取或切换了任务会话。");
            var guidance = (TextBlock)view.FindName("GuidanceText");
            var axisButton = (Button)view.FindName("AxisManualButton");
            Assert(guidance.Text.Contains("相关轴已使能") && guidance.Text.Contains("物料状态安全"), "未提示伺服和物料前置检查。");
            axisButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(requestedPage == "轴手动" && card.Calls == 0, "检查轴使能入口未导航到轴手动或触发了设备动作。");
            runtime.AcquireTaskEngineControlAsync(CancellationToken.None).GetAwaiter().GetResult();
            view.SetRuntime(runtime);
            Assert(guidance.Text.Contains("停止并关闭任务") && guidance.Text.Contains("确认轴停稳"), "任务会话占用时未提示先安全关闭任务。");
            view.SetLanguage("en-US");
            Assert(guidance.Text.Contains("axes are enabled") && guidance.Text.Contains("stop and close the task")
                && axisButton.Content.ToString() == "Check servo enable", "轴使能提示或入口未随语言切换。");
            Assert(((TextBlock)view.FindName("ReadOnlyChecksText")).Text.Contains("Unknown"), "准备前未知状态未随语言切换或错误显示已确认。");
            runtime.ReleaseTaskEngineControl();
        }
        /// <summary>检查任务页复用的顶部状态资源覆盖全部设备状态，且等待复位在两种语言下可读。</summary>
        private static void MachineStateLocalization()
        {
            var method = typeof(MainWindow).GetMethod("GetMachineStateResourceKey",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert(method != null, "找不到共用设备状态映射。");
            var zh = new ResourceDictionary { Source = new Uri("/MotionControl.Host.Wpf;component/Localization/Strings.zh-CN.xaml", UriKind.Relative) };
            var en = new ResourceDictionary { Source = new Uri("/MotionControl.Host.Wpf;component/Localization/Strings.en-US.xaml", UriKind.Relative) };
            foreach (MachineRunState state in Enum.GetValues(typeof(MachineRunState)))
            {
                string key = (string)method.Invoke(null, new object[] { state });
                Assert(zh.Contains(key) && en.Contains(key), "设备状态缺少语言资源：" + state);
            }
            string resetKey = (string)method.Invoke(null, new object[] { MachineRunState.ResetRequired });
            Assert((string)zh[resetKey] == "等待复位", "中文设备等待复位状态错误。");
            Assert((string)en[resetKey] == "Reset Required", "英文设备等待复位状态错误。");
            foreach (string key in new[] { "控制入口工位流程", "控制入口任务列表", "控制入口无" })
                Assert(zh.Contains(key) && en.Contains(key), "控制入口缺少语言资源：" + key);
        }
        /// <summary>统一输出每组结果，异常保留原始堆栈。</summary>
        private static void Check(string name, Action action, ref int failures)
        { try { action(); Console.WriteLine("[通过] " + name); } catch (Exception error) { failures++; Console.Error.WriteLine("[失败] " + name + "\n" + error); } }
        /// <summary>失败必须中止当前组，不能只打印提示。</summary>
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        /// <summary>确认真实设备状态来源显示为操作员用语，页面不调用卡的连接或动作入口。</summary>
        private static void RuntimeSource()
        {
            var overview = new EquipmentOverviewView(); var alarms = new CurrentAlarmsView();
            Assert(((TextBlock)overview.FindName("MachineStateText")).Text.Contains("未读取"), "没有来源却显示已就绪。");
            Assert(((FrameworkElement)overview.FindName("AggregateMetrics")).Visibility == Visibility.Collapsed,
                "操作员总览仍显示生产统计区。");
            Assert(((TextBlock)alarms.FindName("EmptyText")).Text == "未读取报警状态", "无来源被当作零报警。");
            var card = new GuardCard(); var service = new MachineAlarmService();
            var runtime = CreateRuntime(card, service);
            var notice = new MachineAlarmDefinition("NOTICE", "测试提醒", MachineAlarmKind.NonStop);
            service.ReportCondition(notice, "测试工位", true, "测试条件");
            overview.SetRuntime(runtime); alarms.SetRuntime(runtime);
            Assert(((DataGrid)alarms.FindName("AlarmGrid")).Items.Count == 1, "没有读取当前Runtime报警。");
            Assert(((TextBlock)overview.FindName("SourceText")).Text == "状态来源：设备运行数据", "设备总览显示了内部 Runtime 名称。");
            Assert(((TextBlock)alarms.FindName("SourceText")).Text.StartsWith("状态来源：设备运行数据"), "当前报警显示了内部 Runtime 名称。");
            service.ReportCondition(notice, "测试工位", false, null); alarms.RefreshDisplay();
            Assert(((DataGrid)alarms.FindName("AlarmGrid")).Items.Count == 0, "非停机提醒恢复后没有消失。");
            overview.SetConnectionStatus(true, null);
            overview.SetConnectionStatus(false, "连接中断");
            Assert(((TextBlock)overview.FindName("MachineStateText")).Text == "设备未连接"
                && ((TextBlock)overview.FindName("SourceText")).Text == "控制器状态未确认"
                && ((TextBlock)overview.FindName("AlertText")).Text == "未读取报警状态"
                && ((ItemsControl)overview.FindName("StationCards")).Visibility == Visibility.Collapsed,
                "控制器异常中断时仍显示已确认的运行或报警状态。");
            overview.SetRuntime(null); alarms.SetRuntime(null);
            Assert(((ItemsControl)overview.FindName("StationCards")).Items.Count == 0 && card.Calls == 0, "断开来源残留工位或页面调用了卡。");
        }
        /// <summary>暂停卡片不能继续显示运行，超过三工位仍完整呈现。</summary>
        private static void OverviewStates()
        {
            var snapshot = FourWorkspacesPreviewWindow.CreateExampleSnapshot(); snapshot.State = MachineRunState.Paused;
            for (int i = 3; i < 12; i++) snapshot.Stations.Add(new StationOverviewData { Name = "附加工位" + i, CurrentStep = "保留当前动作" });
            DateTime now = DateTime.UtcNow;
            snapshot.Alarms = new List<MachineAlarmRecord>
            {
                new MachineAlarmRecord(new MachineAlarmDefinition("STOP_LOAD", "上料等待超时", MachineAlarmKind.StopMachine), "上料定位", "DI等待超时", false, now, now),
                new MachineAlarmRecord(new MachineAlarmDefinition("STOP_PRESS", "压装压力异常", MachineAlarmKind.StopMachine), "压装", "压力未建立", true, now, now),
                new MachineAlarmRecord(new MachineAlarmDefinition("NOTICE_VISION", "视觉结果待确认", MachineAlarmKind.NonStop), "视觉检测", "结果待确认", true, now, now),
                new MachineAlarmRecord(new MachineAlarmDefinition("NOTICE_UNLOAD", "下料料框将满", MachineAlarmKind.NonStop), "下料", "料框余量低", true, now, now)
            };
            var view = new EquipmentOverviewView(); view.SetPreview(snapshot);
            Assert(((ItemsControl)view.FindName("StationCards")).Items.Count == 12, "卡片数量被固定为三工位。");
            string alarmSummary = ((TextBlock)view.FindName("AlertText")).Text;
            Assert(alarmSummary.Contains("当前报警 4 条（停机 2，非停机 2）")
                && alarmSummary.Contains("另有 1 条报警"), "多工位总览隐藏了报警总数或停机/提醒分类。");
            Render(view, "overview-multi-alarm-1200", 1200, 780);
            Assert(((TextBlock)view.FindName("MachineStateText")).Text == "设备已暂停", "整机暂停标签错误。");
            Assert(!FindControls<TextBlock>(view).Any(text => text.Text == "整机状态 · 设备运行中"), "暂停后卡片仍标为运行。");
            Assert(FindControls<TextBlock>(view).Any(text => text.Text == "暂停保留步骤"), "暂停卡片没有说明当前步骤已保留。");
            Assert(FindControls<TextBlock>(view).Any(text => text.Text.Contains("40 XY")), "暂停丢失原动作文字。");
            snapshot.State = MachineRunState.ResetRequired;
            view.RefreshDisplay();
            Assert(FindControls<TextBlock>(view).Any(text => text.Text == "待启动步骤"), "等待复位时仍把待执行步骤显示为正在动作。");
            snapshot.State = MachineRunState.Paused;
            view.RefreshDisplay();
            view.SetLanguage("en-US"); Assert(((TextBlock)view.FindName("SourceText")).Text == "Offline UI example", "英文来源标签未切换。");
            Assert(FindControls<TextBlock>(view).Any(text => text.Text == "Step retained during pause"), "English paused-step label did not localize.");
            bool rejected = false; try { view.SetPreview(new OperationSnapshot()); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "未标记的快照可伪装成预览。");
        }
        /// <summary>验证顶部启动拦截和整机状态在总览卡片与流程状态页中保持一致。</summary>
        private static void StartReadinessConsistency()
        {
            const string reason = "逻辑轴 'Unload_X' 尚未回零；工位 '下料工位' 启动前需要先完成回零。";
            var snapshot = FourWorkspacesPreviewWindow.CreateExampleSnapshot();
            snapshot.State = MachineRunState.Ready;
            var overview = new EquipmentOverviewView();
            overview.SetPreview(snapshot);
            overview.SetAutomaticStartReadiness(reason, false);
            Assert(((TextBlock)overview.FindName("MachineStateText")).Text == "启动条件未满足", "总览状态没有显示启动拦截。");
            Assert(((TextBlock)overview.FindName("NextActionText")).Text == reason, "总览没有显示准确的启动拦截原因。");
            Assert(FindControls<TextBlock>(overview).Any(text => text.Text == "整机状态 · 启动条件未满足"), "工位卡片仍显示整机已就绪。");

            var runtime = CreateRuntime(new GuardCard(), new MachineAlarmService());
            var flowStatus = new FlowStatusView(runtime);
            flowStatus.SetAutomaticStartReadiness(reason, false);
            Assert(((TextBlock)flowStatus.FindName("MachineStateText")).Text == "整机状态：启动条件未满足", "流程页状态与总览不同步。");
            Assert(((TextBlock)flowStatus.FindName("StartReadinessText")).Text == reason, "流程页未显示启动拦截原因。");
            flowStatus.SetAutomaticStartReadiness(null, true);
            Assert(((TextBlock)flowStatus.FindName("MachineStateText")).Text == "整机状态：检查启动条件", "流程页没有显示启动条件检查中状态。");
            flowStatus.MarkControllerConnectionLost();
            Assert(((TextBlock)flowStatus.FindName("MachineStateText")).Text == "整机状态：未连接"
                && ((Panel)flowStatus.FindName("StationRowsPanel")).Visibility == Visibility.Collapsed
                && ((TextBlock)flowStatus.FindName("AlarmText")).Text.Contains("通讯已中断"),
                "控制器断开后流程页仍把旧步骤或无报警显示为当前状态。");
        }
        /// <summary>任务控制入口不能把保留的 C# 工位当成任务步骤。</summary>
        private static void TaskOwnerDisplay()
        {
            var card = new GuardCard();
            var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[] { new ReadOnlyFlow("旧流程工位") }, new MachineAlarmService(), new GuardSafety(),
                controlOwner: MachineControlOwner.TaskEngine);
            bool loaded = false;
            var snapshot = OperationSnapshot.Read(runtime);
            Assert(snapshot.ControlOwner == MachineControlOwner.TaskEngine && snapshot.Stations.Count == 0,
                "任务入口快照包含未运行的 C# 工位。");
            var overview = new EquipmentOverviewView();
            overview.SetRuntime(runtime); overview.SetConnectionStatus(true, null);
            overview.SetTaskSessionStateProvider(() => loaded);
            Assert(((Border)overview.FindName("TaskRunPanel")).Visibility == Visibility.Visible
                && ((TextBlock)overview.FindName("TaskRunStatusText")).Text.Contains("尚未读取")
                && ((ItemsControl)overview.FindName("StationCards")).Items.Count == 0
                && ((FrameworkElement)overview.FindName("AggregateMetrics")).Visibility == Visibility.Collapsed,
                "总览未正确显示任务未读取，或显示了其他入口工位及统计。");
            OperatorNavigationEventArgs navigation = null;
            overview.NavigationRequested += (sender, e) => navigation = e;
            FindButton(overview, "查看工位运行").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(navigation != null && navigation.Page == "工位运行", "总览没有导航到统一工位运行页。");
            var flow = new FlowStatusView(runtime, () => loaded);
            Assert(((Panel)flow.FindName("StationRowsPanel")).Visibility == Visibility.Collapsed
                && ((Panel)flow.FindName("TaskRunPanel")).Visibility == Visibility.Visible
                && ((TextBlock)flow.FindName("TaskRunStatusText")).Text.Contains("尚未读取")
                && !flow.FocusStationByName("旧流程工位"), "流程状态显示了旧工位，或错误显示任务状态。");
            navigation = null; flow.NavigationRequested += (sender, e) => navigation = e;
            FindButton(flow, "查看任务运行").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(navigation != null && navigation.Page == "任务接入状态", "流程状态没有导航到任务运行页。");
            loaded = true; overview.RefreshDisplay(); flow.RefreshLocalizedText();
            Assert(((TextBlock)overview.FindName("TaskRunStatusText")).Text.Contains("已读取")
                && ((TextBlock)flow.FindName("TaskRunStatusText")).Text.Contains("已读取"), "已读取会话状态未更新。");
            overview.SetConnectionStatus(false, "连接中断"); flow.MarkControllerConnectionLost();
            Assert(((Border)overview.FindName("TaskRunPanel")).Visibility == Visibility.Collapsed
                && ((Panel)flow.FindName("TaskRunPanel")).Visibility == Visibility.Collapsed
                && ((Panel)flow.FindName("StationRowsPanel")).Visibility == Visibility.Collapsed,
                "断开后显示了过时的任务或工位步骤。");
            Assert(card.Calls == 0, "观察和导航意外调用了控制卡。");
            var manualRuntime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[] { new ReadOnlyFlow("已配置但未启用") }, new MachineAlarmService(), new GuardSafety(),
                controlOwner: MachineControlOwner.None);
            var manualOverview = new EquipmentOverviewView(); manualOverview.SetRuntime(manualRuntime);
            Assert(((TextBlock)manualOverview.FindName("EmptyText")).Text.Contains("未启用自动运行"),
                "仅手动入口错误断言未配置工位。");
            var manualFlow = new FlowStatusView(manualRuntime);
            Assert(((TextBlock)manualFlow.FindName("NoAutomaticRunText")).Visibility == Visibility.Visible
                && ((Panel)manualFlow.FindName("StationRowsPanel")).Visibility == Visibility.Collapsed,
                "仅手动入口没有说明或显示了未启用的工位。");
            manualFlow.MarkControllerConnectionLost();
            Assert(((TextBlock)manualFlow.FindName("NoAutomaticRunText")).Visibility == Visibility.Collapsed,
                "断线时错误宣称已确认自动运行模式。");
        }
        /// <summary>界面只观察报警服务，刷新不清除锁存，状态变化仍选择同一次报警。</summary>
        private static void AlarmConditions()
        {
            var service = new MachineAlarmService();
            var stop = new MachineAlarmDefinition("STOP", "测试停机报警", MachineAlarmKind.StopMachine);
            var notice = new MachineAlarmDefinition("NOTICE", "测试非停机提醒", MachineAlarmKind.NonStop);
            service.ReportCondition(stop, "测试工位", true, "先检查测试输入");
            service.ReportCondition(notice, "另一个工位", true, "等待人工复核");
            var snapshot = new OperationSnapshot { IsPreview = true, HasRuntime = true, State = MachineRunState.Alarmed, SampledAt = DateTime.Now, Alarms = service.Alarms.ToList() };
            snapshot.Stations.Add(new StationOverviewData { Name = "测试工位", CurrentStep = "20 等待输入" });
            snapshot.Stations.Add(new StationOverviewData { Name = "另一个工位", CurrentStep = "30 等待复核" });
            var view = new CurrentAlarmsView(); view.SetPreview(snapshot);
            var grid = (DataGrid)view.FindName("AlarmGrid"); var first = (AlarmDisplayRow)grid.SelectedItem;
            Assert(((TextBlock)view.FindName("CountText")).Text == "当前报警 2 条（停机 1，非停机 1）", "报警页没有区分停机报警和非停机提醒数量。");
            service.ReportCondition(stop, "测试工位", false, null); snapshot.Alarms = service.Alarms.ToList(); view.RefreshDisplay();
            var recovered = (AlarmDisplayRow)grid.SelectedItem;
            Assert(first.Key == recovered.Key && recovered.ConditionText.Contains("等待复位"), "条件恢复后丢失选择或不显示待复位。");
            FindButton(view, "刷新").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(service.HasLatchedStopAlarm, "刷新居然清除了停机报警。");
            ((TextBox)view.FindName("SearchBox")).Text = "stop";
            Assert(grid.Items.Count == 1, "代码搜索未按大小写无关匹配。");
            ((TextBox)view.FindName("SearchBox")).Text = "没有的内容";
            Assert(grid.Items.Count == 0 && !((Button)view.FindName("AxisButton")).IsEnabled, "空搜索结果仍有可操作报警。");
            ((TextBox)view.FindName("SearchBox")).Text = ""; view.SetLanguage("en-US");
            Assert(((AlarmDisplayRow)grid.SelectedItem).Key == first.Key && ((AlarmDisplayRow)grid.SelectedItem).ConditionText.Contains("reset"), "语言切换丢失选择或待复位语义。");
            OperatorNavigationEventArgs requested = null; view.NavigationRequested += (sender, e) => requested = e;
            ((Button)view.FindName("IoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(requested != null && requested.Page == "IO监控" && service.HasLatchedStopAlarm, "只读导航改变了报警状态。");
        }
        /// <summary>验证 Host 导航不重复 IO 监控入口，并保留原内容区导航。</summary>
        private static void HostNavigation()
        {
            var card = new GuardCard();
            var alarmService = new MachineAlarmService();
            var flow = new ReadOnlyInputFlow();
            var alarm = new MachineAlarmDefinition("CLAMP_TIMEOUT", "等待夹紧到位超时", MachineAlarmKind.StopMachine,
                "检查夹紧机构和到位传感器。", "输入恢复后，按设备安全规程整机复位再启动。");
            alarmService.ReportCondition(alarm, flow.StationName, true, "当前步骤等待输入：" + flow.WaitingInputName);
            var visionFlow = new ReadOnlyFlow("视觉检测工位");
            var notice = new MachineAlarmDefinition("VISION_REVIEW", "视觉结果待复核", MachineAlarmKind.NonStop);
            alarmService.ReportCondition(notice, visionFlow.StationName, true, "视觉结果等待操作员复核");
            var runtime = CreateRuntime(card, alarmService, flow, visionFlow);
            var window = new MainWindow();
            try
            {
                var primaryNavigation = (StackPanel)window.FindName("PrimaryNavigation");
                var sidebar = (StackPanel)window.FindName("SidebarNavigation");
                Assert(!sidebar.Children.OfType<ToggleButton>().Any(button => Equals(button.Tag, "IO状态")),
                    "首页重复列出了独立的 IO 状态页。");
                var motionControlButton = primaryNavigation.Children.OfType<ToggleButton>()
                    .Single(button => Equals(button.Tag, "MotionControl"));
                motionControlButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert(sidebar.Children.OfType<ToggleButton>().Count(button => Equals(button.Tag, "IO监控")) == 1,
                    "运动控制区没有唯一的 IO 监控入口。");

                var flowStatus = new FlowStatusView(runtime);
                string crossPageAlarmSummary = "当前报警 2 条（停机 1，非停机 1）";
                Assert(((TextBlock)flowStatus.FindName("AlarmText")).Text.StartsWith(crossPageAlarmSummary),
                    "流程状态没有展示停机与提醒的分类汇总。");
                var ioMonitor = new UserControl();
                window.SetFlowStatusPage(flowStatus);
                window.SetIoMonitorPage(ioMonitor);
                window.SetMachineRuntime(runtime);
                var footerSummary = (TextBlock)window.FindName("ImportantExceptionSummary");
                Assert(footerSummary.Text.Contains("报警 2") && footerSummary.Text.Contains("控制器未连接"),
                    "非报警页面的底栏没有同时显示报警数量和断连状态。");
                alarmService.ReportCondition(alarm, flow.StationName, false, null);
                window.SetMachineRuntime(runtime);
                Assert(footerSummary.Text.Contains("报警 2/待复位 1") && alarmService.HasLatchedStopAlarm,
                    "报警条件恢复后，底栏没有保留待复位提示。");
                alarmService.ReportCondition(alarm, flow.StationName, true, "当前步骤等待输入：" + flow.WaitingInputName);
                window.SetMachineRuntime(runtime);
                var content = (ContentControl)window.FindName("ManualDebugContent");
                var original = content.Content as EquipmentOverviewView;
                Assert(original != null, "首页仍是占位。");
                FindButton(original, "查看流程").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(content.Content is FlowStatusView, "卡片没有定位实际流程页。");
                ((Button)window.FindName("OperationReturnButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(ReferenceEquals(content.Content, original), "返回重建了原总览。");
                FindButton(original, "查看当前报警").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var alarms = content.Content as CurrentAlarmsView;
                Assert(alarms != null && card.Calls == 0, "报警导航失败或触发了控制卡操作。");
                Assert(((TextBlock)alarms.FindName("CountText")).Text == crossPageAlarmSummary,
                    "从总览进入当前报警后，报警分类数量与流程状态页不一致。");

                var alarmGrid = (DataGrid)alarms.FindName("AlarmGrid");
                string selectedAlarmKey = ((AlarmDisplayRow)alarmGrid.SelectedItem).Key;
                var locateFlow = FindButton(alarms, "定位工位流程");
                Assert(locateFlow.IsEnabled, "工位报警未提供定位对应流程的入口。");
                locateFlow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(ReferenceEquals(content.Content, flowStatus), "当前报警没有打开共享的流程状态页面。");
                var rows = (StackPanel)flowStatus.FindName("StationRowsPanel");
                var stationRow = (StackPanel)rows.Children[0];
                var stepText = stationRow.Children.OfType<TextBlock>().Last();
                Assert(((SolidColorBrush)stepText.Background).Color == Color.FromRgb(230, 241, 255),
                    "报警导航没有高亮来源工位。");

                ((Button)window.FindName("OperationReturnButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(ReferenceEquals(content.Content, alarms)
                    && ((AlarmDisplayRow)alarmGrid.SelectedItem).Key == selectedAlarmKey,
                    "从流程返回后没有恢复同一报警选择。");
                locateFlow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(ReferenceEquals(content.Content, flowStatus), "再次从报警定位工位流程失败。");

                OperatorNavigationEventArgs inputNavigation = null;
                flowStatus.NavigationRequested += (sender, args) => inputNavigation = args;
                FindButton(flowStatus, "核对输入").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(ReferenceEquals(content.Content, ioMonitor)
                    && inputNavigation != null
                    && inputNavigation.Page == "IO监控"
                    && inputNavigation.StationName == flow.StationName
                    && inputNavigation.IoPointName == flow.WaitingInputName,
                    "工位输入入口没有带着报警来源工位和逻辑 DI 进入 IO 监控。");
                ((Button)window.FindName("OperationReturnButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(ReferenceEquals(content.Content, flowStatus), "从 IO 监控没有返回原工位流程。");
                FindButton(flowStatus, "查看当前报警").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(ReferenceEquals(content.Content, alarms)
                    && ((AlarmDisplayRow)alarmGrid.SelectedItem).Key == selectedAlarmKey
                    && card.Calls == 0,
                    "处理路径没有保留报警选择，或导航过程调用了控制卡。");

                var taskConfiguration = new UserControl();
                window.SetTaskConfigurationPage(taskConfiguration);
                motionControlButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var taskConfigurationButton = sidebar.Children.OfType<ToggleButton>()
                    .Single(button => Equals(button.Tag, "任务配置"));
                taskConfigurationButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert(ReferenceEquals(content.Content, taskConfiguration), "任务配置导航没有显示当前任务页。");
                window.ShowFlowStatus();
                Assert(ReferenceEquals(content.Content, flowStatus)
                    && ((Button)window.FindName("OperationReturnButton")).Visibility == Visibility.Visible,
                    "从任务配置查看流程后没有显示返回入口。");
                ((Button)window.FindName("OperationReturnButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(ReferenceEquals(content.Content, taskConfiguration)
                    && Equals(((TextBlock)window.FindName("PageTitle")).Text, "任务配置")
                    && ((Button)window.FindName("OperationReturnButton")).Visibility == Visibility.Collapsed,
                    "从流程状态没有返回原任务配置页，或遗留了过期返回入口。");
                var taskRun = new UserControl();
                window.SetTaskMonitorPage(taskRun);
                window.ShowTaskRun();
                Assert(ReferenceEquals(content.Content, flowStatus)
                    && ((Button)window.FindName("OperationReturnButton")).Visibility == Visibility.Visible,
                    "C# 模式没有复用工位运行页，或缺少返回入口。");
                ((Button)window.FindName("OperationReturnButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(ReferenceEquals(content.Content, taskConfiguration), "任务运行没有返回原任务配置页。");
                motionControlButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert(((Button)window.FindName("OperationReturnButton")).Visibility == Visibility.Collapsed,
                    "普通主导航没有清除上一次操作路径的返回入口。");
            }
            finally { window.Close(); }
        }
        /// <summary>检查暂停与停止的提示分别说明工步保留和停止后的核查门禁。</summary>
        private static void MachineCommandGuidance()
        {
            var chinese = new ResourceDictionary { Source = new Uri("/MotionControl.Host.Wpf;component/Localization/Strings.zh-CN.xaml", UriKind.Relative) };
            var pause = (string)chinese["自动暂停提示"];
            var stop = (string)chinese["自动停止提示"];
            Assert(pause.Contains("受控暂停") && pause.Contains("保留当前工步")
                && pause.Contains("报警复位") && pause.Contains("启动"), "中文暂停提示没有说明保留工步和恢复顺序。");
            Assert(stop.Contains("停止自动生产") && stop.Contains("保留工步和输出")
                && stop.Contains("核查现场") && stop.Contains("报警复位"), "中文停止提示没有说明现场保留及核查要求。");
            Assert((string)chinese["顶部停止待核查"] == "待核查", "顶部停止按钮没有明确显示待核查。");

            var english = new ResourceDictionary { Source = new Uri("/MotionControl.Host.Wpf;component/Localization/Strings.en-US.xaml", UriKind.Relative) };
            var englishPause = (string)english["自动暂停提示"];
            var englishStop = (string)english["自动停止提示"];
            Assert(englishPause.Contains("controlled manner") && englishPause.Contains("retain")
                && englishPause.Contains("Reset alarms") && englishPause.Contains("Start"), "English pause guidance omits step retention or recovery order.");
            Assert(englishStop.Contains("Stop automatic production") && englishStop.Contains("retaining")
                && englishStop.Contains("Review") && englishStop.Contains("alarm reset"), "English stop guidance omits retained context or equipment review.");
            Assert((string)english["顶部停止待核查"] == "Review", "The stop button does not indicate the review requirement in English.");
        }
        /// <summary>版本与草稿来自实际文件，多轴表只显示选中轴参数，切换后逐轴保存。</summary>
        private static void EditorIntegration()
        {
            using (var test = new PreviewFiles())
            {
                var page = new TaskConfigurationView(test.Configuration, "TaskEditorPreview", () => null,
                    () => "等待复位", () => new[] { "正式上料定位工位", "正式压装工位" }, () => "逻辑轴 Load_X 尚未回零。");
                string runtimeStations = ((TextBlock)page.FindName("RuntimeStationsText")).Text;
                string draftStations = ((TextBlock)page.FindName("TaskDraftStationsText")).Text;
                string draftBoundary = ((TextBlock)page.FindName("TaskDraftBoundaryText")).Text;
                Assert(runtimeStations.Contains("正式上料定位工位") && runtimeStations.Contains("正式压装工位"), "任务配置页没有显示正式 Runtime 工位清单。 ");
                Assert(((Button)page.FindName("CurrentFlowButton")).IsEnabled
                    && (string)((Button)page.FindName("CurrentFlowButton")).Content == "查看当前流程状态", "代码流程模式的导航发生变化。");
                var taskPage = new TaskConfigurationView(test.Configuration, "TaskEditorPreview", () => null,
                    () => "等待复位", () => { throw new InvalidOperationException("任务列表模式不应读取 C# 工位"); }, null, () => true);
                var taskStationText = (TextBlock)taskPage.FindName("RuntimeStationsText");
                var taskFlowButton = (Button)taskPage.FindName("CurrentFlowButton");
                Assert(taskStationText.Text.Contains("任务运行") && taskFlowButton.IsEnabled
                    && (string)taskFlowButton.Content == "查看任务运行", "任务列表模式没有引导查看实际任务。");
                bool navigationRequested = false;
                taskPage.CurrentFlowRequested += (sender, args) => navigationRequested = true;
                taskFlowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(navigationRequested, "任务运行入口没有请求导航。");
                taskPage.SetLanguage("en-US");
                Assert(taskStationText.Text.Contains("loaded tasks and stations")
                    && (string)taskFlowButton.Content == "View task operation", "任务列表模式的英文指引不正确。");
                Assert(draftStations.Contains("参与 3/3 个") && draftStations.Contains("上料工位 → 上料任务")
                    && draftStations.Contains("搬运工位 → 搬运任务") && draftStations.Contains("检测工位 → 检测任务"),
                    "任务草稿的工位、任务分配或参与数量没有显示完整。");
                Assert(draftBoundary.Contains("只影响草稿") && draftBoundary.Contains("不改变当前设备流程")
                    && draftBoundary.Contains("重新读取任务"),
                    "任务草稿与正式设备流程的运行边界没有说明清楚。");
                var library = (DataGrid)page.FindName("TasksGrid");
                Assert(library.Items.Cast<TaskLibraryRow>().All(row => row.AppliedVersion == "R1" && row.DraftState == "无修改"), "文件版本或无修改状态错误。");
                Render(page, "station-row-navigation", 1200, 780);
                var stations = (DataGrid)page.FindName("StationsGrid");
                var cell = stations.Columns[5].GetCellContent(stations.Items[1]);
                var edit = (Button)VisualTreeHelper.GetChild(cell, 0);
                edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var editor = (TaskEditorView)((ContentControl)page.FindName("ContentHost")).Content;
                Assert(editor.Definition.TaskId == "Transfer", "工位行内按钮没有打开其指定任务。");
                ((TextBox)editor.FindName("TaskNameBox")).Text = "未保存的搬运任务";
                Assert(page.OpenTaskById("Transfer") && ReferenceEquals(((ContentControl)page.FindName("ContentHost")).Content, editor), "再次定位同一任务重新创建了编辑器。");
                Assert(((TextBox)editor.FindName("TaskNameBox")).Text == "未保存的搬运任务", "再次定位同一任务丢失输入。");
                var group = AllNodes(editor.Definition.Root).OfType<MoveAxisGroupNode>().First(); editor.FocusNode(group.Id);
                var grid = FindControls<DataGrid>(editor).First(item => item.Columns.Count == 4 && (string)item.Columns[0].Header == "逻辑轴");
                Assert(grid.Items.Count == group.Targets.Count && grid.Items.Count >= 2, "多轴目标表没有完整读取轴。");
                grid.SelectedIndex = 1;
                var second = FindControls<Expander>(editor).Single(item => item.Visibility == Visibility.Visible);
                SpeedBox(second).Text = "7";
                grid.SelectedIndex = 0;
                var first = FindControls<Expander>(editor).Single(item => item.Visibility == Visibility.Visible);
                SpeedBox(first).Text = "5";
                Assert(editor.SaveDraft(), "多轴参数保存失败。");
                var saved = test.Store.Load("TaskEditorPreview");
                var savedGroup = AllNodes(saved.Tasks.Single(task => task.Definition.TaskId == "Transfer").Definition.Root).OfType<MoveAxisGroupNode>().First();
                Assert(savedGroup.Targets[0].Profile.Speed == 5 && savedGroup.Targets[1].Profile.Speed == 7, "切换轴后参数串行或丢失。");
                Assert(((TextBlock)editor.FindName("DirtyIndicator")).Visibility == Visibility.Collapsed, "保存后仍显示未保存标记。");
                Assert(page.ReturnToConfiguration(), "已保存任务无法返回配置页。");
                var libraryRow = library.Items.Cast<TaskLibraryRow>().Single(item => item.Definition.TaskId == "Transfer");
                Assert(libraryRow.AppliedVersion == "R1" && libraryRow.DraftState == "草稿待检查", "保存草稿冒充了已应用版本或已通过检查。");
                FindButton(page, "保存并检查").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(library.Items.Cast<TaskLibraryRow>().Single(item => item.Definition.TaskId == "Transfer").DraftState == "任务检查通过", "检查结果没有更新任务行。");
                var machineDetails = (StackPanel)page.FindName("MachineDetails");
                machineDetails.Children.OfType<TextBox>().First().Text += " 修改";
                Assert(library.Items.Cast<TaskLibraryRow>().All(item => item.DraftState != "任务检查通过"), "修改后仍保留旧检查通过状态。");
            }
            using (var test = new PreviewFiles())
            {
                var draft = test.Store.Load("TaskEditorPreview");
                var entry = draft.Tasks.First(item => AllNodes(item.Definition.Root).OfType<WaitInputNode>().Any());
                var wait = AllNodes(entry.Definition.Root).OfType<WaitInputNode>().First();
                entry.Definition.Root.TimeoutMs = wait.TimeoutMs;
                test.Store.SaveDraft(draft);
                var page = new TaskConfigurationView(test.Configuration, "TaskEditorPreview", () => null);
                FindButton(page, "保存并检查").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var library = (DataGrid)page.FindName("TasksGrid");
                var affected = library.Items.Cast<TaskLibraryRow>().Single(row => row.Definition.TaskId == entry.Definition.TaskId);
                Assert(affected.DraftState == "任务检查有提醒", "任务存在等待超时提醒时仍显示检查通过。");
                var issues = (DataGrid)page.FindName("IssuesGrid");
                Assert(issues.Visibility == Visibility.Visible && issues.Items.Count > 0,
                    "任务配置页没有展示可定位的检查提醒。");
            }
        }
        /// <summary>只按工程字段标签找到当前轴的速度框，不反射读取私有状态。</summary>
        private static TextBox SpeedBox(Expander expander)
        {
            foreach (var panel in FindControls<StackPanel>(expander))
                for (int i = 0; i + 1 < panel.Children.Count; i++)
                    if (panel.Children[i] is TextBlock label && label.Text == "速度") return (TextBox)panel.Children[i + 1];
            throw new InvalidOperationException("当前轴速度框未找到。");
        }
        /// <summary>两种语言和尺寸导出布局补充图，图片明确不是桌面截图。</summary>
        private static void RenderWorkspaces()
        {
            using (var test = new PreviewFiles())
            {
                foreach (string language in new[] { "zh-CN", "en-US" })
                {
                    var overview = new EquipmentOverviewView(); overview.SetPreview(FourWorkspacesPreviewWindow.CreateExampleSnapshot()); overview.SetLanguage(language);
                    var alarms = new CurrentAlarmsView(); alarms.SetPreview(FourWorkspacesPreviewWindow.CreateExampleSnapshot()); alarms.SetLanguage(language);
                    var preparation = new EquipmentPreparationView(null, null); preparation.SetLanguage(language);
                    var configuration = new TaskConfigurationView(test.Configuration, "TaskEditorPreview", () => null,
                        () => "等待复位", () => new[] { "正式上料定位工位", "正式压装工位" }, () => "逻辑轴 Load_X 尚未回零。"); configuration.SetLanguage(language);
                    string draftStations = ((TextBlock)configuration.FindName("TaskDraftStationsText")).Text;
                    string draftBoundary = ((TextBlock)configuration.FindName("TaskDraftBoundaryText")).Text;
                    Assert(draftStations.Contains(language == "zh-CN" ? "参与 3/3 个" : "Enabled 3/3"), "任务配置页工位汇总没有随语言切换。");
                    Assert(language == "zh-CN"
                        ? draftBoundary.Contains("只影响草稿") && draftBoundary.Contains("重新读取任务")
                        : draftBoundary.Contains("only the draft") && draftBoundary.Contains("Reload the task"),
                        "任务配置页未用当前语言说明草稿边界和重新读取任务的要求。");
                    foreach (int width in new[] { 1200, 824 })
                    {
                        int height = width == 1200 ? 780 : 550;
                        Render(overview, "overview-" + language + "-" + width, width, height);
                        Render(alarms, "alarms-" + language + "-" + width, width, height);
                        Render(preparation, "preparation-" + language + "-" + width, width, height);
                        Render(configuration, "configuration-" + language + "-" + width, width, height);
                    }
                    Assert(configuration.OpenTaskById("Transfer"), "编辑预览入口失败。");
                    var editor = (TaskEditorView)((ContentControl)configuration.FindName("ContentHost")).Content;
                    editor.FocusNode(AllNodes(editor.Definition.Root).OfType<MoveAxisGroupNode>().First().Id);
                    Render(configuration, "editor-" + language + "-1200", 1200, 780);
                    Render(configuration, "editor-" + language + "-824", 824, 550);
                    Assert(!editor.IsDirty, "只显示多轴目标就误标未保存。");
                }
            }
        }
        /// <summary>离屏排空布局后渲染，不能作为实际鼠标和焦点验收。</summary>
        private static void Render(FrameworkElement view, string name, int width, int height)
        {
            view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            var frame = new System.Windows.Threading.DispatcherFrame();
            view.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Previews"); Directory.CreateDirectory(directory);
            using (var file = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(file);
        }
        /// <summary>按实际控件内容查找按钮。</summary>
        private static Button FindButton(DependencyObject root, string text)
        { return FindControls<Button>(root).First(button => button.Content as string == text); }
        /// <summary>递归检查具体控件，不访问私有字段或执行动态脚本。</summary>
        private static IEnumerable<T> FindControls<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T control) yield return control;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject node) foreach (var item in FindControls<T>(node)) yield return item;
        }
        /// <summary>沿已定义的容器关系读取节点。</summary>
        private static IEnumerable<TaskNode> AllNodes(TaskNode node)
        {
            yield return node;
            IEnumerable<TaskNode> children = new TaskNode[0];
            if (node is SequenceNode sequence) children = sequence.Children;
            else if (node is ParallelGroupNode parallel) children = parallel.Branches;
            else if (node is ResourceScopeNode scope) children = new[] { scope.Body };
            else if (node is ConditionBranchNode branch) children = new[] { branch.WhenTrue, branch.WhenFalse };
            foreach (var child in children.Where(item => item != null)) foreach (var item in AllNodes(child)) yield return item;
        }
        /// <summary>Runtime 仅供观察，卡和安全入口都带调用保护。</summary>
        private static MachineRuntime CreateRuntime(GuardCard card, MachineAlarmService service)
        { return CreateRuntime(card, service, new ReadOnlyFlow()); }
        /// <summary>只读页面检查可以注入指定工位，以验证报警来源与 IO 定位链。</summary>
        private static MachineRuntime CreateRuntime(GuardCard card, MachineAlarmService service, params IStationFlow[] flows)
        { return new MachineRuntime(new MotionCardGroup(new IMotionCard[] { card }), flows, service, new GuardSafety()); }
    }

    /// <summary>测试观察页不能访问的卡入口；任何调用都会失败并计数。</summary>
    internal sealed class GuardCard : IMotionCard
    {
        /// <summary>记录所有卡生命周期调用。</summary>
        public int Calls { get; private set; }
        /// <summary>检查专用卡标识。</summary>
        public string ControllerId { get { return "ReadOnlyCheck"; } }
        /// <summary>不建立连接。</summary>
        public bool IsConnected { get { return false; } }
        /// <summary>无需任何实轴。</summary>
        public IReadOnlyCollection<IAxis> Axes { get { return new IAxis[0]; } }
        /// <summary>页面若意外连接立即失败。</summary>
        public Task ConnectAsync(CancellationToken token) { Calls++; throw new InvalidOperationException("页面禁止连接检查卡。"); }
        /// <summary>页面若意外断开立即失败。</summary>
        public Task DisconnectAsync(CancellationToken token) { Calls++; throw new InvalidOperationException("页面禁止断开检查卡。"); }
    }
    /// <summary>只读检查不应启动安全输入轮询。</summary>
    internal sealed class GuardSafety : IMachineSafetyStatus
    {
        /// <summary>意外读取也会令检查失败。</summary>
        public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken token) { throw new InvalidOperationException("只读页面不应读取安全硬件。"); }
    }
    /// <summary>有明确名称和当前动作，但绝不执行的测试工位。</summary>
    internal sealed class ReadOnlyFlow : IStationFlow
    {
        /// <summary>用于本次多工位检查或默认测试流程的稳定名称。</summary>
        private readonly string _stationName;
        /// <summary>支持在同一个 Runtime 中建立多个不同名称的只读工位。</summary>
        internal ReadOnlyFlow(string stationName = "测试工位") { _stationName = stationName; }
        /// <summary>用于报警和导航的稳定名称。</summary>
        public string StationName { get { return _stationName; } }
        /// <summary>当前动作原文。</summary>
        public string CurrentStepName { get { return "20 等待测试输入"; } }
        /// <summary>页面禁止运行流程。</summary>
        public Task ExecuteCurrentStepAsync(CancellationToken token) { throw new InvalidOperationException("页面不应执行流程。"); }
        /// <summary>页面禁止复位流程。</summary>
        public Task ResetAsync(CancellationToken token) { throw new InvalidOperationException("页面不应复位流程。"); }
    }
    /// <summary>提供稳定工位名和等待 DI 的只读工位，供报警到信号定位路径检查。</summary>
    internal sealed class ReadOnlyInputFlow : IStationFlow, IStationInputWaitState
    {
        /// <summary>报警来源和总览、流程状态列表共同使用的工位名称。</summary>
        public string StationName { get { return "测试工位"; } }
        /// <summary>明确显示当前正在等待夹紧输入。</summary>
        public string CurrentStepName { get { return "20 - 等待夹紧到位"; } }
        /// <summary>供流程状态页直接定位的逻辑 DI 名称。</summary>
        public string WaitingInputName { get { return "夹紧到位"; } }
        /// <summary>只读导航测试不得运行工艺步骤。</summary>
        public Task ExecuteCurrentStepAsync(CancellationToken token) { throw new InvalidOperationException("页面不应执行流程。"); }
        /// <summary>只读导航测试不得复位工艺步骤。</summary>
        public Task ResetAsync(CancellationToken token) { throw new InvalidOperationException("页面不应复位流程。"); }
    }
    /// <summary>本轮检查使用独立临时配置目录。</summary>
    internal sealed class PreviewFiles : IDisposable
    {
        /// <summary>当前检查绝对目录。</summary>
        private readonly string _root;
        /// <summary>实际 JSON 存储。</summary>
        internal MotionControl.Framework.Runtime.Configuration.MachineConfigurationStore Configuration { get; private set; }
        /// <summary>实际草稿服务。</summary>
        internal TaskWorkspaceStore Store { get; private set; }
        /// <summary>复制现有内存示例定义，不连接任何控制器。</summary>
        internal PreviewFiles()
        { _root = Path.Combine(Path.GetTempPath(), "MotionControlOperatorChecks", Guid.NewGuid().ToString("N")); Configuration = TaskEditingPreviewWindow.CreatePreviewStore(_root); Store = new TaskWorkspaceStore(Configuration); }
        /// <summary>递归清理前确认路径处于本测试专用父目录内。</summary>
        public void Dispose()
        {
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MotionControlOperatorChecks")) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(_root);
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || target.Length <= parent.Length) throw new InvalidOperationException("测试清理路径超出专用目录。");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
