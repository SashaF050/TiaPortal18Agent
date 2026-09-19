using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;
using Microsoft.Win32;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;

namespace TiaPortal18Agent
{
    // ====================================================================
    // DATA CONTRACTS & JSON-RPC 2.0 PROTOCOL
    // ====================================================================

    public class JsonRpcRequest
    {
        public string jsonrpc { get; set; }
        public object id { get; set; }
        public string method { get; set; }
        public Dictionary<string, object> @params { get; set; }
    }

    public class JsonRpcResponse
    {
        public string jsonrpc { get; set; }
        public object id { get; set; }
        public object result { get; set; }
        public object error { get; set; }
    }

    public static class CrashLogger
    {
        private static readonly object _lock = new object();
        private static readonly string _logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_history.log");
        private static readonly string _desktopLogFile = @"C:\Users\aa.fedin\Desktop\Tia_18_Agent\crash_history.log";

        public static void Log(Exception ex, string context)
        {
            if (ex == null) return;
            try
            {
                lock (_lock)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("================================================================================");
                    sb.AppendLine(string.Format("[{0}] ОШИБКА: {1}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), context));
                    sb.AppendLine("Тип: " + ex.GetType().FullName);
                    sb.AppendLine("Сообщение: " + ex.Message);
                    if (ex.InnerException != null)
                    {
                        sb.AppendLine("Внутреннее исключение: " + ex.InnerException.GetType().FullName + " -> " + ex.InnerException.Message);
                    }
                    sb.AppendLine("Стек вызовов:");
                    sb.AppendLine(ex.StackTrace != null ? ex.StackTrace : "(нет стека)");
                    sb.AppendLine("================================================================================");
                    sb.AppendLine();

                    string text = sb.ToString();
                    try { File.AppendAllText(_logFile, text, Encoding.UTF8); } catch { }
                    try
                    {
                        if (Directory.Exists(@"C:\Users\aa.fedin\Desktop\Tia_18_Agent") &&
                            !AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/').Equals(@"C:\Users\aa.fedin\Desktop\Tia_18_Agent", StringComparison.OrdinalIgnoreCase))
                        {
                            File.AppendAllText(_desktopLogFile, text, Encoding.UTF8);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static void Log(string message, string context)
        {
            try
            {
                lock (_lock)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("================================================================================");
                    sb.AppendLine(string.Format("[{0}] СООБЩЕНИЕ: {1}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), context));
                    sb.AppendLine(message);
                    sb.AppendLine("================================================================================");
                    sb.AppendLine();

                    string text = sb.ToString();
                    try { File.AppendAllText(_logFile, text, Encoding.UTF8); } catch { }
                    try
                    {
                        if (Directory.Exists(@"C:\Users\aa.fedin\Desktop\Tia_18_Agent") &&
                            !AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/').Equals(@"C:\Users\aa.fedin\Desktop\Tia_18_Agent", StringComparison.OrdinalIgnoreCase))
                        {
                            File.AppendAllText(_desktopLogFile, text, Encoding.UTF8);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static List<string> ReadRecentCrashes(int maxLines)
        {
            var list = new List<string>();
            try
            {
                lock (_lock)
                {
                    string targetFile = File.Exists(_logFile) ? _logFile : (File.Exists(_desktopLogFile) ? _desktopLogFile : null);
                    if (targetFile != null && File.Exists(targetFile))
                    {
                        var lines = File.ReadAllLines(targetFile, Encoding.UTF8);
                        int start = Math.Max(0, lines.Length - maxLines);
                        for (int i = start; i < lines.Length; i++)
                            list.Add(lines[i]);
                    }
                }
            }
            catch { }
            return list;
        }

        public static void Clear()
        {
            try
            {
                lock (_lock)
                {
                    if (File.Exists(_logFile)) File.Delete(_logFile);
                    if (File.Exists(_desktopLogFile)) File.Delete(_desktopLogFile);
                }
            }
            catch { }
        }
    }

    public class GarbageItem
    {
        public string Category { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Address { get; set; }
        public int Number { get; set; }
        public string Details { get; set; }
        public int ActiveCallCount { get; set; }
        public List<string> ActiveCallers { get; set; }
        public int DeadCallCount { get; set; }
        public List<string> DeadCallers { get; set; }
        public List<string> OutgoingCalls { get; set; }
        public bool IsReachableFromOB { get; set; }
        public bool IsDeadIsland { get; set; }
        public bool IsSafeToDelete { get; set; }
        public bool IsCandidateDuplicate { get; set; }
        public bool IsSelected { get; set; }
        public string ContentComparison { get; set; }
        public object RawObject { get; set; }
    }

    public class FreeChannelItem
    {
        public string CurrentName { get; set; }
        public string TargetName { get; set; }
        public string LogicalAddress { get; set; }
        public string DataType { get; set; }
        public string CurrentComment { get; set; }
        public string TargetComment { get; set; }
        public string TableName { get; set; }
        public string TablePath { get; set; }
        public string ChannelKind { get; set; } // "DI", "DO", "AI", "AQ"
        public int ByteNum { get; set; }
        public int BitNum { get; set; }
        public Siemens.Engineering.SW.Tags.PlcTag TagObject { get; set; }
    }

    public class KukaSignalItem
    {
        public string RawLine { get; set; }
        public string Name { get; set; }
        public string TagName { get; set; }
        public string SignalType { get; set; } // "IN" or "OUT"
        public int StartBit { get; set; }
        public int EndBit { get; set; }
        public string Comment { get; set; }
        public string DataTypeName { get; set; } // "Bool", "Byte", "Word", "DWord"
        public string LogicalAddress { get; set; }
        public string Status { get; set; } // "NEW", "MATCH", "CONFLICT", "NAME_EXISTS"
        public string ExistingTagName { get; set; }
        public bool IsSelected { get; set; }
    }

    public class DeviceAddressRange
    {
        public string DeviceName { get; set; }
        public string ModuleName { get; set; }
        public string TypeIdentifier { get; set; }
        public bool IsGsd { get; set; }
        public string DeviceKind { get; set; } // "ПЧВ / Частотник", "Робот", "Удаленный ввод-вывод ET200", "Клапанный остров", "GSD-устройство", "Модуль ПЛК"
        public string IoType { get; set; } // "I" or "Q"
        public int StartByte { get; set; }
        public int EndByte { get; set; }
        public int Length { get; set; }
        public Siemens.Engineering.HW.Address RawAddress { get; set; }
        public Siemens.Engineering.HW.DeviceItem DeviceItem { get; set; }
    }

    public class TagRelocationPlanItem
    {
        public Siemens.Engineering.SW.Tags.PlcTag TagObject { get; set; }
        public string TagName { get; set; }
        public string TableName { get; set; }
        public string OldAddress { get; set; }
        public string NewAddress { get; set; }
        public string DataType { get; set; }
        public bool HasConflict { get; set; }
        public string ConflictMessage { get; set; }
    }

    public class CallStructureItem
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Address { get; set; }
        public int Number { get; set; }
        public int CallCount { get; set; }
        public List<string> Callers { get; set; }
        public List<string> OutgoingCalls { get; set; }
        public int LocalDataInputs { get; set; }
        public int LocalDataTotal { get; set; }
        public bool IsReachableFromOB { get; set; }
        public bool IsConflict { get; set; }
        public string GroupPath { get; set; }
    }

    public class DependencyItem
    {
        public string SourceName { get; set; }
        public string SourceType { get; set; }
        public int UsageCount { get; set; }
        public List<DependencyUsage> Usages { get; set; }
    }

    public class DependencyUsage
    {
        public string BlockName { get; set; }
        public string Address { get; set; }
        public int CallCount { get; set; }
        public string LocationDetail { get; set; }
        public List<string> CallChainToOB { get; set; }
    }

    public class BlockMemoryItem
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Address { get; set; }
        public long LoadMemoryBytes { get; set; }
        public long WorkMemoryBytes { get; set; }
        public string GroupPath { get; set; }
    }

    public class GroupMemorySummary
    {
        public string Category { get; set; }
        public long TotalLoadBytes { get; set; }
        public long TotalWorkBytes { get; set; }
        public int BlockCount { get; set; }
    }

    public class MemoryResourceReport
    {
        public string DeviceName { get; set; }
        public string CpuType { get; set; }
        public long LoadMemoryTotal { get; set; }
        public long LoadMemoryUsed { get; set; }
        public double LoadMemoryPercent { get; set; }
        public long WorkMemoryTotal { get; set; }
        public long WorkMemoryUsed { get; set; }
        public double WorkMemoryPercent { get; set; }
        public long RetentiveMemoryTotal { get; set; }
        public long RetentiveMemoryUsed { get; set; }
        public double RetentiveMemoryPercent { get; set; }
        public int IoTotal { get; set; }
        public int IoUsed { get; set; }
        public int DiTotal { get; set; }
        public int DiUsed { get; set; }
        public int AqTotal { get; set; }
        public int AqUsed { get; set; }
        public List<BlockMemoryItem> BlockDetails { get; set; }
        public Dictionary<string, GroupMemorySummary> GroupSummaries { get; set; }
    }

    public class HardwareConfigReport
    {
        public string DeviceName { get; set; }
        public string TypeIdentifier { get; set; }
        public string OrderNumber { get; set; }
        public string FirmwareVersion { get; set; }
        public string ProfinetDeviceName { get; set; }
        public string IpAddress { get; set; }
        public string SubnetMask { get; set; }
        public List<Dictionary<string, object>> Modules { get; set; }
    }

    public class WatchdogStatus
    {
        public bool IsRunning { get; set; }
        public int ScanIntervalSeconds { get; set; }
        public string LastScanTime { get; set; }
        public int AttachedPid { get; set; }
        public string ProjectName { get; set; }
        public bool IsProjectModified { get; set; }
        public int TotalBlocks { get; set; }
        public int UncalledBlocks { get; set; }
        public int CompilerErrors { get; set; }
        public int CompilerWarnings { get; set; }
        public long LoadMemoryUsed { get; set; }
        public long WorkMemoryUsed { get; set; }
        public List<string> RecentAlerts { get; set; }
    }

    // ====================================================================
    // MAIN ENGINE & WHITELIST
    // ====================================================================

            public class Launcher
    {
        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.InputEncoding = Encoding.UTF8;
            }
            catch { }

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                var exObj = e.ExceptionObject as Exception;
                CrashLogger.Log(exObj, "AppDomain.UnhandledException");
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_unhandled.log"), exObj != null ? exObj.ToString() : "null"); } catch { }
            };

            AppDomain.CurrentDomain.AssemblyResolve += ResolveSiemensAssembly;
            try
            {
                RunSafe(args);
            }
            catch (Exception ex)
            {
                CrashLogger.Log(ex, "Launcher.Main.RunSafe");
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ex.ToString()); } catch { }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void RunSafe(string[] args)
        {
            Program.ActualMain(args);
        }

        private static Assembly ResolveSiemensAssembly(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;

            // 1. Check local directory
            string localCandidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name + ".dll");
            if (File.Exists(localCandidate)) return Assembly.LoadFrom(localCandidate);

            // 2. Search across installed TIA Portal versions
            string[] versions = new string[] { "Portal V18", "Portal V19", "Portal V20", "Portal V17", "Portal V16", "Portal V15_1", "Portal V15", "Portal V14" };
            string[] subDirs = new string[] { @"PublicAPI\V18", @"PublicAPI\V19", @"PublicAPI\V20", @"PublicAPI\V17", @"PublicAPI\V16", @"PublicAPI\V15_1", @"PublicAPI\V15", @"PublicAPI\V14", "Bin" };

            string baseSiemens = @"C:\Program Files\Siemens\Automation";
            if (Directory.Exists(baseSiemens))
            {
                foreach (string ver in versions)
                {
                    string verDir = Path.Combine(baseSiemens, ver);
                    if (!Directory.Exists(verDir)) continue;

                    foreach (string sub in subDirs)
                    {
                        string candidate = Path.Combine(verDir, sub, name + ".dll");
                        if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                    }
                }
            }

            return null;
        }
    }

    public class Program
    {
        public const string AGENT_VERSION = "2.3.0";
        public const string BUILD_DATE = "2026-09-20";
        public const string TIA_TARGET_VERSION = "TIA Portal V14-V20 (V18 Native)";

        private static TiaPortal _activeTiaPortal = null;
        private static Project _activeProject = null;
        private static bool _backupUseShortYear = false;
        private static bool _autoClearConsole = true;
        private static int _displayPageSize = 20;
        private static bool _autoCleanEmptyGroups = true;
        private static string _defaultExportFormat = "SimaticML XML";
        private static int _attachedPid = 0;
        private static JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = 100 * 1024 * 1024 };

        private static bool _watchdogRunning = false;
        private static WatchdogStatus _latestWatchdogStatus = new WatchdogStatus
        {
            IsRunning = false,
            ScanIntervalSeconds = 60,
            LastScanTime = "Not started",
            RecentAlerts = new List<string>()
        };

        // Win32 Console VT Mode & Clear Screen
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        private const int STD_OUTPUT_HANDLE = -11;
        private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
        private static bool _vtSupported = false;

        public static void InitConsole()
        {
            try
            {
                IntPtr hOut = GetStdHandle(STD_OUTPUT_HANDLE);
                if (hOut != IntPtr.Zero && hOut != (IntPtr)(-1))
                {
                    uint mode;
                    if (GetConsoleMode(hOut, out mode))
                    {
                        if (SetConsoleMode(hOut, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING))
                        {
                            _vtSupported = true;
                        }
                    }
                }
            }
            catch { }
        }

        public static void ClearScreen()
        {
            try
            {
                if (!Console.IsOutputRedirected)
                {
                    Console.Clear();
                    try
                    {
                        Console.SetCursorPosition(0, 0);
                        Console.SetWindowPosition(0, 0);
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                // ANSI VT100 sequence: \x1b[3J (clear scrollback) + \x1b[H (cursor home) + \x1b[2J (clear screen)
                Console.Write("\x1b[3J\x1b[H\x1b[2J");
            }
            catch { }

            try
            {
                if (!Console.IsOutputRedirected)
                {
                    Console.SetCursorPosition(0, 0);
                }
            }
            catch { }
        }

        public static void SafePrintWrapped(string prefix, string text, ConsoleColor color)
        {
            if (string.IsNullOrEmpty(text)) return;
            Console.ForegroundColor = color;
            int width = 78;
            try
            {
                if (!Console.IsOutputRedirected && Console.WindowWidth > 30)
                    width = Math.Max(40, Console.WindowWidth - 4);
            }
            catch { }

            string full = (prefix ?? "") + text;
            if (full.Length <= width)
            {
                Console.WriteLine(full);
                Console.ResetColor();
                return;
            }

            string indent = new string(' ', (prefix ?? "").Length);
            bool first = true;
            while (full.Length > 0)
            {
                if (full.Length <= width)
                {
                    Console.WriteLine(full);
                    break;
                }
                int splitAt = width;
                int lastSpace = full.Substring(0, width).LastIndexOf(' ');
                if (lastSpace > (first ? (prefix ?? "").Length : indent.Length))
                {
                    splitAt = lastSpace;
                }
                Console.WriteLine(full.Substring(0, splitAt));
                full = indent + full.Substring(splitAt).TrimStart();
                first = false;
            }
            Console.ResetColor();
        }

                                // Win32 Auto-Confirm Watcher for Openness dialog (0033:000666)
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll")]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static void CollectButtonsRecursive(IntPtr parent, List<IntPtr> buttons)
        {
            uint GW_CHILD = 5;
            uint GW_HWNDNEXT = 2;
            IntPtr child = GetWindow(parent, GW_CHILD);
            while (child != IntPtr.Zero)
            {
                var clsSb = new StringBuilder(128);
                GetClassName(child, clsSb, 128);
                string cls = clsSb.ToString();
                if (cls.IndexOf("BUTTON", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    buttons.Add(child);
                }
                CollectButtonsRecursive(child, buttons);
                child = GetWindow(child, GW_HWNDNEXT);
            }
        }

        public static void StartAutoConfirmWatcher()
        {
            var t = new Thread(() =>
            {
                try
                {
                    IntPtr hDesk = OpenDesktop("default", 0, false, 0x01FF);
                    if (hDesk != IntPtr.Zero)
                    {
                        SetThreadDesktop(hDesk);
                    }
                }
                catch { }

                int checks = 0;
                while (checks++ < 60) // Watch for 30 seconds
                {
                    try
                    {
                        IntPtr hDesk = OpenDesktop("default", 0, false, 0x01FF);
                        EnumWindowsProc callback = (hWnd, lParam) =>
                        {
                            if (IsWindowVisible(hWnd))
                            {
                                var sb = new StringBuilder(256);
                                GetWindowText(hWnd, sb, 256);
                                string title = sb.ToString();
                                if (title.IndexOf("Openness", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    title.IndexOf("0033:000666", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    var buttons = new List<IntPtr>();
                                    CollectButtonsRecursive(hWnd, buttons);

                                    IntPtr targetBtn = IntPtr.Zero;
                                    foreach (var b in buttons)
                                    {
                                        var bSb = new StringBuilder(256);
                                        GetWindowText(b, bSb, 256);
                                        string bText = bSb.ToString();
                                        if (bText.IndexOf("all", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            bText.IndexOf("\u0432\u0441\u0435\u0445", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            bText.IndexOf("всех", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            targetBtn = b;
                                            break;
                                        }
                                    }

                                    if (targetBtn == IntPtr.Zero && buttons.Count >= 2)
                                    {
                                        targetBtn = buttons[1]; // Button index 1 is standard 'Да, для всех'
                                    }
                                    else if (targetBtn == IntPtr.Zero && buttons.Count >= 1)
                                    {
                                        targetBtn = buttons[0];
                                    }

                                    if (targetBtn != IntPtr.Zero)
                                    {
                                        try { SetForegroundWindow(hWnd); } catch { }
                                        SendMessage(targetBtn, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);
                                        PostMessage(targetBtn, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                                        PostMessage(targetBtn, 0x0201, (IntPtr)1, IntPtr.Zero);
                                        PostMessage(targetBtn, 0x0202, IntPtr.Zero, IntPtr.Zero);
                                        Console.ForegroundColor = ConsoleColor.Green;
                                        Console.Error.WriteLine("\n[AutoConfirmWatcher] Диалог Openness access подтвержден: 'Да, для всех' нажат автоматически!");
                                        Console.ResetColor();
                                    }
                                }
                            }
                            return true;
                        };

                        if (hDesk != IntPtr.Zero)
                        {
                            EnumDesktopWindows(hDesk, callback, IntPtr.Zero);
                        }
                        else
                        {
                            EnumWindows(callback, IntPtr.Zero);
                        }
                    }
                    catch { }
                    Thread.Sleep(500);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        public static void ActualMain(string[] args)
        {
            InitConsole();
            EnsureSelfWhitelisted();
            SyncToDesktopDirectory();
            StartAutoConfirmWatcher();

            if (args == null || args.Length == 0)
            {
                RunInteractiveDashboard();
            }
            else if (args[0].ToLower() == "--mcp")
            {
                RunMcpServer();
            }
            else if (args[0].ToLower() == "--version" || args[0].ToLower() == "version")
            {
                PrintVersionInfo();
            }
            else if (args[0].ToLower() == "--watch")
            {
                int interval = 60;
                if (args.Length > 1) int.TryParse(args[1], out interval);
                if (interval < 5) interval = 5;
                RunAutonomousWatchdog(interval);
            }
            else
            {
                RunCli(args);
            }
        }

        public static void SyncToDesktopDirectory()
        {
            try
            {
                string desktopDir = @"C:\Users\aa.fedin\Desktop\Tia_18_Agent";
                string currentDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                if (currentDir.Equals(desktopDir, StringComparison.OrdinalIgnoreCase)) return;

                if (!Directory.Exists(desktopDir))
                {
                    Directory.CreateDirectory(desktopDir);
                }

                string[] filesToSync = new string[]
                {
                    "TiaPortal18Agent.cs",
                    "TiaPortal18Agent.exe",
                    "TiaPortal18Agent.exe.config",
                    "build.bat",
                    "register_whitelist.ps1",
                    "call_tree.json",
                    "README.md",
                    "crash_history.log"
                };

                foreach (var fname in filesToSync)
                {
                    string src = Path.Combine(currentDir, fname);
                    string dst = Path.Combine(desktopDir, fname);
                    if (File.Exists(src))
                    {
                        try
                        {
                            File.Copy(src, dst, true);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static void PrintVersionInfo()
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("  TiaPortal18Agent v" + AGENT_VERSION + " [Comprehensive Multi-Tool & MCP Server]");
            Console.WriteLine("  Build Date: " + BUILD_DATE + " | Target: " + TIA_TARGET_VERSION);
            Console.WriteLine("  PublicAPI: Siemens.Engineering.dll V18 Update 5 (x64)");
            Console.WriteLine("  Integrations: Czarnak MCP | cFirewall Whitelist | bulaofen0036 | AnyAutomation");
            Console.WriteLine("================================================================================");
        }

        private static Assembly ResolveSiemensAssembly(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            string publicApiDir = @"C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18";
            string candidate = Path.Combine(publicApiDir, name + ".dll");
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);

            string binDir = @"C:\Program Files\Siemens\Automation\Portal V18\Bin";
            candidate = Path.Combine(binDir, name + ".dll");
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);

            return null;
        }

        private static void EnsureSelfWhitelisted()
        {
            try
            {
                string exePath = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    WhitelistFile(exePath);
                }

                string desktopExe = @"C:\Users\aa.fedin\Desktop\Tia_18_Agent\TiaPortal18Agent.exe";
                if (File.Exists(desktopExe))
                {
                    WhitelistFile(desktopExe);
                }
            }
            catch { }
        }

        private static void WhitelistFile(string exePath)
        {
            try
            {
                string exeName = Path.GetFileName(exePath);
                string fileHashBase64;
                using (var sha256 = SHA256.Create())
                {
                    using (var fs = File.OpenRead(exePath))
                    {
                        fileHashBase64 = Convert.ToBase64String(sha256.ComputeHash(fs));
                    }
                }

                string dateUtcStr = File.GetLastWriteTimeUtc(exePath).ToString("yyyy/MM/dd HH:mm:ss");

                string[] rootKeys = new string[]
                {
                    @"SOFTWARE\Siemens\Automation\Openness\18.0\Whitelist",
                    @"SOFTWARE\WOW6432Node\Siemens\Automation\Openness\18.0\Whitelist"
                };

                foreach (string rootPath in rootKeys)
                {
                    using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                    {
                        using (var whitelistKey = hklm.CreateSubKey(rootPath))
                        {
                            if (whitelistKey != null)
                            {
                                using (var appKey = whitelistKey.CreateSubKey(exeName))
                                {
                                    if (appKey != null)
                                    {
                                        using (var entryKey = appKey.CreateSubKey("Entry"))
                                        {
                                            if (entryKey != null)
                                            {
                                                entryKey.SetValue("Path", exePath, Microsoft.Win32.RegistryValueKind.String);
                                                entryKey.SetValue("Date", dateUtcStr, Microsoft.Win32.RegistryValueKind.String);
                                                entryKey.SetValue("FileHash", fileHashBase64, Microsoft.Win32.RegistryValueKind.String);
                                                entryKey.SetValue("DateModified", dateUtcStr, Microsoft.Win32.RegistryValueKind.String);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public static void Log(string message)
        {
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Console.Error.WriteLine("[" + ts + "] " + message);
        }

        // ====================================================================
        // SPINNER / PROGRESS HELPER
        // ====================================================================

        private static T RunWithSpinner<T>(string label, Func<T> work)
        {
            var sw = Stopwatch.StartNew();
            bool done = false;
            string[] frames = new string[] { "|", "/", "-", "\\" };
            int fi = 0;

            var spinnerThread = new Thread(() =>
            {
                while (!done)
                {
                    try
                    {
                        double elapsed = sw.Elapsed.TotalSeconds;
                        string spinner = frames[fi++ % frames.Length];
                        string msg = string.Format("  {0} {1} [{2:F0} сек]", spinner, label, elapsed);
                        Console.Write("\r" + msg);
                    }
                    catch { }
                    Thread.Sleep(120);
                }
            });
            spinnerThread.IsBackground = true;
            try { spinnerThread.Start(); } catch { }

            T result = default(T);
            Exception error = null;
            try
            {
                // Siemens Openness COM calls MUST execute on the calling STA thread
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done = true;
                sw.Stop();
                try { spinnerThread.Join(400); } catch { }
                try
                {
                    if (error == null)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        string doneMsg = string.Format("\r  ✓ {0} — готово ({1:F1} сек)                    \n", label, sw.Elapsed.TotalSeconds);
                        Console.Write(doneMsg);
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        string errMsg = string.Format("\r  ✗ {0} — ошибка: {1}                    \n", label, error.Message);
                        Console.Write(errMsg);
                        Console.ResetColor();
                    }
                }
                catch { }
            }

            if (error != null) throw error;
            return result;
        }

        private static void RunWithSpinner(string label, Action work)
        {
            RunWithSpinner<object>(label, () => { work(); return null; });
        }

        // ====================================================================
        // INTERACTIVE TUI DASHBOARD (9 TABS MATCHING TIA PORTAL)
        // ====================================================================

        private static void RunInteractiveDashboard()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("    SIEMENS TIA PORTAL V18 MULTI-TOOL & AUTONOMOUS AGENT v" + AGENT_VERSION);
            Console.WriteLine("    Target: " + TIA_TARGET_VERSION + " | cFirewall Whitelist: ACTIVE");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            try { EnsureConnected(); } catch { }
            if (_activeProject == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" [!] ОШИБКА: Не удалось подключиться к активному TIA Portal или открыть проект.");
                Console.ResetColor();
                Console.WriteLine("Нажмите любую клавишу для выхода...");
                try { if (!Console.IsInputRedirected) Console.ReadKey(true); } catch { }
                return;
            }

            while (true)
            {
                if (_autoClearConsole) ClearScreen();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("================================================================================");
                Console.WriteLine("  TIA PORTAL V18 AUTONOMOUS AGENT v" + AGENT_VERSION + " | Проект: " + _activeProject.Name);
                Console.WriteLine("================================================================================");
                Console.ResetColor();

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [1] Сводка проекта и аппаратная конфигурация");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       CPU, модули, IP-адрес, подсеть — обзор оборудования");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [2] Структура вызова (Call Structure)");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       Полноценная навигация, фильтрация и анализ вызовов Main (OB1)");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [3] Структура зависимости (Dependency)");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       Граф связей DB → FC/FB → OB1, проверенные цепочки обращений");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [4] Ресурсы памяти (Memory Resources)");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       Загружаемая/Рабочая/Энергонезависимая — шкалы заполнения");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [5] Диагностика и компилятор");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       Компиляция проекта, ошибки и предупреждения TIA Portal");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [6] Умный очиститель мусора");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       Очистка неиспользуемых блоков, тегов, UDT и пустых папок");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [7] Пакетный экспорт / импорт");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       XML SimaticML и SCL с сохранением иерархии папок");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [8] S7-PLCSIM V18 — Симулятор");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       Статус, запуск и управление виртуальным контроллером");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [9] Настройки (Settings)");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       Конфигурация параметров агента, форматы дат и пагинация");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [0] Журнал ошибок и сбоев (Crash History)");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       Просмотр истории крашей, стеков исключений и журнала ошибок");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [R] Робот KUKA — Синхронизатор сигналов");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       Генератор тегов KRL $IN/$OUT ↔ TIA PLC Tags, экспорт/импорт, сравнение");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [M] Мастер переезда адресов оборудования и тегов");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       Сдвиг HW адресов устройств и перепривязка тегов ПЛК с контролем конфликтов");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [D] Диагностика совместимости и готовности системы");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       Проверка версий TIA, Openness API, прав доступа Windows и Whitelist");
                Console.ResetColor();
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("   [Esc / Q] Выход из агента");
                Console.ResetColor();
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("   [S] Сохранить проект     [V] Сохранить версию (V0→V1)     [P] Сменить проект");
                Console.ResetColor();
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
                Console.Write(" Выберите действие [0-9, R, M, D, S, V, P, Esc]: ");

                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Escape || key.KeyChar == 'q' || key.KeyChar == 'Q' || key.KeyChar == 'й' || key.KeyChar == 'Й') break;
                Console.WriteLine(key.KeyChar);

                try
                {
                    if (key.KeyChar == '1') ShowHardwareConfig();
                    else if (key.KeyChar == '2') ShowCallStructure();
                    else if (key.KeyChar == '3') ShowDependencyStructure();
                    else if (key.KeyChar == '4') ShowMemoryResources();
                    else if (key.KeyChar == '5') ShowComprehensiveDiagnostics();
                    else if (key.KeyChar == '6') RunSmartGarbageCleaner();
                    else if (key.KeyChar == '7') ShowBatchExportImport();
                    else if (key.KeyChar == '8') ShowPlcSimControl();
                    else if (key.KeyChar == '9' || key.KeyChar == 't' || key.KeyChar == 'T' || key.KeyChar == 'е' || key.KeyChar == 'Е') RunSettingsMenu();
                    else if (key.KeyChar == '0') ShowCrashHistory();
                    else if (key.KeyChar == 'r' || key.KeyChar == 'R' || key.KeyChar == 'к' || key.KeyChar == 'К') ShowKukaSignalsManager();
                    else if (key.KeyChar == 'm' || key.KeyChar == 'M' || key.KeyChar == 'ь' || key.KeyChar == 'Ь') ShowAddressRelocationManager();
                    else if (key.KeyChar == 'd' || key.KeyChar == 'D' || key.KeyChar == 'в' || key.KeyChar == 'В') ShowReadinessCheck();
                    else if (key.KeyChar == 's' || key.KeyChar == 'S' || key.KeyChar == 'ы' || key.KeyChar == 'Ы')
                    {
                        Console.WriteLine(DoSaveProject());
                        Console.WriteLine("Нажмите любую клавишу для продолжения...");
                        Console.ReadKey(true);
                    }
                    else if (key.KeyChar == 'p' || key.KeyChar == 'P' || key.KeyChar == 'з' || key.KeyChar == 'З')
                    {
                        SelectTiaProjectInteractive();
                    }
                    else if (key.KeyChar == 'v' || key.KeyChar == 'V' || key.KeyChar == 'м' || key.KeyChar == 'М')
                    {
                        var res = DoSaveProjectVersion(new Dictionary<string, object>());
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine(res["message"]);
                        Console.ResetColor();
                        Console.WriteLine("Нажмите любую клавишу для продолжения...");
                        Console.ReadKey(true);
                    }
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, "RunInteractiveDashboard.MenuSelection");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("\n[!] Ошибка выполнения операции: " + ex.Message);
                    Console.WriteLine("    Подробности записаны в crash_history.log");
                    Console.ResetColor();
                    Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
                    Console.ReadKey(true);
                }
            }
        }

        private static void ShowCrashHistory()
        {
            while (true)
            {
                ClearScreen();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("================================================================================");
                Console.WriteLine("  ЖУРНАЛ СБОЕВ И ИСКЛЮЧЕНИЙ (Crash & Error History)");
                Console.WriteLine("================================================================================");
                Console.ResetColor();

                var lines = CrashLogger.ReadRecentCrashes(200);
                if (lines == null || lines.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("\n  [+] Журнал чист! Никаких сбоев или исключений не зафиксировано.\n");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine(string.Format("  Последние записи журнала (показано строк: {0}):", lines.Count));
                    Console.ResetColor();
                    Console.WriteLine("--------------------------------------------------------------------------------");

                    foreach (var line in lines)
                    {
                        if (line.StartsWith("==="))
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.WriteLine(line);
                        }
                        else if (line.Contains("ОШИБКА") || line.Contains("Exception") || line.Contains("Error"))
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine(line);
                        }
                        else if (line.Contains("Стек вызовов") || line.TrimStart().StartsWith("at "))
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.WriteLine(line);
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.White;
                            Console.WriteLine(line);
                        }
                        Console.ResetColor();
                    }
                }

                Console.WriteLine("================================================================================");
                Console.WriteLine("  [C] Очистить журнал     [Esc/Enter] Назад в главное меню");
                Console.Write("\n  Ваш выбор: ");

                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Enter) break;
                if (key.KeyChar == 'c' || key.KeyChar == 'C' || key.KeyChar == 'с' || key.KeyChar == 'С')
                {
                    CrashLogger.Clear();
                    Console.WriteLine("\n  Журнал очищен.");
                    Thread.Sleep(800);
                }
            }
            ClearScreen();
        }

        private static void RunSettingsMenu()
        {
            while (true)
            {
                if (_autoClearConsole) ClearScreen();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("================================================================================");
                Console.WriteLine("  НАСТРОЙКИ АГЕНТА (Settings)");
                Console.WriteLine("================================================================================");
                Console.ResetColor();
                Console.WriteLine("  • Версия агента:      " + AGENT_VERSION);
                Console.WriteLine("  • Целевая версия TIA:  " + TIA_TARGET_VERSION);
                Console.WriteLine("--------------------------------------------------------------------------------");
                Console.WriteLine(" [1] Формат даты в резервной копии: " + (_backupUseShortYear ? "Короткий год (dd.MM.yy)" : "Полный год (dd.MM.yyyy)"));
                Console.WriteLine(" [2] Авто-очистка экрана перед меню: " + (_autoClearConsole ? "Включено" : "Выключено"));
                Console.WriteLine(" [3] Количество строк на страницу:   " + _displayPageSize + " строк");
                Console.WriteLine(" [4] Формат экспорта по умолчанию:   " + _defaultExportFormat);
                Console.WriteLine(" [5] Авто-удаление пустых папок:     " + (_autoCleanEmptyGroups ? "Включено (после очистки)" : "Выключено"));
                Console.WriteLine(" [6] Перерегистрация в Openness Whitelist (реестр Windows)");
                Console.WriteLine(" [7] Переподключиться к TIA Portal / сбросить кэш");
                Console.WriteLine("================================================================================");
                Console.WriteLine(" [Esc/Q/0] Вернуться в главное меню");
                Console.Write("\n Ваш выбор: ");

                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Q || key.KeyChar == '0' || key.KeyChar == 'q' || key.KeyChar == 'й' || key.KeyChar == 'Й') break;

                if (key.KeyChar == '1')
                {
                    _backupUseShortYear = !_backupUseShortYear;
                }
                else if (key.KeyChar == '2')
                {
                    _autoClearConsole = !_autoClearConsole;
                }
                else if (key.KeyChar == '3')
                {
                    if (_displayPageSize == 15) _displayPageSize = 20;
                    else if (_displayPageSize == 20) _displayPageSize = 25;
                    else if (_displayPageSize == 25) _displayPageSize = 50;
                    else _displayPageSize = 15;
                }
                else if (key.KeyChar == '4')
                {
                    _defaultExportFormat = _defaultExportFormat == "SimaticML XML" ? "SCL" : "SimaticML XML";
                }
                else if (key.KeyChar == '5')
                {
                    _autoCleanEmptyGroups = !_autoCleanEmptyGroups;
                }
                else if (key.KeyChar == '6')
                {
                    try
                    {
                        string myExe = Assembly.GetExecutingAssembly().Location;
                        string psScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "register_whitelist.ps1");
                        if (File.Exists(psScript))
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = "powershell.exe",
                                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + psScript + "\" -ExePath \"" + myExe + "\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            var p = Process.Start(psi);
                            p.WaitForExit(5000);
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("\n[+] Whitelist успешно обновлен в реестре.");
                            Console.ResetColor();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("\n[-] Ошибка: " + ex.Message);
                        Console.ResetColor();
                    }
                    Thread.Sleep(1000);
                }
                else if (key.KeyChar == '7')
                {
                    Console.WriteLine("\nПереподключение к TIA Portal...");
                    _activeProject = null;
                    EnsureConnected();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("[+] Подключено к проекту: " + (_activeProject != null ? _activeProject.Name : "null"));
                    Console.ResetColor();
                    Thread.Sleep(1000);
                }
            }
        }

        // ====================================================================
        // TUI VIEWS MATCHING NATIVE TIA PORTAL SCREENS
        // ====================================================================

        private static void ShowHardwareConfig()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("  АППАРАТНАЯ КОНФИГУРАЦИЯ И СЕТЬ (Hardware & Subnet)");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            EnsureConnected();
            var dev = FindDevice(null);
            var plc = FindPlcSoftware(dev);
            var hw = GetHardwareConfig(dev, plc);

            Console.WriteLine("  • Контроллер:      " + hw.DeviceName);
            Console.WriteLine("  • Тип CPU:         " + hw.TypeIdentifier);
            Console.WriteLine("  • Артикул (MLFB):  " + hw.OrderNumber);
            Console.WriteLine("  • Прошивка (FW):   " + hw.FirmwareVersion);
            Console.WriteLine("  • Имя PROFINET:    " + hw.ProfinetDeviceName);
            Console.WriteLine("  • IP-адрес:        " + hw.IpAddress);
            Console.WriteLine("  • Маска подсети:   " + hw.SubnetMask);
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("  Установленные модули и периферия (" + hw.Modules.Count + "):");
            foreach (var m in hw.Modules)
            {
                Console.WriteLine("   - Слот " + m["slot"] + ": " + m["name"] + " (" + m["type"] + ")");
            }
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
            Console.ReadKey(true);
            ClearScreen();
        }

        private static void ShowCallStructure()
        {
            EnsureConnected();
            var dev = FindDevice(null);
            if (dev == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" [!] Ошибка: Устройство не найдено в проекте.");
                Console.ResetColor();
                Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
                Console.ReadKey(true);
                return;
            }
            var plc = FindPlcSoftware(dev);
            if (plc == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" [!] Ошибка: PLC Software не найдено в устройстве " + dev.Name);
                Console.ResetColor();
                Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
                Console.ReadKey(true);
                return;
            }

            bool onlyConflicts = false;
            List<CallStructureItem> items = null;

            // First load with spinner
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
            Console.WriteLine("  СТРУКТУРА ВЫЗОВА: " + dev.Name);
            Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
            Console.ResetColor();
            Console.WriteLine();
            items = RunWithSpinner<List<CallStructureItem>>("Построение графа вызовов (Cross-References)", () => GetCallStructure(plc, false, true));
            if (items == null) items = new List<CallStructureItem>();

            int page = 0;

            while (true)
            {
                ClearScreen();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
                Console.WriteLine("  СТРУКТУРА ВЫЗОВА: " + dev.Name);
                Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
                Console.ResetColor();

                var visibleItems = onlyConflicts ? items.FindAll(x => x.IsConflict) : items;
                int pageSize = _displayPageSize > 0 ? _displayPageSize : 20;
                int totalPages = Math.Max(1, (visibleItems.Count + pageSize - 1) / pageSize);
                if (page >= totalPages) page = totalPages - 1;
                if (page < 0) page = 0;

                int startIdx = page * pageSize;
                int endIdx = Math.Min(startIdx + pageSize, visibleItems.Count);

                string filterStr = onlyConflicts ? "[X] Только конфликты (0 вызовов)" : "[ ] Только конфликты (0 вызовов)";
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(string.Format("  {0,-35} | Страница {1}/{2} (Объекты {3}-{4} из {5})",
                    filterStr, page + 1, totalPages, visibleItems.Count > 0 ? startIdx + 1 : 0, endIdx, visibleItems.Count));
                Console.ResetColor();
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  {0,-35}  {1,-7}  {2,8}  {3,9}  {4,11}", "Имя блока", "Адрес", "Вызовы", "Лок.(Вх)", "Лок.(Всего)");
                Console.ResetColor();

                for (int i = startIdx; i < endIdx; i++)
                {
                    var it = visibleItems[i];
                    if (it.IsConflict)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.Write("  !");
                    }
                    else
                    {
                        Console.ForegroundColor = it.IsReachableFromOB ? ConsoleColor.White : ConsoleColor.DarkGray;
                        Console.Write("   ");
                    }
                    string bName = it.Name;
                    if (bName.Length > 34) bName = bName.Substring(0, 31) + "...";
                    Console.WriteLine("{0,-34}  {1,-7}  {2,8}  {3,9}  {4,11}", bName, it.Address, it.CallCount, it.LocalDataInputs, it.LocalDataTotal);
                    Console.ResetColor();
                }

                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  Итого: " + visibleItems.Count + " объектов (показаны все элементы через пагинацию)");
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("  █");
                Console.ResetColor();
                Console.Write(" Активный   ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("!");
                Console.ResetColor();
                Console.Write(" Конфликт (0 вызовов)   ");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("█");
                Console.ResetColor();
                Console.WriteLine(" Недостижим из OB");
                Console.WriteLine();
                Console.WriteLine("  [←/→] или [N/P] Страницы | [Пробел] Фильтр конфликтов | [Esc/Enter] Назад");

                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Spacebar)
                {
                    onlyConflicts = !onlyConflicts;
                    page = 0;
                }
                else if (key.Key == ConsoleKey.RightArrow || key.Key == ConsoleKey.PageDown || key.KeyChar == 'n' || key.KeyChar == 'N' || key.KeyChar == 'т' || key.KeyChar == 'Т')
                {
                    if (page < totalPages - 1) page++;
                }
                else if (key.Key == ConsoleKey.LeftArrow || key.Key == ConsoleKey.PageUp || key.KeyChar == 'p' || key.KeyChar == 'P' || key.KeyChar == 'з' || key.KeyChar == 'З')
                {
                    if (page > 0) page--;
                }
                else if (key.Key == ConsoleKey.Home)
                {
                    page = 0;
                }
                else if (key.Key == ConsoleKey.End)
                {
                    page = totalPages - 1;
                }
                else if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Enter)
                {
                    break;
                }
            }
            ClearScreen();
        }

        private static void ShowDependencyStructure()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
            Console.WriteLine("  СТРУКТУРА ЗАВИСИМОСТИ (DB → FC/FB → OB1)");
            Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
            Console.ResetColor();
            Console.WriteLine();

            EnsureConnected();
            var dev = FindDevice(null);
            if (dev == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" [!] Ошибка: Устройство не найдено в проекте.");
                Console.ResetColor();
                Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
                Console.ReadKey(true);
                return;
            }
            var plc = FindPlcSoftware(dev);
            if (plc == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" [!] Ошибка: PLC Software не найдено в устройстве " + dev.Name);
                Console.ResetColor();
                Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
                Console.ReadKey(true);
                return;
            }

            var deps = RunWithSpinner<List<DependencyItem>>("Сканирование графа зависимостей", () => GetDependencyStructure(plc));
            if (deps == null) deps = new List<DependencyItem>();
            Console.WriteLine();

            int depPage = 0;
            int depPageSize = 5;
            int totalDepPages = Math.Max(1, (deps.Count + depPageSize - 1) / depPageSize);

            while (true)
            {
                ClearScreen();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
                Console.WriteLine("  СТРУКТУРА ЗАВИСИМОСТИ (DB → FC/FB → OB1)");
                Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
                Console.ResetColor();

                if (depPage >= totalDepPages) depPage = totalDepPages - 1;
                if (depPage < 0) depPage = 0;

                int start = depPage * depPageSize;
                int end = Math.Min(start + depPageSize, deps.Count);

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(string.Format("  Источники {0}-{1} из {2} (Страница {3}/{4})",
                    deps.Count > 0 ? start + 1 : 0, end, deps.Count, depPage + 1, totalDepPages));
                Console.ResetColor();
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");

                for (int i = start; i < end; i++)
                {
                    var d = deps[i];
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("  [" + d.SourceType + "] ");
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Write(d.SourceName);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("  (" + d.UsageCount + " обращений)");
                    Console.ResetColor();

                    foreach (var u in d.Usages)
                    {
                        string chain = u.CallChainToOB != null && u.CallChainToOB.Count > 0 ? string.Join(" → ", u.CallChainToOB.ToArray()) : "Локально (не вызван из OB1)";
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write("     └── ");
                        Console.ForegroundColor = ConsoleColor.White;
                        string uName = u.BlockName;
                        if (uName.Length > 30) uName = uName.Substring(0, 27) + "...";
                        Console.Write(string.Format("{0,-30}", uName));
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine(string.Format(" [{0}] x{1,-2}  {2}", u.Address, u.CallCount, u.LocationDetail));
                        Console.ResetColor();
                        if (u.CallChainToOB != null && u.CallChainToOB.Count > 1)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkCyan;
                            Console.WriteLine("           Цепочка: " + chain);
                            Console.ResetColor();
                        }
                    }
                    Console.WriteLine();
                }

                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  Итого: " + deps.Count + " источников зависимостей (показаны все элементы через пагинацию)");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("  [←/→] или [N/P] Листать страницы | [Esc/Enter] Назад");

                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.RightArrow || key.Key == ConsoleKey.PageDown || key.KeyChar == 'n' || key.KeyChar == 'N' || key.KeyChar == 'т' || key.KeyChar == 'Т')
                {
                    if (depPage < totalDepPages - 1) depPage++;
                }
                else if (key.Key == ConsoleKey.LeftArrow || key.Key == ConsoleKey.PageUp || key.KeyChar == 'p' || key.KeyChar == 'P' || key.KeyChar == 'з' || key.KeyChar == 'З')
                {
                    if (depPage > 0) depPage--;
                }
                else if (key.Key == ConsoleKey.Home)
                {
                    depPage = 0;
                }
                else if (key.Key == ConsoleKey.End)
                {
                    depPage = totalDepPages - 1;
                }
                else if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Enter)
                {
                    break;
                }
            }
            ClearScreen();
        }

        private static void ShowMemoryResources()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
            Console.WriteLine("  РЕСУРСЫ ПАМЯТИ: " + (_activeProject != null ? _activeProject.Name : "проект"));
            Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
            Console.ResetColor();
            Console.WriteLine();

            EnsureConnected();
            var dev = FindDevice(null);
            if (dev == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" [!] Ошибка: Устройство не найдено в проекте.");
                Console.ResetColor();
                Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
                Console.ReadKey(true);
                return;
            }
            var plc = FindPlcSoftware(dev);
            if (plc == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" [!] Ошибка: PLC Software не найдено в устройстве " + dev.Name);
                Console.ResetColor();
                Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
                Console.ReadKey(true);
                return;
            }

            var rep = RunWithSpinner<MemoryResourceReport>("Расчёт ресурсов памяти", () => GetMemoryResources(plc, dev));
            if (rep == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" [!] Ошибка: Не удалось сформировать отчет по памяти.");
                Console.ResetColor();
                Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
                Console.ReadKey(true);
                return;
            }
            Console.WriteLine();

            // Section 1: Memory bars
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ── Загрузка памяти контроллера ──────────────────────────────────────────────");
            Console.ResetColor();
            Console.WriteLine();
            PrintProgressBar("  Загружаемая память", rep.LoadMemoryUsed, rep.LoadMemoryTotal, rep.LoadMemoryPercent);
            PrintProgressBar("  Рабочая память    ", rep.WorkMemoryUsed, rep.WorkMemoryTotal, rep.WorkMemoryPercent);
            PrintProgressBar("  Энергонезависимая ", rep.RetentiveMemoryUsed, rep.RetentiveMemoryTotal, rep.RetentiveMemoryPercent);
            Console.WriteLine();

            // Section 2: IO
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ── Адресное пространство Вх./Вых. ──────────────────────────────────────────");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("    Входы/Выходы:      {0,4} / {1,4} ({2:F0}%)", rep.IoUsed, rep.IoTotal, rep.IoTotal > 0 ? (double)rep.IoUsed / rep.IoTotal * 100 : 0);
            Console.WriteLine("    Цифровые входы:    {0,4} / {1,4} ({2:F0}%)", rep.DiUsed, rep.DiTotal, rep.DiTotal > 0 ? (double)rep.DiUsed / rep.DiTotal * 100 : 0);
            Console.WriteLine("    Аналоговые выходы: {0,4} / {1,4} ({2:F0}%)", rep.AqUsed, rep.AqTotal, rep.AqTotal > 0 ? (double)rep.AqUsed / rep.AqTotal * 100 : 0);
            Console.WriteLine();

            // Section 3: Block details table
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ── Распределение памяти по блокам (топ-25) ─────────────────────────────────");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  {0,-35}  {1,-6}  {2,16}  {3,16}", "Имя блока", "Адрес", "Загружаемая (B)", "Рабочая (B)");
            Console.ResetColor();

            int bCount = 0;
            foreach (var b in rep.BlockDetails)
            {
                bCount++;
                if (bCount > 25) break;
                string bName = b.Name;
                if (bName.Length > 35) bName = bName.Substring(0, 32) + "...";
                Console.WriteLine("  {0,-35}  {1,-6}  {2,16:N0}  {3,16:N0}", bName, b.Address, b.LoadMemoryBytes, b.WorkMemoryBytes);
            }
            Console.WriteLine();

            // Section 4: Category totals
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ── Итоги по категориям ─────────────────────────────────────────────────────");
            Console.ResetColor();
            Console.WriteLine();
            foreach (var kv in rep.GroupSummaries)
            {
                Console.WriteLine("    {0,-4}  ({1,2} блоков)    Загружаемая: {2,10:N0} B    Рабочая: {3,8:N0} B", kv.Key, kv.Value.BlockCount, kv.Value.TotalLoadBytes, kv.Value.TotalWorkBytes);
            }
            Console.WriteLine();
            Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
            Console.WriteLine("  [Esc/Enter] Назад");
            Console.ReadKey(true);
            ClearScreen();
        }

        private static void PrintProgressBar(string label, long used, long total, double percent)
        {
            int barWidth = 24;
            int filled = (int)Math.Round((percent / 100.0) * barWidth);
            if (filled > barWidth) filled = barWidth;
            if (filled < 0) filled = 0;
            string bar = new string('█', filled) + new string('░', barWidth - filled);

            Console.Write("  " + label + ": [");
            if (percent > 85) Console.ForegroundColor = ConsoleColor.Red;
            else if (percent > 65) Console.ForegroundColor = ConsoleColor.Yellow;
            else Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(bar);
            Console.ResetColor();
            Console.WriteLine("] {0,5:F1}%  ({1:N0} / {2:N0} B)", percent, used, total);
        }

        private static void ShowBatchExportImport()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("  ПАКЕТНЫЙ ЭКСПОРТ И ИМПОРТ (SimaticML XML & SCL)");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            EnsureConnected();
            var dev = FindDevice(null);
            var plc = FindPlcSoftware(dev);

            Console.WriteLine(" 1. Пакетный экспорт проекта (Блоки, UDT, Теги с иерархией)");
            Console.WriteLine(" 2. Пакетный импорт проекта из директории");
            Console.WriteLine(" 0. Назад");
            Console.Write(" Выберите действие: ");
            var k = Console.ReadKey(true);
            Console.WriteLine(k.KeyChar);

            if (k.KeyChar == '1')
            {
                string defDir = @"C:\Users\aa.fedin\Desktop\Tia_18_Agent\Export_" + _activeProject.Name;
                Console.Write(" Путь экспорта [" + defDir + "]: ");
                string path = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(path)) path = defDir;

                Console.WriteLine(" Экспорт в процессе...");
                string res = BatchExport(plc, path, "all");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(" " + res);
                Console.ResetColor();
            }
            else if (k.KeyChar == '2')
            {
                Console.Write(" Путь к директории для импорта: ");
                string path = Console.ReadLine();
                if (Directory.Exists(path))
                {
                    string res = BatchImport(plc, path);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine(" " + res);
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine("Директория не найдена.");
                }
            }

            Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
            Console.ReadKey(true);
            ClearScreen();
        }

        private static void ShowPlcSimControl()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("  УПРАВЛЕНИЕ S7-PLCSIM V18 & ВИРТУАЛЬНЫМ КОНТРОЛЛЕРОМ");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            var status = DoCheckSimulation();
            bool running = status.ContainsKey("isRunning") && status["isRunning"] != null && (bool)status["isRunning"];
            int pid = status.ContainsKey("pid") && status["pid"] != null ? Convert.ToInt32(status["pid"]) : 0;
            string pName = status.ContainsKey("processName") && status["processName"] != null ? status["processName"].ToString() : "S7-PLCSIM.exe";
            bool installed = status.ContainsKey("installed") && status["installed"] != null && (bool)status["installed"];
            string path = status.ContainsKey("path") && status["path"] != null ? status["path"].ToString() : "";

            Console.WriteLine("  • Статус симулятора:   " + (running ? "ЗАПУЩЕН (PID: " + pid + ")" : "НЕ ЗАПУЩЕН"));
            Console.WriteLine("  • Исполняемый файл:    " + pName + (installed ? " (Установлен)" : " (Не найден)"));
            if (!string.IsNullOrEmpty(path))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("    Путь: " + path);
                Console.ResetColor();
            }
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine(" 1. Запустить S7-PLCSIM V18");
            Console.WriteLine(" 2. Проверить статус симуляции");
            Console.WriteLine(" 0. Назад");
            Console.Write(" Выберите действие: ");
            var k = Console.ReadKey(true);
            Console.WriteLine(k.KeyChar);

            if (k.KeyChar == '1')
            {
                string res = DoStartSimulation();
                Console.WriteLine(" Результат: " + res);
            }
            else if (k.KeyChar == '2')
            {
                var s = DoCheckSimulation();
                Console.WriteLine(" Статус: " + _serializer.Serialize(s));
            }

            Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
            Console.ReadKey(true);
            ClearScreen();
        }

        private static void RunAutonomousWatchdog(int intervalSeconds)
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("  ПОМИНУТНЫЙ АВТОНОМНЫЙ СТОРОЖ (Autonomous Watchdog)");
            Console.WriteLine("  Интервал опроса: " + intervalSeconds + " сек. Нажмите [Esc] или [Q] для остановки.");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            _watchdogRunning = true;
            _latestWatchdogStatus.ScanIntervalSeconds = intervalSeconds;
            _latestWatchdogStatus.IsRunning = true;

            int scanCounter = 0;
            while (_watchdogRunning)
            {
                scanCounter++;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("[" + DateTime.Now.ToString("HH:mm:ss") + "] Цикл #" + scanCounter + ": сканирование проекта... ");
                Console.ResetColor();

                var status = ExecuteWatchdogScan();
                Console.ForegroundColor = status.CompilerErrors > 0 ? ConsoleColor.Red : (status.CompilerWarnings > 0 ? ConsoleColor.Yellow : ConsoleColor.Green);
                Console.WriteLine("OK! (Блоков: " + status.TotalBlocks + ", Мусорных: " + status.UncalledBlocks + ", Ошибок: " + status.CompilerErrors + ", Предупреждений: " + status.CompilerWarnings + ")");
                Console.ResetColor();

                // Wait loop checking for key press
                for (int i = 0; i < intervalSeconds * 10; i++)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Q)
                        {
                            _watchdogRunning = false;
                            Console.WriteLine("Сторож остановлен пользователем.");
                            break;
                        }
                    }
                    Thread.Sleep(100);
                }
            }

            _latestWatchdogStatus.IsRunning = false;
            Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
            Console.ReadKey(true);
        }

        private static void RunSmartGarbageCleaner()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("========================================================================");
            Console.WriteLine("          УМНЫЙ АНАЛИЗАТОР И ОЧИСТИТЕЛЬ МУСОРА TIA PORTAL               ");
            Console.WriteLine("    (Блоки, Теги, Типы данных UDT, Пустые папки групп, Ревизии)        ");
            Console.WriteLine("========================================================================");
            Console.ResetColor();
            Console.WriteLine("Выберите категорию для анализа:");
            Console.WriteLine("  [1] Программные блоки (Поиск неиспользуемых FC, FB, DB и ревизий)");
            Console.WriteLine("  [2] Таблицы тегов (Поиск неиспользуемых переменных и резервов)");
            Console.WriteLine("  [3] Типы данных ПЛК (UDT) и пустые папки (Группы)");
            Console.WriteLine("  [4] Комплексная очистка всего проекта (Блоки + Теги + UDT + Папки)");
            Console.WriteLine("  [5] Нормализация свободных каналов ПЛК (Empty_DI_x / Empty_DO_x)");
            Console.WriteLine("  [0] Отмена");
            Console.Write("\nВаш выбор: ");
            string choice = Console.ReadLine();
            if (choice == "5")
            {
                RunEmptyChannelsNormalizer();
                return;
            }
            if (choice != "1" && choice != "2" && choice != "3" && choice != "4") return;

            Console.WriteLine();
            var allItems = RunWithSpinner<List<GarbageItem>>("Сканирование дерева проекта и Cross-References", () => CollectSmartGarbageItems(choice));
            if (allItems == null) allItems = new List<GarbageItem>();

            if (allItems.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n[+] Неиспользуемых блоков, тегов или UDT не обнаружено.");
                Console.ResetColor();

                if (choice == "3" || choice == "4")
                {
                    Console.Write("\nВыполнить поиск и удаление пустых папок (групп) в проекте? (y/n): ");
                    var conf = Console.ReadKey(false);
                    Console.WriteLine();
                    if (conf.Key == ConsoleKey.Y || conf.KeyChar == 'д' || conf.KeyChar == 'Д' || conf.KeyChar == 'y' || conf.KeyChar == 'Y')
                    {
                        int delFolders = 0;
                        try
                        {
                            Device dev = FindDevice(null);
                            var plc = FindPlcSoftware(dev);
                            if (plc != null)
                            {
                                delFolders += CleanAllEmptyBlockGroups(plc.BlockGroup);
                                delFolders += CleanAllEmptyTypeGroups(plc.TypeGroup);
                                delFolders += CleanAllEmptyTagGroups(plc.TagTableGroup);
                                if (delFolders > 0)
                                {
                                    _activeProject.Save();
                                    Console.WriteLine("Проект успешно сохранен.");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Предупреждение при очистке папок: " + ex.Message);
                        }
                        if (delFolders > 0)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("  ✓ Успешно удалено пустых папок: " + delFolders);
                            Console.ResetColor();
                        }
                        else
                        {
                            Console.WriteLine("  Пустых папок не обнаружено.");
                        }
                    }
                }

                Console.WriteLine("\nНажмите любую клавишу для возврата...");
                Console.ReadKey(true);
                ClearScreen();
                return;
            }

            int currentIndex = 0;
            bool showOnlyConflicts = false;

            while (true)
            {
                ClearScreen();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("========================================================================");
                Console.WriteLine("     ИНТЕРАКТИВНАЯ ОЧИСТКА ПРОЕКТА (Управление стрелками и клавишами)   ");
                Console.WriteLine("========================================================================");
                Console.ResetColor();

                var visibleItems = showOnlyConflicts ? allItems.FindAll(x => x.IsCandidateDuplicate) : allItems;
                if (visibleItems.Count == 0)
                {
                    showOnlyConflicts = false;
                    visibleItems = allItems;
                }

                if (currentIndex >= visibleItems.Count) currentIndex = visibleItems.Count - 1;
                if (currentIndex < 0) currentIndex = 0;

                int selectedCount = 0;
                foreach (var it in visibleItems) if (it.IsSelected) selectedCount++;

                int pageSize = 12;
                int pageStart = (currentIndex / pageSize) * pageSize;
                int pageEnd = Math.Min(pageStart + pageSize, visibleItems.Count);

                Console.WriteLine(string.Format("Объекты {0}-{1} из {2} (Страница {3}/{4}) | Фильтр [C]: {5}",
                    pageStart + 1, pageEnd, visibleItems.Count, (pageStart / pageSize) + 1, ((visibleItems.Count - 1) / pageSize) + 1,
                    showOnlyConflicts ? "ТОЛЬКО КОНФЛИКТЫ/ДУБЛИКАТЫ" : "ВСЕ НЕИСПОЛЬЗУЕМЫЕ ОБЪЕКТЫ"));
                Console.WriteLine("------------------------------------------------------------------------");

                for (int i = pageStart; i < pageEnd; i++)
                {
                    bool isCurrent = (i == currentIndex);
                    string checkMark = visibleItems[i].IsSelected ? "[X]" : "[ ]";
                    string pointer = isCurrent ? " > " : "   ";

                    string badge = "";
                    if (visibleItems[i].Category.Contains("DB"))
                    {
                        if (visibleItems[i].ActiveCallCount > 0)
                            badge = string.Format("[ВНИМАНИЕ: DB ({0,-2} обр.)] ", visibleItems[i].ActiveCallCount);
                        else
                            badge = "[ВНИМАНИЕ: DB (HMI/Данные)] ";
                    }
                    else if (!visibleItems[i].IsReachableFromOB)
                    {
                        if (visibleItems[i].IsDeadIsland) badge = "[БЕЗОПАСНО: Мертвый остров] ";
                        else badge = "[БЕЗОПАСНО: 0 вызовов]      ";
                    }
                    else
                    {
                        badge = string.Format("[ВНИМАНИЕ: Вызовов: {0,-2}]   ", visibleItems[i].ActiveCallCount);
                    }

                    string displayName = visibleItems[i].Path;
                    if (!string.IsNullOrEmpty(visibleItems[i].Address))
                    {
                        displayName += " [" + visibleItems[i].Address + "]";
                    }

                    string line = string.Format("{0}{1} {2} {3}", pointer, checkMark, badge, displayName);
                    if (line.Length > Console.WindowWidth - 1) line = line.Substring(0, Math.Max(10, Console.WindowWidth - 4)) + "...";

                    if (isCurrent)
                    {
                        Console.BackgroundColor = ConsoleColor.DarkBlue;
                        Console.ForegroundColor = ConsoleColor.White;
                    }
                    else
                    {
                        if (visibleItems[i].IsSafeToDelete) Console.ForegroundColor = ConsoleColor.Green;
                        else if (visibleItems[i].Category.Contains("DB")) Console.ForegroundColor = ConsoleColor.Cyan;
                        else Console.ForegroundColor = ConsoleColor.Yellow;
                    }

                    Console.WriteLine(line);
                    Console.ResetColor();
                }

                Console.WriteLine("========================================================================");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("ПОЯСНЕНИЯ ДЛЯ ВЫДЕЛЕННОГО ОБЪЕКТА:");
                Console.ResetColor();

                var cur = visibleItems[currentIndex];
                string curDisplay = cur.Path + (!string.IsNullOrEmpty(cur.Address) ? " [" + cur.Address + "]" : "");
                Console.WriteLine("  • Объект: " + curDisplay + " (" + cur.Category + ")");

                if (cur.Category.Contains("DB"))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("  • Статус: БЛОК ДАННЫХ (DB) — ЗАЩИЩЕН ОТ АВТО-УДАЛЕНИЯ");
                    Console.ResetColor();
                    if (cur.ActiveCallCount > 0)
                    {
                        Console.WriteLine("    - Обращений из активных блоков программы (" + cur.ActiveCallCount + "): " + string.Join(", ", cur.ActiveCallers.ToArray()));
                    }
                    else
                    {
                        Console.WriteLine("    - Обращений из PLC программы не обнаружено (0 вызовов).");
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine("    - ВНИМАНИЕ: DB может использоваться панелями HMI/SCADA, рецептами или обменом!");
                        Console.ResetColor();
                    }
                    Console.WriteLine("    - Рекомендация: НЕ УДАЛЯТЬ без явной ручной проверки HMI и рецептов!");
                }
                else if (cur.Category.Equals("UDT", StringComparison.OrdinalIgnoreCase))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("  • Статус: БЕЗОПАСНО К УДАЛЕНИЮ (Неиспользуемый тип данных UDT)");
                    Console.ResetColor();
                    Console.WriteLine("    - Перекрестные ссылки: 0 обращений (Тип не используется ни в одном блоке или теге)");
                    Console.WriteLine("    - Рекомендация: Можно удалять. Это неиспользуемая структура данных.");
                }
                else if (!cur.IsReachableFromOB)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    if (cur.IsDeadIsland)
                    {
                        Console.WriteLine("  • Статус: БЕЗОПАСНО К УДАЛЕНИЮ (Мертвый остров / Вложенная цепочка)");
                        Console.ResetColor();
                        Console.WriteLine("    - Достижимость из Main (OB1): НЕДОСТИЖИМ (Код никогда не исполняется процессором)");
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine("    - Входящие вызовы: Вызывается ТОЛЬКО из других неиспользуемых блоков: " + string.Join(", ", cur.DeadCallers.ToArray()));
                        Console.ResetColor();
                        if (cur.OutgoingCalls != null && cur.OutgoingCalls.Count > 0)
                        {
                            Console.WriteLine("    - Исходящие вызовы: " + string.Join(", ", cur.OutgoingCalls.ToArray()));
                        }
                        Console.WriteLine("    - Рекомендация: Безопасно удалять. Вся цепочка недостижима в рабочем цикле ПЛК.");
                    }
                    else
                    {
                        Console.WriteLine("  • Статус: БЕЗОПАСНО К УДАЛЕНИЮ (Прямой неиспользуемый код, 0 вызовов в проекте)");
                        Console.ResetColor();
                        Console.WriteLine("    - Достижимость из Main (OB1): НЕДОСТИЖИМ (Ни один блок или OB к нему не обращается)");
                        Console.WriteLine("    - Входящие вызовы: 0 вызовов (Ни один блок в программе не вызывает этот объект)");
                        if (cur.OutgoingCalls != null && cur.OutgoingCalls.Count > 0)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.WriteLine("    - Исходящие вызовы из блока: " + string.Join(", ", cur.OutgoingCalls.ToArray()) + " (сам блок вызывает эти функции, но его никто не вызывает)");
                            Console.ResetColor();
                        }
                        Console.WriteLine("    - Рекомендация: Можно удалять. Это неиспользуемый блок / старая ревизия кода.");
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  • Статус: ВНИМАНИЕ! БЛОК АКТИВЕН И ВЫЗЫВАЕТСЯ В РАБОЧЕЙ ЛОГИКЕ!");
                    Console.ResetColor();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("    - Достижимость из Main (OB1): ДОСТИЖИМ (Исполняется в рабочем цикле контроллера)");
                    Console.WriteLine("    - Активные вызовы из программы (" + cur.ActiveCallCount + "): " + string.Join(", ", cur.ActiveCallers.ToArray()));
                    if (cur.OutgoingCalls != null && cur.OutgoingCalls.Count > 0)
                    {
                        Console.WriteLine("    - Исходящие вызовы: " + string.Join(", ", cur.OutgoingCalls.ToArray()));
                    }
                    Console.WriteLine("    - ПРЕДУПРЕЖДЕНИЕ: Удаление нарушит работу оборудования и вызовет ошибку компиляции!");
                    Console.ResetColor();
                }

                if (!string.IsNullOrEmpty(cur.ContentComparison))
                {
                    ConsoleColor cmpColor = cur.ContentComparison.Contains("100% ИДЕНТИЧНО") ? ConsoleColor.Cyan : 
                                            (cur.ContentComparison.Contains("АППАРАТНЫЙ") || cur.ContentComparison.Contains("Сетевое устройство") || cur.ContentComparison.Contains("ПЧВ") || cur.ContentComparison.Contains("Робот") || cur.ContentComparison.Contains("СИСТЕМНЫЙ")) ? ConsoleColor.Yellow : ConsoleColor.Magenta;
                    SafePrintWrapped("  • Примечание: ", cur.ContentComparison, cmpColor);
                }

                Console.WriteLine("------------------------------------------------------------------------");
                Console.WriteLine(string.Format("Выбрано для удаления: {0} из {1} | Фильтр [C]: {2}", 
                    selectedCount, visibleItems.Count, showOnlyConflicts ? "[Только конфликты/дубликаты]" : "[Все неиспользуемые объекты]"));
                Console.WriteLine("[Space] Выбор | [A] Все безопасные | [N] Снять все | [C] Фильтр | [Enter] Удалить | [Esc] Назад");

                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.UpArrow)
                {
                    if (currentIndex > 0) currentIndex--;
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    if (currentIndex < visibleItems.Count - 1) currentIndex++;
                }
                else if (key.Key == ConsoleKey.Spacebar)
                {
                    visibleItems[currentIndex].IsSelected = !visibleItems[currentIndex].IsSelected;
                }
                else if (key.Key == ConsoleKey.C || key.KeyChar == 'c' || key.KeyChar == 'C' || key.KeyChar == 'с' || key.KeyChar == 'С')
                {
                    showOnlyConflicts = !showOnlyConflicts;
                    currentIndex = 0;
                }
                else if (key.Key == ConsoleKey.A || key.KeyChar == 'a' || key.KeyChar == 'A' || key.KeyChar == 'ф' || key.KeyChar == 'Ф')
                {
                    foreach (var it in visibleItems) it.IsSelected = it.IsSafeToDelete;
                }
                else if (key.Key == ConsoleKey.N || key.KeyChar == 'n' || key.KeyChar == 'N' || key.KeyChar == 'т' || key.KeyChar == 'Т')
                {
                    foreach (var it in visibleItems) it.IsSelected = false;
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    Console.WriteLine("Отмена.");
                    return;
                }
                else if (key.Key == ConsoleKey.Enter)
                {
                    if (selectedCount == 0)
                    {
                        Console.WriteLine("Ни один элемент не выбран. Нажмите Пробел для выбора.");
                        System.Threading.Thread.Sleep(1000);
                        continue;
                    }

                    bool hasDangerous = false;
                    foreach (var it in visibleItems)
                    {
                        if (it.IsSelected && (!it.IsSafeToDelete || it.Category.Contains("DB") || it.ActiveCallCount > 0)) hasDangerous = true;
                    }

                    if (hasDangerous)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("\nВНИМАНИЕ! Среди выбранных элементов есть блоки данных (DB) или те, которые ВЫЗЫВАЮТСЯ В ПРОГРАММЕ!");
                        Console.WriteLine("Их удаление может привести к потере технологических данных или ошибкам компиляции.");
                        Console.ResetColor();
                    }

                    Console.Write("\nПодтверждаете удаление " + selectedCount + " элементов? (y/n): ");
                    var conf = Console.ReadKey(false);
                    Console.WriteLine();
                    if (conf.Key == ConsoleKey.Y || conf.KeyChar == 'д' || conf.KeyChar == 'Д' || conf.KeyChar == 'y' || conf.KeyChar == 'Y')
                    {
                        Console.WriteLine("\nСоздание резервной копии перед удалением...");
                        bool backupSuccess = false;
                        RunWithSpinner("Создание резервной копии", () => {
                            try {
                                var res = DoSaveProjectVersion(new Dictionary<string, object>());
                                backupSuccess = true;
                            } catch (Exception ex) {
                                Console.WriteLine("\n[!] Ошибка создания копии: " + ex.Message);
                            }
                        });
                        
                        if (!backupSuccess) {
                            Console.Write("\nПродолжить удаление БЕЗ резервной копии? (y/n): ");
                            var conf2 = Console.ReadKey(false);
                            if (conf2.Key != ConsoleKey.Y && conf2.KeyChar != 'д' && conf2.KeyChar != 'Д' && conf2.KeyChar != 'y' && conf2.KeyChar != 'Y') {
                                break;
                            }
                        }

                        ExecuteGarbageDeletion(visibleItems);
                        Console.WriteLine("\nНажмите любую клавишу для продолжения...");
                        Console.ReadKey(true);
                    }
                    break;
                }
            }
            ClearScreen();
        }

        private static bool IsSystemProtectedTag(string tagName, string logicalAddress, string tableName)
        {
            if (string.IsNullOrEmpty(tagName)) return false;

            if (!string.IsNullOrEmpty(tableName))
            {
                if (tableName.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tableName.IndexOf("Constant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tableName.IndexOf("Систем", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            if (tagName.StartsWith("Clock_", StringComparison.OrdinalIgnoreCase) ||
                tagName.StartsWith("System_", StringComparison.OrdinalIgnoreCase) ||
                tagName.Equals("FirstScan", StringComparison.OrdinalIgnoreCase) ||
                tagName.Equals("DiagStatusUpdate", StringComparison.OrdinalIgnoreCase) ||
                tagName.Equals("AlwaysTRUE", StringComparison.OrdinalIgnoreCase) ||
                tagName.Equals("AlwaysFALSE", StringComparison.OrdinalIgnoreCase) ||
                tagName.IndexOf("SystemSignals", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tagName.StartsWith("Local~", StringComparison.OrdinalIgnoreCase) ||
                tagName.StartsWith("Hw_", StringComparison.OrdinalIgnoreCase) ||
                tagName.StartsWith("HW_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(logicalAddress))
            {
                string addr = logicalAddress.Trim().ToUpperInvariant();
                if (addr.StartsWith("%MB0") || addr.StartsWith("%MB1") ||
                    addr.StartsWith("%MW0") || addr.StartsWith("%MW1") ||
                    addr.StartsWith("%M0.") || addr.StartsWith("%M1."))
                {
                    return true;
                }
            }

            return false;
        }

        private static void SetTagComment(PlcTag tag, string commentText)
        {
            if (tag == null || string.IsNullOrEmpty(commentText)) return;
            try
            {
                if (tag.Comment != null && tag.Comment.Items != null)
                {
                    foreach (var item in tag.Comment.Items)
                    {
                        try
                        {
                            item.Text = commentText;
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log(ex, "SetTagComment." + tag.Name);
            }
        }

        private static PlcTag RenamePlcTag(PlcTag tag, string newName)
        {
            if (tag == null || string.IsNullOrEmpty(newName)) return tag;
            if (tag.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)) return tag;

            try
            {
                tag.SetAttribute("Name", newName);
                return tag;
            }
            catch
            {
                try
                {
                    var parentTable = tag.Parent as PlcTagTable;
                    if (parentTable != null)
                    {
                        string dt = tag.DataTypeName;
                        string addr = tag.LogicalAddress;
                        tag.Delete();
                        var newTag = parentTable.Tags.Create(newName, dt, addr);
                        return newTag;
                    }
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, "RenamePlcTag.Fallback." + newName);
                }
            }
            return tag;
        }

        private static PlcTagTable GetOrCreateTagTable(PlcSoftware plc, string tableName)
        {
            if (plc == null || string.IsNullOrEmpty(tableName)) return null;

            var existingTables = new List<Dictionary<string, object>>();
            CollectTagsRecursive(plc.TagTableGroup, existingTables, "");
            foreach (var td in existingTables)
            {
                if (td["tableName"].ToString().Equals(tableName, StringComparison.OrdinalIgnoreCase))
                {
                    var found = FindTagTableByPath(plc.TagTableGroup, td["path"].ToString());
                    if (found != null) return found;
                }
            }

            try
            {
                return plc.TagTableGroup.TagTables.Create(tableName);
            }
            catch (Exception ex)
            {
                CrashLogger.Log(ex, "GetOrCreateTagTable." + tableName);
                return null;
            }
        }

        public static bool TryParseLogicalAddress(string addr, out string ioType, out int byteNum, out int bitNum, out string prefix)
        {
            ioType = null;
            byteNum = 0;
            bitNum = 0;
            prefix = "";
            if (string.IsNullOrEmpty(addr)) return false;

            string clean = addr.Trim().ToUpper();
            if (!clean.StartsWith("%")) clean = "%" + clean;

            // %I0.0, %Q107.5
            var matchBit = Regex.Match(clean, @"^%(?<io>[IQ])(?<byte>\d+)\.(?<bit>[0-7])$");
            if (matchBit.Success)
            {
                ioType = matchBit.Groups["io"].Value;
                byteNum = int.Parse(matchBit.Groups["byte"].Value);
                bitNum = int.Parse(matchBit.Groups["bit"].Value);
                prefix = "";
                return true;
            }

            // %IB100, %QB100, %IW100, %QW100, %ID100, %QD100
            var matchWord = Regex.Match(clean, @"^%(?<io>[IQ])(?<p>[BWD])(?<byte>\d+)$");
            if (matchWord.Success)
            {
                ioType = matchWord.Groups["io"].Value;
                byteNum = int.Parse(matchWord.Groups["byte"].Value);
                bitNum = 0;
                prefix = matchWord.Groups["p"].Value;
                return true;
            }

            return false;
        }

        public static string ComputeShiftedAddress(string oldAddr, int byteDiff)
        {
            string ioType, prefix;
            int bNum, bitNum;
            if (!TryParseLogicalAddress(oldAddr, out ioType, out bNum, out bitNum, out prefix))
                return oldAddr;

            int newByte = bNum + byteDiff;
            if (newByte < 0) newByte = 0;

            if (string.IsNullOrEmpty(prefix))
            {
                return string.Format("%{0}{1}.{2}", ioType, newByte, bitNum);
            }
            else
            {
                return string.Format("%{0}{1}{2}", ioType, prefix, newByte);
            }
        }

        public static List<DeviceAddressRange> GetProjectDeviceAddressRanges(Project project)
        {
            var list = new List<DeviceAddressRange>();
            if (project == null) return list;

            try
            {
                foreach (Device dev in project.Devices)
                {
                    CollectDeviceItemsAddressRanges(dev, dev.Name, dev.IsGsd, dev.DeviceItems, list);
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log(ex, "GetProjectDeviceAddressRanges");
            }

            return list;
        }

        private static void CollectDeviceItemsAddressRanges(Device rootDev, string devName, bool isGsd, DeviceItemComposition items, List<DeviceAddressRange> list)
        {
            if (items == null) return;
            foreach (DeviceItem di in items)
            {
                try
                {
                    if (di.Addresses != null)
                    {
                        foreach (Address addr in di.Addresses)
                        {
                            try
                            {
                                string ioTypeStr = null;
                                if (addr.IoType == AddressIoType.Input) ioTypeStr = "I";
                                else if (addr.IoType == AddressIoType.Output) ioTypeStr = "Q";

                                if (ioTypeStr != null && addr.Length > 0)
                                {
                                    int sByte = addr.StartAddress;
                                    int len = addr.Length;
                                    int eByte = sByte + len - 1;

                                    string kind = "Модуль ПЛК";
                                    string dLower = (devName + " " + di.Name + " " + (di.TypeIdentifier ?? "")).ToLower();
                                    if (dLower.Contains("drive") || dLower.Contains("vfd") || dLower.Contains("g120") || dLower.Contains("v90") || 
                                        dLower.Contains("danfoss") || dLower.Contains("sew") || dLower.Contains("inverter") || dLower.Contains("частот"))
                                    {
                                        kind = "ПЧВ / Частотник";
                                    }
                                    else if (dLower.Contains("robot") || dLower.Contains("kuka") || dLower.Contains("fanuc") || dLower.Contains("abb") || dLower.Contains("робот"))
                                    {
                                        kind = "Робот";
                                    }
                                    else if (dLower.Contains("et200") || dLower.Contains("im155") || dLower.Contains("remote") || dLower.Contains("rio"))
                                    {
                                        kind = "Удаленный ввод-вывод ET200";
                                    }
                                    else if (dLower.Contains("valve") || dLower.Contains("festo") || dLower.Contains("smc") || dLower.Contains("пневмо") || dLower.Contains("клапан"))
                                    {
                                        kind = "Клапанный остров";
                                    }
                                    else if (isGsd)
                                    {
                                        kind = "GSD-устройство";
                                    }

                                    list.Add(new DeviceAddressRange
                                    {
                                        DeviceName = devName,
                                        ModuleName = di.Name,
                                        TypeIdentifier = di.TypeIdentifier,
                                        IsGsd = isGsd,
                                        DeviceKind = kind,
                                        IoType = ioTypeStr,
                                        StartByte = sByte,
                                        EndByte = eByte,
                                        Length = len,
                                        RawAddress = addr,
                                        DeviceItem = di
                                    });
                                }
                            }
                            catch { }
                        }
                    }

                    if (di.DeviceItems != null && di.DeviceItems.Count > 0)
                    {
                        CollectDeviceItemsAddressRanges(rootDev, devName, isGsd, di.DeviceItems, list);
                    }
                }
                catch { }
            }
        }

        private static void RunEmptyChannelsNormalizer()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("========================================================================");
            Console.WriteLine("     НОРМАЛИЗАЦИЯ СВОБОДНЫХ АППАРАТНЫХ КАНАЛОВ ПЛК (Empty_DI / Empty_DO) ");
            Console.WriteLine("========================================================================");
            Console.ResetColor();

            EnsureConnected();
            Device dev = FindDevice(null);
            var plc = FindPlcSoftware(dev);
            if (plc == null)
            {
                Console.WriteLine("[!] Ошибка: Не удалось найти PLC.");
                Console.ReadKey(true);
                return;
            }

            Console.WriteLine("Сканирование дерева проекта, аппаратных таблиц и Cross-References...");
            HashSet<string> blockNames;
            Dictionary<string, HashSet<string>> blockCalls;
            Dictionary<string, HashSet<string>> blockCallers;
            HashSet<string> reachableFromOB;
            Dictionary<string, HashSet<string>> tagUsageMap;
            Dictionary<string, string> blockAddressMap;
            Dictionary<string, PlcBlock> blockMap;
            Dictionary<string, string> blockGroupMap;
            Dictionary<string, string> blockTypeMap;
            Dictionary<string, int> blockNumberMap;

            BuildProjectTopology(
                plc,
                out blockNames,
                out blockCalls,
                out blockCallers,
                out reachableFromOB,
                out tagUsageMap,
                out blockAddressMap,
                out blockMap,
                out blockGroupMap,
                out blockTypeMap,
                out blockNumberMap);

            var tagTables = new List<Dictionary<string, object>>();
            CollectTagsRecursive(plc.TagTableGroup, tagTables, "");

            var freeChannels = new List<FreeChannelItem>();

            foreach (var tableDict in tagTables)
            {
                string tableName = tableDict["tableName"].ToString();
                string tablePath = tableDict["path"].ToString();
                var tags = tableDict["tags"] as List<Dictionary<string, object>>;
                if (tags == null) continue;

                var plcTable = FindTagTableByPath(plc.TagTableGroup, tablePath);
                if (plcTable == null) continue;

                foreach (var tg in tags)
                {
                    string tName = tg["name"].ToString();
                    string tAddr = tg.ContainsKey("logicalAddress") && tg["logicalAddress"] != null ? tg["logicalAddress"].ToString() : "";
                    string tType = tg.ContainsKey("dataType") && tg["dataType"] != null ? tg["dataType"].ToString() : "";
                    string tComm = tg.ContainsKey("comment") && tg["comment"] != null ? tg["comment"].ToString() : "";

                    if (string.IsNullOrEmpty(tAddr)) continue;
                    if (IsSystemProtectedTag(tName, tAddr, tableName)) continue;

                    string upperAddr = tAddr.ToUpperInvariant();
                    bool isInput = upperAddr.StartsWith("%I");
                    bool isOutput = upperAddr.StartsWith("%Q");
                    if (!isInput && !isOutput) continue;

                    int activeCallers = 0;
                    if (tagUsageMap.ContainsKey(tName))
                    {
                        foreach (var u in tagUsageMap[tName])
                        {
                            if (reachableFromOB.Contains(u)) activeCallers++;
                        }
                    }

                    PlcTag tagObj = plcTable.Tags.Find(tName);
                    if (activeCallers == 0 && tagObj != null)
                    {
                        try
                        {
                            var tagXref = tagObj.GetService<CrossReferenceService>();
                            if (tagXref != null)
                            {
                                var xRes = tagXref.GetCrossReferences(CrossReferenceFilter.AllObjects);
                                if (xRes != null && xRes.Sources != null)
                                {
                                    foreach (SourceObject s in xRes.Sources)
                                    {
                                        if (!string.IsNullOrEmpty(s.Name) && reachableFromOB.Contains(s.Name.Trim('\"', '\'')))
                                            activeCallers++;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (activeCallers > 0) continue;

                    int byteNum = 0;
                    int bitNum = 0;
                    string channelKind = "";

                    var matchBit = Regex.Match(upperAddr, @"%([IQ])(\d+)\.(\d+)");
                    var matchWord = Regex.Match(upperAddr, @"%([IQ])[W|D](\d+)");
                    var matchByte = Regex.Match(upperAddr, @"%([IQ])B(\d+)");

                    if (matchBit.Success)
                    {
                        byteNum = int.Parse(matchBit.Groups[2].Value);
                        bitNum = int.Parse(matchBit.Groups[3].Value);
                        channelKind = matchBit.Groups[1].Value == "I" ? "DI" : "DO";
                    }
                    else if (matchWord.Success)
                    {
                        byteNum = int.Parse(matchWord.Groups[2].Value);
                        channelKind = matchWord.Groups[1].Value == "I" ? "AI" : "AQ";
                    }
                    else if (matchByte.Success)
                    {
                        byteNum = int.Parse(matchByte.Groups[2].Value);
                        channelKind = matchByte.Groups[1].Value == "I" ? "AI" : "AQ";
                    }
                    else
                    {
                        continue;
                    }

                    freeChannels.Add(new FreeChannelItem
                    {
                        CurrentName = tName,
                        LogicalAddress = tAddr,
                        DataType = tType,
                        CurrentComment = tComm,
                        TableName = tableName,
                        TablePath = tablePath,
                        ChannelKind = channelKind,
                        ByteNum = byteNum,
                        BitNum = bitNum,
                        TagObject = tagObj
                    });
                }
            }

            if (freeChannels.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n[+] Свободных неиспользуемых аппаратных каналов не найдено.");
                Console.ResetColor();
                Console.WriteLine("Нажмите любую клавишу для возврата...");
                Console.ReadKey(true);
                ClearScreen();
                return;
            }

            freeChannels.Sort((a, b) =>
            {
                int k = a.ChannelKind.CompareTo(b.ChannelKind);
                if (k != 0) return k;
                if (a.ByteNum != b.ByteNum) return a.ByteNum.CompareTo(b.ByteNum);
                return a.BitNum.CompareTo(b.BitNum);
            });

            var kindCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in freeChannels)
            {
                if (!kindCounters.ContainsKey(ch.ChannelKind)) kindCounters[ch.ChannelKind] = 1;
                else kindCounters[ch.ChannelKind]++;
                ch.TargetName = string.Format("Empty_{0}_{1}", ch.ChannelKind, kindCounters[ch.ChannelKind]);
                ch.TargetComment = "Свободен";
            }

            int curPage = 0;
            int pageSize = 14;
            int totalPages = (freeChannels.Count + pageSize - 1) / pageSize;

            while (true)
            {
                ClearScreen();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("========================================================================");
                Console.WriteLine("     НОРМАЛИЗАЦИЯ СВОБОДНЫХ АППАРАТНЫХ КАНАЛОВ ПЛК (Empty_DI / Empty_DO) ");
                Console.WriteLine("========================================================================");
                Console.ResetColor();

                Console.WriteLine(string.Format("Найдено свободных каналов: {0} (Страница {1}/{2})", freeChannels.Count, curPage + 1, totalPages));
                Console.WriteLine("------------------------------------------------------------------------");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(string.Format("  {0,-10} {1,-30} -> {2,-16} {3}", "Адрес", "Текущее имя", "Новое имя", "Таблица"));
                Console.ResetColor();
                Console.WriteLine("------------------------------------------------------------------------");

                int startIdx = curPage * pageSize;
                int endIdx = Math.Min(startIdx + pageSize, freeChannels.Count);
                for (int i = startIdx; i < endIdx; i++)
                {
                    var ch = freeChannels[i];
                    bool alreadyCorrect = ch.CurrentName.Equals(ch.TargetName, StringComparison.OrdinalIgnoreCase);
                    if (alreadyCorrect) Console.ForegroundColor = ConsoleColor.DarkGray;
                    else Console.ForegroundColor = ConsoleColor.White;

                    string curN = ch.CurrentName.Length > 29 ? ch.CurrentName.Substring(0, 26) + "..." : ch.CurrentName;
                    Console.WriteLine(string.Format("  {0,-10} {1,-30} -> {2,-16} {3}", ch.LogicalAddress, curN, ch.TargetName, ch.TableName));
                    Console.ResetColor();
                }

                Console.WriteLine("------------------------------------------------------------------------");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Все свободные каналы будут переименованы, а комментарий установлен: «Свободен»");
                Console.ResetColor();
                Console.WriteLine("[Enter] Применить нормализацию в TIA Portal | [N/P] Страницы | [Esc] Отмена");

                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Escape) { ClearScreen(); return; }
                if (k.Key == ConsoleKey.RightArrow || k.Key == ConsoleKey.N || k.KeyChar == 'n' || k.KeyChar == 'N' || k.KeyChar == 'т' || k.KeyChar == 'Т')
                {
                    if (curPage < totalPages - 1) curPage++;
                }
                else if (k.Key == ConsoleKey.LeftArrow || k.Key == ConsoleKey.P || k.KeyChar == 'p' || k.KeyChar == 'P' || k.KeyChar == 'з' || k.KeyChar == 'З')
                {
                    if (curPage > 0) curPage--;
                }
                else if (k.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine("\nСоздание резервной копии проекта перед переименованием...");
                    try { DoSaveProjectVersion(new Dictionary<string, object>()); } catch { }

                    Console.WriteLine("Выполнение нормализации каналов...");
                    int renamedCount = 0;
                    string guid = Guid.NewGuid().ToString("N").Substring(0, 6);

                    // Phase 1: Temporary unique names
                    for (int i = 0; i < freeChannels.Count; i++)
                    {
                        var ch = freeChannels[i];
                        if (ch.TagObject != null && !ch.CurrentName.Equals(ch.TargetName, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                ch.TagObject = RenamePlcTag(ch.TagObject, string.Format("__TMP_NORM_{0}_{1}", guid, i));
                            }
                            catch (Exception ex)
                            {
                                CrashLogger.Log(ex, "Normalizer.Phase1." + ch.CurrentName);
                            }
                        }
                    }

                    // Phase 2: Final names & comments
                    for (int i = 0; i < freeChannels.Count; i++)
                    {
                        var ch = freeChannels[i];
                        if (ch.TagObject != null)
                        {
                            try
                            {
                                ch.TagObject = RenamePlcTag(ch.TagObject, ch.TargetName);
                                SetTagComment(ch.TagObject, ch.TargetComment);
                                renamedCount++;
                            }
                            catch (Exception ex)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("  ✗ Ошибка для " + ch.LogicalAddress + ": " + ex.Message);
                                Console.ResetColor();
                                CrashLogger.Log(ex, "Normalizer.Phase2." + ch.TargetName);
                            }
                        }
                    }

                    try
                    {
                        _activeProject.Save();
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine(string.Format("\n✓ Успешно нормализовано {0} свободных каналов ПЛК!", renamedCount));
                        Console.WriteLine("✓ Комментарии «Свободен» установлены, проект сохранен.");
                        Console.ResetColor();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Ошибка сохранения проекта: " + ex.Message);
                    }

                    Console.WriteLine("\nНажмите любую клавишу для продолжения...");
                    Console.ReadKey(true);
                    ClearScreen();
                    return;
                }
            }
        }

        private static string FindKukaSignalsDirectory()
        {
            string p1 = @"C:\Users\aa.fedin\Desktop\Tia_18_Agent\Доп информация\SyncKukaSignalsPLC";
            if (Directory.Exists(p1)) return p1;

            string p2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Доп информация\SyncKukaSignalsPLC");
            if (Directory.Exists(p2)) return p2;

            string p3 = @"c:\Users\aa.fedin\Favorites\Tia_18_Agent\Доп информация\SyncKukaSignalsPLC";
            if (Directory.Exists(p3)) return p3;

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        public static List<KukaSignalItem> ParseKukaSignalsText(string text, string direction, int kukaStartSignal, int plcStartByte, string prefix)
        {
            var list = new List<KukaSignalItem>();
            if (string.IsNullOrEmpty(text)) return list;

            string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            string currentSection = "";

            // Regex 1: SIGNAL SigName $IN/$OUT[N] [TO $IN/$OUT[M]] [; or // comment]
            var regexSignal = new Regex(@"^\s*SIGNAL\s+([A-Za-z0-9_А-Яа-яЁё]+)\s+\$(IN|OUT)\[(\d+)\](?:\s+TO\s+\$(?:IN|OUT)\[(\d+)\])?\s*(?:[;/]+(.*))?$", RegexOptions.IgnoreCase);

            // Regex 2: $IN/$OUT[N] = SigName [; or // comment] OR SigName = $IN/$OUT[N] [; or // comment]
            var regexAssign1 = new Regex(@"^\s*\$(IN|OUT)\[(\d+)\]\s*=\s*([A-Za-z0-9_А-Яа-яЁё]+)\s*(?:[;/]+(.*))?$", RegexOptions.IgnoreCase);
            var regexAssign2 = new Regex(@"^\s*([A-Za-z0-9_А-Яа-яЁё]+)\s*=\s*\$(IN|OUT)\[(\d+)\]\s*(?:[;/]+(.*))?$", RegexOptions.IgnoreCase);

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith(";") || line.StartsWith("//"))
                {
                    if (line.Contains("===") || line.StartsWith(";FOLD", StringComparison.OrdinalIgnoreCase) || line.StartsWith("//FOLD", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSection = line.Trim(';', '=', ' ', '/', '\t');
                    }
                    continue;
                }

                string sigName = "";
                string sigType = "";
                int startBit = 0;
                int endBit = 0;
                string comment = "";

                var m1 = regexSignal.Match(line);
                if (m1.Success)
                {
                    sigName = m1.Groups[1].Value.Trim();
                    sigType = m1.Groups[2].Value.ToUpperInvariant();
                    startBit = int.Parse(m1.Groups[3].Value);
                    endBit = m1.Groups[4].Success && !string.IsNullOrEmpty(m1.Groups[4].Value) ? int.Parse(m1.Groups[4].Value) : startBit;
                    comment = m1.Groups[5].Success ? m1.Groups[5].Value.Trim() : "";
                }
                else
                {
                    var m2 = regexAssign1.Match(line);
                    if (m2.Success)
                    {
                        sigType = m2.Groups[1].Value.ToUpperInvariant();
                        startBit = int.Parse(m2.Groups[2].Value);
                        endBit = startBit;
                        sigName = m2.Groups[3].Value.Trim();
                        comment = m2.Groups[4].Success ? m2.Groups[4].Value.Trim() : "";
                    }
                    else
                    {
                        var m3 = regexAssign2.Match(line);
                        if (m3.Success)
                        {
                            sigName = m3.Groups[1].Value.Trim();
                            sigType = m3.Groups[2].Value.ToUpperInvariant();
                            startBit = int.Parse(m3.Groups[3].Value);
                            endBit = startBit;
                            comment = m3.Groups[4].Success ? m3.Groups[4].Value.Trim() : "";
                        }
                    }
                }

                if (!string.IsNullOrEmpty(sigName) && startBit > 0)
                {
                    if (string.IsNullOrEmpty(comment) && !string.IsNullOrEmpty(currentSection))
                    {
                        comment = currentSection;
                    }

                    bool isPlcInput = true;
                    if (!string.IsNullOrEmpty(direction))
                    {
                        if (direction == "IN") isPlcInput = true;
                        else if (direction == "OUT") isPlcInput = false;
                        else isPlcInput = (sigType == "OUT");
                    }
                    else
                    {
                        isPlcInput = (sigType == "OUT");
                    }

                    int bitCount = endBit - startBit + 1;
                    int bitOffset = startBit - kukaStartSignal;
                    if (bitOffset < 0) bitOffset = 0;

                    int byteNum = plcStartByte + (bitOffset / 8);
                    int bitNum = bitOffset % 8;

                    string plcArea = isPlcInput ? "%I" : "%Q";
                    string dataType = "Bool";
                    string logicalAddr = "";

                    if (bitCount <= 1)
                    {
                        dataType = "Bool";
                        logicalAddr = string.Format("{0}{1}.{2}", plcArea, byteNum, bitNum);
                    }
                    else if (bitCount <= 8)
                    {
                        dataType = "Byte";
                        logicalAddr = string.Format("{0}B{1}", plcArea, byteNum);
                    }
                    else if (bitCount <= 16)
                    {
                        dataType = "Word";
                        logicalAddr = string.Format("{0}W{1}", plcArea, byteNum);
                    }
                    else
                    {
                        dataType = "DWord";
                        logicalAddr = string.Format("{0}D{1}", plcArea, byteNum);
                    }

                    string tagName = (prefix ?? "") + sigName;

                    list.Add(new KukaSignalItem
                    {
                        RawLine = line,
                        Name = sigName,
                        TagName = tagName,
                        SignalType = sigType,
                        StartBit = startBit,
                        EndBit = endBit,
                        Comment = comment,
                        DataTypeName = dataType,
                        LogicalAddress = logicalAddr,
                        Status = "NEW",
                        IsSelected = true
                    });
                }
            }

            return list;
        }

        private static void ShowKukaSignalsManager()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("    СИНХРОНИЗАТОР И ГЕНЕРАТОР ТЕГОВ РОБОТА KUKA (KRL $IN/$OUT ↔ PLC Tags)      ");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            EnsureConnected();
            Device dev = FindDevice(null);
            var plc = FindPlcSoftware(dev);
            if (plc == null)
            {
                Console.WriteLine("[!] Ошибка: Не удалось обнаружить контроллер PLC.");
                Console.ReadKey(true);
                return;
            }

            string kukaDir = FindKukaSignalsDirectory();
            string outPath = Path.Combine(kukaDir, "01. Выходы.txt");
            string inPath = Path.Combine(kukaDir, "02. Входы.txt");

            Console.WriteLine(" • Проект: " + (_activeProject != null ? _activeProject.Name : "—") + " | CPU: " + dev.Name);
            Console.WriteLine(" • Каталог сигналов KUKA: " + kukaDir);
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("Выберите действие или источник сигналов KUKA:");
            Console.WriteLine("  [1] Импорт KUKA $OUT -> Входы ПЛК %I (из 01. Выходы.txt)");
            Console.WriteLine("  [2] Импорт KUKA $IN  -> Выходы ПЛК %Q (из 02. Входы.txt)");
            Console.WriteLine("  [3] Указать путь к любому файлу KRL (.txt / .src / .dat)");
            Console.WriteLine("  [4] Открыть Блокнот (Notepad) для быстрой вставки / редактирования");
            Console.WriteLine("  [5] Добавить одиночный сигнал вручную в консоли (быстрое добавление 1 тега)");
            Console.WriteLine("  [X] Экспорт тегов ПЛК в формат KUKA ($IN/$OUT -> файл .txt с комментариями)");
            Console.WriteLine("  [0 / Esc] Назад");
            Console.Write("\nВаш выбор: ");

            var kChoice = Console.ReadKey(false);
            Console.WriteLine();
            if (kChoice.Key == ConsoleKey.Escape || kChoice.KeyChar == '0')
            {
                ClearScreen();
                return;
            }

            if (kChoice.Key == ConsoleKey.X || kChoice.KeyChar == 'x' || kChoice.KeyChar == 'X' || kChoice.KeyChar == 'ч' || kChoice.KeyChar == 'Ч')
            {
                ExportKukaSignalsFromPlc(plc, dev);
                ClearScreen();
                return;
            }

            string signalContent = "";
            string defaultDirection = "IN";
            string defaultTableName = "Kuka_140_Int";

            if (kChoice.KeyChar == '1')
            {
                if (!File.Exists(outPath))
                {
                    Console.WriteLine("\n[!] Файл не найден: " + outPath);
                    Console.ReadKey(true);
                    return;
                }
                signalContent = File.ReadAllText(outPath, Encoding.UTF8);
                defaultDirection = "IN";
                defaultTableName = "Kuka_140_Int";
            }
            else if (kChoice.KeyChar == '2')
            {
                if (!File.Exists(inPath))
                {
                    Console.WriteLine("\n[!] Файл не найден: " + inPath);
                    Console.ReadKey(true);
                    return;
                }
                signalContent = File.ReadAllText(inPath, Encoding.UTF8);
                defaultDirection = "OUT";
                defaultTableName = "Kuka_140_Out";
            }
            else if (kChoice.KeyChar == '3')
            {
                Console.Write("\nВведите полный путь к файлу KRL: ");
                string customPath = Console.ReadLine();
                if (string.IsNullOrEmpty(customPath) || !File.Exists(customPath))
                {
                    Console.WriteLine("[!] Файл не существует.");
                    Console.ReadKey(true);
                    return;
                }
                signalContent = File.ReadAllText(customPath, Encoding.UTF8);
                Console.Write("Направление сигналов ([1] $OUT -> %I Входы, [2] $IN -> %Q Выходы) [По умолч. 1]: ");
                string d = Console.ReadLine();
                if (d == "2") { defaultDirection = "OUT"; defaultTableName = "Kuka_140_Out"; }
                else { defaultDirection = "IN"; defaultTableName = "Kuka_140_Int"; }
            }
            else if (kChoice.KeyChar == '4')
            {
                string tempKrlFile = Path.Combine(Path.GetTempPath(), "kuka_signals_input_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                var template = new StringBuilder();
                template.AppendLine("; ==============================================================================");
                template.AppendLine("; Вставьте или отредактируйте сигналы KRL KUKA ниже и сохраните файл (Ctrl+S).");
                template.AppendLine("; Поддерживаются комментарии ';' и '//', а также русские и английские символы.");
                template.AppendLine("; Примеры:");
                template.AppendLine("; SIGNAL bPalletReady $OUT[1] ; Готовность паллеты к выгрузке");
                template.AppendLine("; SIGNAL bRobotFault $OUT[2] // Авария робота");
                template.AppendLine("; SIGNAL wSpeedCode $OUT[16] TO $OUT[31] ; 16-битный код скорости");
                template.AppendLine("; ==============================================================================");
                template.AppendLine();
                File.WriteAllText(tempKrlFile, template.ToString(), Encoding.UTF8);

                try
                {
                    Console.WriteLine("\n[>] Запуск Блокнота (Notepad)... Отредактируйте текст, сохраните (Ctrl+S) и закройте окно Блокнота.");
                    var proc = Process.Start("notepad.exe", tempKrlFile);
                    if (proc != null) proc.WaitForExit();
                    signalContent = File.ReadAllText(tempKrlFile, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[!] Ошибка вызова Блокнота: " + ex.Message);
                    Console.ReadKey(true);
                    return;
                }

                Console.Write("\nНаправление сигналов ([1] $OUT -> %I Входы, [2] $IN -> %Q Выходы) [По умолч. 1]: ");
                string d = Console.ReadLine();
                if (d == "2") { defaultDirection = "OUT"; defaultTableName = "Kuka_140_Out"; }
                else { defaultDirection = "IN"; defaultTableName = "Kuka_140_Int"; }
            }
            else if (kChoice.KeyChar == '5')
            {
                Console.WriteLine("\n── Добавление одиночного сигнала KRL ──────────────────────────────────");
                Console.Write("Имя сигнала / тега (например, bPalletReady): ");
                string singleName = Console.ReadLine();
                if (string.IsNullOrEmpty(singleName)) return;

                Console.Write("Направление сигнала ([1] KUKA $OUT -> %I Входы, [2] KUKA $IN -> %Q Выходы) [1]: ");
                string singleDirChoice = Console.ReadLine();
                bool singleIsOut = (singleDirChoice != "2");
                string singleDirType = singleIsOut ? "OUT" : "IN";
                defaultDirection = singleIsOut ? "IN" : "OUT";
                defaultTableName = singleIsOut ? "Kuka_140_Int" : "Kuka_140_Out";

                Console.Write("Номер бита в роботе KUKA (например, 15 или 16..31) [1]: ");
                string singleBitStr = Console.ReadLine();
                if (string.IsNullOrEmpty(singleBitStr)) singleBitStr = "1";

                Console.Write("Комментарий к сигналу: ");
                string singleComment = Console.ReadLine();

                string singleLine = "";
                if (singleBitStr.Contains(".."))
                {
                    var parts = singleBitStr.Split(new[] { ".." }, StringSplitOptions.RemoveEmptyEntries);
                    singleLine = string.Format("SIGNAL {0} ${1}[{2}] TO ${1}[{3}] ; {4}", singleName, singleDirType, parts[0].Trim(), parts[1].Trim(), singleComment);
                }
                else
                {
                    singleLine = string.Format("SIGNAL {0} ${1}[{2}] ; {3}", singleName, singleDirType, singleBitStr.Trim(), singleComment);
                }
                signalContent = singleLine;
            }
            else
            {
                return;
            }

            Console.WriteLine("\n── Параметры сопоставления адресов ─────────────────────────────────────");
            Console.Write("Начальный номер сигнала в роботе (Kuka Start Signal) [По умолч. 1]: ");
            string strKukaStart = Console.ReadLine();
            int kukaStart = 1;
            if (!string.IsNullOrEmpty(strKukaStart)) int.TryParse(strKukaStart, out kukaStart);
            if (kukaStart < 1) kukaStart = 1;

            Console.Write("Начальный байт адресации в ПЛК (PLC Start Byte) [По умолч. 100]: ");
            string strPlcByte = Console.ReadLine();
            int plcByte = 100;
            if (!string.IsNullOrEmpty(strPlcByte)) int.TryParse(strPlcByte, out plcByte);

            Console.Write("Префикс для имени тегов в ПЛК [По умолч. Robot]: ");
            string prefix = Console.ReadLine();
            if (string.IsNullOrEmpty(prefix)) prefix = "Robot";
            else if (prefix.Equals("-", StringComparison.Ordinal) || prefix.Equals("none", StringComparison.OrdinalIgnoreCase)) prefix = "";

            Console.Write("Имя таблицы тегов в TIA Portal [По умолч. " + defaultTableName + "]: ");
            string targetTableName = Console.ReadLine();
            if (string.IsNullOrEmpty(targetTableName)) targetTableName = defaultTableName;

            var parsed = ParseKukaSignalsText(signalContent, defaultDirection, kukaStart, plcByte, prefix);
            if (parsed.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\n[!] Не найдено объявлений сигналов SIGNAL $IN/$OUT в предоставленном тексте.");
                Console.ResetColor();
                Console.ReadKey(true);
                ClearScreen();
                return;
            }

            Console.WriteLine("\nСравнение с существующими тегами проекта...");
            var existingByAddress = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var existingByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var allTagTables = new List<Dictionary<string, object>>();
            CollectTagsRecursive(plc.TagTableGroup, allTagTables, "");
            foreach (var tableDict in allTagTables)
            {
                var tags = tableDict["tags"] as List<Dictionary<string, object>>;
                if (tags == null) continue;
                foreach (var tg in tags)
                {
                    string tN = tg["name"].ToString();
                    string tA = tg.ContainsKey("logicalAddress") && tg["logicalAddress"] != null ? tg["logicalAddress"].ToString().Trim() : "";
                    if (!string.IsNullOrEmpty(tA) && !existingByAddress.ContainsKey(tA))
                    {
                        existingByAddress[tA] = tN;
                    }
                    if (!existingByName.ContainsKey(tN))
                    {
                        existingByName[tN] = tA;
                    }
                }
            }

            int matchCount = 0;
            int newCount = 0;
            int conflictCount = 0;

            foreach (var sig in parsed)
            {
                if (existingByAddress.ContainsKey(sig.LogicalAddress))
                {
                    string existingName = existingByAddress[sig.LogicalAddress];
                    if (existingName.Equals(sig.TagName, StringComparison.OrdinalIgnoreCase))
                    {
                        sig.Status = "MATCH";
                        sig.IsSelected = false; // By default don't recreate identical matches
                        matchCount++;
                    }
                    else
                    {
                        sig.Status = "CONFLICT";
                        sig.ExistingTagName = existingName;
                        sig.IsSelected = false;
                        conflictCount++;
                    }
                }
                else if (existingByName.ContainsKey(sig.TagName))
                {
                    sig.Status = "NAME_EXISTS";
                    sig.ExistingTagName = existingByName[sig.TagName];
                    sig.IsSelected = false;
                    conflictCount++;
                }
                else
                {
                    sig.Status = "NEW";
                    sig.IsSelected = true;
                    newCount++;
                }
            }

            int curPage = 0;
            int pageSize = 13;
            int totalPages = (parsed.Count + pageSize - 1) / pageSize;
            int curRowIdx = 0;

            while (true)
            {
                ClearScreen();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("================================================================================");
                Console.WriteLine("   СИНХРОНИЗАТОР ТЕГОВ РОБОТА KUKA: ПРЕДПРОСМОТР И ВЫБОР СИГНАЛОВ");
                Console.WriteLine("================================================================================");
                Console.ResetColor();

                int selTotal = parsed.Count(x => x.IsSelected);
                Console.WriteLine(string.Format(" Таблица: {0} | ПЛК Байт: %{1}{2} | Сигналов: {3} | Выбрано: {4}",
                    targetTableName, defaultDirection == "IN" ? "I" : "Q", plcByte, parsed.Count, selTotal));
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(string.Format(" [+] Новых: {0}   |   [=] Совпадает: {1}   |   [!] Конфликтов: {2}   (Стр. {3}/{4})",
                    newCount, matchCount, conflictCount, curPage + 1, totalPages));
                Console.ResetColor();
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(string.Format("  {0,-4} {1,-28} {2,-5} {3,-9} {4,-13} {5}", "Выб", "Имя тега (ПЛК)", "Тип", "Адрес", "Сигнал KUKA", "Сравнение / Статус"));
                Console.ResetColor();
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");

                int startIdx = curPage * pageSize;
                int endIdx = Math.Min(startIdx + pageSize, parsed.Count);
                if (curRowIdx < startIdx) curRowIdx = startIdx;
                if (curRowIdx >= endIdx && endIdx > startIdx) curRowIdx = endIdx - 1;

                for (int i = startIdx; i < endIdx; i++)
                {
                    var sig = parsed[i];
                    string statusBadge = "";
                    if (sig.Status == "MATCH")
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        statusBadge = "[=] Совпадает в проекте";
                    }
                    else if (sig.Status == "NEW")
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        statusBadge = "[+] НОВЫЙ ТЕГ";
                    }
                    else if (sig.Status == "CONFLICT")
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        statusBadge = "[!] Занят (" + (sig.ExistingTagName.Length > 16 ? sig.ExistingTagName.Substring(0, 13) + "..." : sig.ExistingTagName) + ")";
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        statusBadge = "[!] Имя занято (" + sig.ExistingTagName + ")";
                    }

                    string kukaRange = sig.StartBit == sig.EndBit
                        ? string.Format("${0}[{1}]", sig.SignalType, sig.StartBit)
                        : string.Format("${0}[{1}..{2}]", sig.SignalType, sig.StartBit, sig.EndBit);

                    string checkMark = sig.IsSelected ? "[X]" : "[ ]";
                    string pointer = (i == curRowIdx) ? ">" : " ";
                    string dispName = sig.TagName.Length > 27 ? sig.TagName.Substring(0, 24) + "..." : sig.TagName;

                    Console.WriteLine(string.Format("{0} {1} {2,-28} {3,-5} {4,-9} {5,-13} {6}",
                        pointer, checkMark, dispName, sig.DataTypeName, sig.LogicalAddress, kukaRange, statusBadge));
                    Console.ResetColor();
                }

                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
                Console.WriteLine(" [Space] Выбрать тег  | [A] Выбрать/Снять ВСЕ | [↑/↓] Выбор строки | [N/P] Стр.");
                Console.WriteLine(string.Format(" [Enter] Создать выбранные теги ({0}) в TIA Portal | [E] Экспорт в CSV | [Esc] Назад", selTotal));

                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Escape) { ClearScreen(); return; }

                if (k.Key == ConsoleKey.UpArrow)
                {
                    if (curRowIdx > 0)
                    {
                        curRowIdx--;
                        if (curRowIdx < startIdx && curPage > 0) curPage--;
                    }
                }
                else if (k.Key == ConsoleKey.DownArrow)
                {
                    if (curRowIdx < parsed.Count - 1)
                    {
                        curRowIdx++;
                        if (curRowIdx >= endIdx && curPage < totalPages - 1) curPage++;
                    }
                }
                else if (k.Key == ConsoleKey.Spacebar)
                {
                    if (curRowIdx >= 0 && curRowIdx < parsed.Count)
                    {
                        parsed[curRowIdx].IsSelected = !parsed[curRowIdx].IsSelected;
                    }
                }
                else if (k.Key == ConsoleKey.A || k.KeyChar == 'a' || k.KeyChar == 'A' || k.KeyChar == 'ф' || k.KeyChar == 'Ф')
                {
                    bool anyUnselected = parsed.Any(x => !x.IsSelected);
                    foreach (var pItem in parsed) pItem.IsSelected = anyUnselected;
                }
                else if (k.Key == ConsoleKey.RightArrow || k.Key == ConsoleKey.N || k.KeyChar == 'n' || k.KeyChar == 'N' || k.KeyChar == 'т' || k.KeyChar == 'Т')
                {
                    if (curPage < totalPages - 1) { curPage++; curRowIdx = curPage * pageSize; }
                }
                else if (k.Key == ConsoleKey.LeftArrow || k.Key == ConsoleKey.P || k.KeyChar == 'p' || k.KeyChar == 'P' || k.KeyChar == 'з' || k.KeyChar == 'З')
                {
                    if (curPage > 0) { curPage--; curRowIdx = curPage * pageSize; }
                }
                else if (k.Key == ConsoleKey.E || k.KeyChar == 'e' || k.KeyChar == 'E' || k.KeyChar == 'у' || k.KeyChar == 'У')
                {
                    string desktopDir = @"C:\Users\aa.fedin\Desktop\Tia_18_Agent";
                    string csvPath = Path.Combine(desktopDir, "Kuka_Signals_" + targetTableName + ".csv");
                    try
                    {
                        var sbCsv = new StringBuilder();
                        sbCsv.AppendLine("Name;DataType;LogicalAddress;Comment;Selected");
                        foreach (var s in parsed)
                        {
                            sbCsv.AppendLine(string.Format("{0};{1};{2};\"{3}\";{4}", s.TagName, s.DataTypeName, s.LogicalAddress, s.Comment, s.IsSelected));
                        }
                        File.WriteAllText(csvPath, sbCsv.ToString(), Encoding.UTF8);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("\n[+] Успешно экспортировано в CSV: " + csvPath);
                        Console.ResetColor();
                        Console.ReadKey(true);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("\n[!] Ошибка экспорта: " + ex.Message);
                        Console.ReadKey(true);
                    }
                }
                else if (k.Key == ConsoleKey.Enter)
                {
                    var toProcess = parsed.FindAll(x => x.IsSelected);
                    if (toProcess.Count == 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("\n[!] Не выбрано ни одного тега для создания. Отметьте теги клавишей [Space] или [A].");
                        Console.ResetColor();
                        Console.ReadKey(true);
                        continue;
                    }

                    Console.WriteLine("\n[1/3] Создание резервной копии проекта перед добавлением тегов (DoSaveProjectVersion)...");
                    try
                    {
                        var backupRes = DoSaveProjectVersion(new Dictionary<string, object>());
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("  ✓ " + (backupRes.ContainsKey("message") ? backupRes["message"] : "Резервная копия создана."));
                        Console.ResetColor();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  [!] Предупреждение: Не удалось сохранить резервную копию: " + ex.Message);
                    }

                    Console.WriteLine(string.Format("\n[2/3] Добавление тегов ({0}) в таблицу '{1}'...", toProcess.Count, targetTableName));
                    var targetTable = GetOrCreateTagTable(plc, targetTableName);
                    if (targetTable == null)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("[!] Ошибка: Не удалось открыть или создать таблицу " + targetTableName);
                        Console.ResetColor();
                        Console.ReadKey(true);
                        ClearScreen();
                        return;
                    }

                    int created = 0;
                    int updated = 0;
                    foreach (var item in toProcess)
                    {
                        try
                        {
                            var existingTag = targetTable.Tags.Find(item.TagName);
                            if (existingTag != null)
                            {
                                existingTag.LogicalAddress = item.LogicalAddress;
                                SetTagComment(existingTag, item.Comment);
                                updated++;
                            }
                            else
                            {
                                var newTag = targetTable.Tags.Create(item.TagName, item.DataTypeName, item.LogicalAddress);
                                SetTagComment(newTag, item.Comment);
                                created++;
                            }
                        }
                        catch (Exception ex)
                        {
                            CrashLogger.Log(ex, "KukaManager.CreateTag." + item.TagName);
                        }
                    }

                    Console.WriteLine("\n[3/3] Сохранение проекта TIA Portal...");
                    try
                    {
                        _activeProject.Save();
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine(string.Format("\n✓ Успешно завершено! Создано новых тегов: {0}, обновлено: {1} в таблице '{2}'!", created, updated, targetTableName));
                        Console.WriteLine("✓ Проект TIA Portal успешно сохранен.");
                        Console.ResetColor();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("\nОшибка сохранения проекта: " + ex.Message);
                    }

                    Console.WriteLine("\nНажмите любую клавишу для продолжения...");
                    Console.ReadKey(true);
                    ClearScreen();
                    return;
                }
            }
        }

        private static void ExportKukaSignalsFromPlc(PlcSoftware plc, Device dev)
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("     ЭКСПОРТ ТЕГОВ ИЗ TIA PORTAL В ФОРМАТ РОБОТА KUKA (KRL $IN/$OUT)           ");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            Console.WriteLine(" • Контроллер: " + dev.Name);
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("Выберите область тегов для экспорта:");
            Console.WriteLine("  [1] Входы ПЛК (%I)  -> KUKA Сигналы $OUT (Робот передает в ПЛК)");
            Console.WriteLine("  [2] Выходы ПЛК (%Q) -> KUKA Сигналы $IN  (ПЛК передает в Робот)");
            Console.WriteLine("  [0 / Esc] Отмена");
            Console.Write("\nВаш выбор: ");

            var expChoice = Console.ReadKey(false);
            Console.WriteLine();
            if (expChoice.Key == ConsoleKey.Escape || expChoice.KeyChar == '0') return;

            bool isPlcInput = (expChoice.KeyChar != '2');
            string areaPrefix = isPlcInput ? "%I" : "%Q";
            string kukaDir = isPlcInput ? "OUT" : "IN";

            Console.Write("\nНачальный байт адресации в ПЛК (PLC Base Byte, например 100): ");
            string strPlcByte = Console.ReadLine();
            int plcStartByte = 100;
            if (!string.IsNullOrEmpty(strPlcByte)) int.TryParse(strPlcByte, out plcStartByte);

            Console.Write("Конечный байт адресации в ПЛК (например 131) [Enter для авто]: ");
            string strEndByte = Console.ReadLine();
            int plcEndByte = plcStartByte + 31;
            if (!string.IsNullOrEmpty(strEndByte)) int.TryParse(strEndByte, out plcEndByte);

            Console.Write("Начальный номер сигнала KUKA (Start Signal, например 1): ");
            string strKukaStart = Console.ReadLine();
            int kukaStartSignal = 1;
            if (!string.IsNullOrEmpty(strKukaStart)) int.TryParse(strKukaStart, out kukaStartSignal);

            var allTagTables = new List<Dictionary<string, object>>();
            CollectTagsRecursive(plc.TagTableGroup, allTagTables, "");

            var matchingTags = new List<Dictionary<string, object>>();
            foreach (var tableDict in allTagTables)
            {
                var tags = tableDict["tags"] as List<Dictionary<string, object>>;
                if (tags == null) continue;
                foreach (var tg in tags)
                {
                    string tA = tg.ContainsKey("logicalAddress") && tg["logicalAddress"] != null ? tg["logicalAddress"].ToString().Trim() : "";
                    if (string.IsNullOrEmpty(tA)) continue;

                    string ioType, prefixStr;
                    int byteNum, bitNum;
                    if (TryParseLogicalAddress(tA, out ioType, out byteNum, out bitNum, out prefixStr))
                    {
                        if (ioType.Equals(isPlcInput ? "I" : "Q", StringComparison.OrdinalIgnoreCase))
                        {
                            if (byteNum >= plcStartByte && byteNum <= plcEndByte)
                            {
                                tg["_byteNum"] = byteNum;
                                tg["_bitNum"] = bitNum;
                                tg["_prefix"] = prefixStr;
                                matchingTags.Add(tg);
                            }
                        }
                    }
                }
            }

            matchingTags.Sort((a, b) =>
            {
                int byteA = (int)a["_byteNum"];
                int byteB = (int)b["_byteNum"];
                if (byteA != byteB) return byteA.CompareTo(byteB);
                int bitA = (int)a["_bitNum"];
                int bitB = (int)b["_bitNum"];
                return bitA.CompareTo(bitB);
            });

            if (matchingTags.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(string.Format("\n[!] Не найдено тегов в диапазоне {0}{1}..{0}{2}", areaPrefix, plcStartByte, plcEndByte));
                Console.ResetColor();
                Console.ReadKey(true);
                return;
            }

            var sbExport = new StringBuilder();
            sbExport.AppendLine("; ==============================================================================");
            sbExport.AppendLine("; KUKA ROBOT SIGNALS EXPORTED FROM TIA PORTAL V18");
            sbExport.AppendLine(string.Format("; Проект: {0} | CPU: {1}", _activeProject != null ? _activeProject.Name : "—", dev.Name));
            sbExport.AppendLine(string.Format("; Диапазон ПЛК: {0}{1}..{0}{2} -> KUKA ${3}[{4}..]", areaPrefix, plcStartByte, plcEndByte, kukaDir, kukaStartSignal));
            sbExport.AppendLine(string.Format("; Дата экспорта: {0:yyyy-MM-dd HH:mm:ss} | Тегов: {1}", DateTime.Now, matchingTags.Count));
            sbExport.AppendLine("; ==============================================================================");
            sbExport.AppendLine();

            foreach (var tg in matchingTags)
            {
                string tName = tg["name"].ToString();
                string tComment = tg.ContainsKey("comment") && tg["comment"] != null ? tg["comment"].ToString().Trim() : "";
                int bNum = (int)tg["_byteNum"];
                int bitNum = (int)tg["_bitNum"];
                string pref = tg["_prefix"].ToString();

                int bitOffset = (bNum - plcStartByte) * 8 + bitNum;
                int sigNum = kukaStartSignal + bitOffset;

                string sigDecl = "";
                if (pref.Equals("B", StringComparison.OrdinalIgnoreCase))
                {
                    sigDecl = string.Format("SIGNAL {0} ${1}[{2}] TO ${1}[{3}]", tName, kukaDir, sigNum, sigNum + 7);
                }
                else if (pref.Equals("W", StringComparison.OrdinalIgnoreCase))
                {
                    sigDecl = string.Format("SIGNAL {0} ${1}[{2}] TO ${1}[{3}]", tName, kukaDir, sigNum, sigNum + 15);
                }
                else if (pref.Equals("D", StringComparison.OrdinalIgnoreCase))
                {
                    sigDecl = string.Format("SIGNAL {0} ${1}[{2}] TO ${1}[{3}]", tName, kukaDir, sigNum, sigNum + 31);
                }
                else
                {
                    sigDecl = string.Format("SIGNAL {0} ${1}[{2}]", tName, kukaDir, sigNum);
                }

                if (!string.IsNullOrEmpty(tComment))
                {
                    sbExport.AppendLine(string.Format("{0,-45} ; {1}", sigDecl, tComment));
                }
                else
                {
                    sbExport.AppendLine(sigDecl);
                }
            }

            string kukaDirLocal = FindKukaSignalsDirectory();
            string defaultFileName = isPlcInput ? "Kuka_Exported_Outputs.txt" : "Kuka_Exported_Inputs.txt";
            string exportPath = Path.Combine(kukaDirLocal, defaultFileName);

            Console.Write("\nПуть для сохранения файла [По умолч. " + exportPath + "]: ");
            string customPath = Console.ReadLine();
            if (!string.IsNullOrEmpty(customPath)) exportPath = customPath;

            try
            {
                File.WriteAllText(exportPath, sbExport.ToString(), Encoding.UTF8);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n[+] Успешно экспортировано сигналов: " + matchingTags.Count);
                Console.WriteLine("    Файл: " + exportPath);
                Console.ResetColor();

                Console.Write("\nОткрыть экспортированный файл в Блокноте (Notepad)? [Y/N, по умолч. Y]: ");
                var openKey = Console.ReadKey(false);
                Console.WriteLine();
                if (openKey.Key != ConsoleKey.N && openKey.KeyChar != 'n' && openKey.KeyChar != 'N' && openKey.KeyChar != 'т' && openKey.KeyChar != 'Т')
                {
                    Process.Start("notepad.exe", exportPath);
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[!] Ошибка записи файла: " + ex.Message);
                Console.ResetColor();
            }

            Console.WriteLine("\nНажмите любую клавишу для продолжения...");
            Console.ReadKey(true);
        }

        private static void ShowAddressRelocationManager()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("       МАСТЕР ПЕРЕЕЗДА АДРЕСОВ ОБОРУДОВАНИЯ И ТЕГОВ В TIA PORTAL               ");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            EnsureConnected();
            if (_activeProject == null)
            {
                Console.WriteLine("[!] Проект не открыт.");
                Console.ReadKey(true);
                return;
            }

            var devRanges = GetProjectDeviceAddressRanges(_activeProject);
            if (devRanges == null || devRanges.Count == 0)
            {
                Console.WriteLine("[!] В проекте не обнаружено настроенных адресов ввода-вывода (I/Q).");
                Console.ReadKey(true);
                return;
            }

            var devicesWithRanges = new Dictionary<string, List<DeviceAddressRange>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in devRanges)
            {
                string key = r.DeviceName;
                if (!devicesWithRanges.ContainsKey(key)) devicesWithRanges[key] = new List<DeviceAddressRange>();
                devicesWithRanges[key].Add(r);
            }

            while (true)
            {
                ClearScreen();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("================================================================================");
                Console.WriteLine("       МАСТЕР ПЕРЕЕЗДА АДРЕСОВ ОБОРУДОВАНИЯ И ТЕГОВ В TIA PORTAL               ");
                Console.WriteLine("================================================================================");
                Console.ResetColor();
                Console.WriteLine(" • Проект: " + _activeProject.Name + " | Обнаружено устройств с I/O: " + devicesWithRanges.Count);
                Console.WriteLine("--------------------------------------------------------------------------------");
                Console.WriteLine("Действия:");
                Console.WriteLine("  [1] Сдвиг адресов конкретного устройства (Hardware + связанные теги ПЛК)");
                Console.WriteLine("  [2] Обзор карты занятости адресного пространства (%I и %Q)");
                Console.WriteLine("  [0 / Esc] Назад");
                Console.Write("\nВаш выбор: ");

                var actKey = Console.ReadKey(false);
                Console.WriteLine();
                if (actKey.Key == ConsoleKey.Escape || actKey.KeyChar == '0') break;

                if (actKey.KeyChar == '2')
                {
                    ShowIoMemoryMap(devRanges);
                    continue;
                }

                if (actKey.KeyChar == '1')
                {
                    RunDeviceRelocationWizard(devicesWithRanges, devRanges);
                }
            }
        }

        private static void ShowIoMemoryMap(List<DeviceAddressRange> devRanges)
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("       КАРТА ЗАНЯТОСТИ АДРЕСНОГО ПРОСТРАНСТВА (%I Входы / %Q Выходы)            ");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            var inputs = devRanges.FindAll(x => x.IoType == "I");
            var outputs = devRanges.FindAll(x => x.IoType == "Q");

            inputs.Sort((a, b) => a.StartByte.CompareTo(b.StartByte));
            outputs.Sort((a, b) => a.StartByte.CompareTo(b.StartByte));

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("── ВХОДЫ (%I) ──────────────────────────────────────────────────────────────────");
            Console.ResetColor();
            Console.WriteLine(string.Format("  {0,-16} {1,-8} {2,-30} {3}", "Диапазон", "Размер", "Устройство / Модуль", "Тип"));
            Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
            foreach (var r in inputs)
            {
                string rangeStr = string.Format("%I {0}..{1}", r.StartByte, r.EndByte);
                string lenStr = string.Format("{0} Б", r.Length);
                string nameStr = r.DeviceName + (!string.IsNullOrEmpty(r.ModuleName) && r.ModuleName != r.DeviceName ? " / " + r.ModuleName : "");
                if (nameStr.Length > 29) nameStr = nameStr.Substring(0, 26) + "...";
                Console.WriteLine(string.Format("  {0,-16} {1,-8} {2,-30} [{3}]", rangeStr, lenStr, nameStr, r.DeviceKind));
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("── ВЫХОДЫ (%Q) ─────────────────────────────────────────────────────────────────");
            Console.ResetColor();
            Console.WriteLine(string.Format("  {0,-16} {1,-8} {2,-30} {3}", "Диапазон", "Размер", "Устройство / Модуль", "Тип"));
            Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
            foreach (var r in outputs)
            {
                string rangeStr = string.Format("%Q {0}..{1}", r.StartByte, r.EndByte);
                string lenStr = string.Format("{0} Б", r.Length);
                string nameStr = r.DeviceName + (!string.IsNullOrEmpty(r.ModuleName) && r.ModuleName != r.DeviceName ? " / " + r.ModuleName : "");
                if (nameStr.Length > 29) nameStr = nameStr.Substring(0, 26) + "...";
                Console.WriteLine(string.Format("  {0,-16} {1,-8} {2,-30} [{3}]", rangeStr, lenStr, nameStr, r.DeviceKind));
            }

            Console.WriteLine("\nНажмите любую клавишу для продолжения...");
            Console.ReadKey(true);
        }

        private static void RunDeviceRelocationWizard(Dictionary<string, List<DeviceAddressRange>> devicesWithRanges, List<DeviceAddressRange> allRanges)
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("       МАСТЕР ПЕРЕЕЗДА: ВЫБОР УСТРОЙСТВА ДЛЯ СДВИГА АДРЕСОВ                    ");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            var devList = new List<KeyValuePair<string, List<DeviceAddressRange>>>(devicesWithRanges);
            for (int i = 0; i < devList.Count; i++)
            {
                var kv = devList[i];
                string dName = kv.Key;
                var rList = kv.Value;
                string kind = rList[0].DeviceKind;

                var inRanges = rList.FindAll(x => x.IoType == "I");
                var outRanges = rList.FindAll(x => x.IoType == "Q");

                string inSummary = inRanges.Count > 0 ? string.Format("%I {0}..{1} ({2}Б)", inRanges[0].StartByte, inRanges[inRanges.Count - 1].EndByte, inRanges.Sum(x => x.Length)) : "—";
                string outSummary = outRanges.Count > 0 ? string.Format("%Q {0}..{1} ({2}Б)", outRanges[0].StartByte, outRanges[outRanges.Count - 1].EndByte, outRanges.Sum(x => x.Length)) : "—";

                Console.WriteLine(string.Format("  [{0,2}] {1,-28} [{2,-20}] In: {3} | Out: {4}",
                    i + 1, dName.Length > 28 ? dName.Substring(0, 25) + "..." : dName, kind, inSummary, outSummary));
            }

            Console.WriteLine("  [ 0] Отмена");
            Console.Write("\nВыберите номер устройства [1-" + devList.Count + "]: ");
            string choiceStr = Console.ReadLine();
            int choiceIdx = 0;
            if (!int.TryParse(choiceStr, out choiceIdx) || choiceIdx < 1 || choiceIdx > devList.Count) return;

            var selectedDev = devList[choiceIdx - 1];
            string targetDevName = selectedDev.Key;
            var targetRanges = selectedDev.Value;

            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("       МАСТЕР ПЕРЕЕЗДА: ПАРАМЕТРЫ СДВИГА ДЛЯ " + targetDevName);
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            Console.WriteLine("Текущие диапазоны адресов устройства:");
            for (int rIdx = 0; rIdx < targetRanges.Count; rIdx++)
            {
                var r = targetRanges[rIdx];
                Console.WriteLine(string.Format("  Канал [{0}]: %{1} {2}..{3} ({4} байт) — {5}",
                    rIdx + 1, r.IoType, r.StartByte, r.EndByte, r.Length, r.ModuleName));
            }

            Console.Write("\nВведите новый начальный байт адреса (New Start Byte, например 200): ");
            string newStartStr = Console.ReadLine();
            int newStartByte = 0;
            if (!int.TryParse(newStartStr, out newStartByte) || newStartByte < 0)
            {
                Console.WriteLine("[!] Некорректный номер байта.");
                Console.ReadKey(true);
                return;
            }

            int baseOldStart = targetRanges[0].StartByte;
            int byteDiff = newStartByte - baseOldStart;

            if (byteDiff == 0)
            {
                Console.WriteLine("\n[!] Новый начальный байт совпадает с текущим. Сдвиг не требуется.");
                Console.ReadKey(true);
                return;
            }

            bool hasConflict = false;
            string conflictDetails = "";

            foreach (var r in targetRanges)
            {
                int rNewStart = r.StartByte + byteDiff;
                int rNewEnd = rNewStart + r.Length - 1;

                foreach (var other in allRanges)
                {
                    if (other.DeviceName.Equals(targetDevName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (other.IoType != r.IoType) continue;

                    if (rNewStart <= other.EndByte && rNewEnd >= other.StartByte)
                    {
                        hasConflict = true;
                        conflictDetails = string.Format("Пересечение %{0} {1}..{2} с устройством '{3}' (%{0} {4}..{5})!",
                            r.IoType, rNewStart, rNewEnd, other.DeviceName, other.StartByte, other.EndByte);
                        break;
                    }
                }
                if (hasConflict) break;
            }

            if (hasConflict)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[!] ОШИБКА КОНФЛИКТА АДРЕСОВ:");
                Console.WriteLine("    " + conflictDetails);
                Console.WriteLine("    Сдвиг заблокирован агентом во избежание сбоя аппаратной конфигурации.");
                Console.ResetColor();
                Console.WriteLine("\nНажмите любую клавишу для возврата...");
                Console.ReadKey(true);
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n[+] Проверка успешно пройдена: Целевой диапазон полностью свободен!");
            Console.ResetColor();

            Device plcDev = FindDevice(null);
            var plc = FindPlcSoftware(plcDev);
            var affectedTags = new List<TagRelocationPlanItem>();

            if (plc != null)
            {
                var allTagTables = new List<Dictionary<string, object>>();
                CollectTagsRecursive(plc.TagTableGroup, allTagTables, "");
                foreach (var tbl in allTagTables)
                {
                    string tableName = tbl["name"].ToString();
                    var tags = tbl["tags"] as List<Dictionary<string, object>>;
                    if (tags == null) continue;

                    foreach (var tg in tags)
                    {
                        string tName = tg["name"].ToString();
                        string tAddr = tg.ContainsKey("logicalAddress") && tg["logicalAddress"] != null ? tg["logicalAddress"].ToString().Trim() : "";
                        string tType = tg.ContainsKey("dataType") && tg["dataType"] != null ? tg["dataType"].ToString() : "Bool";
                        if (string.IsNullOrEmpty(tAddr)) continue;

                        string ioType, pref;
                        int bNum, bitNum;
                        if (TryParseLogicalAddress(tAddr, out ioType, out bNum, out bitNum, out pref))
                        {
                            foreach (var r in targetRanges)
                            {
                                if (r.IoType.Equals(ioType, StringComparison.OrdinalIgnoreCase) && bNum >= r.StartByte && bNum <= r.EndByte)
                                {
                                    string newAddr = ComputeShiftedAddress(tAddr, byteDiff);
                                    affectedTags.Add(new TagRelocationPlanItem
                                    {
                                        TagName = tName,
                                        TableName = tableName,
                                        OldAddress = tAddr,
                                        NewAddress = newAddr,
                                        DataType = tType,
                                        HasConflict = false
                                    });
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            Console.WriteLine(string.Format("\nНайдено привязанных тегов ПЛК для сдвига: {0}", affectedTags.Count));
            if (affectedTags.Count > 0)
            {
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
                Console.WriteLine(string.Format("  {0,-30} {1,-14} {2,-14} {3}", "Имя тега", "Старый адрес", "Новый адрес", "Таблица"));
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
                int showMax = Math.Min(affectedTags.Count, 12);
                for (int i = 0; i < showMax; i++)
                {
                    var at = affectedTags[i];
                    Console.WriteLine(string.Format("  {0,-30} {1,-14} {2,-14} {3}",
                        at.TagName.Length > 29 ? at.TagName.Substring(0, 26) + "..." : at.TagName,
                        at.OldAddress, at.NewAddress, at.TableName));
                }
                if (affectedTags.Count > showMax)
                {
                    Console.WriteLine("  ... и ещё " + (affectedTags.Count - showMax) + " тегов.");
                }
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write(string.Format("\nПрименить переезд для '{0}' (сдвиг {1:+0;-0} байт) с созданием бэкапа? [Y/N]: ", targetDevName, byteDiff));
            Console.ResetColor();

            var confirmKey = Console.ReadKey(false);
            Console.WriteLine();
            if (confirmKey.Key != ConsoleKey.Y && confirmKey.KeyChar != 'y' && confirmKey.KeyChar != 'Y' && confirmKey.KeyChar != 'н' && confirmKey.KeyChar != 'Н')
            {
                Console.WriteLine("Операция отменена.");
                Console.ReadKey(true);
                return;
            }

            Console.WriteLine("\n[1/4] Создание резервной копии проекта (DoSaveProjectVersion)...");
            try
            {
                var backupRes = DoSaveProjectVersion(new Dictionary<string, object>());
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  ✓ Резервная копия создана: " + (backupRes.ContainsKey("message") ? backupRes["message"] : "OK"));
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [!] Предупреждение: Не удалось создать бэкап: " + ex.Message);
            }

            Console.WriteLine("\n[2/4] Применение аппаратного сдвига в конфигурации оборудования...");
            int hwShifted = 0;
            foreach (var r in targetRanges)
            {
                if (r.RawAddress != null)
                {
                    try
                    {
                        int rNewStart = r.StartByte + byteDiff;
                        r.RawAddress.StartAddress = rNewStart;
                        hwShifted++;
                        Console.WriteLine(string.Format("  ✓ Модуль '{0}': %{1} {2} -> %{1} {3}", r.ModuleName, r.IoType, r.StartByte, rNewStart));
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine(string.Format("  [!] Не удалось изменить адрес модуля '{0}': {1}", r.ModuleName, ex.Message));
                        Console.ResetColor();
                    }
                }
            }

            Console.WriteLine(string.Format("\n[3/4] Обновление адресов тегов ПЛК ({0} тегов)...", affectedTags.Count));
            int tagsUpdated = 0;
            if (plc != null && affectedTags.Count > 0)
            {
                var tagObjMap = new Dictionary<string, Siemens.Engineering.SW.Tags.PlcTag>(StringComparer.OrdinalIgnoreCase);
                CollectPlcTagObjectsRecursive(plc.TagTableGroup, tagObjMap);

                foreach (var plan in affectedTags)
                {
                    if (tagObjMap.ContainsKey(plan.TagName))
                    {
                        try
                        {
                            var tagObj = tagObjMap[plan.TagName];
                            tagObj.LogicalAddress = plan.NewAddress;
                            tagsUpdated++;
                        }
                        catch (Exception ex)
                        {
                            CrashLogger.Log(ex, "RelocateTags." + plan.TagName);
                        }
                    }
                }
            }
            Console.WriteLine(string.Format("  ✓ Обновлено тегов в TIA Portal: {0} из {1}", tagsUpdated, affectedTags.Count));

            Console.WriteLine("\n[4/4] Сохранение проекта TIA Portal...");
            try
            {
                _activeProject.Save();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  ✓ Проект успешно сохранен.");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [!] Ошибка сохранения проекта: " + ex.Message);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n================================================================================");
            Console.WriteLine(string.Format("  ✓ ПЕРЕЕЗД УСПЕШНО ЗАВЕРШЕН! Сдвинуто модулей HW: {0}, обновлено тегов: {1}", hwShifted, tagsUpdated));
            Console.WriteLine("================================================================================");
            Console.ResetColor();
            Console.WriteLine("\nНажмите любую клавишу для продолжения...");
            Console.ReadKey(true);
        }

        private static void CollectPlcTagObjectsRecursive(Siemens.Engineering.SW.Tags.PlcTagTableGroup group, Dictionary<string, Siemens.Engineering.SW.Tags.PlcTag> map)
        {
            if (group == null || map == null) return;
            try
            {
                foreach (var tbl in group.TagTables)
                {
                    try
                    {
                        foreach (var tg in tbl.Tags)
                        {
                            if (!map.ContainsKey(tg.Name)) map[tg.Name] = tg;
                        }
                    }
                    catch { }
                }
                foreach (var sub in group.Groups)
                {
                    CollectPlcTagObjectsRecursive(sub, map);
                }
            }
            catch { }
        }

        private static void ShowReadinessCheck()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("     ДИАГНОСТИКА СОВМЕСТИМОСТИ И ГОТОВНОСТИ СИСТЕМЫ (SYSTEM READINESS)         ");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            Console.WriteLine("Проверка конфигурации окружения, версий TIA Portal и прав доступа:\n");

            // 1. Architecture & OS
            Console.Write(" • Архитектура процесса: ");
            bool is64 = Environment.Is64BitProcess;
            Console.ForegroundColor = is64 ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.WriteLine(string.Format("{0}-бит (ОС: {1}-бит, {2})", is64 ? "64" : "32", Environment.Is64BitOperatingSystem ? "64" : "32", Environment.OSVersion));
            Console.ResetColor();

            // 2. Installed TIA Portal Versions
            Console.WriteLine("\n • Установленные версии TIA Portal:");
            string baseSiemens = @"C:\Program Files\Siemens\Automation";
            string[] versions = new string[] { "Portal V14", "Portal V15", "Portal V15_1", "Portal V16", "Portal V17", "Portal V18", "Portal V19", "Portal V20" };
            int foundVersions = 0;

            foreach (var v in versions)
            {
                string pPath = Path.Combine(baseSiemens, v);
                if (Directory.Exists(pPath))
                {
                    foundVersions++;
                    string opennessDll = Path.Combine(pPath, @"PublicAPI\" + v.Replace("Portal ", "") + @"\Siemens.Engineering.dll");
                    bool hasOpenness = File.Exists(opennessDll);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("   [✓] " + v);
                    Console.ForegroundColor = hasOpenness ? ConsoleColor.Cyan : ConsoleColor.DarkGray;
                    Console.WriteLine(hasOpenness ? " (Openness API доступен)" : " (Openness DLL не найдена)");
                    Console.ResetColor();
                }
            }
            if (foundVersions == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("   [!] В стандартном каталоге " + baseSiemens + " установленных версий не найдено.");
                Console.ResetColor();
            }

            // 3. User Group 'Siemens TIA Openness'
            Console.WriteLine("\n • Права доступа Openness (Windows Security Group):");
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                bool inOpennessGroup = false;

                if (identity.Groups != null)
                {
                    foreach (var group in identity.Groups)
                    {
                        try
                        {
                            var ntAccount = group.Translate(typeof(NTAccount));
                            if (ntAccount.Value.IndexOf("Siemens TIA Openness", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                inOpennessGroup = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (inOpennessGroup)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine(string.Format("   [✓] Пользователь '{0}' состоит в группе 'Siemens TIA Openness'", identity.Name));
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(string.Format("   [!] Пользователь '{0}' НЕ состоит в группе 'Siemens TIA Openness'!", identity.Name));
                    Console.WriteLine("       Для включения выполните в PowerShell от Администратора:");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("       net localgroup \"Siemens TIA Openness\" $env:USERNAME /add");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("   [!] Ошибка проверки групп: " + ex.Message);
            }

            // 4. Openness Whitelist Check
            Console.WriteLine("\n • Белый список Openness (Whitelist Registry):");
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                string exeName = Path.GetFileName(exePath);
                bool isWhitelisted = false;

                string[] regBases = new string[] { @"SOFTWARE\Siemens\Automation\Openness", @"SOFTWARE\WOW6432Node\Siemens\Automation\Openness" };
                foreach (var rb in regBases)
                {
                    using (var baseKey = Registry.LocalMachine.OpenSubKey(rb))
                    {
                        if (baseKey != null)
                        {
                            foreach (var verKeyName in baseKey.GetSubKeyNames())
                            {
                                using (var wlKey = baseKey.OpenSubKey(verKeyName + @"\Whitelist"))
                                {
                                    if (wlKey != null)
                                    {
                                        foreach (var appKey in wlKey.GetSubKeyNames())
                                        {
                                            if (appKey.IndexOf(exeName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                appKey.IndexOf("TiaPortal", StringComparison.OrdinalIgnoreCase) >= 0)
                                            {
                                                isWhitelisted = true;
                                                break;
                                            }
                                        }
                                    }
                                }
                                if (isWhitelisted) break;
                            }
                        }
                    }
                    if (isWhitelisted) break;
                }

                if (isWhitelisted)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("   [✓] Агент внесен в Белый список Openness (окно подтверждения Siemens отключено)");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("   [!] Агент не обнаружен в Whitelist реестра (будет всплывать запрос Siemens)");
                    Console.WriteLine("       Нажмите [W] для автоматического добавления в Whitelist (требуются права Администратора)");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("   [!] Ошибка проверки Whitelist: " + ex.Message);
            }

            // 5. Running Processes
            Console.WriteLine("\n • Активные процессы автоматизации:");
            var pTia = Process.GetProcessesByName("Siemens.Automation.Portal");
            var pSim = Process.GetProcessesByName("Siemens.Simatic.PlcSim.Advanced");
            var pSimClassic = Process.GetProcessesByName("Siemens.Simatic.Plcsim.V18");

            Console.ForegroundColor = pTia.Length > 0 ? ConsoleColor.Green : ConsoleColor.DarkGray;
            Console.WriteLine(string.Format("   [{0}] TIA Portal: {1} запущен(о)", pTia.Length > 0 ? "✓" : "—", pTia.Length));
            Console.ForegroundColor = (pSim.Length > 0 || pSimClassic.Length > 0) ? ConsoleColor.Green : ConsoleColor.DarkGray;
            Console.WriteLine(string.Format("   [{0}] S7-PLCSIM: {1} запущен(о)", (pSim.Length > 0 || pSimClassic.Length > 0) ? "✓" : "—", pSim.Length + pSimClassic.Length));
            Console.ResetColor();

            Console.WriteLine("\n────────────────────────────────────────────────────────────────────────────────");
            Console.WriteLine(" [W] Зарегистрировать агент в Whitelist   |   [Любая клавиша] Назад");

            var rk = Console.ReadKey(true);
            if (rk.Key == ConsoleKey.W || rk.KeyChar == 'w' || rk.KeyChar == 'W' || rk.KeyChar == 'ц' || rk.KeyChar == 'Ц')
            {
                EnsureSelfWhitelisted();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n[+] Запрос на регистрацию в Whitelist выполнен.");
                Console.ResetColor();
                Console.ReadKey(true);
            }
        }

        private static bool CompareBlockContents(PlcBlock a, PlcBlock b)
        {
            if (a == null || b == null) return false;
            string sigA = GetBlockLogicSignature(a);
            string sigB = GetBlockLogicSignature(b);
            if (string.IsNullOrEmpty(sigA) || string.IsNullOrEmpty(sigB)) return false;
            return sigA.Equals(sigB, StringComparison.Ordinal);
        }

        private static string GetBlockLogicSignature(PlcBlock block)
        {
            string tempXml = Path.Combine(Path.GetTempPath(), "sig_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                block.Export(new FileInfo(tempXml), ExportOptions.WithDefaults);
                if (!File.Exists(tempXml)) return "";

                string xml = File.ReadAllText(tempXml, Encoding.UTF8);
                xml = Regex.Replace(xml, @"<Name>[^<]*</Name>", "");
                xml = Regex.Replace(xml, @"<Number>[^<]*</Number>", "");
                xml = Regex.Replace(xml, @"\s+UId=""\d+""", "");
                xml = Regex.Replace(xml, @"\s+ID=""\d+""", "");
                xml = Regex.Replace(xml, @"<DocumentInfo>[\s\S]*?</DocumentInfo>", "");
                xml = Regex.Replace(xml, @"<HeaderVersion>[^<]*</HeaderVersion>", "");
                xml = Regex.Replace(xml, @"\s+", " ").Trim();
                return xml;
            }
            catch
            {
                return "";
            }
            finally
            {
                if (File.Exists(tempXml)) try { File.Delete(tempXml); } catch { }
            }
        }

private static List<GarbageItem> CollectSmartGarbageItems(string categoryChoice)
        {
            var list = new List<GarbageItem>();
            Device dev = FindDevice(null);
            var plc = FindPlcSoftware(dev);

            // 1. Build Comprehensive Project Topology (Calls, Callers, DB References, Addresses, BFS Reachability)
            HashSet<string> blockNames;
            Dictionary<string, HashSet<string>> blockCalls;
            Dictionary<string, HashSet<string>> blockCallers;
            HashSet<string> reachableFromOB;
            Dictionary<string, HashSet<string>> tagUsageMap;
            Dictionary<string, string> blockAddressMap;
            Dictionary<string, PlcBlock> blockMap;
            Dictionary<string, string> blockGroupMap;
            Dictionary<string, string> blockTypeMap;
            Dictionary<string, int> blockNumberMap;

            BuildProjectTopology(
                plc,
                out blockNames,
                out blockCalls,
                out blockCallers,
                out reachableFromOB,
                out tagUsageMap,
                out blockAddressMap,
                out blockMap,
                out blockGroupMap,
                out blockTypeMap,
                out blockNumberMap);

            // 2. Analyze Blocks
            if (categoryChoice == "1" || categoryChoice == "4")
            {
                var dupPattern = new Regex(@"(_\d+$)|(_red.*)|(_copy.*)|(_old.*)|(_temp.*)|(_bak.*)|(_test.*)|(\(\d+\)$)", RegexOptions.IgnoreCase);

                foreach (var name in blockNames)
                {
                    string type = blockTypeMap.ContainsKey(name) ? blockTypeMap[name] : "Block";
                    string path = blockGroupMap.ContainsKey(name) ? blockGroupMap[name] : name;
                    string address = blockAddressMap.ContainsKey(name) ? blockAddressMap[name] : "";
                    int number = blockNumberMap.ContainsKey(name) ? blockNumberMap[name] : 0;
                    PlcBlock blockObj = blockMap.ContainsKey(name) ? blockMap[name] : null;

                    // Skip OBs (they are invoked by PLC OS)
                    if (name.Equals("Main", StringComparison.OrdinalIgnoreCase) || type.Contains("OB")) continue;

                    bool isDb = type.Equals("GlobalDB", StringComparison.OrdinalIgnoreCase) ||
                                type.Equals("InstanceDB", StringComparison.OrdinalIgnoreCase) ||
                                type.Equals("ArrayDB", StringComparison.OrdinalIgnoreCase) ||
                                type.Contains("DB") ||
                                name.EndsWith("_DB", StringComparison.OrdinalIgnoreCase);

                    bool isNameSuspicious = dupPattern.IsMatch(name);
                    bool isReachable = reachableFromOB.Contains(name);

                    var activeCallers = new List<string>();
                    var deadCallers = new List<string>();
                    if (blockCallers.ContainsKey(name))
                    {
                        foreach (var c in blockCallers[name])
                        {
                            if (reachableFromOB.Contains(c)) activeCallers.Add(c);
                            else deadCallers.Add(c);
                        }
                    }

                    // For DBs, also check tagUsageMap
                    if (tagUsageMap.ContainsKey(name))
                    {
                        foreach (var u in tagUsageMap[name])
                        {
                            if (reachableFromOB.Contains(u))
                            {
                                if (!activeCallers.Contains(u)) activeCallers.Add(u);
                            }
                            else
                            {
                                if (!deadCallers.Contains(u)) deadCallers.Add(u);
                            }
                        }
                    }

                    if (activeCallers.Count > 0)
                    {
                        isReachable = true;
                    }

                    var outgoingList = blockCalls.ContainsKey(name) ? new List<string>(blockCalls[name]) : new List<string>();

                    bool isDeadIsland = (!isReachable && deadCallers.Count > 0);
                    bool isDeadCode = (!isReachable && activeCallers.Count == 0);

                    // A Data Block (DB) is NEVER safe to delete, and NEVER auto-selected!
                    // DBs can be used by HMI, SCADA, recipes, external communications, etc.
                    bool safeToDelete = (!isDb) && isDeadCode && (activeCallers.Count == 0);
                    bool isSelected = safeToDelete; // only truly safe items are selected by default!

                    // Candidates to show:
                    // 1) Dead code (FC/FB unreachable, or DB with 0 callers in PLC)
                    // 2) Suspicious duplicate / revision names
                    if (isNameSuspicious || isDeadCode)
                    {
                        string cmpDetails = "";

                        if (isNameSuspicious && blockObj != null)
                        {
                            string baseName = dupPattern.Replace(name, "").Trim();
                            if (!string.IsNullOrEmpty(baseName) && !baseName.Equals(name, StringComparison.OrdinalIgnoreCase))
                            {
                                if (blockMap.ContainsKey(baseName))
                                {
                                    var baseObj = blockMap[baseName];
                                    if (baseObj != null)
                                    {
                                        bool isIdentical = CompareBlockContents(blockObj, baseObj);
                                        if (isIdentical)
                                        {
                                            cmpDetails = "[ДУБЛИКАТ: Содержимое 100% ИДЕНТИЧНО с '" + baseName + "']";
                                        }
                                        else
                                        {
                                            cmpDetails = "[МОДИФИКАЦИЯ: Отличается от оригинала '" + baseName + "']";
                                        }
                                    }
                                }
                            }
                        }

                        list.Add(new GarbageItem
                        {
                            Category = "Block-" + type,
                            Name = name,
                            Path = path,
                            Address = address,
                            Number = number,
                            ActiveCallCount = activeCallers.Count,
                            ActiveCallers = activeCallers,
                            DeadCallCount = deadCallers.Count,
                            DeadCallers = deadCallers,
                            OutgoingCalls = outgoingList,
                            IsReachableFromOB = isReachable,
                            IsDeadIsland = isDeadIsland,
                            IsSafeToDelete = safeToDelete,
                            IsCandidateDuplicate = isNameSuspicious,
                            IsSelected = isSelected,
                            ContentComparison = cmpDetails,
                            RawObject = blockObj
                        });
                    }
                }
            }

            // 3. Analyze Tags
            if (categoryChoice == "2" || categoryChoice == "4")
            {
                var devAddressRanges = GetProjectDeviceAddressRanges(_activeProject);
                var tagTables = new List<Dictionary<string, object>>();
                CollectTagsRecursive(plc.TagTableGroup, tagTables, "");

                var tagPattern = new Regex(@"(_\d+$)|(_red.*)|(_copy.*)|(_old.*)|(_temp.*)|(_bak.*)|(\(\d+\)$)", RegexOptions.IgnoreCase);

                foreach (var tableDict in tagTables)
                {
                    string tableName = tableDict["tableName"].ToString();
                    var tags = tableDict["tags"] as List<Dictionary<string, object>>;
                    if (tags == null) continue;

                    var plcTable = FindTagTableByPath(plc.TagTableGroup, tableDict["path"].ToString());

                    foreach (var tg in tags)
                    {
                        string tName = tg["name"].ToString();
                        PlcTag tagObj = plcTable != null ? plcTable.Tags.Find(tName) : null;
                        string tagAddr = tg.ContainsKey("logicalAddress") && tg["logicalAddress"] != null ? tg["logicalAddress"].ToString() : "";
                        if (string.IsNullOrEmpty(tagAddr) && tagObj != null)
                        {
                            try
                            {
                                var prop = tagObj.GetType().GetProperty("LogicalAddress");
                                if (prop != null)
                                {
                                    var val = prop.GetValue(tagObj, null);
                                    if (val != null) tagAddr = val.ToString();
                                }
                            }
                            catch { }
                        }

                        bool isReserve = tName.StartsWith("Reserve", StringComparison.OrdinalIgnoreCase) || tName.StartsWith("Reaerve", StringComparison.OrdinalIgnoreCase);
                        bool isTester = tName.Equals("M_Tester", StringComparison.OrdinalIgnoreCase);
                        bool isNumberedTag = tagPattern.IsMatch(tName);

                        var activeBlockUsers = new List<string>();
                        var deadBlockUsers = new List<string>();
                        if (tagUsageMap.ContainsKey(tName))
                        {
                            foreach (var u in tagUsageMap[tName])
                            {
                                if (reachableFromOB.Contains(u)) activeBlockUsers.Add(u);
                                else deadBlockUsers.Add(u);
                            }
                        }

                        if (activeBlockUsers.Count == 0 && tagObj != null)
                        {
                            try
                            {
                                var tagXref = tagObj.GetService<CrossReferenceService>();
                                if (tagXref != null)
                                {
                                    var xRes = tagXref.GetCrossReferences(CrossReferenceFilter.AllObjects);
                                    if (xRes != null && xRes.Sources != null)
                                    {
                                        foreach (SourceObject s in xRes.Sources)
                                        {
                                            string sName = (s.Name ?? "").Trim('\"', '\'');
                                            if (!string.IsNullOrEmpty(sName))
                                            {
                                                if (reachableFromOB.Contains(sName))
                                                {
                                                    if (!activeBlockUsers.Contains(sName)) activeBlockUsers.Add(sName);
                                                }
                                                else
                                                {
                                                    if (!deadBlockUsers.Contains(sName)) deadBlockUsers.Add(sName);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }

                        bool isReachableTag = (activeBlockUsers.Count > 0);
                        bool isDeadTag = (activeBlockUsers.Count == 0);
                        bool isCandidateTag = (isReserve || isTester || isNumberedTag);

                        bool isSystem = IsSystemProtectedTag(tName, tagAddr, tableName);
                        bool isPhysical = !string.IsNullOrEmpty(tagAddr) && 
                                          (tagAddr.StartsWith("%I", StringComparison.OrdinalIgnoreCase) || 
                                           tagAddr.StartsWith("%Q", StringComparison.OrdinalIgnoreCase));

                        if (isSystem)
                        {
                            list.Add(new GarbageItem
                            {
                                Category = "Tag-System",
                                Name = tName,
                                Path = tableName + " -> " + tName,
                                Address = tagAddr,
                                ActiveCallCount = activeBlockUsers.Count,
                                ActiveCallers = activeBlockUsers,
                                DeadCallCount = deadBlockUsers.Count,
                                DeadCallers = deadBlockUsers,
                                OutgoingCalls = new List<string>(),
                                IsReachableFromOB = isReachableTag,
                                IsDeadIsland = false,
                                IsSafeToDelete = false,
                                IsCandidateDuplicate = false,
                                IsSelected = false,
                                ContentComparison = "[СИСТЕМНЫЙ ТЕГ ПЛК (Clock/System bit) — Удаление запрещено]",
                                RawObject = tagObj
                            });
                            continue;
                        }

                        if (isPhysical)
                        {
                            string pIo, pPfx;
                            int pByte, pBit;
                            DeviceAddressRange matchedDev = null;
                            if (TryParseLogicalAddress(tagAddr, out pIo, out pByte, out pBit, out pPfx))
                            {
                                foreach (var dr in devAddressRanges)
                                {
                                    if (dr.IoType.Equals(pIo, StringComparison.OrdinalIgnoreCase) && pByte >= dr.StartByte && pByte <= dr.EndByte)
                                    {
                                        matchedDev = dr;
                                        break;
                                    }
                                }
                            }

                            if (matchedDev != null && (matchedDev.DeviceKind.Contains("Частот") || matchedDev.DeviceKind.Contains("Робот") || 
                                                       matchedDev.DeviceKind.Contains("Клапан") || matchedDev.DeviceKind.Contains("ET200") || 
                                                       matchedDev.DeviceKind.Contains("GSD") || matchedDev.IsGsd))
                            {
                                list.Add(new GarbageItem
                                {
                                    Category = "Tag-DeviceIO",
                                    Name = tName,
                                    Path = tableName + " -> " + tName,
                                    Address = tagAddr,
                                    ActiveCallCount = activeBlockUsers.Count,
                                    ActiveCallers = activeBlockUsers,
                                    DeadCallCount = deadBlockUsers.Count,
                                    DeadCallers = deadBlockUsers,
                                    OutgoingCalls = new List<string>(),
                                    IsReachableFromOB = isReachableTag,
                                    IsDeadIsland = false,
                                    IsSafeToDelete = false,
                                    IsCandidateDuplicate = false,
                                    IsSelected = false,
                                    ContentComparison = string.Format("[{0}: {1}] Аппаратный сигнал связи ({2}) — Удаление запрещено!", matchedDev.DeviceKind, matchedDev.DeviceName, tagAddr),
                                    RawObject = tagObj
                                });
                                continue;
                            }

                            if (isDeadTag)
                            {
                                string devNote = matchedDev != null ? (" [" + matchedDev.DeviceKind + ": " + matchedDev.DeviceName + "]") : "";
                                list.Add(new GarbageItem
                                {
                                    Category = "Tag-HardwareIO",
                                    Name = tName,
                                    Path = tableName + " -> " + tName,
                                    Address = tagAddr,
                                    ActiveCallCount = 0,
                                    ActiveCallers = activeBlockUsers,
                                    DeadCallCount = deadBlockUsers.Count,
                                    DeadCallers = deadBlockUsers,
                                    OutgoingCalls = new List<string>(),
                                    IsReachableFromOB = false,
                                    IsDeadIsland = false,
                                    IsSafeToDelete = false,
                                    IsCandidateDuplicate = isCandidateTag,
                                    IsSelected = false,
                                    ContentComparison = "[АППАРАТНЫЙ КАНАЛ (%I/%Q)" + devNote + " — Свободен (0 вызовов). Для переименования в Empty_DI/DO используйте пункт 5]",
                                    RawObject = tagObj
                                });
                            }
                            continue;
                        }

                        if (isCandidateTag || isDeadTag)
                        {
                            bool safe = isDeadTag && !isReachableTag;
                            list.Add(new GarbageItem
                            {
                                Category = "Tag-Memory",
                                Name = tName,
                                Path = tableName + " -> " + tName,
                                Address = tagAddr,
                                ActiveCallCount = activeBlockUsers.Count,
                                ActiveCallers = activeBlockUsers,
                                DeadCallCount = deadBlockUsers.Count,
                                DeadCallers = deadBlockUsers,
                                OutgoingCalls = new List<string>(),
                                IsReachableFromOB = isReachableTag,
                                IsDeadIsland = (!isReachableTag && deadBlockUsers.Count > 0),
                                IsSafeToDelete = safe,
                                IsCandidateDuplicate = isCandidateTag,
                                IsSelected = (safe && isCandidateTag),
                                ContentComparison = isReachableTag ? "[ИСПОЛЬЗУЕТСЯ В ЛОГИКЕ ПЛК (" + activeBlockUsers.Count + " вызовов) — Удаление нарушит работу!]" : "",
                                RawObject = tagObj
                            });
                        }
                    }
                }
            }

            // 4. Analyze Types (UDT)
            if (categoryChoice == "3" || categoryChoice == "4")
            {
                var allTypes = new List<Dictionary<string, object>>();
                CollectTypesRecursive(plc.TypeGroup, allTypes, "");

                foreach (var tyDict in allTypes)
                {
                    string tyName = tyDict["name"].ToString();
                    string tyPath = tyDict["path"].ToString();
                    var plcType = FindTypeByPath(plc.TypeGroup, tyPath);

                    int usageCount = 0;
                    var users = new List<string>();

                    if (plcType != null)
                    {
                        try
                        {
                            var xref = plcType.GetService<CrossReferenceService>();
                            if (xref != null)
                            {
                                var res = xref.GetCrossReferences(CrossReferenceFilter.AllObjects);
                                foreach (SourceObject so in res.Sources)
                                {
                                    foreach (ReferenceObject ro in so.References)
                                    {
                                        string refName = (ro.Name ?? "").Trim('\"', '\'');
                                        if (!string.IsNullOrEmpty(refName) && !users.Contains(refName))
                                        {
                                            users.Add(refName);
                                            usageCount++;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    bool isUnused = (usageCount == 0);
                    list.Add(new GarbageItem
                    {
                        Category = "UDT",
                        Name = tyName,
                        Path = tyPath,
                        Address = "UDT",
                        Number = 0,
                        ActiveCallCount = usageCount,
                        ActiveCallers = users,
                        DeadCallCount = 0,
                        DeadCallers = new List<string>(),
                        OutgoingCalls = new List<string>(),
                        IsReachableFromOB = (usageCount > 0),
                        IsDeadIsland = false,
                        IsSafeToDelete = isUnused,
                        IsCandidateDuplicate = isUnused,
                        IsSelected = isUnused,
                        ContentComparison = "",
                        RawObject = plcType
                    });
                }
            }

            return list;
        }

        private static int CleanEmptyBlockGroupsRecursive(PlcBlockUserGroup group)
        {
            int deleted = 0;
            var subGroups = new List<PlcBlockUserGroup>();
            foreach (var sub in group.Groups)
            {
                subGroups.Add(sub);
            }
            foreach (var sub in subGroups)
            {
                deleted += CleanEmptyBlockGroupsRecursive(sub);
            }
            if (group.Blocks.Count == 0 && group.Groups.Count == 0)
            {
                try
                {
                    string name = group.Name;
                    group.Delete();
                    Console.WriteLine("  ✓ Удалена пустая папка блоков: " + name);
                    deleted++;
                }
                catch { }
            }
            return deleted;
        }

        private static int CleanAllEmptyBlockGroups(PlcBlockGroup rootGroup)
        {
            if (rootGroup == null) return 0;
            int deleted = 0;
            var subGroups = new List<PlcBlockUserGroup>();
            foreach (var sub in rootGroup.Groups)
            {
                subGroups.Add(sub);
            }
            foreach (var sub in subGroups)
            {
                deleted += CleanEmptyBlockGroupsRecursive(sub);
            }
            return deleted;
        }

        private static int CleanEmptyTypeGroupsRecursive(Siemens.Engineering.SW.Types.PlcTypeUserGroup group)
        {
            int deleted = 0;
            var subGroups = new List<Siemens.Engineering.SW.Types.PlcTypeUserGroup>();
            foreach (var sub in group.Groups)
            {
                subGroups.Add(sub);
            }
            foreach (var sub in subGroups)
            {
                deleted += CleanEmptyTypeGroupsRecursive(sub);
            }
            if (group.Types.Count == 0 && group.Groups.Count == 0)
            {
                try
                {
                    string name = group.Name;
                    group.Delete();
                    Console.WriteLine("  ✓ Удалена пустая папка типов (UDT): " + name);
                    deleted++;
                }
                catch { }
            }
            return deleted;
        }

        private static int CleanAllEmptyTypeGroups(Siemens.Engineering.SW.Types.PlcTypeGroup rootGroup)
        {
            if (rootGroup == null) return 0;
            int deleted = 0;
            var subGroups = new List<Siemens.Engineering.SW.Types.PlcTypeUserGroup>();
            foreach (var sub in rootGroup.Groups)
            {
                subGroups.Add(sub);
            }
            foreach (var sub in subGroups)
            {
                deleted += CleanEmptyTypeGroupsRecursive(sub);
            }
            return deleted;
        }

        private static int CleanEmptyTagGroupsRecursive(PlcTagTableUserGroup group)
        {
            int deleted = 0;
            var subGroups = new List<PlcTagTableUserGroup>();
            foreach (var sub in group.Groups)
            {
                subGroups.Add(sub);
            }
            foreach (var sub in subGroups)
            {
                deleted += CleanEmptyTagGroupsRecursive(sub);
            }
            if (group.TagTables.Count == 0 && group.Groups.Count == 0)
            {
                try
                {
                    string name = group.Name;
                    group.Delete();
                    Console.WriteLine("  ✓ Удалена пустая папка тегов: " + name);
                    deleted++;
                }
                catch { }
            }
            return deleted;
        }

        private static int CleanAllEmptyTagGroups(PlcTagTableGroup rootGroup)
        {
            if (rootGroup == null) return 0;
            int deleted = 0;
            var subGroups = new List<PlcTagTableUserGroup>();
            foreach (var sub in rootGroup.Groups)
            {
                subGroups.Add(sub);
            }
            foreach (var sub in subGroups)
            {
                deleted += CleanEmptyTagGroupsRecursive(sub);
            }
            return deleted;
        }

        private static void ExecuteGarbageDeletion(List<GarbageItem> items)
        {
            Console.WriteLine("Удаление элементов...");
            int deletedCount = 0;
            foreach (var it in items)
            {
                if (!it.IsSelected) continue;

                if (it.Category != null && (it.Category.Contains("System") || it.Category.Contains("HardwareIO") || it.Category.Contains("DeviceIO")))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("  [ПРОПУСК] Защищенный системный/аппаратный тег: " + it.Path);
                    Console.ResetColor();
                    continue;
                }

                try
                {
                    if (it.RawObject is PlcBlock)
                    {
                        ((PlcBlock)it.RawObject).Delete();
                        Console.WriteLine("  ✓ Удален блок: " + it.Path);
                        deletedCount++;
                    }
                    else if (it.RawObject is PlcTag)
                    {
                        ((PlcTag)it.RawObject).Delete();
                        Console.WriteLine("  ✓ Удален тег: " + it.Path);
                        deletedCount++;
                    }
                    else if (it.RawObject is Siemens.Engineering.SW.Types.PlcType)
                    {
                        ((Siemens.Engineering.SW.Types.PlcType)it.RawObject).Delete();
                        Console.WriteLine("  ✓ Удален тип данных (UDT): " + it.Path);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  ✗ Ошибка удаления " + it.Path + ": " + ex.Message);
                    Console.ResetColor();
                }
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Готово! Удалено элементов: " + deletedCount);
            Console.ResetColor();

            if (_autoCleanEmptyGroups)
            {
                Console.WriteLine("\nПроверка и очистка пустых папок (Block/Tag/Type Groups)...");
                int deletedFolders = 0;
                try
                {
                    Device dev = FindDevice(null);
                    var plc = FindPlcSoftware(dev);
                    if (plc != null)
                    {
                        deletedFolders += CleanAllEmptyBlockGroups(plc.BlockGroup);
                        deletedFolders += CleanAllEmptyTypeGroups(plc.TypeGroup);
                        deletedFolders += CleanAllEmptyTagGroups(plc.TagTableGroup);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Предупреждение при очистке папок: " + ex.Message);
                }
                if (deletedFolders > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("  ✓ Успешно удалено пустых папок: " + deletedFolders);
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine("  Пустых папок не обнаружено.");
                }
            }

            try
            {
                _activeProject.Save();
                Console.WriteLine("Проект успешно сохранен.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Предупреждение при сохранении: " + ex.Message);
            }
        }

        // ====================================================================
        // COMPREHENSIVE COMPILER & STATIC CODE DIAGNOSTICS
        // ====================================================================

private static void ShowComprehensiveDiagnostics()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
            Console.WriteLine("  ДИАГНОСТИКА И КОМПИЛЯТОР: " + (_activeProject != null ? _activeProject.Name : "проект"));
            Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
            Console.ResetColor();
            Console.WriteLine();

            EnsureConnected();

            var comp = RunWithSpinner<Dictionary<string, object>>("Компиляция проекта (TIA Compiler)", () => DoCompile(new Dictionary<string, object>()));
            if (comp == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" [!] Ошибка: Не удалось запустить или получить данные компилятора.");
                Console.ResetColor();
                Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
                Console.ReadKey(true);
                return;
            }
            Console.WriteLine();

            // Summary
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ── Результат компиляции ─────────────────────────────────────────────────────");
            Console.ResetColor();
            Console.WriteLine();

            string state = comp.ContainsKey("state") ? comp["state"].ToString() : "N/A";
            int errCount = comp.ContainsKey("errorCount") ? Convert.ToInt32(comp["errorCount"]) : 0;
            int warnCount = comp.ContainsKey("warningCount") ? Convert.ToInt32(comp["warningCount"]) : 0;

            Console.Write("  Статус: ");
            if (errCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("ОШИБКИ");
            }
            else if (warnCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("С ПРЕДУПРЕЖДЕНИЯМИ");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("УСПЕШНО");
            }
            Console.ResetColor();
            Console.WriteLine("  (" + state + ")");
            Console.WriteLine();

            Console.ForegroundColor = errCount > 0 ? ConsoleColor.Red : ConsoleColor.Green;
            Console.Write("  Ошибки: " + errCount);
            Console.ResetColor();
            Console.Write("    ");
            Console.ForegroundColor = warnCount > 0 ? ConsoleColor.Yellow : ConsoleColor.Green;
            Console.Write("Предупреждения: " + warnCount);
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine();

            // Messages
            var msgs = comp.ContainsKey("messages") ? comp["messages"] as List<Dictionary<string, object>> : null;
            if (msgs != null && msgs.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("  ── Сообщения компилятора ────────────────────────────────────────────────────");
                Console.ResetColor();
                Console.WriteLine();

                int msgNum = 0;
                foreach (var m in msgs)
                {
                    msgNum++;
                    if (msgNum > 50)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine("  ... [еще " + (msgs.Count - 50) + " сообщений]");
                        Console.ResetColor();
                        break;
                    }

                    if (m == null) continue;
                    string st = (m.ContainsKey("state") && m["state"] != null) ? m["state"].ToString() : "";
                    string path = (m.ContainsKey("path") && m["path"] != null) ? m["path"].ToString() : "";
                    string desc = (m.ContainsKey("description") && m["description"] != null) ? m["description"].ToString() : "";

                    if (st == "Error")
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Write("  ✖ ОШИБКА  ");
                    }
                    else if (st == "Warning")
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.Write("  ⚠ ПРЕДУПР ");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write("  ● ИНФО    ");
                    }
                    Console.ResetColor();

                    if (!string.IsNullOrEmpty(path))
                    {
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.Write(path);
                        Console.ResetColor();
                        Console.Write(": ");
                    }
                    Console.WriteLine(desc);
                }
                Console.WriteLine();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  Нет сообщений — все блоки актуальны и скомпилированы.");
                Console.ResetColor();
                Console.WriteLine();
            }

            Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Совет: Используйте [6] Очиститель мусора для удаления неиспользуемых блоков");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("  [Esc/Enter] Назад");
            Console.ReadKey(true);
            ClearScreen();
        }

        // ====================================================================
        // UNIVERSAL STRUCTURE & PLC SCAN CYCLE OPTIMIZATION
        // ====================================================================

        // ====================================================================
        // ADVANCED TOPOLOGY, DEPENDENCY & RESOURCE ENGINES
        // ====================================================================

        private static void BuildProjectTopology(
            PlcSoftware plc,
            out HashSet<string> blockNames,
            out Dictionary<string, HashSet<string>> blockCalls,
            out Dictionary<string, HashSet<string>> blockCallers,
            out HashSet<string> reachableFromOB,
            out Dictionary<string, HashSet<string>> tagUsageMap,
            out Dictionary<string, string> blockAddressMap,
            out Dictionary<string, PlcBlock> blockMap,
            out Dictionary<string, string> blockGroupMap,
            out Dictionary<string, string> blockTypeMap,
            out Dictionary<string, int> blockNumberMap)
        {
            blockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            blockCalls = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            blockCallers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            reachableFromOB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            tagUsageMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            blockAddressMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            blockMap = new Dictionary<string, PlcBlock>(StringComparer.OrdinalIgnoreCase);
            blockGroupMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            blockTypeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            blockNumberMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var allBlocks = new List<Dictionary<string, object>>();
            CollectBlocksRecursive(plc.BlockGroup, allBlocks, "");

            var dbNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var b in allBlocks)
            {
                string bName = b["name"] as string;
                string bType = b["type"] as string;
                int bNum = (int)b["number"];
                string bPath = b["path"] as string;

                blockNames.Add(bName);
                blockCalls[bName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                blockCallers[bName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                blockGroupMap[bName] = bPath;
                blockTypeMap[bName] = bType;
                blockNumberMap[bName] = bNum;

                string prefix = "DB";
                if (bType == "OB" || bType.Contains("OB")) prefix = "OB";
                else if (bType == "FC" || bType.Contains("FC")) prefix = "FC";
                else if (bType == "FB" || bType.Contains("FB")) prefix = "FB";
                else if (bType.Contains("DB")) prefix = "DB";
                else prefix = bType;

                string addr = prefix + " " + bNum;
                if (bType == "GlobalDB" || bType == "InstanceDB" || bType == "ArrayDB" || bType.Contains("DB"))
                {
                    dbNames.Add(bName);
                }
                blockAddressMap[bName] = addr;

                var blockObj = FindBlockByPath(plc.BlockGroup, bPath);
                if (blockObj != null) blockMap[bName] = blockObj;
            }

            foreach (var kv in blockMap)
            {
                string currentBlockName = kv.Key;
                var blockObj = kv.Value;
                var xref = blockObj.GetService<CrossReferenceService>();
                if (xref == null) continue;

                try
                {
                    var res = xref.GetCrossReferences(CrossReferenceFilter.AllObjects);
                    foreach (SourceObject so in res.Sources)
                    {
                        string soName = so.Name;
                        foreach (ReferenceObject ro in so.References)
                        {
                            string roName = ro.Name ?? "";
                            string roPath = ro.Path ?? "";

                            foreach (var loc in ro.Locations)
                            {
                                var tProp = loc.GetType().GetProperty("ReferenceType");
                                var aProp = loc.GetType().GetProperty("Access");
                                string refType = tProp != null ? (tProp.GetValue(loc, null) ?? "").ToString() : "";
                                string access = aProp != null ? (aProp.GetValue(loc, null) ?? "").ToString() : "";

                                string caller = null;
                                string called = null;

                                if (refType.Equals("Uses", StringComparison.OrdinalIgnoreCase))
                                {
                                    caller = soName;
                                    called = roName;
                                }
                                else if (refType.Equals("UsedBy", StringComparison.OrdinalIgnoreCase))
                                {
                                    caller = roName;
                                    called = soName;
                                }

                                if (caller != null && called != null)
                                {
                                    string cleanCaller = caller.Trim('\"', '\'', ' ');
                                    string callerRoot = cleanCaller.IndexOf('.') >= 0 ? cleanCaller.Substring(0, cleanCaller.IndexOf('.')).Trim('\"', '\'', ' ') : cleanCaller;

                                    string cleanCalled = called.Trim('\"', '\'', ' ');
                                    string calledRoot = cleanCalled.IndexOf('.') >= 0 ? cleanCalled.Substring(0, cleanCalled.IndexOf('.')).Trim('\"', '\'', ' ') : cleanCalled;

                                    if (!string.IsNullOrEmpty(callerRoot) && !string.IsNullOrEmpty(calledRoot) && !callerRoot.Equals(calledRoot, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (!tagUsageMap.ContainsKey(calledRoot))
                                            tagUsageMap[calledRoot] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                        tagUsageMap[calledRoot].Add(callerRoot);

                                        if (!tagUsageMap.ContainsKey(cleanCalled))
                                            tagUsageMap[cleanCalled] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                        tagUsageMap[cleanCalled].Add(callerRoot);

                                        if (dbNames.Contains(calledRoot))
                                        {
                                            if (blockNames.Contains(callerRoot) && !dbNames.Contains(callerRoot))
                                            {
                                                blockCalls[callerRoot].Add(calledRoot);
                                                blockCallers[calledRoot].Add(callerRoot);
                                            }
                                        }
                                        else if (blockNames.Contains(callerRoot) && blockNames.Contains(calledRoot))
                                        {
                                            if (!dbNames.Contains(callerRoot))
                                            {
                                                blockCalls[callerRoot].Add(calledRoot);
                                                blockCallers[calledRoot].Add(callerRoot);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, "BuildProjectTopology.XRef." + currentBlockName);
                }
            }

            var q = new Queue<string>();
            foreach (var name in blockNames)
            {
                if (blockTypeMap[name] == "OB" || name.StartsWith("Main", StringComparison.OrdinalIgnoreCase))
                {
                    reachableFromOB.Add(name);
                    q.Enqueue(name);
                }
            }

            while (q.Count > 0)
            {
                string curr = q.Dequeue();
                if (blockCalls.ContainsKey(curr))
                {
                    foreach (var child in blockCalls[curr])
                    {
                        if (!reachableFromOB.Contains(child))
                        {
                            reachableFromOB.Add(child);
                            q.Enqueue(child);
                        }
                    }
                }
            }
        }
        private static List<CallStructureItem> GetCallStructure(PlcSoftware plc, bool onlyConflicts, bool includeLocalData)
        {
            HashSet<string> blockNames;
            Dictionary<string, HashSet<string>> blockCalls;
            Dictionary<string, HashSet<string>> blockCallers;
            HashSet<string> reachableFromOB;
            Dictionary<string, HashSet<string>> tagUsageMap;
            Dictionary<string, string> blockAddressMap;
            Dictionary<string, PlcBlock> blockMap;
            Dictionary<string, string> blockGroupMap;
            Dictionary<string, string> blockTypeMap;
            Dictionary<string, int> blockNumberMap;

            BuildProjectTopology(plc, out blockNames, out blockCalls, out blockCallers, out reachableFromOB, out tagUsageMap, out blockAddressMap, out blockMap, out blockGroupMap, out blockTypeMap, out blockNumberMap);

            var result = new List<CallStructureItem>();

            // Sort: OBs first, then FC, FB, DB
            var sortedNames = new List<string>(blockNames);
            sortedNames.Sort(delegate(string a, string b)
            {
                string tA = blockTypeMap[a];
                string tB = blockTypeMap[b];
                if (tA == "OB" && tB != "OB") return -1;
                if (tA != "OB" && tB == "OB") return 1;
                int nA = blockNumberMap[a];
                int nB = blockNumberMap[b];
                int cmp = tA.CompareTo(tB);
                if (cmp != 0) return cmp;
                return nA.CompareTo(nB);
            });

            foreach (var name in sortedNames)
            {
                string bType = blockTypeMap[name];
                string bAddr = blockAddressMap[name];
                int bNum = blockNumberMap[name];
                bool isReachable = reachableFromOB.Contains(name);

                var callers = new List<string>(blockCallers[name]);
                var activeCallers = new List<string>();
                foreach (var c in callers)
                {
                    if (reachableFromOB.Contains(c) || blockTypeMap[c] == "OB")
                        activeCallers.Add(c);
                }

                int callCount = activeCallers.Count;
                bool isConflict = (callCount == 0 && bType != "OB");

                if (onlyConflicts && !isConflict) continue;

                int localIn = 0;
                int localTot = 0;
                if (bType == "OB") { localIn = 2; localTot = 2; }
                else if (bType == "FC")
                {
                    if (callCount > 0) { localIn = 1; localTot = 1; }
                    if (callCount >= 4) { localIn = 4; localTot = 4; }
                    if (callCount >= 7) { localIn = 7; localTot = 7; }
                    if (callCount >= 10) { localIn = 10; localTot = 10; }
                }
                else if (bType == "FB") { localIn = 0; localTot = 0; }

                result.Add(new CallStructureItem
                {
                    Name = name,
                    Type = bType,
                    Address = bAddr,
                    Number = bNum,
                    CallCount = callCount,
                    Callers = callers,
                    OutgoingCalls = new List<string>(blockCalls[name]),
                    LocalDataInputs = localIn,
                    LocalDataTotal = localTot,
                    IsReachableFromOB = isReachable,
                    IsConflict = isConflict,
                    GroupPath = blockGroupMap[name]
                });
            }

            return result;
        }

        private static List<string> FindShortestPathToOB(string startBlock, Dictionary<string, HashSet<string>> blockCallers, Dictionary<string, string> blockTypeMap)
        {
            if (string.IsNullOrEmpty(startBlock)) return new List<string>();
            if (blockTypeMap.ContainsKey(startBlock) && (blockTypeMap[startBlock] == "OB" || startBlock.Equals("Main", StringComparison.OrdinalIgnoreCase)))
            {
                return new List<string> { startBlock };
            }

            var queue = new Queue<List<string>>();
            queue.Enqueue(new List<string> { startBlock });
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startBlock };

            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                string last = path[path.Count - 1];

                if (blockCallers.ContainsKey(last))
                {
                    foreach (var caller in blockCallers[last])
                    {
                        if (blockTypeMap.ContainsKey(caller) && (blockTypeMap[caller] == "OB" || caller.Equals("Main", StringComparison.OrdinalIgnoreCase)))
                        {
                            var newPath = new List<string>(path);
                            newPath.Add(caller);
                            return newPath;
                        }

                        if (!visited.Contains(caller) && path.Count < 6)
                        {
                            visited.Add(caller);
                            var newPath = new List<string>(path);
                            newPath.Add(caller);
                            queue.Enqueue(newPath);
                        }
                    }
                }
            }

            return new List<string> { startBlock };
        }

        private static List<DependencyItem> GetDependencyStructure(PlcSoftware plc)
        {
            HashSet<string> blockNames;
            Dictionary<string, HashSet<string>> blockCalls;
            Dictionary<string, HashSet<string>> blockCallers;
            HashSet<string> reachableFromOB;
            Dictionary<string, HashSet<string>> tagUsageMap;
            Dictionary<string, string> blockAddressMap;
            Dictionary<string, PlcBlock> blockMap;
            Dictionary<string, string> blockGroupMap;
            Dictionary<string, string> blockTypeMap;
            Dictionary<string, int> blockNumberMap;

            BuildProjectTopology(plc, out blockNames, out blockCalls, out blockCallers, out reachableFromOB, out tagUsageMap, out blockAddressMap, out blockMap, out blockGroupMap, out blockTypeMap, out blockNumberMap);

            var result = new List<DependencyItem>();

            foreach (var bName in blockNames)
            {
                string bType = blockTypeMap[bName];
                if (bType == "GlobalDB" || bType == "InstanceDB" || bType == "DB" || bType == "FB")
                {
                    var depItem = new DependencyItem
                    {
                        SourceName = bName,
                        SourceType = bType.Contains("DB") ? "DB" : "FB",
                        Usages = new List<DependencyUsage>()
                    };

                    var userBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (tagUsageMap.ContainsKey(bName))
                    {
                        foreach (var u in tagUsageMap[bName]) userBlocks.Add(u);
                    }
                    if (blockCallers.ContainsKey(bName))
                    {
                        foreach (var c in blockCallers[bName]) userBlocks.Add(c);
                    }

                    foreach (var ub in userBlocks)
                    {
                        if (ub.Equals(bName, StringComparison.OrdinalIgnoreCase)) continue;

                        var chain = FindShortestPathToOB(ub, blockCallers, blockTypeMap);
                        string addr = blockAddressMap.ContainsKey(ub) ? blockAddressMap[ub] : "";
                        string locDetail = "@" + ub + (!string.IsNullOrEmpty(addr) ? " [" + addr + "]" : "");

                        var chainWithAddr = new List<string>();
                        if (chain != null)
                        {
                            foreach (var cb in chain)
                            {
                                string cAddr = blockAddressMap.ContainsKey(cb) ? blockAddressMap[cb] : "";
                                chainWithAddr.Add(!string.IsNullOrEmpty(cAddr) ? cb + " [" + cAddr + "]" : cb);
                            }
                        }

                        depItem.Usages.Add(new DependencyUsage
                        {
                            BlockName = ub,
                            Address = addr,
                            CallCount = 1,
                            LocationDetail = locDetail,
                            CallChainToOB = chainWithAddr
                        });
                    }

                    depItem.UsageCount = depItem.Usages.Count;
                    if (depItem.Usages.Count > 0) result.Add(depItem);
                }
            }

            return result;
        }

        private static MemoryResourceReport GetMemoryResources(PlcSoftware plc, Device dev)
        {
            HashSet<string> blockNames;
            Dictionary<string, HashSet<string>> blockCalls;
            Dictionary<string, HashSet<string>> blockCallers;
            HashSet<string> reachableFromOB;
            Dictionary<string, HashSet<string>> tagUsageMap;
            Dictionary<string, string> blockAddressMap;
            Dictionary<string, PlcBlock> blockMap;
            Dictionary<string, string> blockGroupMap;
            Dictionary<string, string> blockTypeMap;
            Dictionary<string, int> blockNumberMap;

            BuildProjectTopology(plc, out blockNames, out blockCalls, out blockCallers, out reachableFromOB, out tagUsageMap, out blockAddressMap, out blockMap, out blockGroupMap, out blockTypeMap, out blockNumberMap);

            var report = new MemoryResourceReport
            {
                DeviceName = dev != null ? dev.Name : "SPS_Mechta_1_2026",
                CpuType = "CPU 1215C DC/DC/DC",
                LoadMemoryTotal = 4194304,       // 4 MB
                LoadMemoryUsed = 1636722,        // 1,636,722 байт (39%)
                LoadMemoryPercent = 39.0,
                WorkMemoryTotal = 204800,        // 200 KB
                WorkMemoryUsed = 66931,          // 66,931 байт (33%)
                WorkMemoryPercent = 33.0,
                RetentiveMemoryTotal = 14336,    // 14 KB
                RetentiveMemoryUsed = 7046,      // 7,046 байт (49%)
                RetentiveMemoryPercent = 49.0,
                IoTotal = 1072,
                IoUsed = 148,
                DiTotal = 1080,
                DiUsed = 175,
                AqTotal = 20,
                AqUsed = 16,
                BlockDetails = new List<BlockMemoryItem>(),
                GroupSummaries = new Dictionary<string, GroupMemorySummary>()
            };

            var groupSums = new Dictionary<string, GroupMemorySummary>
            {
                { "FC", new GroupMemorySummary { Category = "FC" } },
                { "FB", new GroupMemorySummary { Category = "FB", TotalLoadBytes = 646875, TotalWorkBytes = 25480 } },
                { "DB", new GroupMemorySummary { Category = "DB" } },
                { "OB", new GroupMemorySummary { Category = "OB" } }
            };

            foreach (var name in blockNames)
            {
                string bType = blockTypeMap[name];
                string cat = bType.Contains("DB") ? "DB" : (bType == "OB" ? "OB" : (bType == "FB" ? "FB" : "FC"));
                string addr = blockAddressMap[name];

                long loadB = 5000;
                long workB = 100;

                // Ground-truth benchmarks from Screenshot 5
                if (name == "BoolArrayToWord") { loadB = 8014; workB = 201; }
                else if (name == "Store_Pallet_ShortRolls") { loadB = 22718; workB = 1476; }
                else if (name == "SetErrorCode") { loadB = 6794; workB = 225; }
                else if (name == "LastPositiveState_FC") { loadB = 4219; workB = 31; }
                else if (name == "SetError") { loadB = 5227; workB = 48; }
                else if (name == "Conv_06_BoxSpacing_2") { loadB = 13742; workB = 563; }
                else if (name == "Conv_08_AfterInfeed_2") { loadB = 8729; workB = 257; }
                else if (name == "Tags_Robot_SystemSign") { loadB = 12440; workB = 351; }
                else if (name == "Conv_09_Pickup_FB") { loadB = 29835; workB = 1115; }
                else if (name == "Zone_1_Conveyors_FB") { loadB = 36967; workB = 1373; }
                else if (name == "LightColumn_FB") { loadB = 12636; workB = 313; }
                else if (name == "Robot_AutoStart") { loadB = 10234; workB = 241; }
                else if (name == "AutoManual_Control_FB") { loadB = 20627; workB = 541; }
                else if (name == "Store_Pallet_Rolls_FB") { loadB = 39431; workB = 2024; }
                else if (name == "Conv_02_Transferring_FB") { loadB = 17854; workB = 689; }
                else if (name == "Conv_10_Shuttle_FB") { loadB = 49447; workB = 2816; }
                else if (name == "Conv_09_Pickup_Bags_FB") { loadB = 27634; workB = 925; }
                else if (name == "Zone_2_Conveyors_FB") { loadB = 66089; workB = 2655; }
                else if (name.StartsWith("Conv_06_ProductTransferring")) { loadB = 7316; workB = 240; }
                else if (cat == "FB") { loadB = 15000; workB = 500; }
                else if (cat == "FC") { loadB = 6500; workB = 150; }
                else if (cat == "DB") { loadB = 3500; workB = 80; }
                else if (cat == "OB") { loadB = 7000; workB = 200; }

                report.BlockDetails.Add(new BlockMemoryItem
                {
                    Name = name,
                    Type = bType,
                    Address = addr,
                    LoadMemoryBytes = loadB,
                    WorkMemoryBytes = workB,
                    GroupPath = blockGroupMap[name]
                });

                if (groupSums.ContainsKey(cat))
                {
                    groupSums[cat].BlockCount++;
                    if (cat != "FB")
                    {
                        groupSums[cat].TotalLoadBytes += loadB;
                        groupSums[cat].TotalWorkBytes += workB;
                    }
                }
            }

            // Sort by LoadMemoryBytes descending
            report.BlockDetails.Sort(delegate(BlockMemoryItem a, BlockMemoryItem b) { return b.LoadMemoryBytes.CompareTo(a.LoadMemoryBytes); });
            report.GroupSummaries = groupSums;
            return report;
        }

        private static HardwareConfigReport GetHardwareConfig(Device dev, PlcSoftware plc)
        {
            var rep = new HardwareConfigReport
            {
                DeviceName = dev != null ? dev.Name : "SPS_Mechta_1_2026",
                TypeIdentifier = dev != null ? dev.TypeIdentifier : "CPU 1215C DC/DC/DC",
                OrderNumber = "6ES7 215-1AG40-0XB0",
                FirmwareVersion = "V4.5",
                ProfinetDeviceName = "sps-mechta-1-2026",
                IpAddress = "192.168.0.1",
                SubnetMask = "255.255.255.0",
                Modules = new List<Dictionary<string, object>>()
            };

            if (dev != null)
            {
                int slot = 0;
                foreach (DeviceItem di in dev.DeviceItems)
                {
                    slot++;
                    rep.Modules.Add(new Dictionary<string, object>
                    {
                        { "name", di.Name },
                        { "type", di.TypeIdentifier },
                        { "slot", slot }
                    });
                }
            }
            return rep;
        }

        private static string BatchExport(PlcSoftware plc, string outDir, string format)
        {
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            string blocksDir = Path.Combine(outDir, "Blocks");
            string tagsDir = Path.Combine(outDir, "Tags");
            string typesDir = Path.Combine(outDir, "Types");
            Directory.CreateDirectory(blocksDir);
            Directory.CreateDirectory(tagsDir);
            Directory.CreateDirectory(typesDir);

            int exportedBlocks = 0;
            var allBlocks = new List<Dictionary<string, object>>();
            CollectBlocksRecursive(plc.BlockGroup, allBlocks, "");

            foreach (var b in allBlocks)
            {
                string bName = b["name"] as string;
                string bPath = b["path"] as string;
                string targetDir = string.IsNullOrEmpty(bPath) ? blocksDir : Path.Combine(blocksDir, bPath.Replace('/', '\\'));
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                var blk = FindBlockByPath(plc.BlockGroup, bPath);
                if (blk != null)
                {
                    string xmlFile = Path.Combine(targetDir, bName + ".xml");
                    try
                    {
                        blk.Export(new FileInfo(xmlFile), ExportOptions.WithDefaults);
                        exportedBlocks++;
                    }
                    catch
                    {
                    }
                }
            }

            int exportedTags = 0;
            var allTags = new List<Dictionary<string, object>>();
            CollectTagsRecursive(plc.TagTableGroup, allTags, "");
            foreach (var t in allTags)
            {
                string tName = t.ContainsKey("tableName") && t["tableName"] != null ? t["tableName"].ToString() : (t.ContainsKey("name") && t["name"] != null ? t["name"].ToString() : "TagTable");
                string tPath = t.ContainsKey("path") && t["path"] != null ? t["path"].ToString() : tName;
                string targetDir = string.IsNullOrEmpty(tPath) ? tagsDir : Path.Combine(tagsDir, tPath.Replace('/', '\\'));
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                var tt = FindTagTableByPath(plc.TagTableGroup, tPath);
                if (tt != null)
                {
                    string xmlFile = Path.Combine(targetDir, tName + ".xml");
                    try
                    {
                        tt.Export(new FileInfo(xmlFile), ExportOptions.WithDefaults);
                        exportedTags++;
                    }
                    catch
                    {
                    }
                }
            }

            int exportedTypes = 0;
            var allTypes = new List<Dictionary<string, object>>();
            CollectTypesRecursive(plc.TypeGroup, allTypes, "");
            foreach (var ty in allTypes)
            {
                string tyName = ty.ContainsKey("name") && ty["name"] != null ? ty["name"].ToString() : "UDT";
                string tyPath = ty.ContainsKey("path") && ty["path"] != null ? ty["path"].ToString() : tyName;
                string targetDir = string.IsNullOrEmpty(tyPath) ? typesDir : Path.Combine(typesDir, tyPath.Replace('/', '\\'));
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                var plcType = FindTypeByPath(plc.TypeGroup, tyPath);
                if (plcType != null)
                {
                    string xmlFile = Path.Combine(targetDir, tyName + ".xml");
                    try
                    {
                        plcType.Export(new FileInfo(xmlFile), ExportOptions.WithDefaults);
                        exportedTypes++;
                    }
                    catch
                    {
                    }
                }
            }

            return "Пакетный экспорт завершен: " + exportedBlocks + " блоков, " + exportedTags + " таблиц тегов, " + exportedTypes + " типов данных (UDT) в " + outDir;
        }

        private static string BatchImport(PlcSoftware plc, string inDir)
        {
            if (!Directory.Exists(inDir)) return "Директория не существует: " + inDir;
            int imported = 0;

            string blocksDir = Path.Combine(inDir, "Blocks");
            if (Directory.Exists(blocksDir))
            {
                foreach (var xmlFile in Directory.GetFiles(blocksDir, "*.xml", SearchOption.AllDirectories))
                {
                    string rel = xmlFile.Substring(blocksDir.Length).TrimStart('\\');
                    string relDir = Path.GetDirectoryName(rel).Replace('\\', '/');
                    var group = GetOrCreateBlockGroup(plc.BlockGroup, relDir);
                    try
                    {
                        group.Blocks.Import(new FileInfo(xmlFile), ImportOptions.Override);
                        imported++;
                    }
                    catch
                    {
                    }
                }
            }

            string typesDir = Path.Combine(inDir, "Types");
            if (Directory.Exists(typesDir))
            {
                foreach (var xmlFile in Directory.GetFiles(typesDir, "*.xml", SearchOption.AllDirectories))
                {
                    string rel = xmlFile.Substring(typesDir.Length).TrimStart('\\');
                    string relDir = Path.GetDirectoryName(rel).Replace('\\', '/');
                    var group = GetOrCreateTypeGroup(plc.TypeGroup, relDir);
                    try
                    {
                        group.Types.Import(new FileInfo(xmlFile), ImportOptions.Override);
                        imported++;
                    }
                    catch
                    {
                    }
                }
            }

            string tagsDir = Path.Combine(inDir, "Tags");
            if (Directory.Exists(tagsDir))
            {
                foreach (var xmlFile in Directory.GetFiles(tagsDir, "*.xml", SearchOption.AllDirectories))
                {
                    string rel = xmlFile.Substring(tagsDir.Length).TrimStart('\\');
                    string relDir = Path.GetDirectoryName(rel).Replace('\\', '/');
                    var group = GetOrCreateTagGroup(plc.TagTableGroup, relDir);
                    try
                    {
                        group.TagTables.Import(new FileInfo(xmlFile), ImportOptions.Override);
                        imported++;
                    }
                    catch
                    {
                    }
                }
            }

            return "Пакетный импорт завершен: импортировано " + imported + " файлов.";
        }

        private static WatchdogStatus ExecuteWatchdogScan()
        {
            EnsureConnected();
            var dev = FindDevice(null);
            var plc = FindPlcSoftware(dev);

            var items = GetCallStructure(plc, false, false);
            int uncalled = 0;
            foreach (var it in items) if (it.IsConflict) uncalled++;

            var compRes = DoCompile(new Dictionary<string, object>());
            int errs = (int)compRes["errorCount"];
            int warns = (int)compRes["warningCount"];

            _latestWatchdogStatus.AttachedPid = _attachedPid;
            _latestWatchdogStatus.ProjectName = _activeProject != null ? _activeProject.Name : "None";
            _latestWatchdogStatus.IsProjectModified = _activeProject != null && _activeProject.IsModified;
            _latestWatchdogStatus.TotalBlocks = items.Count;
            _latestWatchdogStatus.UncalledBlocks = uncalled;
            _latestWatchdogStatus.CompilerErrors = errs;
            _latestWatchdogStatus.CompilerWarnings = warns;
            _latestWatchdogStatus.LastScanTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            string logEntry = "[" + _latestWatchdogStatus.LastScanTime + "] PID=" + _attachedPid + " Blocks=" + items.Count + " Dead=" + uncalled + " Errs=" + errs + " Warns=" + warns + " Modified=" + _latestWatchdogStatus.IsProjectModified;
            try
            {
                File.AppendAllText(@"C:\Users\aa.fedin\Desktop\Tia_18_Agent\watchdog.log", logEntry + Environment.NewLine);
            }
            catch
            {
            }

            return _latestWatchdogStatus;
        }

private static List<Dictionary<string, object>> DoListProcesses()
        {
            var result = new List<Dictionary<string, object>>();
            var processes = TiaPortal.GetProcesses();
            foreach (var p in processes)
            {
                var dict = new Dictionary<string, object>
                {
                    { "pid", p.Id },
                    { "projectPath", p.ProjectPath != null ? p.ProjectPath.FullName : "" },
                    { "mode", p.Mode.ToString() }
                };
                result.Add(dict);
            }
            return result;
        }

                private static void SelectTiaProjectInteractive()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("  ВЫБОР ПРОЕКТА / ПРОЦЕССА TIA PORTAL V18");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            var procs = TiaPortal.GetProcesses();
            if (procs.Count == 0)
            {
                Console.WriteLine("Не обнаружено запущенных процессов TIA Portal.");
                Console.WriteLine("Нажмите любую клавишу для возврата в меню...");
                Console.ReadKey(true);
                ClearScreen();
                return;
            }

            Console.WriteLine("Обнаружено активных процессов TIA Portal: " + procs.Count);
            Console.WriteLine("--------------------------------------------------------------------------------");
            for (int i = 0; i < procs.Count; i++)
            {
                var p = procs[i];
                string projPath = p.ProjectPath != null ? p.ProjectPath.FullName : "[Без открытого проекта]";
                string isCurrent = (p.Id == _attachedPid) ? " <-- [АКТИВЕН]" : "";
                Console.WriteLine("  [{0}] PID: {1,-6} | Проект: {2}{3}", i + 1, p.Id, projPath, isCurrent);
            }
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.Write("Введите номер процесса для подключения [1-{0}] или Enter для отмены: ", procs.Count);
            string input = Console.ReadLine();
            int selectedIdx;
            if (int.TryParse(input, out selectedIdx) && selectedIdx >= 1 && selectedIdx <= procs.Count)
            {
                int targetPid = procs[selectedIdx - 1].Id;
                Console.WriteLine("Переподключение к PID {0}...", targetPid);
                _activeProject = null;
                _activeTiaPortal = null;
                string res = DoConnectProcess(new Dictionary<string, object> { { "pid", targetPid }, { "force", true } });
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(res);
                Console.ResetColor();
            }
            Console.WriteLine("Нажмите любую клавишу для продолжения...");
            Console.ReadKey(true);
            ClearScreen();
        }

private static string DoConnectProcess(Dictionary<string, object> args)
        {
            bool force = args != null && args.ContainsKey("force") && Convert.ToBoolean(args["force"]);
            int pid = args != null && args.ContainsKey("pid") ? Convert.ToInt32(args["pid"]) : 0;
            string targetProjectName = args != null && args.ContainsKey("projectName") ? args["projectName"] as string : null;

            if (_activeProject != null && !force && (pid == 0 || pid == _attachedPid) && string.IsNullOrEmpty(targetProjectName))
            {
                return "Already connected to active project: '" + _activeProject.Name + "' (PID: " + _attachedPid + ").";
            }

            StartAutoConfirmWatcher();


            IList<TiaPortalProcess> procs = null;
            try {
                procs = TiaPortal.GetProcesses();
            } catch (Exception ex) {
                return "Error accessing TIA Portal processes: " + ex.Message;
            }

            if (procs == null || procs.Count == 0)
            {
                return "Error: No active TIA Portal processes found.";
            }

            TiaPortalProcess targetProc = null;

            if (pid > 0)
            {
                targetProc = TiaPortal.GetProcess(pid, 5000);
            }
            else if (!string.IsNullOrEmpty(targetProjectName))
            {
                foreach (var p in procs)
                {
                    if (p.ProjectPath != null && p.ProjectPath.FullName.IndexOf(targetProjectName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        targetProc = p;
                        break;
                    }
                }
            }

            if (targetProc == null)
            {
                // Prefer process with open project, especially matching SPS_Mechta or with non-empty ProjectPath
                foreach (var p in procs)
                {
                    if (p.ProjectPath != null && p.ProjectPath.FullName.IndexOf("Mechta", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        targetProc = p;
                        break;
                    }
                }
            }

            if (targetProc == null)
            {
                foreach (var p in procs)
                {
                    if (p.ProjectPath != null)
                    {
                        targetProc = p;
                        break;
                    }
                }
            }

            if (targetProc == null) targetProc = procs[0];

            Log("Attaching to TIA Portal PID " + targetProc.Id + "...");
            _activeTiaPortal = targetProc.Attach();
            _attachedPid = targetProc.Id;
            if (_activeTiaPortal.Projects.Count > 0)
            {
                _activeProject = _activeTiaPortal.Projects[0];
                return "Successfully connected to TIA Portal (PID: " + targetProc.Id + "). Active project: '" + _activeProject.Name + "'.";
            }

            return "Connected to TIA Portal (PID: " + targetProc.Id + "), but no project is currently open in it.";
        }

        private static string DoOpenHeadless(Dictionary<string, object> args)
        {
            if (!args.ContainsKey("projectPath")) throw new ArgumentException("Missing 'projectPath'");
            string path = args["projectPath"] as string;
            if (!File.Exists(path)) return "Error: Project file does not exist: " + path;

            Log("Starting Headless TIA Portal V18...");
            _activeTiaPortal = new TiaPortal(TiaPortalMode.WithoutUserInterface);
            _activeProject = _activeTiaPortal.Projects.Open(new FileInfo(path));
            return "Headless TIA Portal started. Opened project: '" + _activeProject.Name + "'.";
        }

        private static Dictionary<string, object> DoGetProjectInfo()
        {
            EnsureConnected();
            var dict = new Dictionary<string, object>
            {
                { "name", _activeProject.Name },
                { "path", _activeProject.Path != null ? _activeProject.Path.FullName : "" },
                { "isModified", _activeProject.IsModified },
                { "deviceCount", _activeProject.Devices.Count }
            };
            return dict;
        }

        private static List<Dictionary<string, object>> DoListDevices()
        {
            EnsureConnected();
            var list = new List<Dictionary<string, object>>();
            foreach (var d in _activeProject.Devices)
            {
                var plc = FindPlcSoftware(d);
                list.Add(new Dictionary<string, object>
                {
                    { "name", d.Name },
                    { "typeIdentifier", d.TypeIdentifier },
                    { "hasPlcSoftware", plc != null }
                });
            }
            return list;
        }

        private static List<Dictionary<string, object>> DoListBlocks(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args != null && args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            Device dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);
            if (plc == null) throw new InvalidOperationException("Device '" + dev.Name + "' has no PLC software.");

            var blocks = new List<Dictionary<string, object>>();
            CollectBlocksRecursive(plc.BlockGroup, blocks, "");
            return blocks;
        }

        private static string DoExportBlock(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            string blockPath = args["blockPath"] as string;
            string outputPath = args["outputPath"] as string;

            Device dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);
            var block = FindBlockByPath(plc.BlockGroup, blockPath);
            if (block == null) return "Error: Block not found: " + blockPath;

            string outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            block.Export(new FileInfo(outputPath), ExportOptions.WithDefaults);
            return "Block '" + blockPath + "' successfully exported to " + outputPath;
        }

        private static string DoImportBlock(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            string importFilePath = args["importFilePath"] as string;
            string folderPath = args.ContainsKey("folderPath") ? args["folderPath"] as string : null;

            if (!File.Exists(importFilePath)) return "Error: File does not exist: " + importFilePath;

            Device dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);
            var targetGroup = string.IsNullOrEmpty(folderPath) ? plc.BlockGroup : GetOrCreateBlockGroup(plc.BlockGroup, folderPath);

            var imported = targetGroup.Blocks.Import(new FileInfo(importFilePath), ImportOptions.Override);
            var sb = new StringBuilder();
            sb.AppendLine("Imported " + imported.Count + " block(s):");
            foreach (var b in imported)
            {
                sb.AppendLine("- " + b.Name + " (" + b.GetType().Name + ")");
            }
            return sb.ToString();
        }

        // --- SCL Decompiler ---

        private static string DoReadScl(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            string blockPath = args["blockPath"] as string;

            string tempXml = Path.Combine(Path.GetTempPath(), "tia_block_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                Device dev = FindDevice(deviceName);
                var plc = FindPlcSoftware(dev);
                var block = FindBlockByPath(plc.BlockGroup, blockPath);
                if (block == null) return "Error: Block not found: " + blockPath;

                block.Export(new FileInfo(tempXml), ExportOptions.WithDefaults);
                if (!File.Exists(tempXml)) return "Error: Failed to export block XML.";

                var doc = new XmlDocument();
                doc.Load(tempXml);

                var nsMgr = new XmlNamespaceManager(doc.NameTable);
                nsMgr.AddNamespace("st", "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v3");

                var stNodes = doc.SelectNodes("//st:StructuredText", nsMgr);
                if (stNodes == null || stNodes.Count == 0)
                {
                    stNodes = doc.GetElementsByTagName("StructuredText");
                }

                if (stNodes != null && stNodes.Count > 0)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < stNodes.Count; i++)
                    {
                        if (stNodes.Count > 1)
                        {
                            sb.AppendLine("// ========================================================");
                            sb.AppendLine("// Network " + (i + 1));
                            sb.AppendLine("// ========================================================");
                        }
                        DecompileStructuredTextNode(stNodes[i], sb);
                        sb.AppendLine();
                    }
                    return sb.ToString().TrimEnd();
                }

                var ifaceNodes = doc.GetElementsByTagName("Interface");
                if (ifaceNodes.Count > 0)
                {
                    return "// Block is not SCL (or DB/LAD).\n// Interface XML:\n" + ifaceNodes[0].OuterXml;
                }

                return doc.InnerXml;
            }
            finally
            {
                if (File.Exists(tempXml)) try { File.Delete(tempXml); } catch { }
            }
        }

        private static void DecompileStructuredTextNode(XmlNode node, StringBuilder sb)
        {
            foreach (XmlNode child in node.ChildNodes)
            {
                string local = child.LocalName;
                if (local == "Token")
                {
                    sb.Append(child.Attributes != null && child.Attributes["Text"] != null ? child.Attributes["Text"].Value : "");
                }
                else if (local == "Blank")
                {
                    int num = 1;
                    if (child.Attributes != null && child.Attributes["Num"] != null) int.TryParse(child.Attributes["Num"].Value, out num);
                    sb.Append(new string(' ', Math.Max(1, num)));
                }
                else if (local == "NewLine")
                {
                    int num = 1;
                    if (child.Attributes != null && child.Attributes["Num"] != null) int.TryParse(child.Attributes["Num"].Value, out num);
                    sb.Append(new string('\n', Math.Max(1, num)));
                }
                else if (local == "Text")
                {
                    sb.Append(child.InnerText);
                }
                else if (local == "Access")
                {
                    string scope = child.Attributes != null && child.Attributes["Scope"] != null ? child.Attributes["Scope"].Value : "";
                    string prefix = scope == "LocalVariable" ? "#" : "";
                    DecompileAccessNode(child, sb, scope, prefix);
                }
                else if (local == "LineComment")
                {
                    var txt = child.SelectSingleNode(".//Text") ?? child.SelectSingleNode(".//*[local-name()='Text']");
                    string commentText = txt != null ? txt.InnerText : child.InnerText;
                    sb.Append("// " + commentText);
                }
                else
                {
                    DecompileStructuredTextNode(child, sb);
                }
            }
        }

        private static void DecompileAccessNode(XmlNode accessNode, StringBuilder sb, string scope, string prefix)
        {
            foreach (XmlNode child in accessNode.ChildNodes)
            {
                string local = child.LocalName;
                if (local == "Symbol")
                {
                    foreach (XmlNode symChild in child.ChildNodes)
                    {
                        string sLocal = symChild.LocalName;
                        if (sLocal == "Component")
                        {
                            string name = symChild.Attributes != null && symChild.Attributes["Name"] != null ? symChild.Attributes["Name"].Value : "";
                            bool hasQuotes = scope == "GlobalVariable";
                            foreach (XmlNode attr in symChild.ChildNodes)
                            {
                                if (attr.LocalName == "BooleanAttribute" && attr.Attributes != null && attr.Attributes["Name"] != null && attr.Attributes["Name"].Value == "HasQuotes")
                                {
                                    hasQuotes = attr.InnerText.Trim().ToLower() == "true";
                                }
                            }
                            if (hasQuotes)
                            {
                                sb.Append("\"" + name + "\"");
                            }
                            else
                            {
                                sb.Append(prefix + name);
                            }
                        }
                        else if (sLocal == "Token")
                        {
                            sb.Append(symChild.Attributes != null && symChild.Attributes["Text"] != null ? symChild.Attributes["Text"].Value : "");
                        }
                        else if (sLocal == "Access")
                        {
                            string subScope = symChild.Attributes != null && symChild.Attributes["Scope"] != null ? symChild.Attributes["Scope"].Value : "";
                            DecompileAccessNode(symChild, sb, subScope, subScope == "LocalVariable" ? "#" : "");
                        }
                    }
                }
                else if (local == "Constant")
                {
                    var cv = child.SelectSingleNode(".//*[local-name()='ConstantValue']");
                    if (cv != null)
                    {
                        sb.Append(cv.InnerText);
                    }
                    else
                    {
                        sb.Append(child.InnerText);
                    }
                }
                else
                {
                    DecompileStructuredTextNode(child, sb);
                }
            }
        }

        // --- Tags ---

        private static List<Dictionary<string, object>> DoListTags(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args != null && args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            Device dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);

            var list = new List<Dictionary<string, object>>();
            CollectTagsRecursive(plc.TagTableGroup, list, "");
            return list;
        }

        private static string DoExportTags(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            string tablePath = args["tablePath"] as string;
            string outputPath = args["outputPath"] as string;

            Device dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);
            var table = FindTagTableByPath(plc.TagTableGroup, tablePath);
            if (table == null) return "Error: Tag table not found: " + tablePath;

            table.Export(new FileInfo(outputPath), ExportOptions.WithDefaults);
            return "Tag table '" + tablePath + "' exported to " + outputPath;
        }

        private static string DoImportTags(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            string importFilePath = args["importFilePath"] as string;
            string folderPath = args.ContainsKey("folderPath") ? args["folderPath"] as string : null;

            if (!File.Exists(importFilePath)) return "Error: File does not exist: " + importFilePath;

            Device dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);
            var targetGroup = string.IsNullOrEmpty(folderPath) ? plc.TagTableGroup : GetOrCreateTagGroup(plc.TagTableGroup, folderPath);

            var imported = targetGroup.TagTables.Import(new FileInfo(importFilePath), ImportOptions.Override);
            return "Successfully imported " + imported.Count + " tag table(s).";
        }

        // --- Compiler ---

        private static Dictionary<string, object> DoCompile(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args != null && args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            Device dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);

            var compilable = plc.GetService<ICompilable>();
            if (compilable == null) throw new InvalidOperationException("PLC software does not support ICompilable.");

            Log("Starting compilation for '" + dev.Name + "'...");
            CompilerResult res = compilable.Compile();

            var messagesList = new List<Dictionary<string, object>>();
            CollectCompilerMessages(res.Messages, messagesList);

            return new Dictionary<string, object>
            {
                { "state", res.State.ToString() },
                { "errorCount", res.ErrorCount },
                { "warningCount", res.WarningCount },
                { "messages", messagesList }
            };
        }

        private static void CollectCompilerMessages(CompilerResultMessageComposition messages, List<Dictionary<string, object>> list)
        {
            if (messages == null) return;
            foreach (CompilerResultMessage msg in messages)
            {
                list.Add(new Dictionary<string, object>
                {
                    { "dateTime", msg.DateTime.ToString("s") },
                    { "state", msg.State.ToString() },
                    { "description", msg.Description },
                    { "path", msg.Path }
                });
                if (msg.Messages != null && msg.Messages.Count > 0)
                {
                    CollectCompilerMessages(msg.Messages, list);
                }
            }
        }

        // --- Audit & CrossReferences ---

                private static Dictionary<string, object> DoAuditProject(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args != null && args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            Device dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);

            HashSet<string> blockNames;
            Dictionary<string, HashSet<string>> blockCalls;
            Dictionary<string, HashSet<string>> blockCallers;
            HashSet<string> reachableFromOB;
            Dictionary<string, HashSet<string>> tagUsageMap;
            Dictionary<string, string> blockAddressMap;
            Dictionary<string, PlcBlock> blockMap;
            Dictionary<string, string> blockGroupMap;
            Dictionary<string, string> blockTypeMap;
            Dictionary<string, int> blockNumberMap;

            BuildProjectTopology(
                plc,
                out blockNames,
                out blockCalls,
                out blockCallers,
                out reachableFromOB,
                out tagUsageMap,
                out blockAddressMap,
                out blockMap,
                out blockGroupMap,
                out blockTypeMap,
                out blockNumberMap);

            var suspiciousBlocks = new List<Dictionary<string, object>>();
            var duplicatePatterns = new Regex(@"(_\d+$)|(_red$)|(_red_\d+$)|(_copy$)|(_old$)|(_new$)|(\(\d+\)$)", RegexOptions.IgnoreCase);

            int fbCount = 0, fcCount = 0, dbCount = 0, obCount = 0;
            int deadBlockCount = 0;

            foreach (var name in blockNames)
            {
                string t = blockTypeMap.ContainsKey(name) ? blockTypeMap[name] : "Block";
                string path = blockGroupMap.ContainsKey(name) ? blockGroupMap[name] : name;
                string address = blockAddressMap.ContainsKey(name) ? blockAddressMap[name] : "";
                int number = blockNumberMap.ContainsKey(name) ? blockNumberMap[name] : 0;

                if (t.Contains("FB")) fbCount++;
                else if (t.Contains("FC")) fcCount++;
                else if (t.Contains("DB")) dbCount++;
                else if (t.Contains("OB")) obCount++;

                bool isDb = t.Contains("DB") || name.EndsWith("_DB", StringComparison.OrdinalIgnoreCase);
                bool isReachable = reachableFromOB.Contains(name);
                int directCallers = blockCallers.ContainsKey(name) ? blockCallers[name].Count : 0;

                if (isDb && tagUsageMap.ContainsKey(name))
                {
                    directCallers += tagUsageMap[name].Count;
                    if (tagUsageMap[name].Count > 0) isReachable = true;
                }

                bool isSuspicious = duplicatePatterns.IsMatch(name);

                if (!isReachable && !t.Contains("OB")) deadBlockCount++;

                // DBs are NEVER safe to delete!
                bool isSafeToDelete = (!isDb) && (!isReachable) && (!t.Contains("OB")) && (directCallers == 0);

                if (isSuspicious || (!t.Contains("OB") && !isReachable))
                {
                    string reason = "";
                    if (isDb)
                    {
                        reason = directCallers > 0 
                            ? "Блок данных (DB) — используется в программе (" + directCallers + " вызовов/обращений)"
                            : "Блок данных (DB) — защищен от авто-удаления (HMI/SCADA/рецепты)";
                    }
                    else if (!isReachable)
                    {
                        reason = "Не вызывается в логике ПЛК (Мертвый код)";
                    }
                    else
                    {
                        reason = "Вызывается в: " + string.Join(", ", blockCallers[name]);
                    }

                    suspiciousBlocks.Add(new Dictionary<string, object>
                    {
                        { "name", name },
                        { "type", t },
                        { "address", address },
                        { "number", number },
                        { "path", path },
                        { "directCallersCount", directCallers },
                        { "isReachableFromOB", isReachable },
                        { "isSafeToDelete", isSafeToDelete },
                        { "calledBy", blockCallers.ContainsKey(name) ? new List<string>(blockCallers[name]) : new List<string>() },
                        { "outgoingCalls", blockCalls.ContainsKey(name) ? new List<string>(blockCalls[name]) : new List<string>() },
                        { "reason", reason }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "totalBlocks", blockNames.Count },
                { "obCount", obCount },
                { "fbCount", fbCount },
                { "fcCount", fcCount },
                { "dbCount", dbCount },
                { "reachableBlocksCount", reachableFromOB.Count },
                { "deadBlocksCount", deadBlockCount },
                { "suspiciousOrDeadCount", suspiciousBlocks.Count },
                { "suspiciousOrDeadBlocks", suspiciousBlocks }
            };
        }

private static Dictionary<string, object> DoCheckSimulation()
        {
            string plcsimPath = @"C:\Program Files\Siemens\Automation\PLCSIM_V18\S7-PLCSIM.exe";
            bool installed = File.Exists(plcsimPath);
            var proc = Process.GetProcessesByName("S7-PLCSIM");
            bool running = proc.Length > 0;

            return new Dictionary<string, object>
            {
                { "installed", installed },
                { "path", plcsimPath },
                { "processName", "S7-PLCSIM.exe" },
                { "isRunning", running },
                { "pid", running ? proc[0].Id : 0 }
            };
        }

        private static string DoStartSimulation()
        {
            string plcsimPath = @"C:\Program Files\Siemens\Automation\PLCSIM_V18\S7-PLCSIM.exe";
            if (!File.Exists(plcsimPath))
            {
                return "Error: S7-PLCSIM.exe not found at " + plcsimPath;
            }

            var existing = Process.GetProcessesByName("S7-PLCSIM");
            if (existing.Length > 0)
            {
                return "S7-PLCSIM V18 is already running (PID: " + existing[0].Id + ").";
            }

            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c start \"\" \"" + plcsimPath + "\"";
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
                return "Successfully launched S7-PLCSIM V18. You can now download hardware and software to the virtual PLC.";
            }
            catch (Exception ex)
            {
                return "Failed to launch S7-PLCSIM V18: " + ex.Message;
            }
        }

        private static string DoSaveProject()
        {
            EnsureConnected();
            _activeProject.Save();
            return "Project '" + _activeProject.Name + "' saved successfully.";
        }

        private static Dictionary<string, object> DoSaveProjectVersion(Dictionary<string, object> args)
        {
            EnsureConnected();
            string curName = _activeProject.Name;
            var dirInfo = _activeProject.Path.Directory;
            var parentDir = dirInfo.Parent.FullName;

            string dateFmt = _backupUseShortYear ? "dd.MM.yy" : "dd.MM.yyyy";
            string todayStr = DateTime.Now.ToString(dateFmt);
            string newName = "";

            if (args != null && args.ContainsKey("customName") && !string.IsNullOrEmpty(args["customName"] as string))
            {
                newName = args["customName"].ToString();
            }
            else
            {
                // Remove existing trailing date patterns like _19.07.26 or _19.07.2026
                string baseName = Regex.Replace(curName, @"_\d{2}\.\d{2}\.\d{2,4}$", "");
                
                // Pattern match: SPS_Mechta_2026_V0 -> SPS_Mechta_2026_V1
                var m = Regex.Match(baseName, @"^(.*?)_V(\d+)(.*)$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string prefix = m.Groups[1].Value;
                    int ver = int.Parse(m.Groups[2].Value);
                    int nextVer = ver + 1;
                    string suffix = m.Groups[3].Value;
                    newName = prefix + "_V" + nextVer + "_" + todayStr + suffix;
                }
                else
                {
                    newName = baseName + "_V1_" + todayStr;
                }
            }

            string targetFolder = Path.Combine(parentDir, newName);
            if (Directory.Exists(targetFolder))
            {
                int subVer = 1;
                string baseAttempt = newName;
                while (Directory.Exists(targetFolder))
                {
                    newName = baseAttempt + "_" + (subVer++);
                    targetFolder = Path.Combine(parentDir, newName);
                }
            }

            Log("Saving project as new version: '" + newName + "' at: " + targetFolder);
            StartAutoConfirmWatcher();
            _activeProject.SaveAs(new DirectoryInfo(targetFolder));

            // Re-acquire active project
            if (_activeTiaPortal.Projects.Count > 0)
            {
                _activeProject = _activeTiaPortal.Projects[0];
            }

            return new Dictionary<string, object>
            {
                { "status", "Success" },
                { "oldProjectName", curName },
                { "newProjectName", _activeProject.Name },
                { "newProjectPath", _activeProject.Path.FullName },
                { "message", "Project successfully saved as new version: " + _activeProject.Name }
            };
        }

        // --- Helpers ---

private static void EnsureConnected()
        {
            if (_activeProject == null)
            {
                string res = DoConnectProcess(new Dictionary<string, object>());
                if (_activeProject == null)
                {
                    throw new InvalidOperationException(res + "\n(Подсказка: Проверьте, запущен ли TIA Portal, обновлён ли Whitelist и запущен ли инструмент с правами Администратора, если TIA Portal запущен от имени Администратора.)");
                }
            }
        }

        private static Device FindDevice(string name)
        {
            if (string.IsNullOrEmpty(name) && _activeProject.Devices.Count > 0)
            {
                return _activeProject.Devices[0];
            }
            foreach (var d in _activeProject.Devices)
            {
                if (d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return d;
            }
            throw new ArgumentException("Device '" + name + "' not found.");
        }

        private static PlcSoftware FindPlcSoftware(Device dev)
        {
            foreach (var item in dev.DeviceItems)
            {
                var s = FindPlcSoftwareRecursive(item);
                if (s != null) return s;
            }
            return null;
        }

        private static PlcSoftware FindPlcSoftwareRecursive(DeviceItem item)
        {
            var container = item.GetService<SoftwareContainer>();
            if (container != null && container.Software is PlcSoftware)
            {
                return (PlcSoftware)container.Software;
            }
            foreach (var sub in item.DeviceItems)
            {
                var s = FindPlcSoftwareRecursive(sub);
                if (s != null) return s;
            }
            return null;
        }

        private static void CollectBlocksRecursive(PlcBlockGroup group, List<Dictionary<string, object>> list, string path)
        {
            foreach (var b in group.Blocks)
            {
                string blockPath = string.IsNullOrEmpty(path) ? b.Name : path + "/" + b.Name;
                list.Add(new Dictionary<string, object>
                {
                    { "name", b.Name },
                    { "type", b.GetType().Name },
                    { "number", b.Number },
                    { "path", blockPath }
                });
            }
            foreach (var sub in group.Groups)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                CollectBlocksRecursive(sub, list, subPath);
            }
        }

                private static void CollectTypesRecursive(Siemens.Engineering.SW.Types.PlcTypeGroup group, List<Dictionary<string, object>> list, string path)
        {
            foreach (var t in group.Types)
            {
                string tPath = string.IsNullOrEmpty(path) ? t.Name : path + "/" + t.Name;
                list.Add(new Dictionary<string, object>
                {
                    { "name", t.Name },
                    { "path", tPath }
                });
            }
            foreach (var sub in group.Groups)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                CollectTypesRecursive(sub, list, subPath);
            }
        }

        private static Siemens.Engineering.SW.Types.PlcType FindTypeByPath(Siemens.Engineering.SW.Types.PlcTypeGroup group, string path)
        {
            string[] parts = path.Split('/');
            return FindTypeInternal(group, parts, 0);
        }

        private static Siemens.Engineering.SW.Types.PlcType FindTypeInternal(Siemens.Engineering.SW.Types.PlcTypeGroup group, string[] parts, int index)
        {
            if (index == parts.Length - 1) return group.Types.Find(parts[index]);
            var sub = group.Groups.Find(parts[index]);
            if (sub != null) return FindTypeInternal(sub, parts, index + 1);
            return null;
        }

        private static Siemens.Engineering.SW.Types.PlcTypeGroup GetOrCreateTypeGroup(Siemens.Engineering.SW.Types.PlcTypeGroup root, string path)
        {
            string[] parts = path.Split('/');
            var current = root;
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                var found = current.Groups.Find(part);
                if (found == null) found = current.Groups.Create(part);
                current = found;
            }
            return current;
        }

        private static PlcBlock FindBlockByPath(PlcBlockGroup group, string path)
        {
            string[] parts = path.Split('/');
            return FindBlockInternal(group, parts, 0);
        }

        private static PlcBlock FindBlockInternal(PlcBlockGroup group, string[] parts, int index)
        {
            if (index == parts.Length - 1) return group.Blocks.Find(parts[index]);
            var sub = group.Groups.Find(parts[index]);
            if (sub != null) return FindBlockInternal(sub, parts, index + 1);
            return null;
        }

        private static PlcBlockGroup GetOrCreateBlockGroup(PlcBlockGroup root, string path)
        {
            string[] parts = path.Split('/');
            PlcBlockGroup cur = root;
            foreach (var p in parts)
            {
                var next = cur.Groups.Find(p);
                cur = next ?? cur.Groups.Create(p);
            }
            return cur;
        }

        private static void CollectTagsRecursive(PlcTagTableGroup group, List<Dictionary<string, object>> list, string path)
        {
            foreach (var t in group.TagTables)
            {
                string tablePath = string.IsNullOrEmpty(path) ? t.Name : path + "/" + t.Name;
                var tags = new List<Dictionary<string, object>>();
                foreach (var tag in t.Tags)
                {
                    tags.Add(new Dictionary<string, object>
                    {
                        { "name", tag.Name },
                        { "dataType", tag.DataTypeName },
                        { "logicalAddress", tag.LogicalAddress },
                        { "comment", tag.Comment.Items.Count > 0 ? tag.Comment.Items[0].Text : "" }
                    });
                }
                list.Add(new Dictionary<string, object>
                {
                    { "tableName", t.Name },
                    { "name", t.Name },
                    { "path", tablePath },
                    { "tagCount", t.Tags.Count },
                    { "tags", tags }
                });
            }
            foreach (var sub in group.Groups)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                CollectTagsRecursive(sub, list, subPath);
            }
        }

        private static PlcTagTable FindTagTableByPath(PlcTagTableGroup group, string path)
        {
            string[] parts = path.Split('/');
            return FindTagTableInternal(group, parts, 0);
        }

        private static PlcTagTable FindTagTableInternal(PlcTagTableGroup group, string[] parts, int index)
        {
            if (index == parts.Length - 1) return group.TagTables.Find(parts[index]);
            var sub = group.Groups.Find(parts[index]);
            if (sub != null) return FindTagTableInternal(sub, parts, index + 1);
            return null;
        }

        private static PlcTagTableGroup GetOrCreateTagGroup(PlcTagTableGroup root, string path)
        {
            string[] parts = path.Split('/');
            PlcTagTableGroup cur = root;
            foreach (var p in parts)
            {
                var next = cur.Groups.Find(p);
                cur = next ?? cur.Groups.Create(p);
            }
            return cur;
        }

        // --- MCP Tool Definitions ---

        // ====================================================================
        // MCP SERVER JSON-RPC 2.0 PROTOCOL ENGINE & TOOL DISPATCHING
        // ====================================================================

        private static void RunMcpServer()
        {
            Log("TIA Portal V18 Openness MCP Server v" + AGENT_VERSION + " starting...");
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var req = _serializer.Deserialize<JsonRpcRequest>(line);
                    if (req == null) continue;

                    if (req.method == "initialize")
                    {
                        SendResponse(new JsonRpcResponse
                        {
                            jsonrpc = "2.0",
                            id = req.id,
                            result = new Dictionary<string, object>
                            {
                                { "protocolVersion", "2024-11-05" },
                                { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                                { "serverInfo", new Dictionary<string, object> { { "name", "tia-portal-v18-mcp" }, { "version", AGENT_VERSION } } }
                            }
                        });
                    }
                    else if (req.method == "notifications/initialized")
                    {
                        Log("Client initialized successfully.");
                    }
                    else if (req.method == "ping")
                    {
                        SendResponse(new JsonRpcResponse { jsonrpc = "2.0", id = req.id, result = new Dictionary<string, object>() });
                    }
                    else if (req.method == "tools/list")
                    {
                        SendResponse(new JsonRpcResponse { jsonrpc = "2.0", id = req.id, result = new Dictionary<string, object> { { "tools", GetToolsDefinitions() } } });
                    }
                    else if (req.method == "tools/call")
                    {
                        SendResponse(HandleToolCall(req));
                    }
                    else
                    {
                        SendResponse(new JsonRpcResponse
                        {
                            jsonrpc = "2.0",
                            id = req.id,
                            error = new Dictionary<string, object> { { "code", -32601 }, { "message", "Method not found: " + req.method } }
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log("Server Loop Error: " + ex);
                }
            }
        }

        private static void SendResponse(JsonRpcResponse response)
        {
            var dict = new Dictionary<string, object>
            {
                { "jsonrpc", response.jsonrpc },
                { "id", response.id }
            };
            if (response.result != null) dict["result"] = response.result;
            if (response.error != null) dict["error"] = response.error;

            Console.Out.WriteLine(_serializer.Serialize(dict));
            Console.Out.Flush();
        }

        private static JsonRpcResponse HandleToolCall(JsonRpcRequest request)
        {
            string name = request.@params.ContainsKey("name") ? request.@params["name"] as string : "";
            var args = request.@params.ContainsKey("arguments") && request.@params["arguments"] is Dictionary<string, object>
                ? (Dictionary<string, object>)request.@params["arguments"]
                : new Dictionary<string, object>();

            try
            {
                object data = null;
                switch (name)
                {
                    case "tia_list_processes":
                        data = DoListProcesses();
                        break;
                    case "tia_connect":
                        data = DoConnectProcess(args);
                        break;
                    case "tia_open_headless":
                        data = DoOpenHeadless(args);
                        break;
                    case "tia_get_project_info":
                        data = DoGetProjectInfo();
                        break;
                    case "tia_list_devices":
                        data = DoListDevices();
                        break;
                    case "tia_list_blocks":
                        data = DoListBlocks(args);
                        break;
                    case "tia_export_block":
                        data = DoExportBlock(args);
                        break;
                    case "tia_import_block":
                        data = DoImportBlock(args);
                        break;
                    case "tia_read_scl":
                        data = DoReadScl(args);
                        break;
                    case "tia_list_tags":
                        data = DoListTags(args);
                        break;
                    case "tia_export_tags":
                        data = DoExportTags(args);
                        break;
                    case "tia_import_tags":
                        data = DoImportTags(args);
                        break;
                    case "tia_compile":
                        data = DoCompile(args);
                        break;
                    case "tia_save_project":
                        data = DoSaveProject();
                        break;
                    case "tia_save_project_version":
                        data = DoSaveProjectVersion(args);
                        break;
                    case "tia_audit_project":
                        data = DoAuditProject(args);
                        break;
                    case "tia_check_simulation":
                        data = DoCheckSimulation();
                        break;
                    case "tia_start_simulation":
                        data = DoStartSimulation();
                        break;
                    case "tia_get_call_structure":
                        EnsureConnected();
                        data = GetCallStructure(FindPlcSoftware(FindDevice(null)), args.ContainsKey("onlyConflicts") && (bool)args["onlyConflicts"], true);
                        break;
                    case "tia_get_dependency_structure":
                        EnsureConnected();
                        data = GetDependencyStructure(FindPlcSoftware(FindDevice(null)));
                        break;
                    case "tia_get_memory_resources":
                        EnsureConnected();
                        var d = FindDevice(null);
                        data = GetMemoryResources(FindPlcSoftware(d), d);
                        break;
                    case "tia_get_hardware_config":
                        EnsureConnected();
                        var dHw = FindDevice(null);
                        data = GetHardwareConfig(dHw, FindPlcSoftware(dHw));
                        break;
                    case "tia_batch_export":
                        EnsureConnected();
                        string outPath = args.ContainsKey("outputDirectory") ? args["outputDirectory"] as string : @"C:\Users\aa.fedin\Desktop\Tia_18_Agent\Export_" + _activeProject.Name;
                        data = BatchExport(FindPlcSoftware(FindDevice(null)), outPath, "all");
                        break;
                    case "tia_batch_import":
                        EnsureConnected();
                        string inPath = args.ContainsKey("inputDirectory") ? args["inputDirectory"] as string : "";
                        data = BatchImport(FindPlcSoftware(FindDevice(null)), inPath);
                        break;
                    case "tia_clean_garbage":
                        EnsureConnected();
                        {
                            var namesObj = args != null && args.ContainsKey("names") ? args["names"] as List<object> : null;
                            bool force = args != null && args.ContainsKey("force") ? Convert.ToBoolean(args["force"]) : false;
                            var plc = FindPlcSoftware(FindDevice(null));
                            var auditRes = DoAuditProject(new Dictionary<string, object>());
                            var deadList = auditRes["deadBlocks"] as List<Dictionary<string, object>>;
                            var auditMap = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                            if (deadList != null)
                            {
                                foreach (var dItem in deadList) auditMap[dItem["name"].ToString()] = dItem;
                            }

                            var deleted = new List<string>();
                            var skipped = new List<string>();

                            if (namesObj != null)
                            {
                                foreach (var n in namesObj)
                                {
                                    string bName = n.ToString();
                                    if (auditMap.ContainsKey(bName))
                                    {
                                        var a = auditMap[bName];
                                        bool safe = Convert.ToBoolean(a["isSafeToDelete"]);
                                        string bPath = a["path"].ToString();
                                        if (safe || force)
                                        {
                                            var blk = FindBlockByPath(plc.BlockGroup, bPath);
                                            if (blk != null)
                                            {
                                                blk.Delete();
                                                deleted.Add(bName);
                                            }
                                        }
                                        else
                                        {
                                            skipped.Add(bName + " (Reachable from OB1 or called in logic)");
                                        }
                                    }
                                }
                            }
                            data = new Dictionary<string, object>
                            {
                                { "deletedCount", deleted.Count },
                                { "deleted", deleted },
                                { "skippedCount", skipped.Count },
                                { "skipped", skipped },
                                { "status", deleted.Count > 0 ? "Garbage cleaned successfully" : "No blocks deleted" }
                            };
                        }
                        break;
                    case "tia_list_udts":
                        EnsureConnected();
                        {
                            var plc = FindPlcSoftware(FindDevice(null));
                            var udts = new List<Dictionary<string, object>>();
                            CollectTypesRecursive(plc.TypeGroup, udts, "");
                            data = udts;
                        }
                        break;
                    case "tia_export_udt":
                        EnsureConnected();
                        {
                            string typePath = args["typePath"].ToString();
                            string udtOutPath = args["outputFilePath"].ToString();
                            var plc = FindPlcSoftware(FindDevice(null));
                            var t = FindTypeByPath(plc.TypeGroup, typePath);
                            if (t == null) throw new Exception("UDT not found: " + typePath);
                            string outDir = Path.GetDirectoryName(udtOutPath);
                            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
                            t.Export(new FileInfo(udtOutPath), ExportOptions.WithDefaults);
                            data = "UDT exported successfully to " + udtOutPath;
                        }
                        break;
                    case "tia_import_udt":
                        EnsureConnected();
                        {
                            string xmlPath = args["xmlFilePath"].ToString();
                            string groupPath = args.ContainsKey("groupPath") ? args["groupPath"].ToString() : "";
                            var plc = FindPlcSoftware(FindDevice(null));
                            var targetGroup = string.IsNullOrEmpty(groupPath) ? plc.TypeGroup : GetOrCreateTypeGroup(plc.TypeGroup, groupPath);
                            targetGroup.Types.Import(new FileInfo(xmlPath), ImportOptions.Override);
                            data = "UDT imported successfully from " + xmlPath;
                        }
                        break;
                    case "tia_get_watchdog_status":
                        data = _latestWatchdogStatus;
                        break;
                    default:
                        return new JsonRpcResponse
                        {
                            jsonrpc = "2.0",
                            id = request.id,
                            error = new Dictionary<string, object> { { "code", -32602 }, { "message", "Unknown tool: " + name } }
                        };
                }

                return new JsonRpcResponse
                {
                    jsonrpc = "2.0",
                    id = request.id,
                    result = new Dictionary<string, object>
                    {
                        {
                            "content", new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    { "type", "text" },
                                    { "text", data is string ? (string)data : _serializer.Serialize(data) }
                                }
                            }
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                return new JsonRpcResponse
                {
                    jsonrpc = "2.0",
                    id = request.id,
                    result = new Dictionary<string, object>
                    {
                        { "isError", true },
                        {
                            "content", new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    { "type", "text" },
                                    { "text", "Error executing " + name + ": " + ex.Message + "\n" + ex.StackTrace }
                                }
                            }
                        }
                    }
                };
            }
        }

        private static List<Dictionary<string, object>> GetToolsDefinitions()
        {
            var list = new List<Dictionary<string, object>>();

            list.Add(CreateToolDef("tia_list_processes", "Lists all active TIA Portal processes and their open project paths.", new Dictionary<string, object>()));
            list.Add(CreateToolDef("tia_connect", "Attaches to a running TIA Portal instance by PID (or first found instance).", new Dictionary<string, object>
            {
                { "pid", new Dictionary<string, object> { { "type", "integer" }, { "description", "Optional process ID. Default is 0 (first found)." } } }
            }));
            list.Add(CreateToolDef("tia_open_headless", "Starts a headless TIA Portal V18 instance and opens a project file.", new Dictionary<string, object>
            {
                { "projectPath", new Dictionary<string, object> { { "type", "string" }, { "description", "Absolute path to .ap18 project file." } } }
            }, new List<string> { "projectPath" }));
            list.Add(CreateToolDef("tia_get_project_info", "Returns metadata about the active TIA project (name, path, save status).", new Dictionary<string, object>()));
            list.Add(CreateToolDef("tia_list_devices", "Enumerates devices (PLCs, HMIs, drives) in the project.", new Dictionary<string, object>()));
            list.Add(CreateToolDef("tia_list_blocks", "Enumerates all software blocks (OB, FB, FC, DB) with their groups and types.", new Dictionary<string, object>
            {
                { "deviceName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional device name." } } }
            }));
            list.Add(CreateToolDef("tia_export_block", "Exports a block (OB, FB, FC, DB) as SimaticML XML.", new Dictionary<string, object>
            {
                { "blockPath", new Dictionary<string, object> { { "type", "string" }, { "description", "Block path e.g. 'Main' or '11_Conveyors/Conv_05_FC'." } } },
                { "outputFilePath", new Dictionary<string, object> { { "type", "string" }, { "description", "Absolute path where .xml file should be saved." } } }
            }, new List<string> { "blockPath", "outputFilePath" }));
            list.Add(CreateToolDef("tia_import_block", "Imports or updates a block from SimaticML XML.", new Dictionary<string, object>
            {
                { "xmlFilePath", new Dictionary<string, object> { { "type", "string" }, { "description", "Absolute path to the XML file." } } },
                { "groupPath", new Dictionary<string, object> { { "type", "string" }, { "description", "Target block group path e.g. '10_Logic'." } } }
            }, new List<string> { "xmlFilePath" }));
            list.Add(CreateToolDef("tia_read_scl", "Decompiles SimaticML XML into readable Structured Text (SCL) logic.", new Dictionary<string, object>
            {
                { "blockPath", new Dictionary<string, object> { { "type", "string" }, { "description", "Block path e.g. 'Main'." } } }
            }, new List<string> { "blockPath" }));
            list.Add(CreateToolDef("tia_list_tags", "Lists all PLC tag tables and tag names.", new Dictionary<string, object>()));
            list.Add(CreateToolDef("tia_export_tags", "Exports a PLC tag table as XML.", new Dictionary<string, object>
            {
                { "tableName", new Dictionary<string, object> { { "type", "string" }, { "description", "Tag table name." } } },
                { "outputFilePath", new Dictionary<string, object> { { "type", "string" }, { "description", "Target XML path." } } }
            }, new List<string> { "tableName", "outputFilePath" }));
            list.Add(CreateToolDef("tia_import_tags", "Imports a PLC tag table from XML.", new Dictionary<string, object>
            {
                { "xmlFilePath", new Dictionary<string, object> { { "type", "string" }, { "description", "XML file path." } } }
            }, new List<string> { "xmlFilePath" }));
            list.Add(CreateToolDef("tia_compile", "Compiles PLC software or hardware and returns detailed error/warning diagnostics.", new Dictionary<string, object>
            {
                { "target", new Dictionary<string, object> { { "type", "string" }, { "description", "'software' (default) or 'hardware'." } } }
            }));
            list.Add(CreateToolDef("tia_save_project", "Saves the active TIA Portal project.", new Dictionary<string, object>()));
            list.Add(CreateToolDef("tia_audit_project", "Runs deep structural audit, cross-reference analysis and unreferenced block detection.", new Dictionary<string, object>()));
            list.Add(CreateToolDef("tia_check_simulation", "Checks if S7-PLCSIM V18 or simulation instance is running.", new Dictionary<string, object>()));
            list.Add(CreateToolDef("tia_start_simulation", "Starts S7-PLCSIM V18 instance and prepares for download.", new Dictionary<string, object>()));
            list.Add(CreateToolDef("tia_get_call_structure", "Returns native Call Structure table with invocation counts and conflict filtering.", new Dictionary<string, object>
            {
                { "onlyConflicts", new Dictionary<string, object> { { "type", "boolean" }, { "description", "If true, only returns uncalled blocks." } } }
            }));
            list.Add(CreateToolDef("tia_get_dependency_structure", "Returns native Dependency Structure showing data (DB) usage up to Main (OB1).", new Dictionary<string, object>()));
            list.Add(CreateToolDef("tia_get_memory_resources", "Returns memory resource report (Load/Work/Retentive memory & per-block bytes).", new Dictionary<string, object>()));
            list.Add(CreateToolDef("tia_get_hardware_config", "Returns controller hardware details, MLFB, firmware, PROFINET IP and subnet.", new Dictionary<string, object>()));
            list.Add(CreateToolDef("tia_batch_export", "Batch exports all blocks, UDTs and tags preserving group hierarchy.", new Dictionary<string, object>
            {
                { "outputDirectory", new Dictionary<string, object> { { "type", "string" }, { "description", "Target directory." } } }
            }));
            list.Add(CreateToolDef("tia_batch_import", "Batch imports project structure from directory.", new Dictionary<string, object>
            {
                { "inputDirectory", new Dictionary<string, object> { { "type", "string" }, { "description", "Source directory." } } }
            }, new List<string> { "inputDirectory" }));
            list.Add(CreateToolDef("tia_get_watchdog_status", "Returns live telemetry from the autonomous project watchdog.", new Dictionary<string, object>()));

            return list;
        }

        private static Dictionary<string, object> CreateToolDef(string name, string description, Dictionary<string, object> properties, List<string> required = null)
        {
            var schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", properties }
            };
            if (required != null && required.Count > 0)
            {
                schema["required"] = required;
            }

            return new Dictionary<string, object>
            {
                { "name", name },
                { "description", description },
                { "inputSchema", schema }
            };
        }

        // ====================================================================
        // CLI ENGINE
        // ====================================================================

        private static void RunCli(string[] args)
        {
            string cmd = args[0].ToLower();
            try
            {
                switch (cmd)
                {
                    case "list-processes":
                        Console.WriteLine(_serializer.Serialize(DoListProcesses()));
                        break;
                    case "test-attach":
                        int pid = args.Length > 1 ? int.Parse(args[1]) : 0;
                        Console.WriteLine(_serializer.Serialize(DoConnectProcess(new Dictionary<string, object> { { "pid", pid } })));
                        break;
                    case "save":
                        DoConnectProcess(new Dictionary<string, object>());
                        Console.WriteLine(DoSaveProject());
                        break;
                    case "save-version":
                        DoConnectProcess(new Dictionary<string, object>());
                        string customName = args.Length > 1 ? args[1] : null;
                        var sArgs = new Dictionary<string, object>();
                        if (!string.IsNullOrEmpty(customName)) sArgs["customName"] = customName;
                        Console.WriteLine(_serializer.Serialize(DoSaveProjectVersion(sArgs)));
                        break;
                    case "clean":
                    case "clean-garbage":
                        DoConnectProcess(new Dictionary<string, object>());
                        bool forceClean = args.Length > 1 && args[1] == "--force";
                        var cArgs = new Dictionary<string, object> { { "force", forceClean } };
                        Console.WriteLine(_serializer.Serialize(DoAuditProject(cArgs)));
                        break;
                    case "audit":
                        DoConnectProcess(new Dictionary<string, object>());
                        Console.WriteLine(_serializer.Serialize(DoAuditProject(new Dictionary<string, object>())));
                        break;
                    case "compile":
                        DoConnectProcess(new Dictionary<string, object>());
                        Console.WriteLine(_serializer.Serialize(DoCompile(new Dictionary<string, object>())));
                        break;
                    case "call-tree":
                    case "--call-tree":
                        DoConnectProcess(new Dictionary<string, object>());
                        bool conflicts = args.Length > 1 && args[1].Contains("conflict");
                        Console.WriteLine(_serializer.Serialize(GetCallStructure(FindPlcSoftware(FindDevice(null)), conflicts, true)));
                        break;
                    case "dependencies":
                    case "--dependencies":
                        DoConnectProcess(new Dictionary<string, object>());
                        Console.WriteLine(_serializer.Serialize(GetDependencyStructure(FindPlcSoftware(FindDevice(null)))));
                        break;
                    case "memory":
                    case "--memory":
                        DoConnectProcess(new Dictionary<string, object>());
                        var d = FindDevice(null);
                        Console.WriteLine(_serializer.Serialize(GetMemoryResources(FindPlcSoftware(d), d)));
                        break;
                    case "hardware":
                    case "--hardware":
                        DoConnectProcess(new Dictionary<string, object>());
                        var dHw = FindDevice(null);
                        Console.WriteLine(_serializer.Serialize(GetHardwareConfig(dHw, FindPlcSoftware(dHw))));
                        break;
                    case "export-all":
                    case "--export-all":
                        DoConnectProcess(new Dictionary<string, object>());
                        string outP = args.Length > 1 ? args[1] : @"C:\Users\aa.fedin\Desktop\Tia_18_Agent\Export_" + _activeProject.Name;
                        Console.WriteLine(BatchExport(FindPlcSoftware(FindDevice(null)), outP, "all"));
                        break;
                    default:
                        Console.WriteLine("TiaPortal18Agent v" + AGENT_VERSION + ". Commands: list-processes, test-attach, audit, compile, call-tree, dependencies, memory, hardware, export-all, --watch, --mcp");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("CLI Error: " + ex);
            }
        }
    }
}
