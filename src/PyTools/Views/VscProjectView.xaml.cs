using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PyTools.Core;

namespace PyTools.Views
{
    /// <summary>项目条目视图模型（角标锚定可视区，与其它列表同一份规则）。</summary>
    public sealed class VscProjectVM : INotifyPropertyChanged, IBadgedItem
    {
        private string _badge = "";

        public VscProjectVM(VscProject project) { Project = project; }

        public VscProject Project { get; private set; }
        public string Title { get { return Project.Title; } }
        public string Subtitle { get { return Project.Subtitle; } }

        /// <summary>首字母占位块（项目没有图标资源，与剪贴板文本记录同款）。</summary>
        public string Glyph
        {
            get
            {
                var title = Project.Title ?? "";
                return title.Length > 0 ? title.Substring(0, 1).ToUpperInvariant() : "P";
            }
        }

        public string Badge
        {
            get { return _badge; }
            set { if (_badge != value) { _badge = value; if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Badge")); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// VSCode 最近项目视图（M4）：空查询显示全部项目，过滤按标题 + 路径 + 工作区文件匹配；
    /// 动作面板复用 Views/ActionPanel（5 项，Git Bash 未安装时整条不出现）。
    /// </summary>
    public partial class VscProjectView : UserControl, ISearchFocusable
    {
        private readonly List<VscProjectVM> _items = new List<VscProjectVM>();
        private readonly List<VscProject> _projects = new List<VscProject>();
        private readonly DispatcherTimer _toastTimer;
        private ScrollViewer _scroll;
        private bool _actionMode;
        private VscProject _actionTarget;
        private int _generation;
        private bool _gitBashChecked;
        private string _gitBashRoot;

        public VscProjectView()
        {
            InitializeComponent();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            _toastTimer.Tick += delegate { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
            PART_List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScrollChanged));
            PART_List.PreviewMouseLeftButtonUp += OnListClick;
            PART_SearchBox.TextChanged += delegate { RefreshList(); };
            Actions.Invoked += OnActionInvoked;
            Actions.Dismissed += ExitActionModeInternal;
            PreviewKeyDown += OnViewPreviewKeyDown;
            PreviewMouseLeftButtonUp += OnViewPreviewMouseUp; // 底部「操作」键帽的鼠标呼出（根级 hit test）
            Loaded += delegate
            {
                _scroll = ListBadgeHelper.FindScroll(PART_List);
                Dispatcher.BeginInvoke(new Action(delegate { RecalcBadges(); }), DispatcherPriority.Loaded);
            };
        }

        /// <summary>Ctrl+N/P 步进条数（v2.pageStep）。</summary>
        public int PageStep { get; set; }

        /// <summary>动作执行成功：请宿主收起窗口（沿用启动应用的收窗语义）。</summary>
        public event Action CloseRequested;

        public TextBox SearchBox { get { return PART_SearchBox; } }

        public void FocusSearch()
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        /// <summary>进入视图：清空查询、退出动作态，并用缓存列表立即渲染后后台刷新。</summary>
        public void ResetForVsc()
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
            ExitActionModeInternal();
            PART_SearchBox.Text = "";
            RefreshList();
            LoadProjectsAsync();
            Dispatcher.BeginInvoke(new Action(delegate { RecalcBadges(); }), DispatcherPriority.Loaded);
        }

        /// <summary>Esc 分层：动作面板先关，返回是否已消费。</summary>
        public bool TryExitActionMode()
        {
            if (!_actionMode) return false;
            ExitActionModeInternal();
            return true;
        }

        public void ExitActionMode()
        {
            ExitActionModeInternal();
        }

        public void ShowToast(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            ToastText.Text = message;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        // ---- 数据 ----

        /// <summary>后台读 workspaceStorage：目录可能很多，不能卡呼出帧；换代号防止旧结果覆盖。</summary>
        private void LoadProjectsAsync()
        {
            _generation++;
            var generation = _generation;
            Task.Run(delegate
            {
                var projects = VscProjects.Load();
                Dispatcher.Invoke(delegate
                {
                    if (generation != _generation) return;
                    _projects.Clear();
                    _projects.AddRange(projects);
                    RefreshList();
                    Dispatcher.BeginInvoke(new Action(delegate { RecalcBadges(); }), DispatcherPriority.Loaded);
                });
            });
        }

        // ---- 渲染 ----

        private void RefreshList()
        {
            var query = (PART_SearchBox.Text ?? "").Trim().ToLowerInvariant();
            _items.Clear();
            foreach (var project in _projects)
            {
                if (query.Length > 0 && project.SearchText.IndexOf(query, StringComparison.Ordinal) < 0) continue;
                _items.Add(new VscProjectVM(project));
            }

            PART_List.ItemsSource = null;
            PART_List.ItemsSource = _items;
            SetSelected(_items.Count > 0 ? 0 : -1, scrollIntoView: false);
            RecalcBadges();

            EmptyHint.Text = _projects.Count == 0 ? "暂无 VSCode 最近项目" : "没有匹配的项目";
            EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateHintBar();
        }

        /// <summary>提示条：有行就显示；动作面板打开期间整条隐藏（面板浮在上面）。</summary>
        private void UpdateHintBar()
        {
            HintBar.Visibility = !_actionMode && _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (HintBar.Visibility == Visibility.Visible)
                ActionsHint.Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- 动作模式 ----

        private void EnterActionMode(VscProject project)
        {
            _actionMode = true;
            _actionTarget = project;
            UpdateHintBar();
            Actions.PageStep = PageStep;
            Actions.Show(RootBorder, BuildProjectActions(project));
        }

        private void ExitActionModeInternal()
        {
            if (!_actionMode) return;
            _actionMode = false;
            _actionTarget = null;
            Actions.Hide();
            UpdateHintBar();
            SearchBox.Focus();
        }

        /// <summary>项目动作表（v1 createProjectActions 前四项 + Git Bash + 复制路径）。
        /// Git Bash 未安装时 Available=false，面板与提示条自动同步。</summary>
        private List<ActionItem> BuildProjectActions(VscProject project)
        {
            return new List<ActionItem>
            {
                new ActionItem("open",        "用 VSCode 打开项目", "O"),
                new ActionItem("open-folder", "打开项目所在目录",   "F"),
                new ActionItem("cmd",         "在 CMD 打开项目",    "C"),
                new ActionItem("git-bash",    "在 Git Bash 打开项目", "G", GitBashAvailable()),
                new ActionItem("copy-path",   "复制项目路径",       "P")
            };
        }

        private void OnActionInvoked(string actionId)
        {
            var project = _actionTarget;
            if (project == null) return;

            string error;
            switch (actionId)
            {
                case "open": error = OpenInCode(project); break;
                case "open-folder": error = OpenProjectFolder(project); break;
                case "cmd": error = TerminalLauncher.OpenCmd(project.Path); break;
                case "git-bash": error = TerminalLauncher.OpenGitBash(project.Path); break;
                case "copy-path":
                    try
                    {
                        Clipboard.SetText(project.WorkspaceFile.Length > 0 ? project.WorkspaceFile : project.Path);
                        error = null;
                    }
                    catch (Exception ex) { error = "复制失败：" + ex.Message; }
                    break;
                default: error = "未知操作：" + actionId; break;
            }

            if (error == null)
            {
                var handler = CloseRequested;
                if (handler != null) handler();
                return;
            }
            ExitActionModeInternal();
            ShowToast(error);
        }

        private static string OpenInCode(VscProject project)
        {
            var target = project.WorkspaceFile.Length > 0 ? project.WorkspaceFile : project.Path;
            if (!File.Exists(target) && !Directory.Exists(target)) return "路径不存在：" + target;

            var exe = ResolveCodeExe();
            if (exe == null) return "未找到 VSCode（Code.exe），请确认已安装";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "\"" + target + "\"",
                    UseShellExecute = true,
                    WorkingDirectory = project.Path
                });
                return null;
            }
            catch (Exception ex) { return "启动 VSCode 失败：" + ex.Message; }
        }

        /// <summary>code 命令（<root>\bin\code.cmd）反推 Code.exe；再兜底常见安装位置。</summary>
        private static string ResolveCodeExe()
        {
            var cmd = TerminalLauncher.Which("code.cmd");
            if (!string.IsNullOrEmpty(cmd))
            {
                var binDir = Path.GetDirectoryName(cmd);
                var root = string.IsNullOrEmpty(binDir) ? null : Path.GetDirectoryName(binDir);
                var exe = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Code.exe");
                if (exe != null && File.Exists(exe)) return exe;
            }
            var candidates = new List<string>();
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(local)) candidates.Add(Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"));
            if (!string.IsNullOrEmpty(programFiles)) candidates.Add(Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"));
            foreach (var candidate in candidates)
                if (File.Exists(candidate)) return candidate;
            return null;
        }

        private static string OpenProjectFolder(VscProject project)
        {
            var dir = project.Path;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "目录不存在：" + dir;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + dir + "\"", UseShellExecute = true });
                return null;
            }
            catch (Exception ex) { return "打开目录失败：" + ex.Message; }
        }

