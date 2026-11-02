using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Runtime.Alarms;

namespace MotionControl.Host.Wpf.Operation
{
    /// <summary>只读显示当前设备的报警历史，不修改设备或当前报警。</summary>
    public partial class AlarmHistoryView : UserControl
    {
        /// <summary>当前设备日志，所有权仍属于应用。</summary>
        private AlarmHistoryJournal _history;
        /// <summary>标识异步查询所属配置，避免换设备后展示旧数据。</summary>
        private int _version;
        /// <summary>创建历史页面并在进入时查询。</summary>
        public AlarmHistoryView() { InitializeComponent(); Loaded += ViewLoaded; }
        /// <summary>绑定设备历史，切换后不保留上一设备的记录。</summary>
        public void SetHistory(AlarmHistoryJournal history)
        {
            _history = history; _version++; HistoryGrid.ItemsSource = null;
            if (IsLoaded) RefreshHistory();
        }
        /// <summary>进入页面时读取当前设备历史。</summary>
        private void ViewLoaded(object sender, RoutedEventArgs e) { RefreshHistory(); }
        /// <summary>重新读取历史，不发送任何设备指令。</summary>
        private void Refresh_Click(object sender, RoutedEventArgs e) { RefreshHistory(); }
        /// <summary>后台查询日志；失败及无数据均在页面中明确说明。</summary>
        private async void RefreshHistory()
        {
            int version = ++_version;
            var history = _history;
            RefreshButton.IsEnabled = false;
            HistoryGrid.ItemsSource = null;
            EmptyText.Text = history == null ? "设备历史记录尚未接入" : "正在读取报警历史";
            StatusText.Text = string.Empty; DetailText.Text = "选择记录查看现场详情。";
            if (history == null) return;
            try
            {
                var items = await Task.Run(() => history.ReadRecent(7, 1000));
                if (version != _version) return;
                var rows = items.Select(entry => new AlarmHistoryRow(entry)).ToList();
                HistoryGrid.ItemsSource = rows;
                EmptyText.Text = rows.Count == 0 ? "最近 7 天没有报警记录" : string.Empty;
                StatusText.Text = "已读取 " + rows.Count + " 条记录。" + (history.LastError == null ? "" : " " + history.LastError);
            }
            catch (Exception error)
            {
                if (version != _version) return;
                EmptyText.Text = "无法读取报警历史";
                StatusText.Text = "读取失败：" + error.Message;
            }
            finally { if (version == _version) RefreshButton.IsEnabled = _history != null; }
        }
        /// <summary>显示选中记录的设备现场详情及明确关联输入。</summary>
        private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = HistoryGrid.SelectedItem as AlarmHistoryRow;
            DetailText.Text = row == null ? "选择记录查看现场详情。" : "现场详情：" + (string.IsNullOrWhiteSpace(row.Entry.Detail) ? "未提供" : row.Entry.Detail)
                + (string.IsNullOrWhiteSpace(row.Entry.Input) ? "" : "\n关联输入：" + row.Entry.Input);
        }
    }
    /// <summary>只读历史记录与本地时间展示。</summary>
    internal sealed class AlarmHistoryRow
    {
        /// <summary>原始持久化事件。</summary>
        public AlarmHistoryEntry Entry { get; }
        /// <summary>发生时间转换为本机时间。</summary>
        public string Time { get { return Entry.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); } }
        /// <summary>包装一条原始记录。</summary>
        internal AlarmHistoryRow(AlarmHistoryEntry entry) { Entry = entry; }
    }
}
