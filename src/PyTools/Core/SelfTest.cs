using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PyTools.Native;

namespace PyTools.Core
{
    /// <summary>
    /// --selftest 无界面自测：热键解析、配置兼容、单实例命名、前台恢复干跑、日志。
    /// 结果打 stdout（AttachConsole）与日志文件，exit code 0 = 全部通过。
    /// </summary>
    public static class SelfTest
    {
        private sealed class Result
        {
            public string Name; public bool Passed; public string Detail;
        }

        public static int Run()
        {
            NativeMethods.TryAttachConsole();
            var results = new List<Result>();

            results.Add(Test("热键解析：Ctrl+Shift+Space", () =>
            {
                uint mods, vk; string err;
                if (!HotkeyParser.TryParse("Ctrl+Shift+Space", out mods, out vk, out err)) return err;
                const uint expected = NativeMethods.ModControl | NativeMethods.ModShift;
                return (mods == expected && vk == 0x20) ? null : "mods=" + mods + " vk=" + vk;
            }));

            results.Add(Test("热键解析：非法串被拒绝", () =>
            {
                uint mods, vk; string err;
                return HotkeyParser.TryParse("NotAHotkey", out mods, out vk, out err) ? "非法串解析成功" : null;
            }));

            results.Add(Test("配置：缺文件回退默认值", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "pytools-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var cfg = AppConfig.Load(path);
                    return cfg.Hotkey == "Ctrl+Shift+Space" ? null : "默认热键不符: " + cfg.Hotkey;
                }
                finally { try { File.Delete(path); } catch { } }
            }));