        /// <summary>Git Bash 是否可用（首次调用检测一次；本机可能是 scoop 安装，检测逻辑见 TerminalLauncher）。</summary>
        private bool GitBashAvailable()
        {
            if (!_gitBashChecked)
            {
                _gitBashRoot = TerminalLauncher.DetectGitBash();
                _gitBashChecked = true;
            }
            return !string.IsNullOrEmpty(_gitBashRoot);
        }

        // ---- 选择与键位 ----

        private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (_actionMode)
            {
                // 动作态键位策略集中在面板组件（Ctrl+Enter 关面板，与启动器同一套约定）
                if (Actions.HandleKey(e)) e.Handled = true;
                return;
            }

            var count = _items.Count;
            if (e.Key == Key.Down || (isCtrl && e.Key == Key.J))
            {
                if (count > 0) { SetSelected(Math.Min(SelectedIndex() + 1, count - 1)); e.Handled = true; }
                return;
            }
            if (e.Key == Key.Up || (isCtrl && e.Key == Key.K))
            {
                if (count > 0) { SetSelected(Math.Max(SelectedIndex() - 1, 0)); e.Handled = true; }
                return;
            }
            if (isCtrl && e.Key == Key.N)
            {
                if (count > 0) { SetSelected(Math.Min(SelectedIndex() + Math.Max(1, PageStep), count - 1)); e.Handled = true; }
                return;
            }
            if (isCtrl && e.Key == Key.P)
            {
                if (count > 0) { SetSelected(Math.Max(SelectedIndex() - Math.Max(1, PageStep), 0)); e.Handled = true; }
                return;
            }
            if (isCtrl && e.Key == Key.Enter)
            {
                // Ctrl+Enter：动作面板开关（与启动器同一套约定；此处只负责「开」，关闭由面板 HandleKey 处理）
                if (count > 0) EnterActionMode(_items[SelectedIndex()].Project);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                if (count > 0) { ActivateAt(SelectedIndex()); e.Handled = true; }
                return;
            }
            if (!isCtrl && (e.Key >= Key.D0 && e.Key <= Key.D9 || e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9))
            {
                if (count > 0)
                {
                    var digit = (int)((e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
                        ? e.Key - Key.NumPad0
                        : e.Key - Key.D0);
                    var position = digit == 0 ? 9 : digit - 1;
                    var index = ListBadgeHelper.FirstVisibleIndex(PART_List, _scroll, count) + position;
                    if (index < count) ActivateAt(index);
                    e.Handled = true;
                }
            }
        }

