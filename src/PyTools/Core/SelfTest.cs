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