            results.Add(Test("配置：未知键与 v1 键写回保留", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "pytools-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    File.WriteAllText(path, "{\"hotkey\":\"Alt+Q\",\"v1onlyKey\":123,\"nested\":{\"a\":1}}");
                    var cfg = AppConfig.Load(path);
                    if (cfg.Hotkey != "Alt+Q") return "hotkey 未读入";
                    cfg.SaveBounds(1, 2, 3, 4);
                    var after = File.ReadAllText(path);
                    if (!after.Contains("v1onlyKey")) return "未知键丢失";
                    if (!after.Contains("nested")) return "嵌套未知键丢失";
                    if (!after.Contains("\"hotkey\":\"Alt+Q\"")) return "已知键被改写";
                    return null;
                }
                finally { try { File.Delete(path); } catch { } }
            }));

            results.Add(Test("简洁模式键：v2.liteMode 读取", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "pytools-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    File.WriteAllText(path, "{\"v2.liteMode\":true}");
                    var cfg = AppConfig.Load(path);
                    return cfg.LiteMode ? null : "v2.liteMode 未读入";
                }
                finally { try { File.Delete(path); } catch { } }
            }));

            results.Add(Test("前台恢复：零句柄 no-op", () =>
            {
                var report = Foreground.Restore(IntPtr.Zero);
                return (report.Restored && report.Attempts == 0) ? null : "attempts=" + report.Attempts;
            }));

            results.Add(Test("前台 capture：可获取句柄（可能为 0）", () =>
            {
                var handle = Foreground.Capture();
                return handle == IntPtr.Zero ? "(空会话，句柄为 0，合法)" : null;
            }));

            // ---- M2a 启动器 ----

            results.Add(Test("拼音表：加载与多音字集合", () =>
            {
                if (!Pinyin.Ready) return "内嵌拼音表未加载";
                var zhong = Pinyin.LettersOf('重');
                if (zhong != "ctz") return "重 → " + zhong;
                var chang = Pinyin.LettersOf('长');
                if (chang != "cz") return "长 → " + chang;
                return null;
            }));

            results.Add(Test("拼音表：变体展开", () =>
            {
                var tx = Pinyin.Expand("腾讯会议");
                if (!tx.Contains("txhy")) return "腾讯会议 未含 txhy";
                var cq = Pinyin.Expand("重庆");
                if (!cq.Contains("cq") || !cq.Contains("zq")) return "重庆 变体 = " + string.Join(",", cq);
                var wps = Pinyin.Expand("WPS表格");
                if (!wps.Contains("wpsbg")) return "WPS表格 变体 = " + string.Join(",", wps);
                return null;
            }));

            results.Add(Test("分词：quote-aware splitArgs", () =>
            {
                var tokens = AppLauncher.SplitArgs("a \"b c\" d");
                if (tokens.Count != 3) return "count=" + tokens.Count;
                return tokens[1] == "b c" ? null : "token[1]=" + tokens[1];
            }));

            results.Add(Test("配置：v2.pinyinSearch / v2.pageStep", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "pytools-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    File.WriteAllText(path, "{\"v2.pinyinSearch\":false,\"v2.pageStep\":3}");
                    var cfg = AppConfig.Load(path);
                    if (cfg.PinyinSearch) return "pinyinSearch 未读入";
                    if (cfg.PageStep != 3) return "pageStep=" + cfg.PageStep;
                    return null;
                }
                finally { try { File.Delete(path); } catch { } }
            }));

            results.Add(Test("扫描：身份键合并与登记覆盖", () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "pytools-selftest-scan-" + Guid.NewGuid().ToString("N"));
                var path = Path.Combine(Path.GetTempPath(), "pytools-selftest-apps-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    Directory.CreateDirectory(root);
                    var exe = Path.Combine(root, "foo.exe");
                    File.WriteAllText(exe, "dummy"); // 扫描不校验 PE 内容
                    var custom = new CustomApps(path);
                    var added = custom.Add(exe);
                    if (!added.Ok) return "登记失败: " + added.Reason;

                    // 同 target 同 args → 登记覆盖扫描（1 条，IsCustom）
                    var entries = AppScanner.Scan(new List<string> { root }, custom.Load());
                    if (entries.Count != 1) return "合并后 " + entries.Count + " 条（期望 1）";
                    if (!entries[0].IsCustom || entries[0].Name != "foo") return "覆盖未生效: " + entries[0].Name;

                    // 登记副本带参数 → 不同身份键 → 2 条
                    var data = custom.Load();
                    data.Add(new CustomApp { Name = "foo -x", Path = exe, Args = "-x", AddedAt = added.Path });
                    var two = AppScanner.Scan(new List<string> { root }, data);
                    if (two.Count != 2) return "带参副本后 " + two.Count + " 条（期望 2）";
                    return null;
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                    try { File.Delete(path); } catch { }
                }
            }));

            results.Add(Test("启动：死条目移除登记（v1 1.8.1 语义）", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "pytools-selftest-dead-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var custom = new CustomApps(path);
                    var ghost = Path.Combine(Path.GetTempPath(), "pytools-selftest-ghost-" + Guid.NewGuid().ToString("N") + ".exe");
                    File.WriteAllText(ghost, "dummy");
                    custom.Add(ghost); // 先登记真实存在的目标，再删除模拟"目标已消失"
                    var entry = new AppEntry { Name = "ghost", Path = ghost, TargetPath = ghost, IsCustom = true };
                    File.Delete(ghost);
                    var result = AppLauncher.Open(entry, new List<string>(), custom);
                    if (result == null || !result.Ok || !result.RemovedDeadEntry) return "未按移除语义返回";
                    if (custom.Load().Count != 0) return "登记未被移除";
                    return result.Message.Contains("移除") ? null : "提示文案: " + result.Message;
                }
                finally { try { File.Delete(path); } catch { } }
            }));

            results.Add(Test("授权：IsKnownApp 根目录前缀判定", () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "pytools-selftest-known-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(root);
                    var inside = Path.Combine(root, "app.exe");
                    var custom = new CustomApps(Path.Combine(Path.GetTempPath(), "pytools-selftest-none-" + Guid.NewGuid().ToString("N") + ".json"));
                    if (!AppLauncher.IsKnownApp(inside, new List<string> { root }, custom)) return "根内未授权";
                    if (AppLauncher.IsKnownApp(inside + ".bak", new List<string> { root }, custom)) return "非法扩展名被授权";
                    var outside = Path.Combine(Path.GetTempPath(), "outside.exe");
                    if (AppLauncher.IsKnownApp(outside, new List<string> { root }, custom)) return "根外被授权";
                    return null;
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }));

            results.Add(Test("登记：目录目标返回 not-file（v1 语义）", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "pytools-selftest-dir-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var custom = new CustomApps(path);
                    var result = custom.Add(Path.GetTempPath()); // 目录
                    return !result.Ok && result.Reason == "not-file" ? null : "reason=" + result.Reason;
                }
                finally { try { File.Delete(path); } catch { } }
            }));

            results.Add(Test("右键菜单：期望命令串与实况查询（只读）", () =>
            {
                var command = ContextMenuRegistry.ExpectedCommand();
                if (!command.StartsWith("\"") || !command.EndsWith("\" \"--add-app=%1\""))
                    return "command 格式异常: " + command;
                // Configured() 只读查询 HKCU，不写注册表；当前值不作为断言（随用户开关变化）
                ContextMenuRegistry.Configured();
                return null;
            }));

            results.Add(Test("自启：Run 项命令串格式（只读）", () =>
            {
                var value = Startup.CommandValue();
                return value.StartsWith("\"") && value.EndsWith("\"") && value.Length > 4 ? null : "value=" + value;
            }));

            results.Add(Test("自启：历史残留键名判定（S10）", () =>
            {
                if (!Startup.IsLegacyName("electron.app.pytools-1.6.0")) return "v1 版本漂移键应判为残留";
                if (!Startup.IsLegacyName("app.pytools")) return "v1 简式键应判为残留";
                if (Startup.IsLegacyName("PyTools")) return "本应用键名不得判为残留";
                if (Startup.IsLegacyName("pytools")) return "仅大小写不同视同本应用键名";
                if (Startup.IsLegacyName("OneDrive") || Startup.IsLegacyName("") || Startup.IsLegacyName(null))
                    return "无关键名/空名误判为残留";
                return null;
            }));

            results.Add(Test("单实例：add-app payload 文件写入与清理", () =>
            {
                var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SingleInstance.AddAppFileName);
                try
                {
                    var payload = Path.Combine(Path.GetTempPath(), "probe.exe");
                    SingleInstance.SignalAddApp(payload); // 无首实例时信号 Set 失败，但文件应已写出
                    if (!File.Exists(file)) return "payload 文件未写出";
                    var text = File.ReadAllText(file);
                    return text == payload ? null : "payload 内容不符: " + text;
                }
                finally { try { File.Delete(file); } catch { } }
            }));

            // ---- M3 剪贴板历史 ----

            results.Add(Test("热键解析：Win+V / Alt+B", () =>
            {
                uint mods, vk; string err;
                if (!HotkeyParser.TryParse("Win+V", out mods, out vk, out err)) return "Win+V: " + err;
                if (mods != NativeMethods.ModWin || vk != 0x56) return "Win+V -> mods=" + mods + " vk=" + vk;
                if (!HotkeyParser.TryParse("Alt+B", out mods, out vk, out err)) return "Alt+B: " + err;
                return (mods == NativeMethods.ModAlt && vk == 0x42) ? null : "Alt+B -> mods=" + mods + " vk=" + vk;
            }));

            results.Add(Test("配置：剪贴板默认值（键位留空 / 未启用 / 隐私过滤开）", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "pytools-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var cfg = AppConfig.Load(path);
                    if (cfg.ClipboardHotkey != "") return "默认热键应为空串，实为: " + cfg.ClipboardHotkey;
                    if (cfg.ClipboardHotkeyEnabled) return "默认应为未启用";
                    return cfg.ClipboardPrivacyFilter ? null : "默认应开启隐私过滤";
                }
                finally { try { File.Delete(path); } catch { } }
            }));

            results.Add(Test("配置：已有剪贴板键位原样读取（不迁移不改判）", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "pytools-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    File.WriteAllText(path, "{\"clipboardHotkey\":\"Alt+B\",\"clipboardHotkeyEnabled\":true}");
                    var cfg = AppConfig.Load(path);
                    if (cfg.ClipboardHotkey != "Alt+B") return "键位被改写: " + cfg.ClipboardHotkey;
                    return cfg.ClipboardHotkeyEnabled ? null : "启用状态丢失";
                }
                finally { try { File.Delete(path); } catch { } }
            }));

            results.Add(Test("剪贴板：文本捕获与同内容去重", () =>
            {
                var dir = TempClipDir();
                try
                {
                    var history = new ClipboardHistory(dir);
                    string reason;
                    var data = new DataObject();
                    data.SetData(DataFormats.UnicodeText, "hello world");
                    if (history.Capture(data, out reason) == null) return "首次未捕获: " + reason;
                    if (history.Records.Count != 1) return "count=" + history.Records.Count;
                    if (history.Capture(data, out reason) != null || reason != "duplicate") return "重复未判重: " + reason;
                    return history.Records.Count == 1 ? null : "判重后 count=" + history.Records.Count;
                }
                finally { TryDeleteDir(dir); }
            }));

            results.Add(Test("剪贴板：超 100 条淘汰最旧", () =>
            {
                var dir = TempClipDir();
                try
                {
                    var history = new ClipboardHistory(dir);
                    string reason;
                    for (var i = 0; i <= ClipboardHistory.MaxRecords; i++)
                    {
                        var data = new DataObject();
                        data.SetData(DataFormats.UnicodeText, "t" + i);
                        history.Capture(data, out reason);
                    }
                    if (history.Records.Count != ClipboardHistory.MaxRecords) return "count=" + history.Records.Count;
                    foreach (var record in history.Records)
                        if (record.Text == "t0") return "最旧记录未被淘汰";
                    var tail = history.Records[history.Records.Count - 1].Text;
                    return tail == "t1" ? null : "尾部应为 t1，实为 " + tail;
                }
                finally { TryDeleteDir(dir); }
            }));

            results.Add(Test("剪贴板：超长文本整条不记录", () =>
            {
                var dir = TempClipDir();
                try
                {
                    var history = new ClipboardHistory(dir);
                    string reason;
                    var data = new DataObject();
                    data.SetData(DataFormats.UnicodeText, new string('x', (int)ClipboardHistory.MaxTextChars + 1));
                    var record = history.Capture(data, out reason);
                    if (record != null) return "超长文本被记录";
                    return reason == "text-too-large" ? null : "reason=" + reason;
                }
                finally { TryDeleteDir(dir); }
            }));

            results.Add(Test("剪贴板：隐私格式拦截与开关", () =>
            {
                var dir = TempClipDir();
                try
                {
                    var history = new ClipboardHistory(dir) { PrivacyFilter = true };
                    string reason;
                    var data = new DataObject();
                    data.SetData(DataFormats.UnicodeText, "secret");
                    data.SetData(ClipboardHistory.ExcludeFormat, 1);
                    if (history.Capture(data, out reason) != null) return "隐私格式未被拦截";
                    if (reason != "privacy-exclude") return "reason=" + reason;
                    history.PrivacyFilter = false;
                    return history.Capture(data, out reason) != null ? null : "开关关闭后仍被拦截: " + reason;
                }
                finally { TryDeleteDir(dir); }
            }));

            results.Add(Test("剪贴板：图片捕获、PNG 落盘、缩略图解码", () =>
            {
                var dir = TempClipDir();
                try
                {
                    var history = new ClipboardHistory(dir);
                    string reason;
                    var data = new DataObject();
                    data.SetData(DataFormats.Bitmap, new WriteableBitmap(8, 6, 96, 96, PixelFormats.Bgr32, null));
                    var record = history.Capture(data, out reason);
                    if (record == null) return "图片未捕获: " + reason;
                    if (record.Kind != ClipKind.Image) return "类型=" + record.Kind;
                    if (!File.Exists(history.ImageFile(record.Id))) return "PNG 未落盘";
                    var thumb = history.GetThumbnail(record);
                    if (thumb == null) return "缩略图解码失败";
                    if (thumb.PixelWidth > ClipboardHistory.ThumbDecodeEdge) return "缩略图未缩放: " + thumb.PixelWidth;
                    return record.Subtitle.Length > 0 ? null : "副标题为空";
                }
                finally { TryDeleteDir(dir); }
            }));

            results.Add(Test("剪贴板：无效 alpha 校正（a=0 且 RGB 完好 → 不透明，有效 alpha 不动）", () =>
            {
                const int width = 8, height = 6, stride = width * 4;
                // 模拟坏 DIB：RGB 有内容、alpha 全 0
                var pixels = new byte[stride * height];
                for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 30; pixels[i + 1] = 120; pixels[i + 2] = 200; pixels[i + 3] = 0; }
                var broken = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                var fixedImage = ClipboardHistory.CoerceBrokenAlpha(broken);
                var check = new byte[stride * height];
                fixedImage.CopyPixels(check, stride, 0);
                for (var i = 3; i < check.Length; i += 4)
                    if (check[i] != 255) return "a=0 未校正为不透明";
                if (check[0] != 30 || check[2] != 200) return "RGB 内容被破坏";

                // 有效 alpha（部分透明）应原样保留，不被强制
                var validPixels = new byte[stride * height];
                for (var i = 0; i < validPixels.Length; i += 4) { validPixels[i] = 1; validPixels[i + 3] = 128; }
                var valid = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, validPixels, stride);
                var untouched = ClipboardHistory.CoerceBrokenAlpha(valid);
                var check2 = new byte[stride * height];
                untouched.CopyPixels(check2, stride, 0);
                return check2[3] == 128 ? null : "有效 alpha 被改写: " + check2[3];
            }));

            results.Add(Test("剪贴板：存量坏 PNG 惰性修复（缩略图触发重写 + sha1 更新）", () =>
            {
                var dir = TempClipDir();
                try
                {
                    var images = Path.Combine(dir, "clipboard-history", "images");
                    Directory.CreateDirectory(images);
                    // 构造坏 PNG：Bgra32 RGB 内容 + alpha 全 0
                    const int width = 40, height = 30, stride = width * 4;
                    var pixels = new byte[stride * height];
                    for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 10; pixels[i + 1] = 20; pixels[i + 2] = 30; pixels[i + 3] = 0; }
                    var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(source));
                    byte[] brokenPng;
                    using (var stream = new MemoryStream()) { encoder.Save(stream); brokenPng = stream.ToArray(); }
                    File.WriteAllBytes(Path.Combine(images, "img-broken.png"), brokenPng);
                    File.WriteAllText(Path.Combine(dir, "clipboard-history.json"),
                        "{\"version\":1,\"records\":[{\"id\":\"img-broken\",\"type\":\"image\",\"createdAt\":1,\"updatedAt\":2,\"imageFile\":\"img-broken.png\"}]}");

                    var history = new ClipboardHistory(dir);
                    var record = history.Records[0];
                    var thumb = history.GetThumbnail(record);
                    if (thumb == null) return "缩略图解码失败";

                    // 磁盘 PNG 应已重写为不透明，且 RGB 内容保留
                    var redecoded = new BitmapImage();
                    redecoded.BeginInit();
                    redecoded.UriSource = new Uri(history.ImageFile("img-broken"), UriKind.Absolute);
                    redecoded.CacheOption = BitmapCacheOption.OnLoad;
                    redecoded.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    redecoded.EndInit();
                    var stride2 = redecoded.PixelWidth * 4;
                    var check = new byte[stride2 * redecoded.PixelHeight];
                    redecoded.CopyPixels(check, stride2, 0);
                    for (var i = 3; i < check.Length; i += 4)
                        if (check[i] != 255) return "PNG 未修复为不透明";
                    if (check[0] != 10 || check[2] != 30) return "修复后 RGB 内容被破坏";
                    var sha1 = ClipboardHistory.Sha1Hex(File.ReadAllBytes(history.ImageFile("img-broken")));
                    return record.ImageSha1 == sha1 ? null : "sha1 未同步更新";
                }
                finally { TryDeleteDir(dir); }
            }));

            results.Add(Test("剪贴板：预览截断到 200 字", () =>
            {
                var record = new ClipRecord { Kind = ClipKind.Text, Text = new string('a', 300) };
                var preview = record.Preview;
                if (preview.Length != ClipboardHistory.MaxPreviewChars + 1) return "len=" + preview.Length;
                return preview.EndsWith("…") ? null : "缺少省略号";
            }));

            results.Add(Test("剪贴板：v1 数据兼容 + 孤儿 PNG 清理", () =>
            {
                var dir = TempClipDir();
                try
                {
                    var images = Path.Combine(dir, "clipboard-history", "images");
                    Directory.CreateDirectory(images);
                    var png = MakePngBytes();
                    File.WriteAllBytes(Path.Combine(images, "img1.png"), png);
                    File.WriteAllBytes(Path.Combine(images, "orphan.png"), png);
                    File.WriteAllText(Path.Combine(dir, "clipboard-history.json"),
                        "{\"version\":1,\"records\":["
                        + "{\"id\":\"t1\",\"type\":\"text\",\"createdAt\":1,\"updatedAt\":3,\"text\":\"hi\"},"
                        + "{\"id\":\"img1\",\"type\":\"image\",\"createdAt\":1,\"updatedAt\":2,\"imageFile\":\"img1.png\"},"
                        + "{\"id\":\"bad\",\"type\":\"unknown\"}]}");

                    var history = new ClipboardHistory(dir);
                    if (history.Records.Count != 2) return "记录数=" + history.Records.Count;
                    if (history.Records[0].Id != "t1") return "排序错，首位=" + history.Records[0].Id;
                    if (File.Exists(Path.Combine(images, "orphan.png"))) return "孤儿 PNG 未清理";
                    return File.Exists(Path.Combine(images, "img1.png")) ? null : "有效 PNG 被误删";
                }
                finally { TryDeleteDir(dir); }
            }));

            results.Add(Test("VSCode 项目：file URI 解析（盘符大写 / 百分号解码）", () =>
            {
                var a = VscProjects.UriToPath("file:///c%3A/Users/me/My%20Code");
                if (a != @"C:\Users\me\My Code") return "解析结果=" + a;
                if (VscProjects.UriToPath("https://example.com/x") != null) return "非 file URI 未拒绝";
                return VscProjects.UriToPath("file:///d%3A/proj") == @"D:\proj" ? null : "D 盘解析失败";
            }));

            results.Add(Test("VSCode 项目：读取 workspaceStorage（mtime 降序 / 去重 / 多根工作区）", () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "pytools-selftest-vsc-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var s1 = Path.Combine(root, "s1");
                    var s2 = Path.Combine(root, "s2");
                    var s3 = Path.Combine(root, "s3");
                    foreach (var dir in new[] { s1, s2, s3 }) Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(s1, "workspace.json"), "{\"folder\":\"file:///c%3A/dev%2Falpha\"}");
                    File.WriteAllText(Path.Combine(s2, "workspace.json"), "{\"folder\":\"file:///c%3A/dev%2Falpha\"}");
                    File.WriteAllText(Path.Combine(s3, "workspace.json"), "{\"workspace\":\"file:///c%3A/dev%2Fbeta%2Fbeta.code-workspace\"}");
                    // 同目录只留最近一次：mtime s1 > s3 > s2
                    Directory.SetLastWriteTimeUtc(s1, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
                    Directory.SetLastWriteTimeUtc(s2, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                    Directory.SetLastWriteTimeUtc(s3, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

                    var list = VscProjects.LoadFrom(root);
                    if (list.Count != 2) return "条目数=" + list.Count;
                    if (list[0].Title != "alpha") return "排序错，首位=" + list[0].Title;
                    if (list[1].WorkspaceFile.Length == 0) return "多根工作区未识别";
                    if (list[1].Title != "beta") return "工作区标题=" + list[1].Title;
                    return list[1].Path.EndsWith("beta", StringComparison.OrdinalIgnoreCase) ? null : "工作区目录=" + list[1].Path;
                }
                finally { TryDeleteDir(root); }
            }));

            results.Add(Test("Git Bash：检测结果自洽（未安装可为 null，不得误判 WSL）", () =>
            {
                var root = TerminalLauncher.DetectGitBash();
                if (root == null) return null; // 未安装是合法结果
                if (!TerminalLauncher.LooksLikeGitRoot(root)) return "命中但不像 Git 安装根：" + root;
                if (root.IndexOf("System32", StringComparison.OrdinalIgnoreCase) >= 0) return "误判为 WSL bash：" + root;
                return null;
            }));

            results.Add(Test("内置命令：vsc 别名精确/前缀命中，无关查询不误命中", () =>
            {
                var exact = BuiltinCommands.Match("vsc");
                if (exact == null || !exact.IsCommand || exact.CommandId != "vsc") return "精确别名未命中";
                if (exact.Name.Length == 0 || exact.TargetPath.Length == 0) return "标题/副标题为空";
                var prefix = BuiltinCommands.Match("vs");
                if (prefix == null || prefix.CommandId != "vsc") return "前缀匹配失败";
                return BuiltinCommands.Match("zzz") == null ? null : "无关查询误命中";
            }));

            results.Add(Test("内置命令：剪贴板唤醒词（clipboard/剪贴板/标题拼音首字母）命中", () =>
            {
                var full = BuiltinCommands.Match("clipboard");
                if (full == null || !full.IsCommand || full.CommandId != "clipboard") return "clipboard 未命中";
                var prefix = BuiltinCommands.Match("clip");
                if (prefix == null || prefix.CommandId != "clipboard") return "前缀 clip 未命中";
                var cn = BuiltinCommands.Match("剪贴板");
                if (cn == null || cn.CommandId != "clipboard") return "中文别名未命中";
                if (Pinyin.Ready)
                {
                    var initials = BuiltinCommands.Match("jtbjl");
                    if (initials == null || initials.CommandId != "clipboard") return "标题拼音首字母未命中";
                }
                return BuiltinCommands.Match("vsc").CommandId == "vsc" ? null : "vsc 被误改";
            }));

            results.Add(Test("内置命令：format 同词双条目（面板在前、动作在后）", () =>
            {
                var all = BuiltinCommands.MatchAll("format");
                if (all.Count != 2) return "期望 2 条，实际 " + all.Count;
                if (all[0].CommandId != "format" || all[1].CommandId != "format-clipboard")
                    return "顺序错误：" + all[0].CommandId + "," + all[1].CommandId;
                var cn = BuiltinCommands.MatchAll("格式化");
                if (cn.Count != 2 || cn[0].CommandId != "format") return "中文词双条目未命中";
                return null;
            }));

            results.Add(Test("StructuredFormats：JSON 格式化 2 空格 / 压缩（键序保持）", () =>
            {
                var fmt = StructuredFormats.Process("{\"b\":1,\"a\":[1,2],\"s\":\"x\\n\\\"y\\\"\"}", "format", "auto", "2");
                var expect = "{\n  \"b\": 1,\n  \"a\": [\n    1,\n    2\n  ],\n  \"s\": \"x\\n\\\"y\\\"\"\n}";
                if (fmt.Format != "json" || fmt.Output != expect) return "格式化输出不符:\n" + fmt.Output;
                var min = StructuredFormats.Process("{\n  \"b\": 1,\n  \"a\": [1, 2]\n}", "minify", "json", "2");
                if (min.Output != "{\"b\":1,\"a\":[1,2]}") return "压缩输出不符: " + min.Output;
                var tab = StructuredFormats.Process("{\"a\":1}", "format", "auto", "tab");
                if (tab.Output != "{\n\t\"a\": 1\n}") return "Tab 缩进不符: " + tab.Output;
                return null;
            }));

            results.Add(Test("StructuredFormats：XML 保真格式化（注释/CDATA/混合内容）与压缩", () =>
            {
                var fmt = StructuredFormats.Process(
                    "<root><a x=\"1\">text</a><!-- 注释 --><![CDATA[cdata<raw>]]><b/></root>", "format", "auto", "2");
                var expect = "<root>\n  <a x=\"1\">text</a>\n  <!-- 注释 -->\n  <![CDATA[cdata<raw>]]>\n  <b/>\n</root>";
                if (fmt.Format != "xml" || fmt.Output != expect) return "格式化输出不符:\n" + fmt.Output;
                var min = StructuredFormats.Process(
                    "<root>\n  <a x = \"1\" > text </a>\n</root>", "minify", "xml", "2");
                if (min.Output != "<root><a x=\"1\"> text </a></root>") return "压缩输出不符: " + min.Output;
                return null;
            }));

            results.Add(Test("StructuredFormats：非法输入抛 FormatException（v1 语义）", () =>
            {
                try { StructuredFormats.Process("{bad json", "format", "auto", "2"); return "JSON 未报错"; }
                catch (FormatException) { }
                try { StructuredFormats.Process("<a><b></a>", "format", "auto", "2"); return "XML 层级未报错"; }
                catch (FormatException) { }
                try { StructuredFormats.Process("", "format", "auto", "2"); return "空串未报错"; }
                catch (FormatException) { }
                return null;
            }));

            results.Add(Test("TextEncoders：URL 保留字符集与往返（对齐 JS encodeURIComponent）", () =>
            {
                var enc = TextEncoders.Convert("中文 a!~*'()-._", "url", "encode");
                if (enc != "%E4%B8%AD%E6%96%87%20a!~*'()-._") return "编码输出不符: " + enc;
                var dec = TextEncoders.Convert(enc, "url", "decode");
                if (dec != "中文 a!~*'()-._") return "解码往返不符: " + dec;
                try { TextEncoders.Convert("%E4%B", "url", "decode"); return "不完整 % 序列未报错"; }
                catch (FormatException) { }
                try { TextEncoders.Convert("%ZZ", "url", "decode"); return "非法 hex 未报错"; }
                catch (FormatException) { }
                return null;
            }));

            results.Add(Test("TextEncoders：Base64 双向（UTF-8、去空白、容忍缺省 padding、严格解码报错）", () =>
            {
                var enc = TextEncoders.Convert("中文", "base64", "encode");
                if (enc != "5Lit5paH") return "编码输出不符: " + enc;
                if (TextEncoders.Convert("5Lit 5paH\t", "base64", "decode") != "中文") return "空白剥离失败";
                if (TextEncoders.Convert("QQ", "base64", "decode") != "A") return "缺省 padding 失败";
                try { TextEncoders.Convert("//8=", "base64", "decode"); return "非法 UTF-8 序列未报错"; }
                catch (FormatException) { }
                try { TextEncoders.Convert("!!", "base64", "decode"); return "非法字符未报错"; }
                catch (FormatException) { }
                return null;
            }));

            results.Add(Test("TextEncoders：Unicode 转义（UTF-16 码元、\\u 与 \\x、大小写）", () =>
            {
                var enc = TextEncoders.Convert("中A", "unicode", "encode");
                if (enc != "\\u4e2d\\u0041") return "编码输出不符: " + enc;
                var dec = TextEncoders.Convert("\\u4E2D\\x41", "unicode", "decode");
                if (dec != "中A") return "解码不符: " + dec;
                return null;
            }));

            results.Add(Test("TextEncoders：HTML 实体（5 字符编码；解码覆盖数字与命名实体）", () =>
            {
                var enc = TextEncoders.Convert("<a \"b\" 'c' &>", "html", "encode");
                if (enc != "&lt;a &quot;b&quot; &#39;c&#39; &amp;&gt;") return "编码输出不符: " + enc;
                var dec = TextEncoders.Convert("&lt;div&gt;&amp;&#x4E2D;&copy;<keep>", "html", "decode");
                if (dec != "<div>&中©<keep>") return "解码不符: " + dec;
                return null;
            }));

            results.Add(Test("TextEncoders：JSON 字面量双向（非 ASCII 保留、pretty 2 空格、类型约束）", () =>
            {
                var enc = TextEncoders.Convert("{\"a\":\"中\"}", "json", "encode");
                if (enc != "\"{\\\"a\\\":\\\"中\\\"}\"") return "编码输出不符: " + enc;
                var dec = TextEncoders.Convert("\"{\\\"a\\\":1}\"", "json", "decode");
                if (dec != "{\n  \"a\": 1\n}") return "字符串方向解码不符:\n" + dec;
                var decObj = TextEncoders.Convert("{\"a\":[1,2]}", "json", "decode");
                if (decObj != "{\n  \"a\": [\n    1,\n    2\n  ]\n}") return "对象方向解码不符:\n" + decObj;
                try { TextEncoders.Convert("123", "json", "encode"); return "数字输入未报错"; }
                catch (FormatException) { }
                return null;
            }));

            results.Add(Test("TextEncoders：Hex 双向（0x/空白/逗号剥离、奇数与非法报错）", () =>
            {
                var enc = TextEncoders.Convert("中", "hex", "encode");
                if (enc != "e4 b8 ad") return "编码输出不符: " + enc;
                var dec = TextEncoders.Convert("0xE4 0xB8,0xAD", "hex", "decode");
                if (dec != "中") return "解码不符: " + dec;
                try { TextEncoders.Convert("e4 b8", "hex", "decode"); return "奇数长度未报错"; }
                catch (FormatException) { }
                try { TextEncoders.Convert("", "hex", "decode"); return "空输入未报错"; }
                catch (FormatException) { }
                return null;
            }));

            results.Add(Test("内置命令：encode 唤醒词命中", () =>
            {
                var hit = BuiltinCommands.Match("encode");
                if (hit == null || !hit.IsCommand || hit.CommandId != "encode") return "encode 未命中";
                var cn = BuiltinCommands.Match("编码");
                if (cn == null || cn.CommandId != "encode") return "中文别名未命中";
                return null;
            }));

            // ---- Cron 表达式（M4，cron-core.js 移植）----

            results.Add(Test("Cron：六段/五段解析与摘要", () =>
            {
                var six = CronExpr.Parse("0 0 1,5 15,30 * *");
                if (!six.Ok) return "六段解析失败: " + six.Error.Message;
                if (!six.Summary.Contains("每月")) return "摘要不符: " + six.Summary;
                if (six.OrRule) return "日限周不限不应触发或规则";
                var five = CronExpr.Parse("30 4 * * *");
                if (!five.Ok) return "五段解析失败: " + five.Error.Message;
                if (five.Note == null || !five.Note.Contains("5 段")) return "五段说明缺失: " + five.Note;
                if (five.Fields[0].Values[0] != 0) return "五段秒应为固定 0";
                return null;
            }));

            results.Add(Test("Cron：宏与 @reboot、非法宏报错", () =>
            {
                var daily = CronExpr.Parse("@daily");
                if (!daily.Ok || daily.Note == null || !daily.Note.Contains("等价于")) return "@daily 说明缺失";
                var reboot = CronExpr.Parse("@reboot");
                if (!reboot.Ok || reboot.Fields != null) return "@reboot 应 ok 且无字段";
                var bad = CronExpr.Parse("@weeklyly");
                if (bad.Ok || bad.Error.Message.IndexOf("不支持的宏", StringComparison.Ordinal) < 0) return "非法宏未报错";
                return null;
            }));

            results.Add(Test("Cron：字段错误定位与段数错误", () =>
            {
                var badHour = CronExpr.Parse("0 0 99 * * *");
                if (badHour.Ok || badHour.Error.FieldKey != "hour") return "小时段错误未定位: "
                    + (badHour.Error != null ? badHour.Error.FieldKey : "null");
                var badCount = CronExpr.Parse("0 0 0 * * * *");
                if (badCount.Ok || badCount.Error.FieldKey != null) return "段数错误应无字段定位";
                if (badCount.Error.Message.IndexOf("段数不对", StringComparison.Ordinal) < 0) return "段数文案不符";
                return null;
            }));

            results.Add(Test("Cron：触发时间推算（六段固定起点）", () =>
            {
                var from = new DateTime(2026, 11, 3, 10, 0, 0);
                var result = CronExpr.NextRuns("0 0 1,5 15,30 * *", 5, from);
                if (!result.Ok) return "推算失败: " + result.Error.Message;
                if (result.Runs.Count < 4) return "推算条数不足: " + result.Runs.Count;
                if (result.Runs[0] != new DateTime(2026, 11, 15, 1, 0, 0)) return "第一次触发不符: " + result.Runs[0];
                // 小时段为 {1,5}：同一天 05:00 也是合法触发，再轮到 30 日
                if (result.Runs[1] != new DateTime(2026, 11, 15, 5, 0, 0)) return "第二次触发不符: " + result.Runs[1];
                if (result.Runs[2] != new DateTime(2026, 11, 30, 1, 0, 0)) return "第三次触发不符: " + result.Runs[2];
                if (result.Runs[3] != new DateTime(2026, 11, 30, 5, 0, 0)) return "第四次触发不符: " + result.Runs[3];
                if (result.Truncated) return "不应截断";
                return null;
            }));

            results.Add(Test("Cron：月末跳过与永不触发截断", () =>
            {
                var from = new DateTime(2026, 11, 3, 10, 0, 0);
                var month31 = CronExpr.NextRuns("0 0 0 31 * *", 5, from);
                if (!month31.Ok || month31.Runs.Count == 0) return "31 日推算失败";
                if (month31.Runs[0] != new DateTime(2026, 12, 31, 0, 0, 0)) return "11 月无 31 日应跳到 12 月: " + month31.Runs[0];
                var feb30 = CronExpr.NextRuns("0 0 0 30 2 *", 5, from);
                if (!feb30.Ok || feb30.Runs.Count != 0) return "2 月 30 日应有零次触发";
                if (feb30.Note == null || feb30.Note.IndexOf("不会触发", StringComparison.Ordinal) < 0) return "永不触发说明缺失";
                return null;
            }));

            results.Add(Test("Cron：日+周「或」规则匹配", () =>
            {
                var from = new DateTime(2026, 11, 3, 10, 0, 0); // 周二
                var result = CronExpr.NextRuns("0 0 0 1 * 1", 5, from); // 每月 1 日 或 周一
                if (!result.Ok) return "推算失败: " + result.Error.Message;
                if (result.Runs[0] != new DateTime(2026, 11, 9, 0, 0, 0)) return "下一个周一不符: " + result.Runs[0];
                foreach (var run in result.Runs)
                    if (run.Day != 1 && run.DayOfWeek != DayOfWeek.Monday) return "命中非或条件日期: " + run;
                return null;
            }));

            results.Add(Test("Cron：构造器状态往返（every/list/range/custom）", () =>
            {
                // every
                var every = CronExpr.Parse("*/5 * * * * *");
                var everyState = CronExpr.FieldToBuilder(every.Fields[0]);
                if (CronExpr.BuildField("second", everyState) != "*/5") return "every 往返不符";
                // list
                var list = CronExpr.Parse("0 1,3,5 * * * *");
                var listState = CronExpr.FieldToBuilder(list.Fields[1]);
                if (listState.Mode != "list" || CronExpr.BuildField("minute", listState) != "1,3,5") return "list 往返不符";
                // range
                var range = CronExpr.Parse("0 0 9-17/2 * * *");
                var rangeState = CronExpr.FieldToBuilder(range.Fields[2]);
                if (rangeState.Mode != "range" || CronExpr.BuildField("hour", rangeState) != "9-17/2") return "range 往返不符";
                // custom（混合段）
                var custom = CronExpr.Parse("0 0 0 1,3-5 * *");
                var customState = CronExpr.FieldToBuilder(custom.Fields[3]);
                if (customState.Mode != "custom" || CronExpr.BuildField("day", customState) != "1,3-5") return "custom 往返不符";
                // 通配
                var wild = CronExpr.Parse("0 0 0 * * *");
                var wildState = CronExpr.FieldToBuilder(wild.Fields[5]);
                if (wildState.Mode != "every" || wildState.Step.Length != 0) return "通配应还原为空 every";
                return null;
            }));

            results.Add(Test("Cron：BuildField 参数校验报错", () =>
            {
                try { CronExpr.BuildField("minute", new CronBuilderState { Mode = "every", Step = "0" }); return "步进 0 未报错"; }
                catch (CronException) { }
                try { CronExpr.BuildField("minute", new CronBuilderState { Mode = "list", Values = new List<string>() }); return "空列表未报错"; }
                catch (CronException) { }
                try { CronExpr.BuildField("hour", new CronBuilderState { Mode = "range", From = "20", To = "8" }); return "起点大于终点未报错"; }
                catch (CronException) { }
                try { CronExpr.BuildField("day", new CronBuilderState { Mode = "list", Values = new List<string> { "abc" } }); return "非整数未报错"; }
                catch (CronException) { }
                return null;
            }));

            results.Add(Test("内置命令：cron 唤醒词命中", () =>
            {
                var hit = BuiltinCommands.Match("cron");
                if (hit == null || !hit.IsCommand || hit.CommandId != "cron") return "cron 未命中";
                var partial = BuiltinCommands.Match("cro");
                if (partial == null || partial.CommandId != "cron") return "前缀未命中";
                return null;
            }));

            results.Add(Test("RegexTester：基础匹配与位置（g 全局收集）", () =>
            {
                var r = RegexTester.Evaluate(@"\d+", "a12b345x", true, false, false, false);
                if (!r.Ok) return "求值失败: " + r.Error;
                if (r.Matches.Count != 2) return "应为 2 条，实际 " + r.Matches.Count;
                if (r.Matches[0].Index != 1 || r.Matches[0].Value != "12") return "第 1 条错";
                if (r.Matches[1].Index != 4 || r.Matches[1].Value != "345") return "第 2 条错";
                return null;
            }));

            results.Add(Test("RegexTester：无 g 只收第一条（v1 语义）", () =>
            {
                var r = RegexTester.Evaluate(@"\d+", "a12b345x", false, false, false, false);
                if (r.Matches.Count != 1) return "应为 1 条，实际 " + r.Matches.Count;
                if (r.Truncated) return "不应截断";
                return null;
            }));

            results.Add(Test("RegexTester：标志位 i / m / s", () =>
            {
                var r = RegexTester.Evaluate("ABC", "xabcx", true, true, false, false);
                if (r.Matches.Count != 1) return "i 未生效";
                r = RegexTester.Evaluate("^b$", "a\nb\nc", true, false, true, false);
                if (r.Matches.Count != 1) return "m 未生效（^$ 应逐行）";
                r = RegexTester.Evaluate("a.b", "a\nb", true, false, false, true);
                if (r.Matches.Count != 1) return "s 未生效（. 应跨行）";
                r = RegexTester.Evaluate("^b$", "a\nb\nc", true, false, false, false);
                if (r.Matches.Count != 0) return "无 m 时 ^$ 不应命中";
                return null;
            }));

            results.Add(Test("RegexTester：捕获组与命名组展开顺序（命名在前、$1 起）", () =>
            {
                var r = RegexTester.Evaluate(@"(?<year>\d{4})-(\d{2})", "2026-11", true, false, false, false);
                if (r.Matches.Count != 1) return "应 1 条";
                var groups = r.Matches[0].Groups;
                if (groups.Count != 2) return "应展开 2 组，实际 " + groups.Count;
                if (groups[0].Label != "year (命名)" || groups[0].Value != "2026") return "命名组错: " + groups[0].Label;
                // .NET 实测：未命名组先编号（此模式未命名组=1）、命名组排后（year=2）→ $1="11" 与 JS 一致
                if (groups[1].Label != "$1" || groups[1].Value != "11") return "编号组错: " + groups[1].Label;
                // 纯编号组模式：$1/$2 序号照旧
                var plain = RegexTester.Evaluate(@"(\d{4})-(\d{2})", "2026-11", true, false, false, false);
                if (plain.Matches[0].Groups[0].Label != "$1" || plain.Matches[0].Groups[0].Value != "2026") return "纯编号组 $1 错";
                if (plain.Matches[0].Groups[1].Label != "$2" || plain.Matches[0].Groups[1].Value != "11") return "纯编号组 $2 错";
                return null;
            }));

            results.Add(Test("RegexTester：替换语义 $1 与 ${name}（无 g 只换第一处）", () =>
            {
                var out1 = RegexTester.Replace(@"(\d{4})-(\d{2})", "2026-11 2027-01", "$2/$1", true, false, false, false);
                if (out1 != "11/2026 01/2027") return "g 替换错: " + out1;
                var out2 = RegexTester.Replace(@"(?<m>\d{2})$", "a12", "${m}分", true, false, false, false);
                if (out2 != "a12分") return "命名替换错: " + out2;
                var out3 = RegexTester.Replace(@"\d+", "a1b2", "#", false, false, false, false);
                if (out3 != "a#b2") return "无 g 应只换第一处: " + out3;
                return null;
            }));

            results.Add(Test("RegexTester：无效正则报错与零长匹配推进不卡死", () =>
            {
                var r = RegexTester.Evaluate("([unclosed", "x", true, false, false, false);
                if (r.Ok || string.IsNullOrEmpty(r.Error)) return "无效正则未报错";
                var zero = RegexTester.Evaluate("a*", "bbb", true, false, false, false);
                if (!zero.Ok) return "零长匹配求值失败: " + zero.Error;
                if (zero.Matches.Count != 4) return "零长匹配应有限推进（b,b,b+尾），实际 " + zero.Matches.Count;
                return null;
            }));

            results.Add(Test("RegexTester：上限 200 条截断与空表达式", () =>
            {
                var text = new string('a', 500);
                var r = RegexTester.Evaluate("a", text, true, false, false, false);
                if (r.Matches.Count != RegexResult.MatchLimit || !r.Truncated) return "截断未生效";
                var empty = RegexTester.Evaluate("", text, true, false, false, false);
                if (!empty.Ok || empty.Matches.Count != 0) return "空表达式应 Ok 且 0 匹配";
                return null;
            }));

            results.Add(Test("内置命令：regex 唤醒词命中（regex/正则/前缀）", () =>
            {
                var hit = BuiltinCommands.Match("regex");
                if (hit == null || !hit.IsCommand || hit.CommandId != "regex") return "regex 未命中";
                var cn = BuiltinCommands.Match("正则");
                if (cn == null || cn.CommandId != "regex") return "正则 未命中";
                var partial = BuiltinCommands.Match("reg");
                if (partial == null || partial.CommandId != "regex") return "前缀未命中";
                return null;
            }));

            var failed = 0;
            var lines = new List<string>();
            foreach (var r in results)
            {
                var line = (r.Passed ? "[PASS] " : "[FAIL] ") + r.Name + (r.Detail != null ? "  → " + r.Detail : "");
                Console.WriteLine(line);
                lines.Add(line);
                if (!r.Passed) failed++;
            }
            var summary = failed == 0 ? "selftest: ALL PASS (" + results.Count + ")" : "selftest: " + failed + " FAILED";
            Console.WriteLine(summary);
            lines.Add(summary);
            try
            {
                // stdout 在部分宿主下不可见（GUI 进程），落盘一份供自动化读取（带 BOM，PowerShell 5.1 按 ANSI 读会乱码）
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "pytools-selftest-last.txt"), string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(true));
            }
            catch { /* 落盘失败不影响退出码语义 */ }
            return failed == 0 ? 0 : 1;
        }

        private static string TempClipDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pytools-selftest-clip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryDeleteDir(string dir)
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        private static byte[] MakePngBytes()
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgr32, null)));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        private static Result Test(string name, Func<string> body)
        {
            try
            {
                var detail = body();
                return new Result { Name = name, Passed = detail == null, Detail = detail };
            }
            catch (Exception ex)
            {
                return new Result { Name = name, Passed = false, Detail = ex.Message };
            }
        }
    }
}
