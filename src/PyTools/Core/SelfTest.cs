using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            results.Add(Test("多屏定位：ClampIntoWork 钳制口径", () =>
            {
                var work = new Rect(0, 0, 1920, 1040);
                var inBounds = ScreenLayout.ClampIntoWork(work, 100, 200, 640, 80, 120);
                if (inBounds.Item1 != 100 || inBounds.Item2 != 200) return "屏内位置被误钳";
                var overRight = ScreenLayout.ClampIntoWork(work, 1900, 200, 640, 80, 120);
                if (overRight.Item1 != 1840) return "右溢出未钳: " + overRight.Item1; // 1920-80
                var overBottom = ScreenLayout.ClampIntoWork(work, 100, 1200, 640, 80, 120);
                if (overBottom.Item2 != 920) return "下溢出未钳: " + overBottom.Item2; // 1040-120
                var neg = ScreenLayout.ClampIntoWork(work, -300, -500, 640, 80, 120);
                if (neg.Item1 != -300 || neg.Item2 != -500) return "上/左贴边被误钳"; // 只钳右/下
                var empty = ScreenLayout.ClampIntoWork(Rect.Empty, 50, 60, 640, 80, 120);
                if (empty.Item1 != 50 || empty.Item2 != 60) return "空工作区应原样返回";
                return null;
            }));

            results.Add(Test("多屏定位：呼出偏移字段读写与 NaN 不落盘", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "pytools-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    // 旧配置（无偏移键）→ 读回 NaN
                    File.WriteAllText(path, "{\"v2.pageStep\":5}");
                    var cfg = AppConfig.Load(path);
                    if (!double.IsNaN(cfg.SummonOffsetX) || !double.IsNaN(cfg.SummonOffsetY)) return "旧配置应读回 NaN";
                    cfg.SaveBounds(1, 2, 640, 420); // NaN 期间落盘不得把 NaN 写进 JSON
                    var mid = File.ReadAllText(path);
                    if (mid.Contains("summonOffset")) return "NaN 偏移被序列化";
                    // 保存偏移 → 重载读回
                    cfg.SaveSummonOffset(120.5, 240.25);
                    var reloaded = AppConfig.Load(path);
                    if (reloaded.SummonOffsetX != 120.5 || reloaded.SummonOffsetY != 240.25)
                        return "偏移回读不符: " + reloaded.SummonOffsetX + "," + reloaded.SummonOffsetY;
                    if (!File.ReadAllText(path).Contains("v2.summonOffsetX")) return "偏移键未落盘";
                    // 恢复默认位置清除
                    reloaded.ResetBounds();
                    var cleared = AppConfig.Load(path);
                    if (!double.IsNaN(cleared.SummonOffsetX) || !double.IsNaN(cleared.SummonOffsetY)) return "ResetBounds 未清偏移";
                    return null;
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

            results.Add(Test("启动：lnk 带参条目的启动依据取解析后真实目标", () =>
            {
                var lnk = new AppEntry
                {
                    Name = "Google Chrome",
                    Path = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Google Chrome.lnk",
                    TargetPath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                    Args = "--source-shortcut-location=start-menu"
                };
                var real = AppLauncher.RealTarget(lnk);
                if (real == null || !real.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase))
                    return "lnk 未取到真实目标: " + (real ?? "null");

                // 登记条目与扫描到的 exe 条目 Path==TargetPath，不应产生额外跳转
                var exe = new AppEntry { Path = @"C:\Apps\demo.exe", TargetPath = @"C:\Apps\demo.exe" };
                if (AppLauncher.RealTarget(exe) != null) return "exe 条目不应返回真实目标";

                // 未展开环境变量 / 空目标 → 不可用
                var env = new AppEntry { Path = @"C:\x\a.lnk", TargetPath = @"%ProgramFiles%\Google\Chrome\Application\chrome.exe" };
                if (AppLauncher.RealTarget(env) != null) return "未展开环境变量被当作真实目标";
                if (AppLauncher.RealTarget(new AppEntry { Path = @"C:\x\a.lnk" }) != null) return "空目标应返回 null";
                return null;
            }));

            results.Add(Test("启动：lnk 目标缺失按「目标不存在」处理（不再对 lnk 报 193）", () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "pytools-selftest-lnk-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(root);
                    var lnk = Path.Combine(root, "Chrome.lnk");
                    File.WriteAllText(lnk, "dummy lnk");       // 只为让 lnk 本体存在
                    var gone = Path.Combine(root, "chrome.exe"); // 目标不存在
                    var entry = new AppEntry { Name = "Chrome", Path = lnk, TargetPath = gone, Args = "--source-shortcut-location=start-menu" };
                    var custom = new CustomApps(Path.Combine(Path.GetTempPath(), "pytools-selftest-none-" + Guid.NewGuid().ToString("N") + ".json"));
                    var result = AppLauncher.Open(entry, new List<string> { root }, custom);
                    if (result == null) return "目标缺失却报告启动成功";
                    if (result.Ok) return "目标缺失不应返回 Ok";
                    return result.Message.Contains("目标不存在") ? null : "文案: " + result.Message;
                }
                finally { try { Directory.Delete(root, true); } catch { } }
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

            results.Add(Test("内置命令：tools 工具列表入口与 FilterToolList 口径", () =>
            {
                var entry = BuiltinCommands.Match("tools");
                if (entry == null || entry.CommandId != "tools") return "tools 入口未命中";
                var cn = BuiltinCommands.Match("工具");
                if (cn == null || cn.CommandId != "tools") return "中文别名「工具」未命中";

                var all = BuiltinCommands.All();
                var filtered = BuiltinCommands.FilterToolList("");
                if (filtered.Count != all.Count) return "空查询应列全部";
                foreach (var item in filtered)
                    if (item.CommandId == "tools") return "工具列表不应包含自身";
                if (!filtered.Any(e => e.CommandId == "clipboard")) return "工具清单缺剪贴板记录";
                if (Pinyin.Ready)
                {
                    var initials = BuiltinCommands.FilterToolList("jtbjl");
                    if (!initials.Any(e => e.CommandId == "clipboard")) return "工具列表拼音首字母未命中";
                }
                var cn2 = BuiltinCommands.FilterToolList("正则");
                if (!cn2.Any(e => e.CommandId == "regex")) return "工具列表中文别名未命中";
                var bySubtitle = BuiltinCommands.FilterToolList("json");
                if (!bySubtitle.Any(e => e.CommandId == "format")) return "副标题子串未命中";
                return null;
            }));

            results.Add(Test("内置命令：图标资源键全覆盖（Icons.xaml 与条目一一对应）", () =>
            {
                var icons = new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Icons.xaml") };
                foreach (var command in BuiltinCommands.All())
                {
                    if (command.IconKey.Length == 0) return "工具缺图标键: " + command.CommandId;
                    if (!(icons[command.IconKey] is Geometry)) return "图标键无对应几何: " + command.IconKey;
                }
                var toolList = BuiltinCommands.Match("tools");
                if (toolList.IconKey.Length != 0) return "工具列表入口不应有图标（走首字回退）";
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

            // ---- 文件差异对比（M4，diff-worker.js 移植）----

            results.Add(Test("DiffEngine：相同文本零差异、行号连续、CRLF 与 LF 等价", () =>
            {
                var same = DiffEngine.Compare("a\nb\nc", "a\nb\nc");
                if (same.DifferenceCount != 0) return "相同文本差异数=" + same.DifferenceCount;
                if (same.Rows.Count != 3) return "行数=" + same.Rows.Count;
                if (same.Rows[2].LeftNumber != 3 || same.Rows[2].RightNumber != 3) return "行号不连续";
                var crlf = DiffEngine.Compare("a\r\nb", "a\nb");
                if (crlf.DifferenceCount != 0) return "CRLF/LF 应等价（换行协议不计内容）";
                return null;
            }));

            results.Add(Test("DiffEngine：change 配对与行内差异（行尾 / 行头）", () =>
            {
                var tail = DiffEngine.Compare("timeout = 3000", "timeout = 5000");
                if (tail.Rows.Count != 1 || tail.Rows[0].Kind != DiffKind.Change) return "应为 1 个 change 行";
                var leftChanged = string.Concat(tail.Rows[0].LeftParts.Where(p => p.Changed).Select(p => p.Text));
                var rightChanged = string.Concat(tail.Rows[0].RightParts.Where(p => p.Changed).Select(p => p.Text));
                if (leftChanged != "3" || rightChanged != "5") return "行尾行内差异错：" + leftChanged + " / " + rightChanged;

                var head = DiffEngine.Compare("retries = 3", "max_retries = 3");
                if (head.Rows[0].Kind != DiffKind.Change) return "行头场景应为 change";
                var headAdded = string.Concat(head.Rows[0].RightParts.Where(p => p.Changed).Select(p => p.Text));
                if (headAdded != "max_") return "行头行内差异错：" + headAdded;
                var headRemoved = string.Concat(head.Rows[0].LeftParts.Where(p => p.Changed).Select(p => p.Text));
                if (headRemoved.Length != 0) return "行头左侧不应有删除段：" + headRemoved;
                return null;
            }));

            results.Add(Test("DiffEngine：纯增 / 纯删产生空档行且行号归零", () =>
            {
                var inserted = DiffEngine.Compare("a\nb", "a\nx\nb");
                if (inserted.Rows.Count != 3) return "插入场景行数=" + inserted.Rows.Count;
                var ins = inserted.Rows[1];
                if (ins.Kind != DiffKind.Insert || ins.Left != null || ins.Right != "x") return "插入行错位";
                if (ins.LeftNumber != 0 || ins.RightNumber != 2) return "空档行号应为 0";
                if (ins.LeftNumberText != "" || ins.RightNumberText != "2") return "行号文案错";

                var deleted = DiffEngine.Compare("a\nx\nb", "a\nb");
                var del = deleted.Rows[1];
                if (del.Kind != DiffKind.Delete || del.Right != null || del.Left != "x") return "删除行错位";
                if (del.LeftNumber != 2 || del.RightNumber != 0) return "删除行行号错";
                return null;
            }));

            results.Add(Test("DiffEngine：零归一化（空白与标签原样比较）", () =>
            {
                var space = DiffEngine.Compare("a", "a ");
                if (space.DifferenceCount != 1 || space.Rows[0].Kind != DiffKind.Change) return "行尾空格应算差异";
                var tag = DiffEngine.Compare("<b>x</b>", "x");
                if (tag.DifferenceCount != 1) return "<b> 标签应原样参与比较（v1 的剥标签补丁已废除）";
                var fullwidth = DiffEngine.Compare("ａ", "a");
                if (fullwidth.DifferenceCount != 1) return "全半角应算差异";
                return null;
            }));

            results.Add(Test("DiffEngine：重复行（无唯一锚点）不崩且逐行配对", () =>
            {
                var left = string.Join("\n", Enumerable.Repeat("same", 40));
                var right = string.Join("\n", Enumerable.Repeat("other", 40));
                var r = DiffEngine.Compare(left, right);
                if (r.Rows.Count != 40) return "行数=" + r.Rows.Count;
                if (r.DifferenceCount != 40) return "差异数=" + r.DifferenceCount;
                foreach (var row in r.Rows)
                    if (row.Kind != DiffKind.Change) return "应全部为 change：" + row.Kind;
                return null;
            }));

            results.Add(Test("TextFileLoader：编码检测（UTF-8 / BOM / GBK / UTF-16）", () =>
            {
                string name;
                if (TextFileLoader.Decode(Encoding.UTF8.GetBytes("中文"), out name) != "中文" || name != "UTF-8")
                    return "无 BOM UTF-8 检测失败: " + name;
                var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("中文")).ToArray();
                if (TextFileLoader.Decode(bom, out name) != "中文" || name != "UTF-8 BOM")
                    return "UTF-8 BOM 检测失败: " + name;
                var gbk = TextFileLoader.Decode(Encoding.GetEncoding(936).GetBytes("中文测试"), out name);
                if (gbk != "中文测试" || name != "GBK") return "GBK 检测失败: " + name + " / " + gbk;
                var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("中文")).ToArray();
                if (TextFileLoader.Decode(utf16, out name) != "中文" || name != "UTF-16 LE")
                    return "UTF-16 LE BOM 检测失败: " + name;
                return null;
            }));

            results.Add(Test("内置命令：diff 唤醒词命中（diff/对比/前缀）", () =>
            {
                var hit = BuiltinCommands.Match("diff");
                if (hit == null || !hit.IsCommand || hit.CommandId != "diff") return "diff 未命中";
                var cn = BuiltinCommands.Match("对比");
                if (cn == null || cn.CommandId != "diff") return "对比 未命中";
                var partial = BuiltinCommands.Match("dif");
                if (partial == null || partial.CommandId != "diff") return "前缀未命中";
                return null;
            }));

            results.Add(Test("DiffEngine：差异跳转索引与前后查找（含回绕）", () =>
            {
                // 行模型：equal / change / equal / insert / equal / delete / equal
                var r = DiffEngine.Compare("a\nb\nc\nd\ne\nf\ng", "a\nX\nc\nNEW\ne\ng");
                var indexes = DiffEngine.DiffLineIndexes(r.Rows);
                if (indexes.Count != 3) return "差异行索引数=" + indexes.Count;

                bool wrapped;
                var first = DiffEngine.FindJumpTarget(indexes, 0, true, out wrapped);
                if (wrapped || indexes[first] != indexes[0]) return "从头向后应命中第一处";
                var second = DiffEngine.FindJumpTarget(indexes, indexes[0], true, out wrapped);
                if (wrapped || indexes[second] <= indexes[0]) return "第二处应大于第一处";
                var last = DiffEngine.FindJumpTarget(indexes, indexes[1], true, out wrapped);
                if (wrapped || indexes[last] != indexes[2]) return "第三处应为最后一个差异行";
                var loop = DiffEngine.FindJumpTarget(indexes, indexes[2], true, out wrapped);
                if (!wrapped || loop != 0) return "到底应回绕到第一处";

                var back = DiffEngine.FindJumpTarget(indexes, indexes[2], false, out wrapped);
                if (wrapped || indexes[back] >= indexes[2]) return "向前应命中上一个差异行";
                var loopBack = DiffEngine.FindJumpTarget(indexes, 0, false, out wrapped);
                if (!wrapped || loopBack != indexes.Count - 1) return "到顶应回绕到最后一处";
                return null;
            }));

            results.Add(Test("DiffEngine：无差异跳转返回 -1、不可见字符判定", () =>
            {
                bool wrapped;
                if (DiffEngine.FindJumpTarget(new List<int>(), 0, true, out wrapped) != -1) return "空索引应返回 -1";
                if (!DiffEngine.IsInvisibleChar('\u200B')) return "零宽空格应判为不可见";
                if (!DiffEngine.IsInvisibleChar('\uFEFF')) return "BOM/零宽不换行空格应判为不可见";
                if (!DiffEngine.IsInvisibleChar('\u00AD')) return "软连字符应判为不可见";
                if (DiffEngine.IsInvisibleChar(' ') || DiffEngine.IsInvisibleChar('\t') || DiffEngine.IsInvisibleChar('a'))
                    return "普通空格/Tab/字母不应判为不可见";
                return null;
            }));

            results.Add(Test("DiffEngine：不可见字符差异如实报告且片段标记 Changed", () =>
            {
                var r = DiffEngine.Compare("abc", "abc\u200Bdef");
                if (r.DifferenceCount != 1) return "零宽空格差异应算 1 处";
                var row = r.Rows[0];
                if (row.Kind != DiffKind.Change) return "应为 change 行";
                var rightChanged = row.RightParts.Where(delegate(DiffPart p) { return p.Changed && DiffEngine.ContainsInvisible(p.Text); }).ToList();
                if (rightChanged.Count == 0) return "右侧应有含不可见字符的差异片段";
                return null;
            }));

            // ---- AI 共享层（M4 AI 批，AiClient 离线部分；网络请求不进自测）----

            results.Add(Test("AiClient：Status 六态逐项给原因", () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "pytools-selftest-ai-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(dir);
                    string localPath = Path.Combine(dir, "ai-local.json");
                    if (AiClient.Status(dir).Available || AiClient.Status(dir).Reason != "未启用")
                        return "缺配置应判 未启用";
                    File.WriteAllText(localPath, "{\"enabled\":true}");
                    if (AiClient.Status(dir).Reason != "provider 不支持") return "缺 provider 应判 provider 不支持";
                    File.WriteAllText(localPath, "{\"enabled\":true,\"provider\":\"internal-openai-compatible\"}");
                    if (AiClient.Status(dir).Reason != "服务地址缺失") return "缺 baseUrl 应判 服务地址缺失";
                    File.WriteAllText(localPath, "{\"enabled\":true,\"provider\":\"internal-openai-compatible\",\"baseUrl\":\"http://x\"}");
                    if (AiClient.Status(dir).Reason != "模型缺失") return "缺 model 应判 模型缺失";
                    File.WriteAllText(localPath, "{\"enabled\":true,\"provider\":\"internal-openai-compatible\",\"baseUrl\":\"http://x\",\"model\":\"m\"}");
                    if (AiClient.Status(dir).Reason != "密钥缺失") return "缺 apiKey 应判 密钥缺失";
                    File.WriteAllText(localPath, "{\"enabled\":true,\"provider\":\"internal-openai-compatible\",\"baseUrl\":\"http://x\",\"model\":\"m\",\"apiKey\":\"k\"}");
                    if (!AiClient.Status(dir).Available) return "全字段齐应可用";
                    // v1 合并语义：config.json 的 ai.enabled 也能点亮
                    File.WriteAllText(localPath, "{\"provider\":\"internal-openai-compatible\",\"baseUrl\":\"http://x\",\"model\":\"m\",\"apiKey\":\"k\"}");
                    File.WriteAllText(Path.Combine(dir, "config.json"), "{\"ai\":{\"enabled\":true}}");
                    if (!AiClient.Status(dir).Available) return "config.json ai.enabled 合并判断失效";
                    return null;
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }));

            results.Add(Test("AiClient：请求体构建（tools/tool_choice 可关、max_tokens、temperature）", () =>
            {
                var cfg = new AiClient.AiLocalConfig
                {
                    Model = "m1", BaseUrl = "http://x/", ApiKey = "k",
                    Temperature = 0.2, MaxTokens = 512,
                };
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                var withTools = serializer.Serialize(AiClient.BuildChatPayload(cfg, "目标", true));
                if (!withTools.Contains("\"tool_choice\"")) return "tools 开时缺 tool_choice";
                if (!withTools.Contains("regex_generate")) return "缺函数名";
                if (!withTools.Contains("\"max_tokens\":512")) return "max_tokens 未带上";
                var noTools = serializer.Serialize(AiClient.BuildChatPayload(cfg, "目标", false));
                if (noTools.Contains("tool_choice") || noTools.Contains("tools")) return "tools 关时不应出现 tools 字段";
                if (!noTools.Contains("\"temperature\":0.2")) return "temperature 默认 0.2";
                return null;
            }));

            results.Add(Test("AiClient：响应解析 tool_calls 路径 + flags 过滤", () =>
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                var args = new Dictionary<string, object>
                {
                    { "pattern", @"\d+" }, { "flags", new object[] { "g", "x", "i" } },
                    { "confidence", 90 }, { "reason", "数字序列" },
                };
                var raw = serializer.Serialize(new Dictionary<string, object>
                {
                    {
                        "choices", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                {
                                    "message", new Dictionary<string, object>
                                    {
                                        {
                                            "tool_calls", new object[]
                                            {
                                                new Dictionary<string, object>
                                                {
                                                    { "function", new Dictionary<string, object> { { "arguments", serializer.Serialize(args) } } },
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                });
                var parsed = AiClient.ParseResponseBody(raw);
                if (parsed.Error.Length > 0) return "tool_calls 解析报错: " + parsed.Error;
                if (parsed.Pattern != @"\d+") return "pattern = " + parsed.Pattern;
                if (parsed.Flags.Count != 2 || parsed.Flags[0] != "g" || parsed.Flags[1] != "i") return "flags 应过滤为 g,i";
                if (parsed.Confidence != 90 || parsed.Reason != "数字序列") return "confidence/reason 丢失";
                return null;
            }));

            results.Add(Test("AiClient：响应解析 content JSON（围栏）路径", () =>
            {
                var content = "```json\n{\"pattern\":\"[a-z]+\",\"flags\":[\"i\"],\"confidence\":50,\"reason\":\"字母\"}\n```";
                var raw = "{\"choices\":[{\"message\":{\"content\":" +
                          new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(content) + "}}]}";
                var parsed = AiClient.ParseResponseBody(raw);
                if (parsed.Error.Length > 0) return "content JSON 解析报错: " + parsed.Error;
                if (parsed.Pattern != "[a-z]+" || parsed.Confidence != 50) return "pattern/confidence 不符";
                return null;
            }));

            results.Add(Test("AiClient：响应解析 ```regex 围栏与行内正则兜底", () =>
            {
                var fence = AiClient.ParseContent("```regex\nabc\\s+\n```");
                if (fence == null || fence.Pattern != "abc\\s+") return "围栏路径: " + (fence == null ? "null" : fence.Pattern);
                var labeled = AiClient.ParseContent("```\nregex\n\\d{4}\n```");
                if (labeled == null || labeled.Pattern != "\\d{4}") return "regex 标签行未跳过";
                var inline = AiClient.ParseContent("  \\d{4}-\\d{2} 表示年月");
                if (inline == null || inline.Pattern != "\\d{4}-\\d{2} 表示年月") return "行内兜底: " + (inline == null ? "null" : inline.Pattern);
                return null;
            }));

            results.Add(Test("AiClient：服务端 error 提取与非 JSON 判定", () =>
            {
                var detail = AiClient.ParseResponseBody("{\"error\":{\"message\":\"余额不足\"}}");
                if (detail.Error != "余额不足") return "error.message 未提取: " + detail.Error;
                var codeOnly = AiClient.ParseResponseBody("{\"error\":{\"code\":\"rate_limited\"}}");
                if (codeOnly.Error != "rate_limited") return "error.code 未提取";
                var notJson = AiClient.ParseResponseBody("not json at all");
                if (notJson.Error != "AI 返回不是 JSON") return "非 JSON 判定: " + notJson.Error;
                var noPattern = AiClient.ParseResponseBody("{\"choices\":[{\"message\":{\"content\":\"普通文本\"}}]}");
                if (noPattern.Error != "AI 返回内容缺少 pattern") return "缺 pattern 判定: " + noPattern.Error;
                return null;
            }));

            results.Add(Test("AiClient：/models 清单解析（提取/去重/畸形）", () =>
            {
                var ok = AiClient.ParseModelList("{\"data\":[{\"id\":\"b-model\"},{\"id\":\"a-model\"},{\"id\":\"b-model\"},{},{}]}");
                if (ok.Error.Length > 0) return "正常清单报错: " + ok.Error;
                if (ok.Models.Count != 2 || ok.Models[0] != "a-model" || ok.Models[1] != "b-model") return "清单应为排序去重的 2 项; count=" + ok.Models.Count;
                var empty = AiClient.ParseModelList("{\"data\":[]}");
                if (empty.Error.Length > 0 || empty.Models.Count != 0) return "空清单应成功且为空";
                var bad = AiClient.ParseModelList("oops");
                if (bad.Error != "AI 返回不是 JSON") return "畸形响应判定: " + bad.Error;
                var err = AiClient.ParseModelList("{\"error\":{\"message\":\"denied\"}}");
                if (err.Error != "denied") return "服务端错误未透传";
                return null;
            }));

            results.Add(Test("AiClient：tool_choice 降级判断（2026-11-05 真机错误文本）", () =>
            {
                // DeepSeek 思考模型实测 400 错误体，Request 会把该文本包进 WebException.Message
                if (!AiClient.IsToolChoiceError("Thinking mode does not support this tool_choice (request_id: b1cab976-e0e5-41a1-82a2-0b330434)")) return "真机错误文本未命中降级";
                if (!AiClient.IsToolChoiceError("{\"error\":{\"message\":\"tool_choice is not supported\"}}")) return "错误 JSON 文本未命中降级";
                if (AiClient.IsToolChoiceError("连接超时（30s）")) return "超时错误不应触发降级";
                if (AiClient.IsToolChoiceError("无法解析服务地址")) return "网络错误不应触发降级";
                if (AiClient.IsToolChoiceError(null)) return "null 不应命中";
                return null;
            }));

            results.Add(Test("AiClient：SelectModel 写回保留未知键 + 只读失败", () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "pytools-selftest-ai-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, "ai-local.json");
                    File.WriteAllText(path, "{\"enabled\":true,\"model\":\"a\",\"customKey\":123}");
                    if (AiClient.SelectModel("b", dir) != null) return "正常写回应成功";
                    var after = File.ReadAllText(path);
                    if (!after.Contains("\"model\":\"b\"")) return "model 未写回: " + after;
                    if (!after.Contains("customKey")) return "未知键丢失";
                    if (AiClient.CurrentModel(dir) != "b") return "CurrentModel 读不到新值";
                    File.SetAttributes(path, FileAttributes.ReadOnly);
                    var error = AiClient.SelectModel("c", dir);
                    if (error == null) return "只读文件应报错";
                    return null;
                }
                finally
                {
                    try
                    {
                        var path = Path.Combine(dir, "ai-local.json");
                        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
                        Directory.Delete(dir, true);
                    }
                    catch { }
                }
            }));

            // ---- AI 图片识别（ai-ocr，M4 AI 批第二消费者；离线部分）----

            results.Add(Test("AiClient：OCR 请求体（图文 content 数组、无 tools、temperature 0）", () =>
            {
                var cfg = new AiClient.AiLocalConfig { Model = "m1", BaseUrl = "http://x", ApiKey = "k", MaxTokens = 2048 };
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                var json = serializer.Serialize(AiClient.BuildOcrPayload(cfg, "data:image/png;base64,AAAA"));
                if (!json.Contains("\"image_url\"")) return "缺 image_url 内容块";
                if (!json.Contains("data:image/png;base64,AAAA")) return "dataUrl 未原样进入请求";
                if (!json.Contains("\"temperature\":0")) return "OCR 应固定 temperature=0";
                if (json.Contains("\"tools\"")) return "OCR 不应带 tools";
                if (!json.Contains("\"model\":\"m1\"")) return "缺 model";
                if (!json.Contains("\"max_tokens\":2048")) return "缺 max_tokens";
                return null;
            }));

            results.Add(Test("AiClient：OCR 响应解析（content JSON / 围栏 / 纯文本兜底 / error）", () =>
            {
                var direct = AiClient.ParseOcrResponse("{\"choices\":[{\"message\":{\"content\":\"{\\\"text\\\":\\\"发票号 1234\\\"}\"}}]}");
                if (direct.Text != "发票号 1234") return "content JSON 解析失败: " + direct.Error;

                var fenced = AiClient.ParseOcrResponse("{\"choices\":[{\"message\":{\"content\":\"```json\\n{\\\"text\\\":\\\"第二行\\\"}\\n```\"}}]}");
                if (fenced.Text != "第二行") return "围栏 JSON 未解析: " + fenced.Error;

                var plain = AiClient.ParseOcrResponse("{\"choices\":[{\"message\":{\"content\":\"模型直接吐了纯文本\"}}]}");
                if (plain.Text != "模型直接吐了纯文本") return "纯文本兜底失效: " + plain.Error;

                var modelError = AiClient.ParseOcrResponse("{\"choices\":[{\"message\":{\"content\":\"{\\\"text\\\":\\\"\\\",\\\"error\\\":\\\"图片过于模糊\\\"}\"}}]}");
                if (modelError.Text.Length > 0 || modelError.Error != "图片过于模糊") return "模型 error 字段未透出: " + modelError.Error;

                var serverError = AiClient.ParseOcrResponse("{\"error\":{\"message\":\"余额不足\"}}");
                if (serverError.Error != "余额不足") return "服务端 error 未透出";

                var notJson = AiClient.ParseOcrResponse("整段非 JSON 的识别正文");
                if (notJson.Text != "整段非 JSON 的识别正文") return "非 JSON 整段兜底失效";

                var empty = AiClient.ParseOcrResponse("{\"choices\":[{\"message\":{\"content\":\"\"}}]}");
                if (empty.Error.Length == 0) return "空内容应报错";
                return null;
            }));

            // ---- AI 翻译（ai-translate，M4 AI 批第三消费者；离线部分）----

            results.Add(Test("AiClient：翻译请求体（纯文本 content、无 tools、temperature 0）", () =>
            {
                var cfg = new AiClient.AiLocalConfig { Model = "m1", BaseUrl = "http://x", ApiKey = "k" };
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                var json = serializer.Serialize(AiClient.BuildTranslatePayload(cfg, "", "zh-CN", "hello"));
                // user 字典作为字符串塞进 content，内层引号被转义（\"）：断言按转义后的形态匹配
                if (!json.Contains("sourceLang\\\":\\\"\\\"")) return "源语言未知应传空串（由模型自判）";
                if (!json.Contains("targetLang\\\":\\\"zh-CN\\\"")) return "缺 targetLang";
                if (!json.Contains("text\\\":\\\"hello\\\"")) return "缺 text";
                if (!json.Contains("\"temperature\":0")) return "翻译应固定 temperature=0";
                if (json.Contains("\"tools\"")) return "翻译不应带 tools";
                if (!json.Contains("\"model\":\"m1\"")) return "缺 model";
                return null;
            }));

            results.Add(Test("AiClient：翻译响应解析（content JSON / 围栏 / detectedLang / 纯文本兜底 / error）", () =>
            {
                var direct = AiClient.ParseTranslateResponse("{\"choices\":[{\"message\":{\"content\":\"{\\\"translation\\\":\\\"你好\\\",\\\"detectedLang\\\":\\\"en\\\"}\"}}]}");
                if (direct.Translation != "你好") return "content JSON 解析失败: " + direct.Error;
                if (direct.DetectedLang != "en") return "detectedLang 未透出: " + direct.DetectedLang;

                var fenced = AiClient.ParseTranslateResponse("{\"choices\":[{\"message\":{\"content\":\"```json\\n{\\\"translation\\\":\\\"第二行\\\"}\\n```\"}}]}");
                if (fenced.Translation != "第二行") return "围栏 JSON 未解析: " + fenced.Error;

                var plain = AiClient.ParseTranslateResponse("{\"choices\":[{\"message\":{\"content\":\"模型直接吐了译文\"}}]}");
                if (plain.Translation != "模型直接吐了译文") return "纯文本兜底失效: " + plain.Error;

                var modelError = AiClient.ParseTranslateResponse("{\"choices\":[{\"message\":{\"content\":\"{\\\"translation\\\":\\\"\\\",\\\"error\\\":\\\"原文为空\\\"}\"}}]}");
                if (modelError.Translation.Length > 0 || modelError.Error != "原文为空") return "模型 error 字段未透出: " + modelError.Error;

                var serverError = AiClient.ParseTranslateResponse("{\"error\":{\"message\":\"余额不足\"}}");
                if (serverError.Error != "余额不足") return "服务端 error 未透出";

                var notJson = AiClient.ParseTranslateResponse("整段非 JSON 的译文");
                if (notJson.Translation != "整段非 JSON 的译文") return "非 JSON 整段兜底失效";

                var empty = AiClient.ParseTranslateResponse("{\"choices\":[{\"message\":{\"content\":\"\"}}]}");
                if (empty.Error.Length == 0) return "空内容应报错";
                return null;
            }));

            // ---- AI 代码命名（code-naming，M4 AI 批第四消费者；离线部分）----

            results.Add(Test("AiClient：命名请求体（input/exclude 序列化、无 tools、temperature 0）+ 输入校验", () =>
            {
                var cfg = new AiClient.AiLocalConfig { Model = "m1", BaseUrl = "http://x", ApiKey = "k" };
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                var json = serializer.Serialize(AiClient.BuildNamingPayload(cfg, "order time desc", new[] { "orderTime", "placedAt" }));
                if (!json.Contains("input\\\":\\\"order time desc")) return "缺 input";
                if (!json.Contains("orderTime")) return "exclude 未序列化";
                if (!json.Contains("\"temperature\":0")) return "命名应固定 temperature=0";
                if (json.Contains("\"tools\"")) return "命名不应带 tools";
                if (!json.Contains("\"model\":\"m1\"")) return "缺 model";

                if (AiClient.ValidateNamingInput("   ") != "请输入名词或业务描述") return "空白输入应拒绝";
                var tooLong = new string('x', AiClient.NamingMaxInput + 1);
                if (AiClient.ValidateNamingInput(tooLong).Length == 0) return "超长输入应拒绝";
                if (AiClient.ValidateNamingInput(new string('x', AiClient.NamingMaxInput)).Length != 0) return "上限内输入不应拒绝";

                var normalized = AiClient.NormalizeNamingExclude(new[] { " a ", "", "a", "b" });
                if (normalized.Count != 2 || normalized[0] != "a" || normalized[1] != "b")
                    return "exclude 规范化（trim/去空/去重）失效";
                return null;
            }));

            results.Add(Test("AiClient：命名响应解析（白名单过滤 / 非法取值 / 批内去重 / 组序重排）", () =>
            {
                var raw = "{\"choices\":[{\"message\":{\"content\":\"{\\\"core\\\":\\\"order time\\\",\\\"note\\\":\\\"动词+名词\\\","
                    + "\\\"items\\\":["
                    + "{\\\"group\\\":\\\"表名\\\",\\\"style\\\":\\\"snake_case\\\",\\\"value\\\":\\\"order_records\\\"},"
                    + "{\\\"group\\\":\\\"变量\\\",\\\"style\\\":\\\"camelCase\\\",\\\"value\\\":\\\"orderTime\\\"},"
                    + "{\\\"group\\\":\\\"颜色\\\",\\\"style\\\":\\\"camelCase\\\",\\\"value\\\":\\\"colorName\\\"},"
                    + "{\\\"group\\\":\\\"类\\\",\\\"style\\\":\\\"PascalCase\\\",\\\"value\\\":\\\"OrderRecord\\\"},"
                    + "{\\\"group\\\":\\\"字段\\\",\\\"style\\\":\\\"snake_case\\\",\\\"value\\\":\\\"order time\\\"},"
                    + "{\\\"group\\\":\\\"方法\\\",\\\"style\\\":\\\"camelCase\\\",\\\"value\\\":\\\"orderTime\\\"}]}\"}}]}";
                var parsed = AiClient.ParseNamingResponse(raw);
                if (parsed.Items.Count != 3) return "应过滤非白名单组/非法取值/重复值，实得 " + parsed.Items.Count + "（" + parsed.Error + "）";
                if (parsed.Items[0].Group != "变量" || parsed.Items[0].Value != "orderTime") return "组序重排失效（变量应在前）";
                if (parsed.Items[1].Group != "类") return "组序重排失效（类应在第二）";
                if (parsed.Items[2].Group != "表名") return "组序重排失效（表名应在第三）";
                if (parsed.Core != "order time" || parsed.Note.Length == 0) return "core/note 未透出";
                return null;
            }));

            results.Add(Test("AiClient：命名响应解析（围栏 / 服务端 error / 跨批去重 / 空候选报错）", () =>
            {
                var fenced = AiClient.ParseNamingResponse("{\"choices\":[{\"message\":{\"content\":\"```json\\n{\\\"items\\\":[{\\\"group\\\":\\\"常量\\\",\\\"style\\\":\\\"UPPER_SNAKE_CASE\\\",\\\"value\\\":\\\"MAX_RETRY\\\"}]}\\n```\"}}]}");
                if (fenced.Items.Count != 1 || fenced.Items[0].Value != "MAX_RETRY") return "围栏 items 未解析: " + fenced.Error;

                var dedup = AiClient.ParseNamingResponse("{\"choices\":[{\"message\":{\"content\":\"{\\\"items\\\":[{\\\"group\\\":\\\"变量\\\",\\\"style\\\":\\\"camelCase\\\",\\\"value\\\":\\\"orderTime\\\"},{\\\"group\\\":\\\"类\\\",\\\"style\\\":\\\"PascalCase\\\",\\\"value\\\":\\\"OrderRecord\\\"}]}\"}}]}", new[] { "orderTime" });
                if (dedup.Items.Count != 1 || dedup.Items[0].Value != "OrderRecord") return "跨批去重失效";

                var serverError = AiClient.ParseNamingResponse("{\"error\":{\"message\":\"余额不足\"}}");
                if (serverError.Error != "余额不足") return "服务端 error 未透出";

                var empty = AiClient.ParseNamingResponse("{\"choices\":[{\"message\":{\"content\":\"{\\\"items\\\":[]}\"}}]}");
                if (empty.Error != "AI 返回内容缺少可用命名") return "空候选应报错: " + empty.Error;

                var notJson = AiClient.ParseNamingResponse("这不是 JSON");
                if (notJson.Error.Length == 0) return "非 JSON 应报错";
                return null;
            }));

            results.Add(Test("OcrImage：文件白名单 + 大小上限 + 解码校验", () =>
            {
                var dir = TempClipDir();
                try
                {
                    var pngPath = Path.Combine(dir, "shot.png");
                    File.WriteAllBytes(pngPath, MakePngBytes());
                    var ok = OcrImage.FromFile(pngPath);
                    if (!ok.Ok) return "合法 png 应可装载: " + ok.Error;
                    if (ok.Width != 2 || ok.Height != 2) return "尺寸读取错误: " + ok.Width + "x" + ok.Height;
                    if (ok.Mime != "image/png") return "mime 应为 image/png";
                    if (!ok.DataUrl.StartsWith("data:image/png;base64,")) return "dataUrl 前缀错误";

                    var gifPath = Path.Combine(dir, "x.gif");
                    File.WriteAllBytes(gifPath, MakePngBytes());
                    if (OcrImage.FromFile(gifPath).Ok) return "非白名单扩展名应拒绝";

                    var broken = Path.Combine(dir, "broken.jpg");
                    File.WriteAllText(broken, "not an image");
                    if (OcrImage.FromFile(broken).Ok) return "损坏图片应拒绝";

                    if (OcrImage.FromFile(Path.Combine(dir, "missing.png")).Ok) return "不存在的文件应拒绝";
                    if (OcrImage.FromFile(dir).Ok) return "文件夹应拒绝";

                    var big = new byte[OcrImage.MaxBytes + 1];
                    if (OcrImage.FromBytes(big, "image/png").Ok) return "超过 10MB 应拒绝";
                    return null;
                }
                finally { TryDeleteDir(dir); }
            }));

            results.Add(Test("OcrImage：DIB 死 alpha（全 0）自动展平为不透明", () =>
            {
                // 复刻剪贴板 GetImage() 兜底链的产物：Bgra32 且 alpha 全 0（GDI 截图工具不写 alpha 字节）。
                // 修复前：解码正常但渲染全透明（「载入成功但卡片空白」）；修复后：应自动展平为 Bgr32。
                var source = new System.Windows.Media.Imaging.WriteableBitmap(3, 2, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null);
                var pixels = new byte[3 * 2 * 4];
                for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 30; pixels[i + 1] = 60; pixels[i + 2] = 200; }
                source.WritePixels(new System.Windows.Int32Rect(0, 0, 3, 2), pixels, 3 * 4, 0);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                byte[] deadAlphaPng;
                using (var ms = new MemoryStream()) { encoder.Save(ms); deadAlphaPng = ms.ToArray(); }

                var fixedRecord = OcrImage.FromBytes(deadAlphaPng, "image/png");
                if (!fixedRecord.Ok) return "死 alpha 图应修复后通过: " + fixedRecord.Error;
                if (fixedRecord.Width != 3 || fixedRecord.Height != 2) return "尺寸应保持 3x2";

                var frame = BitmapDecoder.Create(new MemoryStream(fixedRecord.Bytes),
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                // WPF 转换可能选 Bgr32 或 Bgr24，只要无 alpha 通道即视为展平成功
                if (frame.Format != System.Windows.Media.PixelFormats.Bgr32
                    && frame.Format != System.Windows.Media.PixelFormats.Bgr24)
                {
                    return "应展平为无 alpha 格式，实际 " + frame.Format;
                }

                var bitmap = fixedRecord.ToBitmap();
                if (bitmap == null) return "修复后应可生成预览位图";
                if (bitmap.Format == System.Windows.Media.PixelFormats.Bgra32
                    || bitmap.Format == System.Windows.Media.PixelFormats.Pbgra32)
                {
                    return "预览位图仍带 alpha 通道: " + bitmap.Format;
                }

                // 正常带 alpha 的 PNG（alpha=255）不得被误伤
                var normalRecord = OcrImage.FromBytes(MakePngBytes(), "image/png");
                if (!normalRecord.Ok) return "正常 png 应通过: " + normalRecord.Error;
                if (normalRecord.Bytes.Length != MakePngBytes().Length) return "正常 png 不应被重编码";
                return null;
            }));

            results.Add(Test("ClipboardHistory：文件条目捕获/去重/落盘往返/失效判定", () =>
            {
                var dir = TempClipDir();
                try
                {
                    var a = Path.Combine(dir, "alpha.txt");
                    var b = Path.Combine(dir, "beta.txt");
                    File.WriteAllText(a, "a");
                    File.WriteAllText(b, "b");

                    var drop = new System.Collections.Specialized.StringCollection();
                    drop.Add(a);
                    drop.Add(b);
                    var data = new DataObject();
                    data.SetFileDropList(drop);

                    var history = new ClipboardHistory(dir);
                    string skip;
                    var record = history.Capture(data, out skip);
                    if (record == null) return "文件条目应被捕获: " + skip;
                    if (record.Kind != ClipKind.Files) return "类型应为 Files";
                    if (record.Files.Count != 2) return "文件数应为 2，实际 " + record.Files.Count;
                    if (record.Files[0].Name != "alpha.txt") return "文件名解析错误: " + record.Files[0].Name;
                    if (record.Signature == null || record.Signature != record.Signature.ToLowerInvariant())
                        return "签名应为小写路径串: " + record.Signature;
                    if (record.Preview != "alpha.txt") return "标题应取首个文件名";

                    // 同路径再复制一次：不新增，只置顶
                    var duplicate = history.Capture(data, out skip);
                    if (duplicate != null || skip != "duplicate") return "重复文件应判重，实际 skip=" + skip;
                    if (history.Records.Count != 1) return "判重后记录数应仍为 1";

                    // 副标题：多文件同目录 → 「共 2 个文件 · 目录」
                    var subtitle = record.Subtitle;
                    if (subtitle.IndexOf("共 2 个文件", StringComparison.Ordinal) < 0) return "多文件副标题缺个数: " + subtitle;
                    if (subtitle.IndexOf(dir, StringComparison.OrdinalIgnoreCase) < 0) return "多文件副标题缺目录: " + subtitle;

                    // 落盘往返：重新打开同一目录应读回同一条
                    var reloaded = new ClipboardHistory(dir);
                    if (reloaded.Records.Count != 1) return "重启后记录数应为 1，实际 " + reloaded.Records.Count;
                    var restored = reloaded.Records[0];
                    if (restored.Kind != ClipKind.Files || restored.Files.Count != 2) return "往返后文件条目丢失";
                    if (!string.Equals(restored.Files[1].Path, b, StringComparison.OrdinalIgnoreCase)) return "往返后路径不一致";

                    // 失效判定：删掉文件 → 灰显 + 副标题提示 + 写回前筛选
                    File.Delete(a);
                    File.Delete(b);
                    var missing = new ClipboardHistory(dir).Records[0];
                    if (!missing.IsMissing) return "文件删除后应判为失效";
                    if (missing.Subtitle != "文件已不存在") return "失效副标题应为「文件已不存在」，实际 " + missing.Subtitle;
                    if (ClipboardHistory.ExistingPaths(missing.Files).Count != 0) return "失效路径应被写回筛掉";

                    File.WriteAllText(a, "a");
                    if (ClipboardHistory.ExistingPaths(missing.Files).Count != 1) return "存活路径应保留";
                    return null;
                }
                finally { TryDeleteDir(dir); }
            }));

            results.Add(Test("Theme：Dark 覆盖层与浅色颜色键集合一致（防漏键白屏）", () =>
            {
                var light = new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Theme.xaml") };
                var dark = new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Theme.Dark.xaml") };
                var lightColors = new System.Collections.Generic.SortedSet<string>();
                var darkColors = new System.Collections.Generic.SortedSet<string>();
                foreach (var k in light.Keys)
                    if (light[k] is SolidColorBrush) lightColors.Add(k.ToString());
                foreach (var k in dark.Keys)
                {
                    if (!(dark[k] is SolidColorBrush)) continue; // 阴影等非颜色覆盖不计入
                    if (!lightColors.Contains(k.ToString())) return "Dark 多出浅色没有的颜色键: " + k;
                    darkColors.Add(k.ToString());
                }
                foreach (var k in lightColors)
                    if (!darkColors.Contains(k)) return "Dark 缺少颜色键: " + k;
                if (darkColors.Count < 30) return "颜色键数量异常: " + darkColors.Count;
                return null;
            }));

            results.Add(Test("Naming：截断气泡——被省略号截断才挂 ToolTip", () =>
            {
                var text = "依据：下单时间在中文业务语境通常指订单创建时刻，英文常用 placedAt / orderCreatedAt / createdAt 等命名";
                var face = new Typeface("Microsoft YaHei UI");
                var brush = System.Windows.Media.Brushes.Black;
                if (!PyTools.Views.NamingView.IsTextTruncated(text, face, 12, brush, 1.0, 100))
                    return "宽 100px 装不下长文本却判为未截断";
                if (PyTools.Views.NamingView.IsTextTruncated("短文本", face, 12, brush, 1.0, 300))
                    return "宽 300px 装得下短文本却判为截断";
                if (PyTools.Views.NamingView.IsTextTruncated("", face, 12, brush, 1.0, 10))
                    return "空文本应判为未截断";
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
