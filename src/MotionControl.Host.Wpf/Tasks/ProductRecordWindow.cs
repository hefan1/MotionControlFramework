using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>产品记录只读窗口；非模态打开，不阻挡正式设备的暂停或停止。</summary>
    internal sealed class ProductRecordWindow : Window
    {
        /// <summary>当前实际设备目录，不能指向另一份预览配置。</summary>
        private readonly string _machineDirectory;
        /// <summary>当前设备编号，用于校验任务记录。</summary>
        private readonly string _machineId;
        /// <summary>打开窗口时的显示语言。</summary>
        private readonly bool _english;
        /// <summary>表格和状态文本。</summary>
        private readonly DataGrid _records;
        /// <summary>缺失、截断和解析错误的提示。</summary>
        private readonly TextBlock _status;
        /// <summary>读取期间禁用重复刷新。</summary>
        private readonly Button _refresh;
        /// <summary>产品编号的只读查询条件；不会修改记录。</summary>
        private readonly TextBox _filter;
        /// <summary>窗口关闭后不再回写控件。</summary>
        private bool _closed;

        /// <summary>创建只读表，读取在后台执行。</summary>
        internal ProductRecordWindow(string machineDirectory, string machineId, bool english)
        {
            _machineDirectory = machineDirectory; _machineId = machineId; _english = english;
            Title = T("产品记录", "Product records"); Width = 1180; Height = 680; MinWidth = 720; MinHeight = 460;
            Background = (Brush)new BrushConverter().ConvertFromString("#F3F6FA");
            var root = new DockPanel { Margin = new Thickness(14) }; Content = root;
            var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
            heading.Children.Add(new TextBlock { Text = T("产品记录（只读）", "Product records (read-only)"), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = (Brush)new BrushConverter().ConvertFromString("#26384D") });
            heading.Children.Add(new TextBlock { Text = T("待出料与已出料分别显示；未保存的记录时间显示未知。产品编号可能跨控制器会话重复，表格顺序不代表生产顺序。", "Pending and completed outputs are separate records. Missing record times are unknown. Product IDs may repeat across controller sessions; row order is not production order."),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 7), Foreground = (Brush)new BrushConverter().ConvertFromString("#40536A") });
            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            bar.Children.Add(new TextBlock { Text = T("产品编号包含：", "Product ID contains:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _filter = new TextBox { Width = 180, MinHeight = 30, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center }; bar.Children.Add(_filter);
            _refresh = new Button { Content = T("查询 / 刷新", "Search / refresh"), Padding = new Thickness(12, 6, 12, 6) };
            _refresh.Click += Refresh_Click; bar.Children.Add(_refresh); heading.Children.Add(bar);
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), Foreground = Brushes.DimGray };
            heading.Children.Add(_status);
            _records = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                SelectionMode = DataGridSelectionMode.Single, RowHeight = 32 };
            AddColumn(T("来源", "Source"), "Source", 85); AddColumn(T("产品编号", "Product ID"), "Product", 125); AddColumn(T("夹位", "Fixture"), "Fixture", 65);
            AddColumn(T("工位", "Station"), "Station", 100); AddColumn(T("阶段", "Stage"), "Stage", 105); AddColumn(T("检测结果", "Result"), "Result", 85);
            AddColumn(T("出口", "Outlet"), "Outlet", 105); AddColumn(T("记录时间", "Record time"), "RecordedTime", 175); AddColumn(T("记录文件", "Record file"), "RecordId", 260);
            root.Children.Add(_records);
            Closed += Window_Closed; Loaded += Window_Loaded;
        }

        /// <summary>增加只读列并允许横向滚动查看完整内容。</summary>
        private void AddColumn(string title, string path, double width)
        {
            _records.Columns.Add(new DataGridTextColumn { Header = title, Binding = new System.Windows.Data.Binding(path), Width = width,
                ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis),
                    new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center),
                    new Setter(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(path)) } } });
        }

        /// <summary>首次打开只读加载；不启动或暂停设备。</summary>
        private async void Window_Loaded(object sender, RoutedEventArgs e) { await RefreshRecordsAsync(); }
        /// <summary>手动刷新只重新读取已写入的记录文件。</summary>
        private async void Refresh_Click(object sender, RoutedEventArgs e) { await RefreshRecordsAsync(); }
        /// <summary>后台读取，错误只显示在本窗口，不改变任务控制状态。</summary>
        private async Task RefreshRecordsAsync()
        {
            _refresh.IsEnabled = false; _filter.IsEnabled = false; _records.ItemsSource = null;
            _status.Foreground = Brushes.DimGray; _status.Text = T("正在读取记录…", "Reading records…");
            string filter = _filter.Text;
            try
            {
                ProductRecordReadResult result = await Task.Run(() => ProductRecordReader.Read(_machineDirectory, _machineId, _english, filter));
                if (_closed) return;
                _records.ItemsSource = result.Rows;
                if (result.Errors.Count > 0)
                {
                    _status.Foreground = Brushes.Firebrick;
                    _status.Text = T("部分记录或目录无法读取，当前列表可能不完整；请联系维护人员。错误 ", "Some records or directories could not be read; this list may be incomplete. Contact maintenance. Errors: ") + result.Errors.Count
                        + " 项：\n" + string.Join("\n", result.Errors.GetRange(0, Math.Min(3, result.Errors.Count)));
                }
                else if (!result.AnyDirectory) _status.Text = T("尚未找到记录目录，无法据此判断是否生产过产品。", "No record directory found; this does not establish whether products were produced.");
                else _status.Text = result.Rows.Count == 0 ? T("已读取记录目录，没有匹配的产品记录。", "Record directories read; no matching product records.") : T("当前显示 ", "Showing ") + result.Rows.Count + T(" 条记录。", " records.");
                if (result.Truncated) _status.Text += T(" 单种来源匹配超过 1000 条，仅显示部分文件；请缩小产品编号范围，顺序不代表生产时间。", " More than 1,000 records match in one source; narrow the product ID. Order is not production time.");
            }
            catch (Exception error)
            {
                if (_closed) return;
                _records.ItemsSource = null; _status.Foreground = Brushes.Firebrick;
                _status.Text = T("无法读取产品记录，状态未知：", "Cannot read product records; status is unknown: ") + error.Message;
            }
            finally { if (!_closed) { _refresh.IsEnabled = true; _filter.IsEnabled = true; } }
        }
        /// <summary>后台读取完成后不更新已关闭窗口。</summary>
        private void Window_Closed(object sender, EventArgs e) { _closed = true; }
        /// <summary>按打开时的界面语言选择固定提示。</summary>
        private string T(string chinese, string english) { return _english ? english : chinese; }
    }
}