        private int SelectedIndex()
        {
            var index = PART_List.SelectedIndex;
            return index < 0 ? 0 : index;
        }

        private void SetSelected(int index, bool scrollIntoView = true)
        {
            PART_List.SelectedIndex = index;
            if (scrollIntoView && index >= 0 && index < _items.Count)
            {
                PART_List.ScrollIntoView(_items[index]);
                Dispatcher.BeginInvoke(new Action(delegate { RecalcBadges(); }), DispatcherPriority.Loaded);
            }
        }

        private void ActivateAt(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            var project = _items[index].Project;
            var error = OpenInCode(project);
            if (error == null)
            {
                var handler = CloseRequested;
                if (handler != null) handler();
                return;
            }
            ShowToast(error);
        }

        private void OnListClick(object sender, MouseButtonEventArgs e)
        {
            var item = e.OriginalSource as DependencyObject;
            while (item != null && !(item is ListBoxItem)) item = VisualTreeHelper.GetParent(item);
            var container = item as ListBoxItem;
            if (container == null) return;
            var index = PART_List.ItemContainerGenerator.IndexFromContainer(container);
            if (index >= 0) { ActivateAt(index); e.Handled = true; }
        }

        /// <summary>鼠标点底部「操作 Ctrl+Enter」键帽 = Ctrl+Enter，呼出动作面板（与启动器同一实现）。
        /// 根级隧道事件 + hit test：直连子元素事件在焦点/捕获场景可能收不到 MouseUp。</summary>
        private void OnViewPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!ActionsHint.IsVisible || _items.Count == 0) return;
            var src = e.OriginalSource as DependencyObject;
            while (src != null && !ReferenceEquals(src, ActionsHint)) src = VisualTreeHelper.GetParent(src);
            if (!ReferenceEquals(src, ActionsHint)) return;
            if (_actionMode) return;
            EnterActionMode(_items[SelectedIndex()].Project);
            e.Handled = true;
        }

        // ---- 角标 ----

        private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scroll == null) _scroll = ListBadgeHelper.FindScroll(PART_List);
            RecalcBadges();
        }

        private void RecalcBadges()
        {
            ListBadgeHelper.UpdateBadges(_items, ListBadgeHelper.FirstVisibleIndex(PART_List, _scroll, _items.Count));
        }

        // ---- 圆角裁切（与其它视图同因：Border.CornerRadius 不裁子内容）----

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            var radius = 8.0;
            if (TryFindResource("Radius.Window") is CornerRadius corner) radius = corner.TopLeft;
            if (radius > 0 && ContentGrid.ActualWidth > 0)
            {
                ContentGrid.Clip = new RectangleGeometry(
                    new Rect(0, 0, ContentGrid.ActualWidth, ContentGrid.ActualHeight), radius, radius);
            }
        }
    }
}
