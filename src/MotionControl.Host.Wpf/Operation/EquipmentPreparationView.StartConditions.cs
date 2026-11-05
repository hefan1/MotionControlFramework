using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Host.Wpf.Operation
{
    public partial class EquipmentPreparationView
    {
        /// <summary>只读条件行；稳定键和对象标识不随语言变化。</summary>
        internal sealed class StartConditionObservation : INotifyPropertyChanged
        {
            /// <summary>条件的稳定机器键。</summary>
            public string Key { get; set; }
            /// <summary>已知关联工位标识；没有明确对象时为空。</summary>
            public string ObjectId { get; set; }
            /// <summary>当前语言的结论与来源说明。</summary>
            private string _description;
            /// <summary>条件变化时仅通知当前行，不重建列表。</summary>
            public string Description { get { return _description; } set { if (_description == value) return; _description = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("DisplayText")); } }
            /// <summary>面向操作员的双语条件名称。</summary>
            public string Name { get; set; }
            /// <summary>产生此观察的实际内存来源。</summary>
            public string Source { get; set; }
            /// <summary>列表文字；内部稳定键不显示。</summary>
            public string DisplayText { get { return Name + (string.IsNullOrEmpty(ObjectId) ? "" : " [" + ObjectId + "]") + " · " + Description + " / " + Source; } }
            /// <summary>当前行内容变化通知。</summary>
            public event PropertyChangedEventHandler PropertyChanged;
            /// <summary>列表显示稳定键、对象与来源，不隐藏能力缺失。</summary>
            public override string ToString() { return DisplayText; }
        }

        /// <summary>稳定行集合保证选择、焦点和滚动不因周期刷新重置。</summary>
        private readonly ObservableCollection<StartConditionObservation> _conditionObservations = new ObservableCollection<StartConditionObservation>();
        private const string StopAlarmKey = "runtime.stop-alarms";
        private MachineRuntime _conditionRuntime;
        private Guid? _conditionSession;
        private long _conditionSourceVersion;
        private long _conditionPreparationVersion;
        /// <summary>仅重新投影已有内存，不调用启动检查、配置装载或设备接口。</summary>
        private void RefreshStartConditions_Click(object sender, RoutedEventArgs e) { RefreshStartConditionsSnapshot(); }

        /// <summary>将稳定条件键转为清楚的操作员名称。</summary>
        private string ConditionName(string key)
        {
            if (key.StartsWith("preparation/")) return PageText("工位准备", "Station preparation");
            switch (key)
            {
                case "runtime.connection": return PageText("控制器连接", "Controller connection");
                case StopAlarmKey: return PageText("停机报警", "Stop alarms");
                case "runtime.recovery": return PageText("恢复确认", "Recovery confirmation");
                case "runtime.production-stop-review": return PageText("生产停止核查", "Production stop review");
                case "preparation.collection": return PageText("工位准备范围", "Preparation coverage");
                case "host.start-readiness": return PageText("现有启动检查摘要", "Existing start check summary");
                default: return PageText("摘要读取", "Summary observation");
            }
        }
        /// <summary>根据条件键列出其实际观察来源，不把主界面缓存当作设备采样。</summary>
        private string ConditionSource(string key)
        {
            if (key.StartsWith("preparation/") || key == "preparation.collection") return PageText("工位准备快照", "Station preparation snapshot");
            switch (key)
            {
                case "runtime.connection": return PageText("连接状态缓存", "Connection state cache");
                case "runtime.recovery": return PageText("恢复状态内存", "Recovery state memory");
                case "runtime.production-stop-review": return PageText("停止核查状态内存", "Stop review memory");
                case StopAlarmKey: return PageText("设备运行时报警记录", "Runtime alarm records");
                case "host.start-readiness": return PageText("主界面检查缓存", "Host check cache");
                default: return PageText("摘要读取失败", "Summary read failed");
            }
        }

        /// <summary>只观察报警服务的一次记录列表；活动和条件已恢复待复位是互斥类别。</summary>
        private StartConditionObservation ReadStopAlarmCondition(MachineRuntime runtime)
        {
            string description = PageText("未知：当前连接会话未确认", "Unknown: current connection session is unconfirmed");
            if (runtime != null)
            {
                try
                {
                    var stops = runtime.AlarmService.Alarms.Where(alarm => alarm.RequiresReset).ToArray();
                    int active = stops.Count(alarm => alarm.ConditionActive);
                    description = string.Format(PageText("活动 {0} 条，条件已恢复待复位 {1} 条",
                        "Active: {0}; recovered and awaiting reset: {1}"), active, stops.Length - active);
                }
                catch (Exception error)
                {
                    description = PageText("未知：停机报警记录读取失败，", "Unknown: stop alarm records could not be read. ")
                        + error.GetBaseException().Message;
                }
            }
            return new StartConditionObservation { Key = StopAlarmKey, Description = description };
        }

        /// <summary>生成部分观察快照，缺少原始采样时间与覆盖证明时保持明确未知。</summary>
        private void RefreshStartConditionsSnapshot()
        {
            if (StartConditionsList == null || StartConditionsContent.Visibility != Visibility.Visible) return;

            MachineRuntime runtime = _runtime;
            var observations = new List<StartConditionObservation>();
            string unknown = PageText("未知", "Unknown");
            Guid? session = runtime?.ConnectionSessionId;
            long sourceVersion = runtime?.SafetyObservationVersion ?? -1;
            long preparationVersion = runtime?.PreparationConfigurationVersion ?? -1;
            bool observedTaskPreparation = false;
            try
            {
                bool connected = runtime != null && runtime.IsConnectionSessionCurrent(session);
                observations.Add(new StartConditionObservation { Key = "runtime.connection", Description = (connected ? PageText("连接会话已建立；当前读写未核实", "Connection session established; current communication unverified") : unknown) });
                if (connected)
                {
                    observations.Add(new StartConditionObservation { Key = "runtime.recovery", Description = (string.IsNullOrWhiteSpace(runtime.RecoveryBlockReason) ? PageText("未报告恢复阻断（仅此属性）", "No recovery blocker reported (this property only)") : PageText("不满足：", "Unmet: ") + runtime.RecoveryBlockReason) });
                    observations.Add(new StartConditionObservation { Key = "runtime.production-stop-review", Description = (runtime.ProductionStopReviewRequired ? PageText("不满足：旧工步待核查", "Unmet: previous step requires review") : PageText("未报告停止核查要求（仅此属性）", "No stop review required (this property only)")) });
                    bool taskSource = runtime.ControlOwner == MachineControlOwner.TaskEngine;
                    bool published = !taskSource || _hasPublishedTaskSession?.Invoke(runtime, session) == true;
                    observedTaskPreparation = taskSource && published;
                    var stations = published ? runtime.PreparationStations : Array.Empty<MotionControl.Framework.Contracts.Preparation.StationPreparationSnapshot>();
                    foreach (var station in stations)
                        observations.Add(new StartConditionObservation { Key = "preparation/" + station.StationId, ObjectId = station.StationId, Description = station.StationName + " · " + (station.IsPrepared ? PageText("满足：工位准备", "Met: station preparation") : PageText("不满足：工位未准备", "Unmet: station not prepared")) + " · " + station.Action + " · " + station.Detail });
                    if (stations.Count == 0) observations.Add(new StartConditionObservation { Key = "preparation.collection", Description = unknown + PageText("：无工位准备快照", ": no station preparation snapshots") });
                    observations.Add(new StartConditionObservation { Key = "host.start-readiness", Description = (_startReadinessChecking ? unknown + PageText("：Host 检查中", ": Host checking") : !string.IsNullOrWhiteSpace(_startBlockReason) ? PageText("不满足：", "Unmet: ") + _startBlockReason : unknown + PageText("：Host 未提供已完成结论", ": Host has not supplied a completed conclusion")) });
                }
                observations.Add(ReadStopAlarmCondition(connected ? runtime : null));
                if (!ReferenceEquals(runtime, _runtime)) return;
                if (connected && ((observedTaskPreparation && _hasPublishedTaskSession?.Invoke(runtime, session) != true)
                    || !runtime.IsConnectionSessionCurrent(session) || sourceVersion < 0
                    || runtime.SafetyObservationVersion != sourceVersion || runtime.PreparationConfigurationVersion != preparationVersion))
                {
                    observations.RemoveRange(1, observations.Count - 1);
                    observations[0].Description = unknown;
                    observations.Add(ReadStopAlarmCondition(null));
                    connected = false;
                }
                if (!connected) session = null;
                StartConditionsMessage.Text = PageText("部分只读内存观察 / 覆盖不完整，不能判断整体可启动。每项设备采样时间、有效期未提供；下方时间仅为界面观察时间。", "Partial read-only memory observations / incomplete coverage; cannot determine overall start permission. Device sample time and validity are not provided for any item; the time below is UI observation time only.")
                    + "\n" + PageText("界面观察：", "UI observed: ") + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + " · " + PageText("连接会话：", "Connection session: ") + (connected ? session.Value.ToString() : PageText("未提供", "Not provided"))
                    + " · " + PageText("控制来源：", "Control source: ") + (runtime == null ? unknown : runtime.ControlOwner.ToString())
                    + "\n" + PageText("工程版本、设备身份和整体快照序号未提供；准备、恢复与 Host 缓存并非原子设备快照。启动提交时仍执行原完整检查。", "Project version, equipment identity and overall snapshot sequence are not provided; preparation, recovery and Host cache are not an atomic device snapshot. Existing complete checks still run on start submission.");
            }
            catch (Exception error)
            {
                session = null;
                observations.Clear();
                observations.Add(new StartConditionObservation { Key = "summary.read", Description = unknown + " · " + error.Message });
                observations.Add(ReadStopAlarmCondition(null));
                StartConditionsMessage.Text = PageText("内存观察失败；覆盖不完整，采样时间未提供，不能判断整体可启动。", "Memory observation failed; coverage incomplete, sample time not provided, overall start permission unknown.");
            }
            if (!ReferenceEquals(runtime, _runtime)) return;
            if (!ReferenceEquals(runtime, _conditionRuntime) || session != _conditionSession || sourceVersion != _conditionSourceVersion
                || preparationVersion != _conditionPreparationVersion)
                StartConditionsList.SelectedItem = null;
            _conditionRuntime = runtime;
            _conditionSession = session;
            _conditionSourceVersion = sourceVersion;
            _conditionPreparationVersion = preparationVersion;
            foreach (var item in observations)
            {
                item.Name = ConditionName(item.Key);
                item.Source = ConditionSource(item.Key);
                var existing = _conditionObservations.FirstOrDefault(row => row.Key == item.Key);
                if (existing == null) _conditionObservations.Add(item);
                else { existing.Name = item.Name; existing.Source = item.Source; existing.Description = item.Description; }
            }
            foreach (var old in _conditionObservations.Where(row => !observations.Any(item => item.Key == row.Key)).ToList()) _conditionObservations.Remove(old);
            if (!ReferenceEquals(StartConditionsList.ItemsSource, _conditionObservations))
            {
                StartConditionsList.ItemsSource = _conditionObservations;
            }
            UpdateConditionDetail();
        }

        /// <summary>选择只改变详情，不查询硬件或申请设备命令。</summary>
        private void ConditionSelection_Changed(object sender, SelectionChangedEventArgs e) { UpdateConditionDetail(); }

        private void UpdateConditionDetail()
        {
            if (ConditionDetailText == null) return;
            var row = StartConditionsList.SelectedItem as StartConditionObservation;
            ConditionDetailText.Text = row == null
                ? PageText("选择条件，查看原因和数据来源。", "Select a condition to see its reason and source.")
                : row.Name + (row.ObjectId == null ? "" : " [" + row.ObjectId + "]") + "\n" + row.Description
                    + "\n" + PageText("来源：", "Source: ") + row.Source;
            string label = null;
            if (row != null && _conditionSession.HasValue)
            {
                if (row.ObjectId != null && _rows.Any(item => item.Id == row.ObjectId))
                    label = PageText("查看该工位准备", "View station preparation");
                else if (row.Key == StopAlarmKey && _navigate != null) label = PageText("查看当前报警", "View current alarms");
                else if (row.Key == "runtime.recovery" && !string.IsNullOrEmpty(_runtime?.RecoveryBlockReason))
                    label = PageText("查看恢复诊断", "View recovery diagnostics");
                else if (row.Key == "host.start-readiness" && _navigate != null) label = PageText("查看工位运行", "View station operation");
            }
            ConditionTargetButton.Content = label;
            ConditionTargetButton.IsEnabled = label != null;
            ConditionTargetButton.Visibility = label == null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>重新核对选择所属的会话、定义和对象；未知对象不尝试按名称定位。</summary>
        private void OpenConditionTarget_Click(object sender, RoutedEventArgs e)
        {
            var selected = StartConditionsList.SelectedItem as StartConditionObservation;
            if (selected == null || !ConditionTargetButton.IsEnabled) return;
            Refresh();
            if (!ReferenceEquals(selected, StartConditionsList.SelectedItem) || !ConditionTargetButton.IsEnabled
                || !ReferenceEquals(_runtime, _conditionRuntime) || _runtime == null
                || !_runtime.IsConnectionSessionCurrent(_conditionSession)
                || _runtime.SafetyObservationVersion != _conditionSourceVersion
                || _runtime.PreparationConfigurationVersion != _conditionPreparationVersion) return;
            if (selected.ObjectId != null)
            {
                var station = _rows.FirstOrDefault(item => item.Id == selected.ObjectId);
                if (station == null) return;
                ReturnToPreparation_Click(sender, e);
                StationsGrid.SelectedItem = station;
                StationsGrid.ScrollIntoView(station);
            }
            else if (selected.Key == StopAlarmKey) _navigate?.Invoke("当前报警");
            else if (selected.Key == "runtime.recovery")
            {
                ReturnToPreparation_Click(sender, e);
                DiagnosticExpander.IsExpanded = true;
            }
            else if (selected.Key == "host.start-readiness") _navigate?.Invoke("生产流程");
        }
    }
}
