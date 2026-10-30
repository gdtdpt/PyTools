using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
