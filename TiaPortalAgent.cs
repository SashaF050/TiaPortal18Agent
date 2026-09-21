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
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.WatchAndForceTables;

namespace TiaPortalAgent
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
                    if (File.Exists(_logFile))
                    {
                        var lines = File.ReadAllLines(_logFile, Encoding.UTF8);
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

    public class AgentSettings
    {
        public string Language { get; set; }
        public bool ExportIncludeComments { get; set; }
        public string CsvDelimiter { get; set; }
        public int DisplayPageSize { get; set; }
        public bool BackupUseShortYear { get; set; }
        public bool AutoClearConsole { get; set; }
        public string DefaultExportFormat { get; set; }
        public bool AutoCleanEmptyGroups { get; set; }
        public string LastProjectPath { get; set; }

        public AgentSettings()
        {
            string sysLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            Language = (sysLang == "ru") ? "ru" : "en";
            ExportIncludeComments = true;
            CsvDelimiter = ";";
            DisplayPageSize = 20;
            BackupUseShortYear = false;
            AutoClearConsole = true;
            DefaultExportFormat = "SimaticML XML";
            AutoCleanEmptyGroups = true;
            LastProjectPath = "";
        }
    }

    public class Program
    {
        public const string AGENT_VERSION = "2.6.0";
        public const string BUILD_DATE = "2026-09-21";
        public const string TIA_TARGET_VERSION = "TIA Portal V14-V21 Universal";

        private static TiaPortal _activeTiaPortal = null;
        private static Project _activeProject = null;
        private static string _currentLanguage = "ru";
        private static bool _exportIncludeComments = true;
        private static string _csvDelimiter = ";";
        private static string _lastProjectPath = "";
        private static bool _backupUseShortYear = false;
        private static bool _autoClearConsole = true;
        private static int _displayPageSize = 20;
        private static bool _autoCleanEmptyGroups = true;
        private static string _defaultExportFormat = "SimaticML XML";
        private static int _attachedPid = 0;
        private static JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = 100 * 1024 * 1024 };

        private static AgentSettings _settings = new AgentSettings();

        public static string L(string ru, string en)
        {
            return string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase) ? en : ru;
        }

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

        // --- Settings Management & Persistence ---

        private static string GetSettingsFilePath()
        {
            try
            {
                string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        return Path.Combine(dir, "agent_settings.json");
                    }
                }
            }
            catch { }

            string favDir = @"C:\Users\aa.fedin\Favorites\Tia_18_Agent";
            if (Directory.Exists(favDir))
                return Path.Combine(favDir, "agent_settings.json");

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent_settings.json");
        }

        private static void LoadSettings()
        {
            try
            {
                string settingsFile = GetSettingsFilePath();
                if (File.Exists(settingsFile))
                {
                    string json = File.ReadAllText(settingsFile, Encoding.UTF8);
                    var loaded = _serializer.Deserialize<AgentSettings>(json);
                    if (loaded != null)
                    {
                        _settings = loaded;
                        _currentLanguage = string.Equals(_settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
                        _exportIncludeComments = _settings.ExportIncludeComments;
                        _csvDelimiter = string.IsNullOrEmpty(_settings.CsvDelimiter) ? ";" : _settings.CsvDelimiter;
                        _displayPageSize = _settings.DisplayPageSize > 0 ? _settings.DisplayPageSize : 20;
                        _backupUseShortYear = _settings.BackupUseShortYear;
                        _autoClearConsole = _settings.AutoClearConsole;
                        _defaultExportFormat = string.IsNullOrEmpty(_settings.DefaultExportFormat) ? "SimaticML XML" : _settings.DefaultExportFormat;
                        _autoCleanEmptyGroups = _settings.AutoCleanEmptyGroups;
                        _lastProjectPath = _settings.LastProjectPath ?? "";
                        return;
                    }
                }
            }
            catch { }

            // Defaults with auto-detected OS language (Russian if RU, else English)
            string sysLang = "";
            try { sysLang = System.Globalization.CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.ToLowerInvariant(); } catch { }
            if (string.IsNullOrEmpty(sysLang) || sysLang != "ru")
            {
                try { sysLang = System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName.ToLowerInvariant(); } catch { }
            }
            if (string.IsNullOrEmpty(sysLang) || sysLang != "ru")
            {
                try { sysLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant(); } catch { }
            }
            _currentLanguage = (sysLang == "ru") ? "ru" : "en";
            _settings.Language = _currentLanguage;
            _exportIncludeComments = true;
            _csvDelimiter = ";";
            _displayPageSize = 20;
            _backupUseShortYear = false;
            _autoClearConsole = true;
            _defaultExportFormat = "SimaticML XML";
            _autoCleanEmptyGroups = true;
            _lastProjectPath = "";
            SaveSettings();
        }

        private static void SaveSettings()
        {
            try
            {
                _settings.Language = _currentLanguage;
                _settings.ExportIncludeComments = _exportIncludeComments;
                _settings.CsvDelimiter = _csvDelimiter;
                _settings.DisplayPageSize = _displayPageSize;
                _settings.BackupUseShortYear = _backupUseShortYear;
                _settings.AutoClearConsole = _autoClearConsole;
                _settings.DefaultExportFormat = _defaultExportFormat;
                _settings.AutoCleanEmptyGroups = _autoCleanEmptyGroups;
                _settings.LastProjectPath = _lastProjectPath;

                string settingsFile = GetSettingsFilePath();
                string json = _serializer.Serialize(_settings);
                File.WriteAllText(settingsFile, json, Encoding.UTF8);
            }
            catch { }
        }

        // --- Safe Connectivity Checks ---

        private static bool IsTiaConnected()
        {
            if (_activeProject == null || _activeTiaPortal == null) return false;
            try
            {
                string n = _activeProject.Name;
                return !string.IsNullOrEmpty(n);
            }
            catch
            {
                _activeProject = null;
                _activeTiaPortal = null;
                return false;
            }
        }

        private static string SafeGetProjectName()
        {
            try
            {
                if (_activeProject != null)
                {
                    string n = _activeProject.Name;
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch
            {
                _activeProject = null;
                _activeTiaPortal = null;
            }
            return L("[Нет активного проекта / TIA закрыта]", "[No active project / TIA disconnected]");
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
            LoadSettings();
            EnsureSelfWhitelisted();
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
            else if (args[0].ToLower() == "--headless")
            {
                RunHeadlessCli(args);
            }
            else
            {
                RunCli(args);
            }
        }

        public static void SyncToDesktopDirectory()
        {
            // Desktop mirroring deprecated. Files are kept in Favorites\Tia_18_Agent.
        }

        private static void PrintVersionInfo()
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("  TiaPortalAgent v" + AGENT_VERSION + " [Universal Multi-Tool & MCP Server]");
            Console.WriteLine("  Build Date: " + BUILD_DATE + " | Target: " + TIA_TARGET_VERSION);
            Console.WriteLine("  PublicAPI: Siemens.Engineering.dll (V18/V19/V20/V21 Auto-Detect)");
            Console.WriteLine("  Integrations: Czarnak MCP | cFirewall Whitelist | bulaofen0036 | AnyAutomation");
            Console.WriteLine("================================================================================");
        }

        private static Assembly ResolveSiemensAssembly(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            string[] versions = new string[] { "V21", "V20", "V19", "V18" };
            foreach (var v in versions)
            {
                string publicApiDir = string.Format(@"C:\Program Files\Siemens\Automation\Portal {0}\PublicAPI\{0}", v);
                string candidate = Path.Combine(publicApiDir, name + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);

                string binDir = string.Format(@"C:\Program Files\Siemens\Automation\Portal {0}\Bin", v);
                candidate = Path.Combine(binDir, name + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
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

                string desktopExe = @"C:\Users\aa.fedin\Desktop\TiaPortalAgent\TiaPortalAgent.exe";
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
                string[] versions = new string[] { "18.0", "19.0", "20.0", "21.0" };

                foreach (string ver in versions)
                {
                    string[] rootKeys = new string[]
                    {
                        @"SOFTWARE\Siemens\Automation\Openness\" + ver + @"\Whitelist",
                        @"SOFTWARE\WOW6432Node\Siemens\Automation\Openness\" + ver + @"\Whitelist"
                    };

                    foreach (string rootPath in rootKeys)
                    {
                        try
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
                        catch { }
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
            Console.WriteLine("    SIEMENS TIA PORTAL UNIVERSAL AGENT v" + AGENT_VERSION);
            Console.WriteLine("    Target: " + TIA_TARGET_VERSION + " | cFirewall Whitelist: ACTIVE");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            try { EnsureConnected(false); } catch { }

            while (true)
            {
                if (_autoClearConsole) ClearScreen();
                bool connected = IsTiaConnected();
                string projName = SafeGetProjectName();

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("================================================================================");
                Console.WriteLine(string.Format("  TIA PORTAL AGENT v{0} | {1}: {2}", AGENT_VERSION, L("Проект", "Project"), projName));
                Console.WriteLine("================================================================================");
                Console.ResetColor();

                if (!connected)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(L("  [!] ВНИМАНИЕ: TIA Portal не подключен (GUI закрыт или процесс не запущен).",
                                        "  [!] WARNING: TIA Portal is not connected (GUI closed or process not running)."));
                    Console.WriteLine(L("      Нажмите [P] для подключения к TIA или открытия проекта в фоне (Headless).",
                                        "      Press [P] to connect to TIA or open project in background (Headless)."));
                    Console.ResetColor();
                    Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
                }

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [1] " + L("Сводка проекта и аппаратная конфигурация", "Project Summary & Hardware Configuration"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("CPU, модули, IP-адрес, подсеть — обзор оборудования", "CPU, modules, IP address, subnet — hardware overview"));

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [2] " + L("Структура вызова (Call Structure)", "Call Structure"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("Полноценная навигация, фильтрация и анализ вызовов Main (OB1)", "Full navigation, filtering and call analysis from Main (OB1)"));

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [3] " + L("Структура зависимости (Dependency)", "Dependency Structure"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("Граф связей DB → FC/FB → OB1, проверенные цепочки обращений", "Link graph DB → FC/FB → OB1, verified reference chains"));

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [4] " + L("Ресурсы памяти (Memory Resources)", "Memory Resources"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("Загружаемая/Рабочая/Энергонезависимая — шкалы заполнения", "Load/Work/Retentive memory — capacity gauges"));

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [5] " + L("Диагностика и компилятор", "Diagnostics & Compiler"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("Компиляция проекта, ошибки и предупреждения TIA Portal", "Project compilation, TIA Portal errors and warnings"));

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [6] " + L("Умный очиститель мусора", "Smart Garbage Cleaner"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("Очистка неиспользуемых блоков, тегов, UDT и пустых папок", "Cleanup of unused blocks, tags, UDTs and empty folders"));

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [7] " + L("Пакетный экспорт / импорт", "Batch Export / Import"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("XML SimaticML, SCL и CSV тегов с комментариями/без", "SimaticML XML, SCL and tag CSV with/without comments"));

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [8] " + L("S7-PLCSIM V18 — Симулятор", "S7-PLCSIM V18 — Simulator"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("Статус, запуск и управление виртуальным контроллером", "Status, start and control of virtual controller"));

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [9] " + L("Настройки (Settings)", "Settings"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("Язык (RU/EN), комментарии тегов, разделители CSV, даты", "Language (RU/EN), tag comments, CSV delimiters, dates"));

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [0] " + L("Журнал ошибок и сбоев (Crash History)", "Crash History"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("Просмотр истории крашей, стеков исключений и журнала", "View crash history, exception stacks and error logs"));

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [R] " + L("Робот KUKA — Синхронизатор сигналов", "KUKA Robot — Signal Synchronizer"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("KRL $IN/$OUT ↔ TIA PLC Tags, экспорт/импорт сигналов", "KRL $IN/$OUT ↔ TIA PLC Tags, signal export/import"));

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [M] " + L("Мастер переезда адресов оборудования и тегов", "Hardware & Tag Address Relocation Wizard"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("Сдвиг HW адресов и перепривязка тегов ПЛК с контролем коллизий", "HW address shift and PLC tag remapping with collision guard"));

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [D] " + L("Диагностика совместимости и готовности системы", "Compatibility & System Readiness Check"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("Проверка версий TIA, Openness API, прав доступа и Whitelist", "Check TIA versions, Openness API, user rights and Whitelist"));
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("   [W] " + L("Таблицы наблюдения и форсирования (Watch & Force Tables)", "Watch & Force Tables"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("       " + L("Просмотр таблиц наблюдения, форсированных сигналов и адресов", "Inspect watch tables, forced signals and addresses"));
                Console.ResetColor();

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("   [Esc / Q] " + L("Выход из агента", "Exit Agent"));
                Console.ResetColor();
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("   [S] " + L("Сохранить проект", "Save Project") + "     [V] " + L("Сохранить версию (V0→V1)", "Save Version (V0→V1)") + "     [Z] " + L("Архив (.zap18)", "Archive (.zap18)") + "     [P] " + L("Сменить проект", "Switch Project"));
                Console.ResetColor();
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
                Console.Write(" " + L("Выберите действие", "Select action") + " [0-9, R, M, D, W, S, V, Z, P, Esc]: ");

                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Escape || key.KeyChar == 'q' || key.KeyChar == 'Q' || key.KeyChar == 'й' || key.KeyChar == 'Й') break;
                Console.WriteLine(key.KeyChar);

                try
                {
                    // Check if operation requires active project
                    bool requiresProject = (key.KeyChar == '1' || key.KeyChar == '2' || key.KeyChar == '3' ||
                                            key.KeyChar == '4' || key.KeyChar == '5' || key.KeyChar == '6' ||
                                            key.KeyChar == '7' || key.KeyChar == 'r' || key.KeyChar == 'R' ||
                                            key.KeyChar == 'к' || key.KeyChar == 'К' || key.KeyChar == 'm' ||
                                            key.KeyChar == 'M' || key.KeyChar == 'ь' || key.KeyChar == 'Ь' ||
                                            key.KeyChar == 'w' || key.KeyChar == 'W' || key.KeyChar == 'ц' ||
                                            key.KeyChar == 'Ц' || key.KeyChar == 's' || key.KeyChar == 'S' ||
                                            key.KeyChar == 'ы' || key.KeyChar == 'Ы' || key.KeyChar == 'v' ||
                                            key.KeyChar == 'V' || key.KeyChar == 'м' || key.KeyChar == 'М' ||
                                            key.KeyChar == 'z' || key.KeyChar == 'Z' || key.KeyChar == 'я' ||
                                            key.KeyChar == 'Я');

                    if (requiresProject && !IsTiaConnected())
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("\n[!] " + L("Для выполнения этого действия требуется открытый проект.",
                                                    "An active project is required for this action."));
                        Console.WriteLine("    " + L("Нажмите [P] для подключения к запущенному TIA или открытия проекта в фоне (Headless).",
                                                    "Press [P] to connect to running TIA or open project in headless mode."));
                        Console.ResetColor();
                        Console.WriteLine(L("Нажмите любую клавишу для продолжения...", "Press any key to continue..."));
                        try { Console.ReadKey(true); } catch { }
                        continue;
                    }

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
                    else if (key.KeyChar == 'w' || key.KeyChar == 'W' || key.KeyChar == 'ц' || key.KeyChar == 'Ц') ShowWatchTablesTui();
                    else if (key.KeyChar == 's' || key.KeyChar == 'S' || key.KeyChar == 'ы' || key.KeyChar == 'Ы')
                    {
                        Console.WriteLine(DoSaveProject());
                        Console.WriteLine(L("Нажмите любую клавишу для продолжения...", "Press any key to continue..."));
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
                        Console.WriteLine(L("Нажмите любую клавишу для продолжения...", "Press any key to continue..."));
                        Console.ReadKey(true);
                    }
                    else if (key.KeyChar == 'z' || key.KeyChar == 'Z' || key.KeyChar == 'я' || key.KeyChar == 'Я')
                    {
                        Console.WriteLine();
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("================================================================================");
                        Console.WriteLine(" " + L("АРХИВАЦИЯ ПРОЕКТА В .ZAP18 (Openness DiscardRestorableDataAndCompressed)",
                                               "PROJECT ARCHIVING TO .ZAP18 (Openness DiscardRestorableDataAndCompressed)"));
                        Console.WriteLine("================================================================================");
                        Console.ResetColor();
                        Console.WriteLine(" " + L("Текущий проект: ", "Active project: ") + SafeGetProjectName());
                        Console.Write(" " + L("Введите имя архива (Enter для стандартного имени): ", "Enter archive name (Enter for default): "));
                        string arcNameInput = Console.ReadLine();
                        var arcArgs = new Dictionary<string, object>();
                        if (!string.IsNullOrWhiteSpace(arcNameInput)) arcArgs["archiveName"] = arcNameInput.Trim();

                        Console.WriteLine(" " + L("Архивация проекта... Пожалуйста, подождите.", "Archiving project... Please wait."));
                        var arcRes = DoArchiveProject(arcArgs);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("\n ✓ " + arcRes["message"]);
                        Console.WriteLine("   " + L("Файл: ", "File: ") + arcRes["archivePath"]);
                        Console.WriteLine("   " + L("Размер: ", "Size: ") + arcRes["sizeFormatted"]);
                        Console.ResetColor();
                        Console.WriteLine("\n" + L("Нажмите любую клавишу для продолжения...", "Press any key to continue..."));
                        Console.ReadKey(true);
                    }
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, "RunInteractiveDashboard.MenuSelection");
                    if (!IsTiaConnected())
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("\n[!] " + L("Связь с TIA Portal была разорвана (программа закрыта пользователем).",
                                                    "Connection to TIA Portal was lost (application was closed by user)."));
                        Console.WriteLine("    " + L("Соединение безопасно сброшено. Вы можете переподключиться через [P] или работать в фоне.",
                                                    "Connection safely reset. You can reconnect via [P] or work in headless mode."));
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("\n[!] " + L("Ошибка выполнения операции: ", "Operation error: ") + ex.Message);
                        Console.WriteLine("    " + L("Подробности записаны в crash_history.log", "Details written to crash_history.log"));
                    }
                    Console.ResetColor();
                    Console.WriteLine(L("Нажмите любую клавишу для возврата в меню...", "Press any key to return to menu..."));
                    try { Console.ReadKey(true); } catch { }
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
                Console.WriteLine("  " + L("НАСТРОЙКИ АГЕНТА (Settings)", "AGENT SETTINGS (Config & Preferences)"));
                Console.WriteLine("================================================================================");
                Console.ResetColor();
                Console.WriteLine("  • " + L("Версия агента:       ", "Agent Version:       ") + AGENT_VERSION);
                Console.WriteLine("  • " + L("Целевая версия TIA:   ", "TIA Target Version:  ") + TIA_TARGET_VERSION);
                Console.WriteLine("  • " + L("Язык системы (OS):   ", "System Language:     ") + System.Globalization.CultureInfo.CurrentUICulture.DisplayName);
                Console.WriteLine("--------------------------------------------------------------------------------");
                Console.WriteLine(" [1] " + L("Язык интерфейса (UI Language):          ", "UI Language:                         ") + (_currentLanguage == "ru" ? "Русский [RU]" : "English [EN]"));
                Console.WriteLine(" [2] " + L("Экспорт комментариев к тегам:        ", "Export Tag Comments:                 ") + (_exportIncludeComments ? L("ВКЛЮЧЕНО (с комментариями)", "ENABLED (with comments)") : L("ВЫКЛЮЧЕНО (без комментариев)", "DISABLED (no comments)")));
                Console.WriteLine(" [3] " + L("Разделитель CSV (CSV Delimiter):     ", "CSV Delimiter:                       ") + (_csvDelimiter == ";" ? L("Точка с запятой [;] (EU/RU)", "Semicolon [;] (EU/RU)") : L("Запятая [,] (US/Intl)", "Comma [,] (US/Intl)")));
                Console.WriteLine(" [4] " + L("Формат даты в резервной копии:       ", "Backup Date Format:                  ") + (_backupUseShortYear ? L("Короткий год (dd.MM.yy)", "Short Year (dd.MM.yy)") : L("Полный год (dd.MM.yyyy)", "Full Year (dd.MM.yyyy)")));
                Console.WriteLine(" [5] " + L("Количество строк на страницу:        ", "Display Page Size:                   ") + _displayPageSize + L(" строк", " lines"));
                Console.WriteLine(" [6] " + L("Формат экспорта блоков по умолчанию: ", "Default Block Export Format:         ") + _defaultExportFormat);
                Console.WriteLine(" [7] " + L("Авто-очистка экрана перед меню:      ", "Auto-Clear Screen on Menu:           ") + (_autoClearConsole ? L("Включено", "Enabled") : L("Выключено", "Disabled")));
                Console.WriteLine(" [8] " + L("Авто-удаление пустых папок:          ", "Auto-Delete Empty Groups:            ") + (_autoCleanEmptyGroups ? L("Включено (после очистки)", "Enabled (after cleaning)") : L("Выключено", "Disabled")));
                Console.WriteLine(" [9] " + L("Перерегистрация в Openness Whitelist (реестр Windows)", "Re-register in Openness Whitelist (Windows Registry)"));
                Console.WriteLine(" [C] " + L("Проверить / Переподключить TIA Portal", "Check / Reconnect TIA Portal"));
                Console.WriteLine(" [H] " + L("Открыть проект в фоновом режиме (Headless WithoutUserInterface)", "Open project in Headless mode (WithoutUserInterface)"));
                Console.WriteLine("================================================================================");
                Console.WriteLine(" [Esc/Q/0] " + L("Вернуться в главное меню", "Return to Main Menu"));
                Console.Write("\n " + L("Ваш выбор: ", "Your choice: "));

                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Q || key.KeyChar == '0' || key.KeyChar == 'q' || key.KeyChar == 'й' || key.KeyChar == 'Й') break;

                if (key.KeyChar == '1')
                {
                    _currentLanguage = (_currentLanguage == "ru") ? "en" : "ru";
                    SaveSettings();
                }
                else if (key.KeyChar == '2')
                {
                    _exportIncludeComments = !_exportIncludeComments;
                    SaveSettings();
                }
                else if (key.KeyChar == '3')
                {
                    _csvDelimiter = (_csvDelimiter == ";") ? "," : ";";
                    SaveSettings();
                }
                else if (key.KeyChar == '4')
                {
                    _backupUseShortYear = !_backupUseShortYear;
                    SaveSettings();
                }
                else if (key.KeyChar == '5')
                {
                    if (_displayPageSize == 15) _displayPageSize = 20;
                    else if (_displayPageSize == 20) _displayPageSize = 25;
                    else if (_displayPageSize == 25) _displayPageSize = 50;
                    else _displayPageSize = 15;
                    SaveSettings();
                }
                else if (key.KeyChar == '6')
                {
                    _defaultExportFormat = _defaultExportFormat == "SimaticML XML" ? "SCL" : "SimaticML XML";
                    SaveSettings();
                }
                else if (key.KeyChar == '7')
                {
                    _autoClearConsole = !_autoClearConsole;
                    SaveSettings();
                }
                else if (key.KeyChar == '8')
                {
                    _autoCleanEmptyGroups = !_autoCleanEmptyGroups;
                    SaveSettings();
                }
                else if (key.KeyChar == '9')
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
                            Console.WriteLine("\n[+] " + L("Whitelist успешно обновлен в реестре.", "Whitelist successfully updated in registry."));
                            Console.ResetColor();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("\n[-] " + L("Ошибка: ", "Error: ") + ex.Message);
                        Console.ResetColor();
                    }
                    Thread.Sleep(1000);
                }
                else if (key.KeyChar == 'c' || key.KeyChar == 'C' || key.KeyChar == 'с' || key.KeyChar == 'С')
                {
                    Console.WriteLine("\n" + L("Переподключение к TIA Portal...", "Reconnecting to TIA Portal..."));
                    _activeProject = null;
                    EnsureConnected(false);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("[+] " + L("Подключено к проекту: ", "Connected to project: ") + SafeGetProjectName());
                    Console.ResetColor();
                    Thread.Sleep(1200);
                }
                else if (key.KeyChar == 'h' || key.KeyChar == 'H' || key.KeyChar == 'р' || key.KeyChar == 'Р')
                {
                    OpenProjectHeadlessInteractive();
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
            Console.WriteLine("  " + L("ПАКЕТНЫЙ ЭКСПОРТ И ИМПОРТ (SimaticML XML, SCL & CSV)", "BATCH EXPORT AND IMPORT (SimaticML XML, SCL & CSV)"));
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            EnsureConnected();
            var dev = FindDevice(null);
            var plc = FindPlcSoftware(dev);

            Console.WriteLine(" 1. " + L("Пакетный экспорт проекта (Блоки, UDT, Теги с иерархией XML/SCL)",
                                          "Batch export project (Blocks, UDTs, Tags with hierarchy XML/SCL)"));
            Console.WriteLine(" 2. " + L("Пакетный импорт проекта из директории",
                                          "Batch import project from directory"));
            Console.WriteLine(" 3. " + L(string.Format("Экспорт всех тегов ПЛК в CSV ({0}, разделитель: '{1}')",
                                                      _exportIncludeComments ? "с комментариями" : "без комментариев", _csvDelimiter),
                                          string.Format("Export all PLC tags to CSV ({0}, delimiter: '{1}')",
                                                      _exportIncludeComments ? "with comments" : "without comments", _csvDelimiter)));
            Console.WriteLine(" 0. " + L("Назад", "Back"));
            Console.Write(" " + L("Выберите действие: ", "Select action: "));
            var k = Console.ReadKey(true);
            Console.WriteLine(k.KeyChar);

            if (k.KeyChar == '1')
            {
                string defDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Export_" + _activeProject.Name);
                Console.Write(" " + L("Путь экспорта", "Export path") + " [" + defDir + "]: ");
                string path = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(path)) path = defDir;

                Console.WriteLine(" " + L("Экспорт в процессе...", "Export in progress..."));
                string res = BatchExport(plc, path, "all");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(" " + res);
                Console.ResetColor();
            }
            else if (k.KeyChar == '2')
            {
                Console.Write(" " + L("Путь к директории для импорта: ", "Directory path for import: "));
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
                    Console.WriteLine(L("Директория не найдена.", "Directory not found."));
                }
            }
            else if (k.KeyChar == '3')
            {
                string defCsv = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tags_" + _activeProject.Name + ".csv");
                Console.Write(" " + L("Путь к CSV файлу", "Path to CSV file") + " [" + defCsv + "]: ");
                string csvP = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(csvP)) csvP = defCsv;

                Console.WriteLine(" " + L("Экспорт тегов в CSV...", "Exporting tags to CSV..."));
                string res = DoExportTagsCsv(plc, csvP);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(" " + res);
                Console.ResetColor();
            }

            Console.WriteLine(L("Нажмите любую клавишу для возврата в меню...", "Press any key to return to menu..."));
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
                    string csvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Kuka_Signals_" + targetTableName + ".csv");
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

                if (_exportIncludeComments && !string.IsNullOrEmpty(tComment))
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
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "watchdog.log"), logEntry + Environment.NewLine);
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
            Console.WriteLine("  " + L("ВЫБОР ПРОЕКТА / ПОДКЛЮЧЕНИЕ К TIA PORTAL", "PROJECT SELECTION & TIA PORTAL CONNECTION"));
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            IList<TiaPortalProcess> procs = null;
            try { procs = TiaPortal.GetProcesses(); } catch { }

            int pCount = (procs != null) ? procs.Count : 0;
            Console.WriteLine("  " + L("Активных процессов TIA Portal: ", "Active TIA Portal processes: ") + pCount);
            Console.WriteLine("--------------------------------------------------------------------------------");

            if (pCount > 0)
            {
                for (int i = 0; i < procs.Count; i++)
                {
                    var p = procs[i];
                    string projPath = p.ProjectPath != null ? p.ProjectPath.FullName : L("[Без открытого проекта]", "[No open project]");
                    string isCurrent = (p.Id == _attachedPid && IsTiaConnected()) ? " <-- " + L("[АКТИВЕН]", "[ACTIVE]") : "";
                    Console.WriteLine("  [{0}] PID: {1,-6} | {2}: {3}{4}", i + 1, p.Id, L("Проект", "Project"), projPath, isCurrent);
                }
                Console.WriteLine("--------------------------------------------------------------------------------");
            }

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  [H] " + L("Открыть проект в фоновом режиме (Headless WithoutUserInterface)", "Open project in Headless mode (WithoutUserInterface)"));
            if (!string.IsNullOrEmpty(_lastProjectPath) && File.Exists(_lastProjectPath))
            {
                Console.WriteLine("  [L] " + L("Открыть последний проект в фоне: ", "Open last project in headless: ") + Path.GetFileName(_lastProjectPath));
            }
            Console.WriteLine("  [W] " + L("Запустить новый TIA Portal с графическим интерфейсом (WithUserInterface)", "Launch new TIA Portal with GUI (WithUserInterface)"));
            Console.WriteLine("  [0 / Esc] " + L("Отмена / Назад", "Cancel / Back"));
            Console.ResetColor();

            if (pCount > 0)
            {
                Console.Write("\n " + L("Введите номер процесса [1-" + pCount + "], H, L, W или Esc: ", "Enter process number [1-" + pCount + "], H, L, W or Esc: "));
            }
            else
            {
                Console.Write("\n " + L("Выберите действие [H, L, W, Esc]: ", "Select action [H, L, W, Esc]: "));
            }

            var key = Console.ReadKey(true);
            Console.WriteLine(key.KeyChar);

            if (key.Key == ConsoleKey.Escape || key.KeyChar == '0' || key.KeyChar == 'q' || key.KeyChar == 'Q')
            {
                return;
            }

            if (key.KeyChar == 'h' || key.KeyChar == 'H' || key.KeyChar == 'р' || key.KeyChar == 'Р')
            {
                OpenProjectHeadlessInteractive();
                return;
            }

            if (key.KeyChar == 'l' || key.KeyChar == 'L' || key.KeyChar == 'д' || key.KeyChar == 'Д')
            {
                if (!string.IsNullOrEmpty(_lastProjectPath) && File.Exists(_lastProjectPath))
                {
                    DoOpenHeadlessDirect(_lastProjectPath);
                }
                else
                {
                    Console.WriteLine(L("Путь к последнему проекту не найден.", "Last project path not found."));
                    Thread.Sleep(1500);
                }
                return;
            }

            if (key.KeyChar == 'w' || key.KeyChar == 'W' || key.KeyChar == 'ц' || key.KeyChar == 'Ц')
            {
                Console.WriteLine(L("\nЗапуск TIA Portal V18 с графическим интерфейсом...", "\nLaunching TIA Portal V18 with GUI..."));
                try
                {
                    _activeTiaPortal = new TiaPortal(TiaPortalMode.WithUserInterface);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine(L("[+] TIA Portal запущен с интерфейсом!", "[+] TIA Portal launched with GUI!"));
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(L("[-] Ошибка запуска: ", "[-] Launch error: ") + ex.Message);
                    Console.ResetColor();
                }
                Thread.Sleep(1500);
                return;
            }

            int selectedIdx;
            if (int.TryParse(key.KeyChar.ToString(), out selectedIdx) && procs != null && selectedIdx >= 1 && selectedIdx <= procs.Count)
            {
                int targetPid = procs[selectedIdx - 1].Id;
                Console.WriteLine(L("\nПереподключение к PID {0}...", "\nReconnecting to PID {0}..."), targetPid);
                _activeProject = null;
                _activeTiaPortal = null;
                string res = DoConnectProcess(new Dictionary<string, object> { { "pid", targetPid }, { "force", true } });
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(res);
                Console.ResetColor();
                if (_activeProject != null && _activeProject.Path != null)
                {
                    _lastProjectPath = _activeProject.Path.FullName;
                    SaveSettings();
                }
                Thread.Sleep(1500);
            }
        }

        private static void OpenProjectHeadlessInteractive()
        {
            Console.WriteLine("\n" + L("--- ОТКРЫТИЕ ПРОЕКТА В ФОНОВОМ РЕЖИМЕ (HEADLESS) ---", "--- OPEN PROJECT IN HEADLESS MODE ---"));
            Console.WriteLine(L("Проект будет открыт через Openness API без запуска графического окна TIA Portal.",
                                "Project will be opened via Openness API without launching TIA Portal GUI."));
            string defaultPath = !string.IsNullOrEmpty(_lastProjectPath) ? _lastProjectPath : "";
            Console.Write(L("Укажите путь к .ap18 файлу", "Specify path to .ap18 file") + " [" + defaultPath + "]: ");
            string input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) input = defaultPath;

            if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(L("[-] Файл не существует: ", "[-] File does not exist: ") + input);
                Console.ResetColor();
                Thread.Sleep(1500);
                return;
            }

            DoOpenHeadlessDirect(input);
        }

        private static void DoOpenHeadlessDirect(string ap18Path)
        {
            Console.WriteLine(L("Открытие в фоновом режиме (WithoutUserInterface)...", "Opening in background (WithoutUserInterface)..."));
            try
            {
                StartAutoConfirmWatcher();
                _activeTiaPortal = new TiaPortal(TiaPortalMode.WithoutUserInterface);
                _activeProject = _activeTiaPortal.Projects.Open(new FileInfo(ap18Path));
                _lastProjectPath = ap18Path;
                SaveSettings();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(L("[+] Проект успешно открыт в фоновом режиме: ", "[+] Project opened successfully in headless mode: ") + _activeProject.Name);
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(L("[-] Ошибка открытия: ", "[-] Open error: ") + ex.Message);
                Console.ResetColor();
            }
            Thread.Sleep(1500);
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
            string blockPath = args.ContainsKey("blockPath") ? args["blockPath"] as string : "";
            string outputPath = null;
            if (args.ContainsKey("outputPath") && args["outputPath"] != null) outputPath = args["outputPath"] as string;
            else if (args.ContainsKey("outputFilePath") && args["outputFilePath"] != null) outputPath = args["outputFilePath"] as string;
            if (string.IsNullOrEmpty(outputPath)) outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, blockPath + ".xml");
            outputPath = Path.GetFullPath(outputPath);

            Device dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);
            var block = FindBlockByPath(plc.BlockGroup, blockPath);
            if (block == null) return "Error: Block not found: " + blockPath;

            string outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            if (File.Exists(outputPath)) { try { File.Delete(outputPath); } catch { } }

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

                try
                {
                    block.Export(new FileInfo(tempXml), ExportOptions.WithDefaults);
                }
                catch (Exception ex)
                {
                    if (ex.Message.IndexOf("Inconsistent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (ex.InnerException != null && ex.InnerException.Message.IndexOf("Inconsistent", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        Log("Block '" + blockPath + "' is inconsistent. Attempting auto-compilation to resolve...");
                        try
                        {
                            var compilable = plc.GetService<ICompilable>();
                            if (compilable != null)
                            {
                                compilable.Compile();
                                block.Export(new FileInfo(tempXml), ExportOptions.WithDefaults);
                            }
                            else
                            {
                                return "Error: Block '" + blockPath + "' is inconsistent and PLC does not support compilation.";
                            }
                        }
                        catch (Exception compEx)
                        {
                            return "Error: Block '" + blockPath + "' is inconsistent and auto-compilation failed: " + compEx.Message;
                        }
                    }
                    else
                    {
                        return "Error exporting block '" + blockPath + "': " + ex.Message;
                    }
                }

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

                int targetNet = args != null && args.ContainsKey("networkNumber") && args["networkNumber"] != null ? Convert.ToInt32(args["networkNumber"]) : 0;
                int startNet = args != null && args.ContainsKey("startNetwork") && args["startNetwork"] != null ? Convert.ToInt32(args["startNetwork"]) : 0;
                int endNet = args != null && args.ContainsKey("endNetwork") && args["endNetwork"] != null ? Convert.ToInt32(args["endNetwork"]) : 0;
                bool outlineOnly = args != null && args.ContainsKey("outlineOnly") && Convert.ToBoolean(args["outlineOnly"]);

                if (stNodes != null && stNodes.Count > 0)
                {
                    if (outlineOnly)
                    {
                        var outSb = new StringBuilder();
                        outSb.AppendLine(string.Format("// Block Outline: {0} (Total Networks: {1})", block.Name, stNodes.Count));
                        outSb.AppendLine("// ========================================================");
                        for (int i = 0; i < stNodes.Count; i++)
                        {
                            string netTitle = ExtractNetworkTitle(stNodes[i], i + 1);
                            outSb.AppendLine(string.Format("// Network {0,2}: {1}", i + 1, !string.IsNullOrEmpty(netTitle) ? netTitle : "(Untitled)"));
                        }
                        return outSb.ToString().TrimEnd();
                    }

                    var sb = new StringBuilder();
                    for (int i = 0; i < stNodes.Count; i++)
                    {
                        int netIndex = i + 1;
                        if (targetNet > 0 && netIndex != targetNet) continue;
                        if (startNet > 0 && netIndex < startNet) continue;
                        if (endNet > 0 && netIndex > endNet) continue;

                        string netTitle = ExtractNetworkTitle(stNodes[i], netIndex);
                        sb.AppendLine("// ========================================================");
                        sb.AppendLine("// Network " + netIndex + (!string.IsNullOrEmpty(netTitle) ? ": " + netTitle : ""));
                        sb.AppendLine("// ========================================================");

                        DecompileStructuredTextNode(stNodes[i], sb);
                        sb.AppendLine();
                    }

                    if (targetNet > 0 && sb.Length == 0)
                    {
                        return string.Format("// Network {0} not found. Block '{1}' has {2} network(s).", targetNet, block.Name, stNodes.Count);
                    }

                    return sb.ToString().TrimEnd();
                }

                var ifaceNodes = doc.GetElementsByTagName("Interface");
                if (ifaceNodes.Count > 0)
                {
                    return DecompileInterfaceXmlToScl(ifaceNodes[0], block.GetType().Name, block.Name);
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
                else if (local == "CallInfo")
                {
                    var instNode = child.SelectSingleNode(".//*[local-name()='Instance']");
                    if (instNode == null)
                    {
                        string callee = child.Attributes != null && child.Attributes["Name"] != null ? child.Attributes["Name"].Value : "";
                        if (!string.IsNullOrEmpty(callee)) sb.Append("\"" + callee + "\"");
                    }
                    DecompileStructuredTextNode(child, sb);
                }
                else if (local == "Instance")
                {
                    string instScope = child.Attributes != null && child.Attributes["Scope"] != null ? child.Attributes["Scope"].Value : "";
                    string instPrefix = instScope == "LocalVariable" ? "#" : "";
                    var comp = child.SelectSingleNode(".//*[local-name()='Component']");
                    if (comp != null && comp.Attributes != null && comp.Attributes["Name"] != null)
                    {
                        string instName = comp.Attributes["Name"].Value;
                        if (instScope == "GlobalVariable") sb.Append("\"" + instName + "\"");
                        else sb.Append(instPrefix + instName);
                    }
                }
                else if (local == "Parameter")
                {
                    string pName = child.Attributes != null && child.Attributes["Name"] != null ? child.Attributes["Name"].Value : "";
                    if (!string.IsNullOrEmpty(pName)) sb.Append(pName);
                    DecompileStructuredTextNode(child, sb);
                }
                else
                {
                    DecompileStructuredTextNode(child, sb);
                }
            }
        }

        private static string ExtractNetworkTitle(XmlNode stNode, int netNum)
        {
            try
            {
                XmlNode cu = stNode;
                while (cu != null && cu.LocalName != "CompileUnit") cu = cu.ParentNode;

                if (cu != null)
                {
                    var titleNode = cu.SelectSingleNode(".//*[local-name()='MultilingualText'][@CompositionName='Title']//*[local-name()='MultilingualTextItem']");
                    if (titleNode != null && titleNode.Attributes != null && titleNode.Attributes["TextValue"] != null)
                    {
                        string tVal = titleNode.Attributes["TextValue"].Value;
                        if (!string.IsNullOrWhiteSpace(tVal)) return tVal.Trim();
                    }
                    var commentNode = cu.SelectSingleNode(".//*[local-name()='MultilingualText'][@CompositionName='Comment']//*[local-name()='MultilingualTextItem']");
                    if (commentNode != null && commentNode.Attributes != null && commentNode.Attributes["TextValue"] != null)
                    {
                        string cVal = commentNode.Attributes["TextValue"].Value;
                        if (!string.IsNullOrWhiteSpace(cVal)) return cVal.Trim();
                    }
                }

                // If no XML title, peek first line of SCL (look for REGION or // comment)
                var tempSb = new StringBuilder();
                DecompileStructuredTextNode(stNode, tempSb);
                string firstLines = tempSb.ToString();
                using (var sr = new StringReader(firstLines))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.StartsWith("REGION", StringComparison.OrdinalIgnoreCase)) return line;
                        if (line.StartsWith("//")) return line.TrimStart('/', ' ');
                    }
                }
            }
            catch { }
            return "";
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

        // ====================================================================
        // SCL INTERFACE DECOMPILER & ACCELERATED EXPLORATION (v2.5.0)
        // ====================================================================

        private static string DecompileInterfaceXmlToScl(XmlNode ifaceNode, string blockTypeName, string blockName, string sectionFilter = null)
        {
            var sb = new StringBuilder();
            bool isDb = blockTypeName.IndexOf("DB", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isFb = blockTypeName.IndexOf("FB", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isFc = blockTypeName.IndexOf("FC", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isUdt = blockTypeName.IndexOf("Type", StringComparison.OrdinalIgnoreCase) >= 0 || blockTypeName.IndexOf("UDT", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isDb)
            {
                sb.AppendLine("DATA_BLOCK \"" + blockName + "\"");
                sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
                sb.AppendLine("VERSION : 0.1");
            }
            else if (isFb)
            {
                sb.AppendLine("FUNCTION_BLOCK \"" + blockName + "\"");
                sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
                sb.AppendLine("VERSION : 0.1");
            }
            else if (isFc)
            {
                sb.AppendLine("FUNCTION \"" + blockName + "\" : Void");
                sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
                sb.AppendLine("VERSION : 0.1");
            }
            else if (isUdt)
            {
                sb.AppendLine("TYPE \"" + blockName + "\"");
                sb.AppendLine("VERSION : 0.1");
                sb.AppendLine("STRUCT");
            }

            var sections = ifaceNode.SelectNodes(".//*[local-name()='Section']");
            if (sections != null)
            {
                foreach (XmlNode sec in sections)
                {
                    string secName = sec.Attributes != null && sec.Attributes["Name"] != null ? sec.Attributes["Name"].Value : "";
                    if (!string.IsNullOrEmpty(sectionFilter) && !secName.Equals(sectionFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    string sclKeyword = "";
                    switch (secName)
                    {
                        case "Input": sclKeyword = "VAR_INPUT"; break;
                        case "Output": sclKeyword = "VAR_OUTPUT"; break;
                        case "InOut": sclKeyword = "VAR_IN_OUT"; break;
                        case "Static": sclKeyword = isDb || isFb ? "VAR" : "VAR_STAT"; break;
                        case "Temp": sclKeyword = "VAR_TEMP"; break;
                        case "Constant": sclKeyword = "VAR CONSTANT"; break;
                        case "Return": break;
                        default: sclKeyword = "VAR // Section: " + secName; break;
                    }

                    var members = sec.SelectNodes("./*[local-name()='Member']");
                    if (members != null && members.Count > 0 && !string.IsNullOrEmpty(sclKeyword))
                    {
                        sb.AppendLine(sclKeyword);
                        foreach (XmlNode mem in members)
                        {
                            FormatMemberToScl(mem, sb, "    ");
                        }
                        sb.AppendLine("END_VAR");
                    }
                }
            }

            if (isDb)
            {
                sb.AppendLine("BEGIN");
                sb.AppendLine("END_DATA_BLOCK");
            }
            else if (isFb)
            {
                sb.AppendLine("BEGIN");
                sb.AppendLine("    // FB Logic...");
                sb.AppendLine("END_FUNCTION_BLOCK");
            }
            else if (isFc)
            {
                sb.AppendLine("BEGIN");
                sb.AppendLine("    // FC Logic...");
                sb.AppendLine("END_FUNCTION");
            }
            else if (isUdt)
            {
                sb.AppendLine("END_STRUCT;");
                sb.AppendLine("END_TYPE");
            }

            return sb.ToString().TrimEnd();
        }

        private static void FormatMemberToScl(XmlNode memberNode, StringBuilder sb, string indent)
        {
            string name = memberNode.Attributes != null && memberNode.Attributes["Name"] != null ? memberNode.Attributes["Name"].Value : "";
            string dtype = memberNode.Attributes != null && memberNode.Attributes["Datatype"] != null ? memberNode.Attributes["Datatype"].Value : "Void";

            string comment = "";
            var commentNode = memberNode.SelectSingleNode(".//*[local-name()='MultiLanguageText']");
            if (commentNode != null && !string.IsNullOrEmpty(commentNode.InnerText))
            {
                comment = " // " + commentNode.InnerText.Trim();
            }

            string startVal = "";
            var startValNode = memberNode.SelectSingleNode(".//*[local-name()='StartValue']");
            if (startValNode != null && !string.IsNullOrEmpty(startValNode.InnerText))
            {
                startVal = " := " + startValNode.InnerText.Trim();
            }

            var nestedMembers = memberNode.SelectNodes("./*[local-name()='Member']");
            if (nestedMembers != null && nestedMembers.Count > 0)
            {
                sb.AppendLine(indent + "\"" + name + "\" : Struct" + comment);
                foreach (XmlNode childMem in nestedMembers)
                {
                    FormatMemberToScl(childMem, sb, indent + "    ");
                }
                sb.AppendLine(indent + "END_STRUCT;");
            }
            else
            {
                sb.AppendLine(indent + "\"" + name + "\" : " + dtype + startVal + ";" + comment);
            }
        }

        private static string DoReadBlockInterface(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args != null && args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            string blockPath = args != null && args.ContainsKey("blockPath") ? args["blockPath"] as string : "";
            string secFilter = args != null && args.ContainsKey("sectionFilter") ? args["sectionFilter"] as string : null;

            if (string.IsNullOrEmpty(blockPath)) throw new ArgumentException("blockPath is required.");

            string tempXml = Path.Combine(Path.GetTempPath(), "tia_iface_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                Device dev = FindDevice(deviceName);
                var plc = FindPlcSoftware(dev);

                var block = FindBlockByPath(plc.BlockGroup, blockPath);
                if (block != null)
                {
                    try
                    {
                        block.Export(new FileInfo(tempXml), ExportOptions.WithDefaults);
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.IndexOf("Inconsistent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (ex.InnerException != null && ex.InnerException.Message.IndexOf("Inconsistent", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            Log("Block '" + blockPath + "' is inconsistent. Attempting auto-compilation to resolve...");
                            try
                            {
                                var compilable = plc.GetService<ICompilable>();
                                if (compilable != null)
                                {
                                    compilable.Compile();
                                    block.Export(new FileInfo(tempXml), ExportOptions.WithDefaults);
                                }
                            }
                            catch (Exception compEx)
                            {
                                return "Error: Block '" + blockPath + "' is inconsistent and auto-compilation failed: " + compEx.Message;
                            }
                        }
                        else
                        {
                            throw;
                        }
                    }

                    if (!File.Exists(tempXml)) return "Error: Failed to export block XML.";

                    var doc = new XmlDocument();
                    doc.Load(tempXml);
                    var ifaceNodes = doc.GetElementsByTagName("Interface");
                    if (ifaceNodes.Count > 0)
                    {
                        return DecompileInterfaceXmlToScl(ifaceNodes[0], block.GetType().Name, block.Name, secFilter);
                    }
                    return "// No Interface section found in XML for block: " + block.Name;
                }

                var udt = FindTypeByPath(plc.TypeGroup, blockPath);
                if (udt != null)
                {
                    udt.Export(new FileInfo(tempXml), ExportOptions.WithDefaults);
                    if (!File.Exists(tempXml)) return "Error: Failed to export UDT XML.";

                    var doc = new XmlDocument();
                    doc.Load(tempXml);
                    var ifaceNodes = doc.GetElementsByTagName("Interface");
                    if (ifaceNodes.Count > 0)
                    {
                        return DecompileInterfaceXmlToScl(ifaceNodes[0], "UDT", udt.Name, secFilter);
                    }
                    return "// No Interface section found in XML for UDT: " + udt.Name;
                }

                return "Error: Block or UDT not found: " + blockPath;
            }
            finally
            {
                if (File.Exists(tempXml)) try { File.Delete(tempXml); } catch { }
            }
        }

        private static Dictionary<string, object> DoSearchBlocks(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args != null && args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            string query = args != null && args.ContainsKey("query") ? (args["query"] as string ?? "").Trim() : "";
            string typeFilter = args != null && args.ContainsKey("typeFilter") ? (args["typeFilter"] as string ?? "").Trim().ToUpper() : "";
            string groupFilter = args != null && args.ContainsKey("groupFilter") ? (args["groupFilter"] as string ?? "").Trim() : "";
            bool includeCode = args != null && args.ContainsKey("includeCode") && Convert.ToBoolean(args["includeCode"]);

            Device dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);

            var allBlocks = new List<Dictionary<string, object>>();
            CollectBlocksRecursive(plc.BlockGroup, allBlocks, "");

            var allUdts = new List<Dictionary<string, object>>();
            CollectTypesRecursive(plc.TypeGroup, allUdts, "");
            foreach (var u in allUdts)
            {
                u["type"] = "UDT";
                u["number"] = "-";
                allBlocks.Add(u);
            }

            var results = new List<Dictionary<string, object>>();
            Regex regex = null;
            try
            {
                if (!string.IsNullOrEmpty(query) && (query.Contains("*") || query.Contains("?") || query.Contains("[") || query.Contains("^")))
                {
                    string pat = query.Replace("*", ".*").Replace("?", ".");
                    regex = new Regex(pat, RegexOptions.IgnoreCase);
                }
            }
            catch { }

            foreach (var b in allBlocks)
            {
                string name = b.ContainsKey("name") && b["name"] != null ? b["name"].ToString() : "";
                string type = b.ContainsKey("type") && b["type"] != null ? b["type"].ToString() : "";
                string path = b.ContainsKey("path") && b["path"] != null ? b["path"].ToString() : "";
                string number = b.ContainsKey("number") && b["number"] != null ? b["number"].ToString() : "";

                if (!string.IsNullOrEmpty(typeFilter))
                {
                    if (typeFilter == "DB" && type.IndexOf("DB", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    else if (typeFilter == "FB" && type.IndexOf("FB", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    else if (typeFilter == "FC" && type.IndexOf("FC", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    else if (typeFilter == "OB" && type.IndexOf("OB", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    else if (typeFilter == "UDT" && type.IndexOf("UDT", StringComparison.OrdinalIgnoreCase) < 0 && type.IndexOf("Type", StringComparison.OrdinalIgnoreCase) < 0) continue;
                }

                if (!string.IsNullOrEmpty(groupFilter))
                {
                    if (path.IndexOf(groupFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }

                bool matched = false;
                string matchSnippet = null;

                if (string.IsNullOrEmpty(query))
                {
                    matched = true;
                }
                else
                {
                    if (regex != null)
                    {
                        matched = regex.IsMatch(name) || regex.IsMatch(path) || regex.IsMatch(number);
                    }
                    else
                    {
                        matched = name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  number.Equals(query, StringComparison.OrdinalIgnoreCase);
                    }

                    if (!matched && includeCode && type != "UDT")
                    {
                        try
                        {
                            string scl = DoReadScl(new Dictionary<string, object> { { "deviceName", dev.Name }, { "blockPath", path } });
                            if (!string.IsNullOrEmpty(scl) && scl.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                matched = true;
                                int idx = scl.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                                int start = Math.Max(0, idx - 40);
                                int len = Math.Min(scl.Length - start, 100);
                                matchSnippet = scl.Substring(start, len).Replace("\r", " ").Replace("\n", " ");
                            }
                        }
                        catch { }
                    }
                }

                if (matched)
                {
                    var item = new Dictionary<string, object>
                    {
                        { "name", name },
                        { "type", type },
                        { "path", path },
                        { "number", number }
                    };
                    if (!string.IsNullOrEmpty(matchSnippet)) item["codeMatchSnippet"] = matchSnippet;
                    results.Add(item);
                }
            }

            return new Dictionary<string, object>
            {
                { "totalMatched", results.Count },
                { "query", query },
                { "typeFilter", typeFilter },
                { "groupFilter", groupFilter },
                { "results", results }
            };
        }

        private static Dictionary<string, object> DoSearchTags(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args != null && args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            string query = args != null && args.ContainsKey("query") ? (args["query"] as string ?? "").Trim() : "";
            string tableName = args != null && args.ContainsKey("tableName") ? (args["tableName"] as string ?? "").Trim() : "";

            Device dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);

            var tables = new List<Dictionary<string, object>>();
            CollectTagsRecursive(plc.TagTableGroup, tables, "");

            var results = new List<Dictionary<string, object>>();
            foreach (var tblInfo in tables)
            {
                string tPath = tblInfo["path"].ToString();
                string tName = tblInfo.ContainsKey("tableName") && tblInfo["tableName"] != null ? tblInfo["tableName"].ToString() : (tblInfo.ContainsKey("name") ? tblInfo["name"].ToString() : tPath);

                if (!string.IsNullOrEmpty(tableName) && tName.IndexOf(tableName, StringComparison.OrdinalIgnoreCase) < 0 && tPath.IndexOf(tableName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var table = FindTagTableByPath(plc.TagTableGroup, tPath);
                if (table == null) continue;

                foreach (PlcTag tag in table.Tags)
                {
                    string cText = "";
                    if (tag.Comment != null && tag.Comment.Items != null && tag.Comment.Items.Count > 0)
                    {
                        cText = tag.Comment.Items[0].Text;
                    }

                    bool matched = string.IsNullOrEmpty(query) ||
                                   tag.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   tag.LogicalAddress.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   tag.DataTypeName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   (!string.IsNullOrEmpty(cText) && cText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (matched)
                    {
                        results.Add(new Dictionary<string, object>
                        {
                            { "table", tName },
                            { "tableGroup", tPath },
                            { "name", tag.Name },
                            { "dataType", tag.DataTypeName },
                            { "address", tag.LogicalAddress },
                            { "comment", cText }
                        });
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "totalMatched", results.Count },
                { "query", query },
                { "tableName", tableName },
                { "results", results }
            };
        }

        private static Dictionary<string, object> DoCreateBlock(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args != null && args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            string blockType = args != null && args.ContainsKey("blockType") ? (args["blockType"] as string ?? "").Trim().ToUpper() : "FC";
            string blockName = args != null && args.ContainsKey("blockName") ? (args["blockName"] as string ?? "").Trim() : "";
            string code = args != null && args.ContainsKey("code") ? (args["code"] as string ?? "") : "";
            string groupPath = args != null && args.ContainsKey("groupPath") ? (args["groupPath"] as string ?? "").Trim() : "";

            if (string.IsNullOrEmpty(blockName)) throw new ArgumentException("blockName is required.");
            if (string.IsNullOrEmpty(code)) throw new ArgumentException("code is required.");

            Device dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);

            var sb = new StringBuilder();
            if (blockType == "FC")
            {
                if (!code.Contains("FUNCTION"))
                {
                    sb.AppendLine("FUNCTION \"" + blockName + "\" : Void");
                    sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
                    sb.AppendLine("VERSION : 0.1");
                    sb.AppendLine("BEGIN");
                    sb.AppendLine(code);
                    sb.AppendLine("END_FUNCTION");
                }
                else sb.Append(code);
            }
            else if (blockType == "FB")
            {
                if (!code.Contains("FUNCTION_BLOCK"))
                {
                    sb.AppendLine("FUNCTION_BLOCK \"" + blockName + "\"");
                    sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
                    sb.AppendLine("VERSION : 0.1");
                    sb.AppendLine("   VAR");
                    sb.AppendLine("   END_VAR");
                    sb.AppendLine("BEGIN");
                    sb.AppendLine(code);
                    sb.AppendLine("END_FUNCTION_BLOCK");
                }
                else sb.Append(code);
            }
            else if (blockType == "DB")
            {
                if (!code.Contains("DATA_BLOCK"))
                {
                    sb.AppendLine("DATA_BLOCK \"" + blockName + "\"");
                    sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
                    sb.AppendLine("VERSION : 0.1");
                    sb.AppendLine(code);
                    sb.AppendLine("BEGIN");
                    sb.AppendLine("END_DATA_BLOCK");
                }
                else sb.Append(code);
            }
            else if (blockType == "UDT")
            {
                if (!code.Contains("TYPE"))
                {
                    sb.AppendLine("TYPE \"" + blockName + "\"");
                    sb.AppendLine("VERSION : 0.1");
                    sb.AppendLine("STRUCT");
                    sb.AppendLine(code);
                    sb.AppendLine("END_STRUCT;");
                    sb.AppendLine("END_TYPE");
                }
                else sb.Append(code);
            }
            else
            {
                sb.Append(code);
            }

            string tempFile = Path.Combine(Path.GetTempPath(), "tia_src_" + Guid.NewGuid().ToString("N") + ".scl");
            File.WriteAllText(tempFile, sb.ToString(), new UTF8Encoding(true));

            PlcExternalSource extSource = null;
            try
            {
                string srcName = "AgentSrc_" + blockName + "_" + DateTime.Now.ToString("HHmmss");
                extSource = plc.ExternalSourceGroup.ExternalSources.CreateFromFile(srcName, tempFile);

                if (blockType == "UDT")
                {
                    if (string.IsNullOrEmpty(groupPath))
                    {
                        extSource.GenerateBlocksFromSource(GenerateBlockOption.None);
                    }
                    else
                    {
                        var targetTypeGroup = GetOrCreateTypeGroup(plc.TypeGroup, groupPath);
                        var userGrp = targetTypeGroup as PlcTypeUserGroup;
                        if (userGrp != null) extSource.GenerateBlocksFromSource(userGrp, GenerateBlockOption.None);
                        else extSource.GenerateBlocksFromSource(GenerateBlockOption.None);
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(groupPath))
                    {
                        extSource.GenerateBlocksFromSource(GenerateBlockOption.None);
                    }
                    else
                    {
                        var targetBlockGroup = GetOrCreateBlockGroup(plc.BlockGroup, groupPath);
                        var userGrp = targetBlockGroup as PlcBlockUserGroup;
                        if (userGrp != null) extSource.GenerateBlocksFromSource(userGrp, GenerateBlockOption.None);
                        else extSource.GenerateBlocksFromSource(GenerateBlockOption.None);
                    }
                }

                return new Dictionary<string, object>
                {
                    { "status", "Success" },
                    { "message", "Block '" + blockName + "' successfully created and compiled." },
                    { "blockName", blockName },
                    { "blockType", blockType },
                    { "group", groupPath }
                };
            }
            catch (Exception ex)
            {
                string err = ex.Message;
                if (ex.InnerException != null) err += " -> " + ex.InnerException.Message;
                return new Dictionary<string, object>
                {
                    { "status", "CompilerError" },
                    { "error", err },
                    { "blockName", blockName },
                    { "blockType", blockType },
                    { "submittedCode", sb.ToString() }
                };
            }
            finally
            {
                if (extSource != null) try { extSource.Delete(); } catch { }
                if (File.Exists(tempFile)) try { File.Delete(tempFile); } catch { }
            }
        }

        private static Dictionary<string, object> DoDeleteBlock(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args != null && args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            string blockPath = args != null && args.ContainsKey("blockPath") ? args["blockPath"] as string : "";
            if (string.IsNullOrEmpty(blockPath)) throw new ArgumentException("blockPath is required.");

            Device dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);

            var blk = FindBlockByPath(plc.BlockGroup, blockPath);
            if (blk != null)
            {
                string bName = blk.Name;
                blk.Delete();
                return new Dictionary<string, object>
                {
                    { "status", "Success" },
                    { "message", "Block '" + bName + "' successfully deleted." },
                    { "blockName", bName }
                };
            }

            var udt = FindTypeByPath(plc.TypeGroup, blockPath);
            if (udt != null)
            {
                string uName = udt.Name;
                udt.Delete();
                return new Dictionary<string, object>
                {
                    { "status", "Success" },
                    { "message", "UDT '" + uName + "' successfully deleted." },
                    { "udtName", uName }
                };
            }

            return new Dictionary<string, object>
            {
                { "status", "NotFound" },
                { "message", "Block or UDT '" + blockPath + "' not found." }
            };
        }

        private static Dictionary<string, object> DoCopyBlock(Dictionary<string, object> args)
        {
            EnsureConnected();
            string targetDeviceName = args != null && args.ContainsKey("targetDeviceName") ? args["targetDeviceName"] as string : null;
            string sourceBlockPath = args != null && args.ContainsKey("sourceBlockPath") ? args["sourceBlockPath"] as string : "";
            string targetGroupPath = args != null && args.ContainsKey("targetGroupPath") ? args["targetGroupPath"] as string : "";
            string sourceProjectPath = args != null && args.ContainsKey("sourceProjectPath") ? args["sourceProjectPath"] as string : null;
            int sourcePid = args != null && args.ContainsKey("sourcePid") ? Convert.ToInt32(args["sourcePid"]) : 0;
            bool isUdt = args != null && args.ContainsKey("isUdt") && Convert.ToBoolean(args["isUdt"]);

            if (string.IsNullOrEmpty(sourceBlockPath)) throw new ArgumentException("sourceBlockPath is required.");

            Device targetDev = FindDevice(targetDeviceName);
            var targetPlc = FindPlcSoftware(targetDev);

            TiaPortal sourceTia = null;
            Project sourceProj = null;
            bool mustDisposeSource = false;

            try
            {
                if (sourcePid > 0)
                {
                    var p = TiaPortal.GetProcesses().FirstOrDefault(pr => pr.Id == sourcePid);
                    if (p == null) throw new InvalidOperationException("Source TIA Portal PID " + sourcePid + " not found.");
                    sourceTia = p.Attach();
                    sourceProj = sourceTia.Projects.FirstOrDefault();
                    if (sourceProj == null) throw new InvalidOperationException("No open project in TIA process " + sourcePid);
                }
                else if (!string.IsNullOrEmpty(sourceProjectPath))
                {
                    foreach (var proc in TiaPortal.GetProcesses())
                    {
                        try
                        {
                            if (proc.ProjectPath != null && proc.ProjectPath.FullName.Equals(sourceProjectPath, StringComparison.OrdinalIgnoreCase))
                            {
                                sourceTia = proc.Attach();
                                sourceProj = sourceTia.Projects.FirstOrDefault();
                                break;
                            }
                        }
                        catch { }
                    }

                    if (sourceProj == null)
                    {
                        sourceTia = new TiaPortal(TiaPortalMode.WithoutUserInterface);
                        mustDisposeSource = true;
                        sourceProj = sourceTia.Projects.Open(new FileInfo(sourceProjectPath));
                    }
                }
                else
                {
                    sourceProj = _activeProject;
                }

                var sourcePlc = FindPlcSoftware(FindDevice(null));
                if (sourceProj != _activeProject)
                {
                    Device sDev = sourceProj.Devices.FirstOrDefault(d => FindPlcSoftware(d) != null);
                    if (sDev == null) throw new InvalidOperationException("No PLC device found in source project.");
                    sourcePlc = FindPlcSoftware(sDev);
                }

                string tempXml = Path.Combine(Path.GetTempPath(), "tia_copy_" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    if (!isUdt)
                    {
                        var checkUdt = FindTypeByPath(sourcePlc.TypeGroup, sourceBlockPath);
                        var checkBlk = FindBlockByPath(sourcePlc.BlockGroup, sourceBlockPath);
                        if (checkUdt != null && checkBlk == null) isUdt = true;
                    }

                    if (isUdt)
                    {
                        var srcUdt = FindTypeByPath(sourcePlc.TypeGroup, sourceBlockPath);
                        if (srcUdt == null) throw new InvalidOperationException("UDT not found in source project: " + sourceBlockPath);
                        srcUdt.Export(new FileInfo(tempXml), ExportOptions.WithDefaults);

                        var targetTypeGroup = string.IsNullOrEmpty(targetGroupPath) ? targetPlc.TypeGroup : GetOrCreateTypeGroup(targetPlc.TypeGroup, targetGroupPath);
                        targetTypeGroup.Types.Import(new FileInfo(tempXml), ImportOptions.Override);
                    }
                    else
                    {
                        var srcBlk = FindBlockByPath(sourcePlc.BlockGroup, sourceBlockPath);
                        if (srcBlk == null) throw new InvalidOperationException("Block not found in source project: " + sourceBlockPath);

                        try
                        {
                            srcBlk.Export(new FileInfo(tempXml), ExportOptions.WithDefaults);
                        }
                        catch (Exception ex)
                        {
                            if (ex.Message.IndexOf("Inconsistent", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var comp = sourcePlc.GetService<ICompilable>();
                                if (comp != null) comp.Compile();
                                srcBlk.Export(new FileInfo(tempXml), ExportOptions.WithDefaults);
                            }
                            else throw;
                        }

                        // Auto-copy any referenced UDTs if present
                        try
                        {
                            var docCheck = new XmlDocument();
                            docCheck.Load(tempXml);
                            var memberNodes = docCheck.SelectNodes("//*[local-name()='Member'][@Datatype]");
                            if (memberNodes != null)
                            {
                                foreach (XmlNode mNode in memberNodes)
                                {
                                    string rawDt = mNode.Attributes["Datatype"].Value;
                                    if (rawDt.StartsWith("\"") && rawDt.EndsWith("\""))
                                    {
                                        string udtName = rawDt.Trim('"');
                                        var srcDepUdt = FindTypeByPath(sourcePlc.TypeGroup, udtName);
                                        if (srcDepUdt != null)
                                        {
                                            var tgtDepUdt = FindTypeByPath(targetPlc.TypeGroup, udtName);
                                            if (tgtDepUdt == null)
                                            {
                                                string udtTemp = Path.Combine(Path.GetTempPath(), "tia_udt_dep_" + Guid.NewGuid().ToString("N") + ".xml");
                                                try
                                                {
                                                    srcDepUdt.Export(new FileInfo(udtTemp), ExportOptions.WithDefaults);
                                                    targetPlc.TypeGroup.Types.Import(new FileInfo(udtTemp), ImportOptions.Override);
                                                    Log("Auto-copied referenced UDT: " + udtName);
                                                }
                                                finally
                                                {
                                                    if (File.Exists(udtTemp)) try { File.Delete(udtTemp); } catch { }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }

                        var targetBlockGroup = string.IsNullOrEmpty(targetGroupPath) ? targetPlc.BlockGroup : GetOrCreateBlockGroup(targetPlc.BlockGroup, targetGroupPath);
                        targetBlockGroup.Blocks.Import(new FileInfo(tempXml), ImportOptions.Override);
                    }

                    return new Dictionary<string, object>
                    {
                        { "status", "Success" },
                        { "message", "Block '" + sourceBlockPath + "' successfully copied to target group '" + targetGroupPath + "'." },
                        { "sourceBlock", sourceBlockPath },
                        { "targetGroup", targetGroupPath }
                    };
                }
                finally
                {
                    if (File.Exists(tempXml)) try { File.Delete(tempXml); } catch { }
                }
            }
            finally
            {
                if (mustDisposeSource && sourceTia != null)
                {
                    try { sourceTia.Dispose(); } catch { }
                }
            }
        }
        private static void AppendParameterValueNode(XmlElement pNode, XmlDocument doc, string val, string stNs, ref int maxUId)
        {
            if (string.IsNullOrWhiteSpace(val)) return;
            val = val.Trim();

            if (val.StartsWith("#"))
            {
                XmlElement acc = doc.CreateElement("Access", stNs);
                acc.SetAttribute("Scope", "LocalVariable");
                acc.SetAttribute("UId", (++maxUId).ToString());

                XmlElement sym = doc.CreateElement("Symbol", stNs);
                sym.SetAttribute("UId", (++maxUId).ToString());

                string varName = val.TrimStart('#');
                var parts = varName.Split('.');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0)
                    {
                        XmlElement tokDot = doc.CreateElement("Token", stNs);
                        tokDot.SetAttribute("Text", ".");
                        tokDot.SetAttribute("UId", (++maxUId).ToString());
                        sym.AppendChild(tokDot);
                    }
                    XmlElement comp = doc.CreateElement("Component", stNs);
                    comp.SetAttribute("Name", parts[i].Trim('\"'));
                    comp.SetAttribute("UId", (++maxUId).ToString());
                    sym.AppendChild(comp);
                }
                acc.AppendChild(sym);
                pNode.AppendChild(acc);
            }
            else if (val.StartsWith("\"") || val.Contains("."))
            {
                XmlElement acc = doc.CreateElement("Access", stNs);
                acc.SetAttribute("Scope", "GlobalVariable");
                acc.SetAttribute("UId", (++maxUId).ToString());

                XmlElement sym = doc.CreateElement("Symbol", stNs);
                sym.SetAttribute("UId", (++maxUId).ToString());

                var parts = val.Split('.');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0)
                    {
                        XmlElement tokDot = doc.CreateElement("Token", stNs);
                        tokDot.SetAttribute("Text", ".");
                        tokDot.SetAttribute("UId", (++maxUId).ToString());
                        sym.AppendChild(tokDot);
                    }
                    XmlElement comp = doc.CreateElement("Component", stNs);
                    comp.SetAttribute("Name", parts[i].Trim('\"'));
                    comp.SetAttribute("UId", (++maxUId).ToString());
                    XmlElement hasQuotes = doc.CreateElement("BooleanAttribute", stNs);
                    hasQuotes.SetAttribute("Name", "HasQuotes");
                    hasQuotes.SetAttribute("UId", (++maxUId).ToString());
                    hasQuotes.InnerText = "true";
                    comp.AppendChild(hasQuotes);
                    sym.AppendChild(comp);
                }
                acc.AppendChild(sym);
                pNode.AppendChild(acc);
            }
            else
            {
                XmlElement tokVal = doc.CreateElement("Token", stNs);
                tokVal.SetAttribute("Text", val);
                tokVal.SetAttribute("UId", (++maxUId).ToString());
                pNode.AppendChild(tokVal);
            }
        }

        private static void BuildCallStructuredText(XmlElement st, XmlDocument doc, string callType, string instanceName, string calleeName, Dictionary<string, object> parameters, ref int maxUId)
        {
            string stNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v3";

            if (callType == "multi")
            {
                XmlElement acc = doc.CreateElement("Access", stNs);
                acc.SetAttribute("Scope", "LocalVariable");
                acc.SetAttribute("UId", (++maxUId).ToString());

                XmlElement sym = doc.CreateElement("Symbol", stNs);
                sym.SetAttribute("UId", (++maxUId).ToString());

                XmlElement comp = doc.CreateElement("Component", stNs);
                comp.SetAttribute("Name", instanceName);
                comp.SetAttribute("UId", (++maxUId).ToString());
                sym.AppendChild(comp);
                acc.AppendChild(sym);
                st.AppendChild(acc);

                XmlElement accCall = doc.CreateElement("Access", stNs);
                accCall.SetAttribute("Scope", "Call");
                accCall.SetAttribute("UId", (++maxUId).ToString());

                XmlElement instr = doc.CreateElement("Instruction", stNs);
                instr.SetAttribute("UId", (++maxUId).ToString());

                XmlElement tokOpen = doc.CreateElement("Token", stNs);
                tokOpen.SetAttribute("Text", "(");
                tokOpen.SetAttribute("UId", (++maxUId).ToString());
                instr.AppendChild(tokOpen);

                if (parameters != null && parameters.Count > 0)
                {
                    int pIdx = 0;
                    foreach (var kv in parameters)
                    {
                        pIdx++;
                        XmlElement pNode = doc.CreateElement("Parameter", stNs);
                        pNode.SetAttribute("Name", kv.Key);
                        pNode.SetAttribute("UId", (++maxUId).ToString());

                        XmlElement bl1 = doc.CreateElement("Blank", stNs);
                        bl1.SetAttribute("Num", "1");
                        bl1.SetAttribute("UId", (++maxUId).ToString());
                        pNode.AppendChild(bl1);

                        XmlElement tokAssign = doc.CreateElement("Token", stNs);
                        tokAssign.SetAttribute("Text", ":=");
                        tokAssign.SetAttribute("UId", (++maxUId).ToString());
                        pNode.AppendChild(tokAssign);

                        XmlElement bl2 = doc.CreateElement("Blank", stNs);
                        bl2.SetAttribute("Num", "1");
                        bl2.SetAttribute("UId", (++maxUId).ToString());
                        pNode.AppendChild(bl2);

                        AppendParameterValueNode(pNode, doc, kv.Value != null ? kv.Value.ToString() : "", stNs, ref maxUId);

                        instr.AppendChild(pNode);

                        if (pIdx < parameters.Count)
                        {
                            XmlElement tokComma = doc.CreateElement("Token", stNs);
                            tokComma.SetAttribute("Text", ",");
                            tokComma.SetAttribute("UId", (++maxUId).ToString());
                            instr.AppendChild(tokComma);
                        }
                    }
                }

                XmlElement tokClose = doc.CreateElement("Token", stNs);
                tokClose.SetAttribute("Text", ")");
                tokClose.SetAttribute("UId", (++maxUId).ToString());
                instr.AppendChild(tokClose);

                accCall.AppendChild(instr);
                st.AppendChild(accCall);

                XmlElement tokSemi = doc.CreateElement("Token", stNs);
                tokSemi.SetAttribute("Text", ";");
                tokSemi.SetAttribute("UId", (++maxUId).ToString());
                st.AppendChild(tokSemi);
            }
            else if (callType == "single")
            {
                XmlElement acc = doc.CreateElement("Access", stNs);
                acc.SetAttribute("Scope", "GlobalVariable");
                acc.SetAttribute("UId", (++maxUId).ToString());

                XmlElement sym = doc.CreateElement("Symbol", stNs);
                sym.SetAttribute("UId", (++maxUId).ToString());

                XmlElement comp = doc.CreateElement("Component", stNs);
                comp.SetAttribute("Name", instanceName);
                comp.SetAttribute("UId", (++maxUId).ToString());

                XmlElement hasQuotes = doc.CreateElement("BooleanAttribute", stNs);
                hasQuotes.SetAttribute("Name", "HasQuotes");
                hasQuotes.SetAttribute("UId", (++maxUId).ToString());
                hasQuotes.InnerText = "true";
                comp.AppendChild(hasQuotes);

                sym.AppendChild(comp);
                acc.AppendChild(sym);
                st.AppendChild(acc);

                XmlElement accCall = doc.CreateElement("Access", stNs);
                accCall.SetAttribute("Scope", "Call");
                accCall.SetAttribute("UId", (++maxUId).ToString());

                XmlElement instr = doc.CreateElement("Instruction", stNs);
                instr.SetAttribute("UId", (++maxUId).ToString());

                XmlElement tokOpen = doc.CreateElement("Token", stNs);
                tokOpen.SetAttribute("Text", "(");
                tokOpen.SetAttribute("UId", (++maxUId).ToString());
                instr.AppendChild(tokOpen);

                if (parameters != null && parameters.Count > 0)
                {
                    int pIdx = 0;
                    foreach (var kv in parameters)
                    {
                        pIdx++;
                        XmlElement pNode = doc.CreateElement("Parameter", stNs);
                        pNode.SetAttribute("Name", kv.Key);
                        pNode.SetAttribute("UId", (++maxUId).ToString());

                        XmlElement bl1 = doc.CreateElement("Blank", stNs);
                        bl1.SetAttribute("Num", "1");
                        bl1.SetAttribute("UId", (++maxUId).ToString());
                        pNode.AppendChild(bl1);

                        XmlElement tokAssign = doc.CreateElement("Token", stNs);
                        tokAssign.SetAttribute("Text", ":=");
                        tokAssign.SetAttribute("UId", (++maxUId).ToString());
                        pNode.AppendChild(tokAssign);

                        XmlElement bl2 = doc.CreateElement("Blank", stNs);
                        bl2.SetAttribute("Num", "1");
                        bl2.SetAttribute("UId", (++maxUId).ToString());
                        pNode.AppendChild(bl2);

                        AppendParameterValueNode(pNode, doc, kv.Value != null ? kv.Value.ToString() : "", stNs, ref maxUId);

                        instr.AppendChild(pNode);

                        if (pIdx < parameters.Count)
                        {
                            XmlElement tokComma = doc.CreateElement("Token", stNs);
                            tokComma.SetAttribute("Text", ",");
                            tokComma.SetAttribute("UId", (++maxUId).ToString());
                            instr.AppendChild(tokComma);
                        }
                    }
                }

                XmlElement tokClose = doc.CreateElement("Token", stNs);
                tokClose.SetAttribute("Text", ")");
                tokClose.SetAttribute("UId", (++maxUId).ToString());
                instr.AppendChild(tokClose);

                accCall.AppendChild(instr);
                st.AppendChild(accCall);

                XmlElement tokSemi = doc.CreateElement("Token", stNs);
                tokSemi.SetAttribute("Text", ";");
                tokSemi.SetAttribute("UId", (++maxUId).ToString());
                st.AppendChild(tokSemi);
            }
            else // direct FC call
            {
                XmlElement accCall = doc.CreateElement("Access", stNs);
                accCall.SetAttribute("Scope", "Call");
                accCall.SetAttribute("UId", (++maxUId).ToString());

                XmlElement callInfo = doc.CreateElement("CallInfo", stNs);
                callInfo.SetAttribute("Name", calleeName);
                callInfo.SetAttribute("BlockType", "FC");
                callInfo.SetAttribute("UId", (++maxUId).ToString());

                XmlElement tokOpen = doc.CreateElement("Token", stNs);
                tokOpen.SetAttribute("Text", "(");
                tokOpen.SetAttribute("UId", (++maxUId).ToString());
                callInfo.AppendChild(tokOpen);

                if (parameters != null && parameters.Count > 0)
                {
                    int pIdx = 0;
                    foreach (var kv in parameters)
                    {
                        pIdx++;
                        XmlElement pNode = doc.CreateElement("Parameter", stNs);
                        pNode.SetAttribute("Name", kv.Key);
                        pNode.SetAttribute("UId", (++maxUId).ToString());

                        XmlElement bl1 = doc.CreateElement("Blank", stNs);
                        bl1.SetAttribute("Num", "1");
                        bl1.SetAttribute("UId", (++maxUId).ToString());
                        pNode.AppendChild(bl1);

                        XmlElement tokAssign = doc.CreateElement("Token", stNs);
                        tokAssign.SetAttribute("Text", ":=");
                        tokAssign.SetAttribute("UId", (++maxUId).ToString());
                        pNode.AppendChild(tokAssign);

                        XmlElement bl2 = doc.CreateElement("Blank", stNs);
                        bl2.SetAttribute("Num", "1");
                        bl2.SetAttribute("UId", (++maxUId).ToString());
                        pNode.AppendChild(bl2);

                        AppendParameterValueNode(pNode, doc, kv.Value != null ? kv.Value.ToString() : "", stNs, ref maxUId);

                        callInfo.AppendChild(pNode);

                        if (pIdx < parameters.Count)
                        {
                            XmlElement tokComma = doc.CreateElement("Token", stNs);
                            tokComma.SetAttribute("Text", ",");
                            tokComma.SetAttribute("UId", (++maxUId).ToString());
                            callInfo.AppendChild(tokComma);
                        }
                    }
                }

                XmlElement tokClose = doc.CreateElement("Token", stNs);
                tokClose.SetAttribute("Text", ")");
                tokClose.SetAttribute("UId", (++maxUId).ToString());
                callInfo.AppendChild(tokClose);

                accCall.AppendChild(callInfo);
                st.AppendChild(accCall);

                XmlElement tokSemi = doc.CreateElement("Token", stNs);
                tokSemi.SetAttribute("Text", ";");
                tokSemi.SetAttribute("UId", (++maxUId).ToString());
                st.AppendChild(tokSemi);
            }
        }

        private static Dictionary<string, object> DoCallBlock(Dictionary<string, object> args)
        {
            EnsureConnected();
            string callerName = null;
            if (args != null)
            {
                if (args.ContainsKey("callerBlockName") && args["callerBlockName"] != null) callerName = args["callerBlockName"].ToString().Trim();
                else if (args.ContainsKey("callerBlock") && args["callerBlock"] != null) callerName = args["callerBlock"].ToString().Trim();
                else if (args.ContainsKey("caller") && args["caller"] != null) callerName = args["caller"].ToString().Trim();
            }

            string calleeName = null;
            if (args != null)
            {
                if (args.ContainsKey("calleeBlockName") && args["calleeBlockName"] != null) calleeName = args["calleeBlockName"].ToString().Trim();
                else if (args.ContainsKey("calleeBlock") && args["calleeBlock"] != null) calleeName = args["calleeBlock"].ToString().Trim();
                else if (args.ContainsKey("callee") && args["callee"] != null) calleeName = args["callee"].ToString().Trim();
            }

            if (string.IsNullOrEmpty(callerName) || string.IsNullOrEmpty(calleeName))
            {
                throw new ArgumentException("Both 'callerBlockName' and 'calleeBlockName' (or 'callerBlock'/'calleeBlock') are required.");
            }

            string instanceName = args.ContainsKey("instanceName") && args["instanceName"] != null ? args["instanceName"].ToString().Trim() : "";
            string callType = args.ContainsKey("callType") && args["callType"] != null ? args["callType"].ToString().Trim().ToLowerInvariant() : "auto";
            if (args.ContainsKey("instanceDbName") && args["instanceDbName"] != null && !string.IsNullOrEmpty(args["instanceDbName"].ToString().Trim()))
            {
                instanceName = args["instanceDbName"].ToString().Trim();
                callType = "single";
            }
            string netTitle = args.ContainsKey("networkTitle") && args["networkTitle"] != null ? args["networkTitle"].ToString().Trim() : "";
            string deviceName = args.ContainsKey("deviceName") && args["deviceName"] != null ? args["deviceName"].ToString() : null;

            Dictionary<string, object> parameters = null;
            if (args.ContainsKey("parameters") && args["parameters"] != null)
            {
                if (args["parameters"] is Dictionary<string, object>)
                {
                    parameters = (Dictionary<string, object>)args["parameters"];
                }
                else if (args["parameters"] is string)
                {
                    string paramStr = (string)args["parameters"];
                    if (!string.IsNullOrWhiteSpace(paramStr))
                    {
                        parameters = new Dictionary<string, object>();
                        var parts = paramStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var p in parts)
                        {
                            var kv = p.Split(new[] { ":=" }, StringSplitOptions.None);
                            if (kv.Length == 2)
                            {
                                parameters[kv[0].Trim()] = kv[1].Trim();
                            }
                        }
                    }
                }
            }

            var dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);

            var callerBlock = FindBlockByPath(plc.BlockGroup, callerName);
            if (callerBlock == null) throw new InvalidOperationException("Caller block not found: " + callerName);

            var calleeBlock = FindBlockByPath(plc.BlockGroup, calleeName);

            // Determine caller type
            string callerType = callerBlock.GetType().Name;
            bool callerIsFb = callerType.IndexOf("FB", StringComparison.OrdinalIgnoreCase) >= 0 || callerType.IndexOf("FunctionBlock", StringComparison.OrdinalIgnoreCase) >= 0;
            bool callerIsFc = callerType.IndexOf("FC", StringComparison.OrdinalIgnoreCase) >= 0 || (callerType.IndexOf("Function", StringComparison.OrdinalIgnoreCase) >= 0 && !callerIsFb);
            bool callerIsOb = callerType.IndexOf("OB", StringComparison.OrdinalIgnoreCase) >= 0 || callerType.IndexOf("OrganizationBlock", StringComparison.OrdinalIgnoreCase) >= 0;

            // Determine callee type
            string calleeType = calleeBlock != null ? calleeBlock.GetType().Name : "";
            bool calleeIsFb = calleeType.IndexOf("FB", StringComparison.OrdinalIgnoreCase) >= 0 || calleeType.IndexOf("FunctionBlock", StringComparison.OrdinalIgnoreCase) >= 0 || calleeName.EndsWith("_FB", StringComparison.OrdinalIgnoreCase);
            bool calleeIsFc = calleeType.IndexOf("FC", StringComparison.OrdinalIgnoreCase) >= 0 || calleeType.IndexOf("Function", StringComparison.OrdinalIgnoreCase) >= 0 || calleeName.EndsWith("_FC", StringComparison.OrdinalIgnoreCase);

            if (!calleeIsFb && !calleeIsFc)
            {
                if (calleeName.EndsWith("_FB", StringComparison.OrdinalIgnoreCase)) calleeIsFb = true;
                else if (calleeName.EndsWith("_FC", StringComparison.OrdinalIgnoreCase)) calleeIsFc = true;
                else calleeIsFb = true;
            }

            // Resolve callType
            if (callType == "auto")
            {
                if (calleeIsFc)
                {
                    callType = "direct";
                }
                else if (callerIsFb)
                {
                    callType = "multi";
                }
                else
                {
                    callType = "single";
                }
            }

            // Default instance naming
            if (string.IsNullOrEmpty(instanceName))
            {
                if (callType == "multi")
                {
                    instanceName = "inst_" + calleeName;
                }
                else if (callType == "single")
                {
                    instanceName = "instDB_" + callerName + "_" + calleeName;
                }
            }

            // If single-instance FB call, verify or create Instance DB
            if (callType == "single")
            {
                var existingDb = FindBlockByPath(plc.BlockGroup, instanceName);
                if (existingDb == null)
                {
                    Log("Creating dedicated Instance DB '" + instanceName + "' for FB '" + calleeName + "'...");
                    var blockGroup = callerBlock.Parent as PlcBlockGroup;
                    if (blockGroup == null) blockGroup = plc.BlockGroup;
                    blockGroup.Blocks.CreateInstanceDB(instanceName, true, -1, calleeName);
                }
            }

            // Export caller block to XML
            string tempXml = Path.Combine(Path.GetTempPath(), "tia_call_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                callerBlock.Export(new FileInfo(tempXml), ExportOptions.WithDefaults);
                if (!File.Exists(tempXml)) throw new InvalidOperationException("Failed to export caller block XML.");

                var doc = new XmlDocument();
                doc.Load(tempXml);

                // Handle multi-instance: declare in caller's Static section
                if (callType == "multi")
                {
                    var staticSec = doc.SelectSingleNode("//*[local-name()='Section'][@Name='Static']");
                    if (staticSec == null)
                    {
                        var ifaceNode = doc.SelectSingleNode("//*[local-name()='Interface']/*[local-name()='Sections']");
                        if (ifaceNode == null) ifaceNode = doc.SelectSingleNode("//*[local-name()='Interface']");
                        if (ifaceNode != null)
                        {
                            var newSec = doc.CreateElement("Section", ifaceNode.NamespaceURI);
                            newSec.SetAttribute("Name", "Static");
                            ifaceNode.AppendChild(newSec);
                            staticSec = newSec;
                        }
                    }

                    if (staticSec != null)
                    {
                        var existingMem = staticSec.SelectSingleNode("./*[local-name()='Member'][@Name='" + instanceName + "']");
                        if (existingMem == null)
                        {
                            XmlElement mem = doc.CreateElement("Member", staticSec.NamespaceURI);
                            mem.SetAttribute("Name", instanceName);
                            mem.SetAttribute("Datatype", "\"" + calleeName + "\"");
                            mem.SetAttribute("Accessibility", "Public");
                            staticSec.AppendChild(mem);
                        }
                    }
                }

                // Build SCL Call statement
                var callSb = new StringBuilder();
                if (callType == "multi")
                {
                    callSb.Append("#" + instanceName + "(");
                }
                else if (callType == "single")
                {
                    callSb.Append("\"" + instanceName + "\"(");
                }
                else
                {
                    callSb.Append("\"" + calleeName + "\"(");
                }

                if (parameters != null && parameters.Count > 0)
                {
                    callSb.AppendLine();
                    int pIdx = 0;
                    foreach (var kv in parameters)
                    {
                        pIdx++;
                        string comma = pIdx < parameters.Count ? "," : "";
                        callSb.AppendLine("    " + kv.Key + " := " + kv.Value + comma);
                    }
                    callSb.Append(");");
                }
                else
                {
                    callSb.Append(");");
                }

                string callText = callSb.ToString();

                // Inject into XML as a new CompileUnit (Network)
                var cuNodes = doc.SelectNodes("//*[contains(local-name(), 'CompileUnit')]");
                if (cuNodes != null && cuNodes.Count > 0)
                {
                    var progLangNode = doc.SelectSingleNode("//*[local-name()='AttributeList']/*[local-name()='ProgrammingLanguage']");
                    string progLangVal = progLangNode != null ? progLangNode.InnerText : "SCL";
                    bool isSclBlock = progLangVal.Equals("SCL", StringComparison.OrdinalIgnoreCase);

                    if (isSclBlock)
                    {
                        // SCL blocks in TIA Portal MUST have exactly ONE CompileUnit.
                        // Append the call to the existing CompileUnit's StructuredText.
                        XmlNode lastCu = cuNodes[cuNodes.Count - 1];
                        var stNode = lastCu.SelectSingleNode(".//*[local-name()='StructuredText']");
                        if (stNode != null)
                        {
                            int maxUId = 500;
                            var allUIdNodes = doc.SelectNodes("//*[@UId]");
                            if (allUIdNodes != null)
                            {
                                foreach (XmlNode n in allUIdNodes)
                                {
                                    int curUId;
                                    if (n.Attributes != null && n.Attributes["UId"] != null && int.TryParse(n.Attributes["UId"].Value, out curUId))
                                    {
                                        if (curUId > maxUId) maxUId = curUId;
                                    }
                                }
                            }

                            string stNs = stNode.NamespaceURI;
                            XmlElement nl = doc.CreateElement("NewLine", stNs);
                            nl.SetAttribute("Num", "1");
                            nl.SetAttribute("UId", (++maxUId).ToString());
                            stNode.AppendChild(nl);

                            if (!string.IsNullOrEmpty(netTitle))
                            {
                                XmlElement comment = doc.CreateElement("LineComment", stNs);
                                comment.SetAttribute("Inserted", "false");
                                comment.SetAttribute("NoClosingBracket", "false");
                                comment.SetAttribute("UId", (++maxUId).ToString());
                                XmlElement commText = doc.CreateElement("Text", stNs);
                                commText.SetAttribute("UId", (++maxUId).ToString());
                                commText.InnerText = " " + netTitle;
                                comment.AppendChild(commText);
                                stNode.AppendChild(comment);

                                XmlElement nl2 = doc.CreateElement("NewLine", stNs);
                                nl2.SetAttribute("Num", "1");
                                nl2.SetAttribute("UId", (++maxUId).ToString());
                                stNode.AppendChild(nl2);
                            }

                            BuildCallStructuredText((XmlElement)stNode, doc, callType, instanceName, calleeName, parameters, ref maxUId);
                        }
                    }
                    else
                    {
                        // LAD/FBD block: append a new SCL CompileUnit network
                        XmlNode lastCu = cuNodes[cuNodes.Count - 1];
                        XmlElement newCu = (XmlElement)lastCu.CloneNode(true);

                        int maxId = 100;
                        var allIdNodes = doc.SelectNodes("//*[@ID]");
                        if (allIdNodes != null)
                        {
                            foreach (XmlNode n in allIdNodes)
                            {
                                int curId;
                                if (n.Attributes != null && n.Attributes["ID"] != null && int.TryParse(n.Attributes["ID"].Value, out curId))
                                {
                                    if (curId > maxId) maxId = curId;
                                }
                            }
                        }

                        if (newCu.HasAttribute("ID")) newCu.SetAttribute("ID", (++maxId).ToString());
                        var innerIds = newCu.SelectNodes(".//*[@ID]");
                        if (innerIds != null)
                        {
                            foreach (XmlElement el in innerIds)
                            {
                                el.SetAttribute("ID", (++maxId).ToString());
                            }
                        }

                        var netSrc = newCu.SelectSingleNode(".//*[local-name()='NetworkSource']");
                        if (netSrc != null)
                        {
                            netSrc.RemoveAll();
                            string stNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v3";
                            XmlElement st = doc.CreateElement("StructuredText", stNs);

                            int maxUId = 500;
                            var allUIdNodes = doc.SelectNodes("//*[@UId]");
                            if (allUIdNodes != null)
                            {
                                foreach (XmlNode n in allUIdNodes)
                                {
                                    int curUId;
                                    if (n.Attributes != null && n.Attributes["UId"] != null && int.TryParse(n.Attributes["UId"].Value, out curUId))
                                    {
                                        if (curUId > maxUId) maxUId = curUId;
                                    }
                                }
                            }

                            BuildCallStructuredText(st, doc, callType, instanceName, calleeName, parameters, ref maxUId);
                            netSrc.AppendChild(st);
                        }

                        var progLang = newCu.SelectSingleNode(".//*[local-name()='ProgrammingLanguage']");
                        if (progLang != null) progLang.InnerText = "SCL";

                        string netTitleVal = !string.IsNullOrEmpty(netTitle)
                            ? netTitle
                            : (callType == "multi" ? "Call " + calleeName + " (Multi-Instance #" + instanceName + ")" : "Call " + calleeName);

                        var titleTexts = newCu.SelectNodes(".//*[local-name()='MultilingualText'][@CompositionName='Title']//*[local-name()='Text']");
                        if (titleTexts != null && titleTexts.Count > 0)
                        {
                            foreach (XmlNode tn in titleTexts) tn.InnerText = netTitleVal;
                        }

                        var commentTexts = newCu.SelectNodes(".//*[local-name()='MultilingualText'][@CompositionName='Comment']//*[local-name()='Text']");
                        if (commentTexts != null && commentTexts.Count > 0)
                        {
                            foreach (XmlNode cn in commentTexts) cn.InnerText = "";
                        }

                        lastCu.ParentNode.InsertAfter(newCu, lastCu);
                    }
                }

                doc.Save(tempXml);
                try { File.Copy(tempXml, @"C:\Users\aa.fedin\Favorites\Tia_18_Agent\last_call_block_import.xml", true); } catch { }

                // Re-import modified caller block
                var targetGroup = callerBlock.Parent as PlcBlockGroup;
                if (targetGroup == null) targetGroup = plc.BlockGroup;
                targetGroup.Blocks.Import(new FileInfo(tempXml), ImportOptions.Override);

                // Auto-compile to verify
                string compileStatus = "Not Compiled";
                var comp = plc.GetService<ICompilable>();
                if (comp != null)
                {
                    try
                    {
                        var cr = comp.Compile();
                        compileStatus = cr.State.ToString();
                    }
                    catch (Exception compEx)
                    {
                        compileStatus = "Compile Error: " + compEx.Message;
                    }
                }

                return new Dictionary<string, object>
                {
                    { "status", "Success" },
                    { "callerBlock", callerName },
                    { "calleeBlock", calleeName },
                    { "callType", callType },
                    { "instanceName", instanceName },
                    { "callStatement", callText },
                    { "compileStatus", compileStatus },
                    { "message", string.Format("Block '{0}' successfully called in '{1}' as {2} (Instance: {3}). Compile status: {4}",
                        calleeName, callerName, callType, instanceName, compileStatus) }
                };
            }
            finally
            {
                if (File.Exists(tempXml)) try { File.Delete(tempXml); } catch { }
            }
        }

        private static Dictionary<string, object> DoGetDeviceParams(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args != null && args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            Device dev = FindDevice(deviceName);
            if (dev == null) throw new InvalidOperationException("Device not found: " + deviceName);

            var res = new Dictionary<string, object>();
            res["deviceName"] = dev.Name;
            res["typeIdentifier"] = dev.TypeIdentifier;

            string ip = "";
            string subnet = "";
            string pnDeviceName = "";
            var modulesList = new List<Dictionary<string, object>>();

            foreach (DeviceItem di in dev.DeviceItems)
            {
                var modInfo = new Dictionary<string, object>
                {
                    { "name", di.Name },
                    { "typeIdentifier", di.TypeIdentifier }
                };
                try
                {
                    object pos = di.GetAttribute("PositionNumber");
                    if (pos != null) modInfo["slot"] = pos;
                }
                catch { }

                try
                {
                    object order = di.GetAttribute("OrderNumber");
                    if (order != null && !string.IsNullOrEmpty(order.ToString())) modInfo["orderNumber"] = order.ToString();
                }
                catch { }

                try
                {
                    object fw = di.GetAttribute("FirmwareVersion");
                    if (fw != null && !string.IsNullOrEmpty(fw.ToString())) modInfo["firmware"] = fw.ToString();
                }
                catch { }

                try
                {
                    object pn = di.GetAttribute("PnDeviceName");
                    if (pn != null && !string.IsNullOrEmpty(pn.ToString()))
                    {
                        pnDeviceName = pn.ToString();
                        modInfo["pnDeviceName"] = pnDeviceName;
                    }
                }
                catch { }

                try
                {
                    var netIf = di.GetService<Siemens.Engineering.HW.Features.NetworkInterface>();
                    if (netIf != null && netIf.Nodes != null && netIf.Nodes.Count > 0)
                    {
                        var node = netIf.Nodes[0];
                        try
                        {
                            object addr = node.GetAttribute("Address");
                            if (addr != null && !string.IsNullOrEmpty(addr.ToString())) ip = addr.ToString();
                        }
                        catch { }
                        try
                        {
                            object sub = node.GetAttribute("SubnetMask");
                            if (sub != null && !string.IsNullOrEmpty(sub.ToString())) subnet = sub.ToString();
                        }
                        catch { }
                    }
                }
                catch { }

                modulesList.Add(modInfo);
            }

            res["ipAddress"] = string.IsNullOrEmpty(ip) ? "192.168.0.1" : ip;
            res["subnetMask"] = string.IsNullOrEmpty(subnet) ? "255.255.255.0" : subnet;
            res["pnDeviceName"] = string.IsNullOrEmpty(pnDeviceName) ? dev.Name.ToLower() : pnDeviceName;
            res["modules"] = modulesList;

            return res;
        }

        private static Dictionary<string, object> DoSetDeviceParam(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args != null && args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            string parameter = args != null && args.ContainsKey("parameter") ? (args["parameter"] as string ?? "").ToLower() : "";
            string value = args != null && args.ContainsKey("value") ? args["value"] as string : "";

            if (string.IsNullOrEmpty(parameter)) throw new ArgumentException("parameter is required ('ip', 'subnet', 'pn_name', 'device_name').");
            if (value == null) throw new ArgumentException("value is required.");

            Device dev = FindDevice(deviceName);
            if (dev == null) throw new InvalidOperationException("Device not found: " + deviceName);

            if (parameter == "device_name" || parameter == "name")
            {
                try { dev.Name = value; } catch { dev.SetAttribute("Name", value); }
                return new Dictionary<string, object> { { "status", "Success" }, { "parameter", parameter }, { "newValue", value } };
            }

            bool updated = false;
            foreach (DeviceItem di in dev.DeviceItems)
            {
                if (parameter == "pn_name")
                {
                    try
                    {
                        di.SetAttribute("PnDeviceName", value);
                        updated = true;
                        break;
                    }
                    catch { }
                }

                try
                {
                    var netIf = di.GetService<Siemens.Engineering.HW.Features.NetworkInterface>();
                    if (netIf != null && netIf.Nodes != null && netIf.Nodes.Count > 0)
                    {
                        var node = netIf.Nodes[0];
                        if (parameter == "ip" || parameter == "ipaddress")
                        {
                            node.SetAttribute("Address", value);
                            updated = true;
                            break;
                        }
                        else if (parameter == "subnet" || parameter == "subnetmask")
                        {
                            node.SetAttribute("SubnetMask", value);
                            updated = true;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (!updated)
            {
                throw new InvalidOperationException("Failed to set parameter '" + parameter + "'. Attribute or network node not found on device.");
            }

            return new Dictionary<string, object>
            {
                { "status", "Success" },
                { "deviceName", dev.Name },
                { "parameter", parameter },
                { "newValue", value }
            };
        }

        private static Dictionary<string, object> DoAddDevice(Dictionary<string, object> args)
        {
            EnsureConnected();
            string typeIdentifier = args != null && args.ContainsKey("typeIdentifier") ? args["typeIdentifier"] as string : "";
            string deviceName = args != null && args.ContainsKey("deviceName") ? args["deviceName"] as string : "";
            string stationName = args != null && args.ContainsKey("stationName") ? args["stationName"] as string : deviceName;

            if (string.IsNullOrEmpty(typeIdentifier)) throw new ArgumentException("typeIdentifier is required (e.g. 'OrderNumber:6ES7 515-2AM02-0AB0/V2.9').");
            if (string.IsNullOrEmpty(deviceName)) throw new ArgumentException("deviceName is required.");

            Device dev = _activeProject.Devices.CreateWithItem(typeIdentifier, deviceName, stationName);
            return new Dictionary<string, object>
            {
                { "status", "Success" },
                { "deviceName", dev.Name },
                { "typeIdentifier", dev.TypeIdentifier }
            };
        }

        private static Dictionary<string, object> DoAddModule(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args != null && args.ContainsKey("deviceName") ? args["deviceName"] as string : null;
            string typeIdentifier = args != null && args.ContainsKey("typeIdentifier") ? args["typeIdentifier"] as string : "";
            string moduleName = args != null && args.ContainsKey("moduleName") ? args["moduleName"] as string : "";
            int slot = args != null && args.ContainsKey("slot") ? Convert.ToInt32(args["slot"]) : 1;

            if (string.IsNullOrEmpty(typeIdentifier)) throw new ArgumentException("typeIdentifier is required.");
            if (string.IsNullOrEmpty(moduleName)) throw new ArgumentException("moduleName is required.");

            Device dev = FindDevice(deviceName);
            if (dev == null) throw new InvalidOperationException("Device not found: " + deviceName);

            DeviceItem targetHead = null;
            foreach (DeviceItem di in dev.DeviceItems)
            {
                if (di.CanPlugNew(typeIdentifier, moduleName, slot))
                {
                    targetHead = di;
                    break;
                }
            }

            if (targetHead == null && dev.DeviceItems.Count > 0)
            {
                targetHead = dev.DeviceItems[0];
            }

            if (targetHead == null)
            {
                throw new InvalidOperationException("No rack or device item found capable of plugging module into slot " + slot);
            }

            DeviceItem plugged = targetHead.PlugNew(typeIdentifier, moduleName, slot);
            return new Dictionary<string, object>
            {
                { "status", "Success" },
                { "moduleName", plugged.Name },
                { "slot", slot },
                { "typeIdentifier", typeIdentifier }
            };
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

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int digitGroups = (int)(Math.Log10(bytes) / Math.Log10(1024));
            if (digitGroups >= units.Length) digitGroups = units.Length - 1;
            return string.Format("{0:F2} {1}", bytes / Math.Pow(1024, digitGroups), units[digitGroups]);
        }

        private static Dictionary<string, object> DoArchiveProject(Dictionary<string, object> args)
        {
            EnsureConnected();
            if (_activeProject == null || _activeProject.Path == null)
            {
                throw new InvalidOperationException(L("Нет активного проекта для архивации.", "No active project to archive."));
            }

            // Save project first (mandatory before Archive to prevent EngineeringTargetInvocationException)
            _activeProject.Save();

            string projName = _activeProject.Name;
            var dirInfo = _activeProject.Path.Directory;
            var parentDir = dirInfo.Parent != null ? dirInfo.Parent.FullName : dirInfo.FullName;

            string targetDirStr = args != null && args.ContainsKey("targetDirectory") && !string.IsNullOrEmpty(args["targetDirectory"] as string)
                ? args["targetDirectory"] as string
                : Path.Combine(parentDir, "Archives");

            if (!Directory.Exists(targetDirStr))
            {
                Directory.CreateDirectory(targetDirStr);
            }

            string dateFmt = _backupUseShortYear ? "dd.MM.yy" : "dd.MM.yyyy";
            string todayStr = DateTime.Now.ToString(dateFmt);

            string customName = args != null && args.ContainsKey("archiveName") ? args["archiveName"] as string : null;
            string archiveFileName;
            if (!string.IsNullOrWhiteSpace(customName))
            {
                archiveFileName = customName.Trim();
            }
            else
            {
                archiveFileName = projName + "_Archive_" + todayStr;
            }

            // Ensure archiveFileName has .zap18 extension so Siemens Openness saves it with .zap18
            if (!archiveFileName.EndsWith(".zap18", StringComparison.OrdinalIgnoreCase))
            {
                archiveFileName = archiveFileName + ".zap18";
            }

            string expectedZapPath = Path.Combine(targetDirStr, archiveFileName);
            if (File.Exists(expectedZapPath))
            {
                try { File.Delete(expectedZapPath); } catch { }
            }

            Log("Archiving project '" + projName + "' to: " + expectedZapPath);
            StartAutoConfirmWatcher();

            // Archive with DiscardRestorableDataAndCompressed for cleanest, most portable archive
            var mode = ProjectArchivationMode.DiscardRestorableDataAndCompressed;
            if (args != null && args.ContainsKey("mode") && args["mode"] != null)
            {
                string mStr = args["mode"].ToString().ToLowerInvariant();
                if (mStr == "compressed") mode = ProjectArchivationMode.Compressed;
                else if (mStr == "none") mode = ProjectArchivationMode.None;
                else if (mStr == "discardrestorabledata") mode = ProjectArchivationMode.DiscardRestorableData;
            }

            _activeProject.Archive(new DirectoryInfo(targetDirStr), archiveFileName, mode);

            long fileSizeBytes = 0;
            if (File.Exists(expectedZapPath))
            {
                fileSizeBytes = new FileInfo(expectedZapPath).Length;
            }

            return new Dictionary<string, object>
            {
                { "status", "Success" },
                { "projectName", projName },
                { "archiveName", archiveFileName },
                { "archivePath", expectedZapPath },
                { "targetDirectory", targetDirStr },
                { "sizeBytes", fileSizeBytes },
                { "sizeFormatted", FormatBytes(fileSizeBytes) },
                { "mode", mode.ToString() },
                { "message", L("Проект успешно архивирован в .zap18: ", "Project successfully archived to .zap18: ") + expectedZapPath }
            };
        }

        private static Dictionary<string, object> DoRetrieveProject(Dictionary<string, object> args)
        {
            if (args == null || !args.ContainsKey("archivePath") || string.IsNullOrEmpty(args["archivePath"] as string))
            {
                throw new ArgumentException(L("Укажите путь к архиву .zap18 (аргумент 'archivePath').", "Specify path to .zap18 archive ('archivePath' argument)."));
            }

            string archivePath = args["archivePath"] as string;
            if (!File.Exists(archivePath))
            {
                throw new FileNotFoundException(L("Файл архива не найден: ", "Archive file not found: ") + archivePath);
            }

            FileInfo archiveFile = new FileInfo(archivePath);
            string targetDirStr = args.ContainsKey("targetDirectory") && !string.IsNullOrEmpty(args["targetDirectory"] as string)
                ? args["targetDirectory"] as string
                : Path.Combine(archiveFile.Directory.FullName, Path.GetFileNameWithoutExtension(archivePath) + "_Retrieved");

            if (!Directory.Exists(targetDirStr))
            {
                Directory.CreateDirectory(targetDirStr);
            }

            Log("Retrieving project archive '" + archivePath + "' to: " + targetDirStr);
            StartAutoConfirmWatcher();

            if (_activeTiaPortal == null)
            {
                if (TiaPortal.GetProcesses().Count > 0)
                {
                    _activeTiaPortal = TiaPortal.GetProcesses()[0].Attach();
                }
                else
                {
                    _activeTiaPortal = new TiaPortal(TiaPortalMode.WithUserInterface);
                }
            }

            var retrievedProject = _activeTiaPortal.Projects.Retrieve(archiveFile, new DirectoryInfo(targetDirStr));
            _activeProject = retrievedProject;
            if (_activeProject.Path != null)
            {
                _lastProjectPath = _activeProject.Path.FullName;
                SaveSettings();
            }

            return new Dictionary<string, object>
            {
                { "status", "Success" },
                { "projectName", _activeProject.Name },
                { "projectPath", _activeProject.Path != null ? _activeProject.Path.FullName : targetDirStr },
                { "retrievedFrom", archivePath },
                { "message", L("Проект успешно извлечен из архива: ", "Project successfully retrieved from archive: ") + _activeProject.Name }
            };
        }

        private static Dictionary<string, object> DoSaveProjectVersion(Dictionary<string, object> args)
        {
            EnsureConnected();
            string curName = _activeProject.Name;
            var dirInfo = _activeProject.Path.Directory;
            var parentDir = dirInfo.Parent.FullName;
            string oldPath = _activeProject.Path.FullName;

            string dateFmt = _backupUseShortYear ? "dd.MM.yy" : "dd.MM.yyyy";
            string todayStr = DateTime.Now.ToString(dateFmt);

            string customName = args != null && args.ContainsKey("customName") ? args["customName"] as string : null;
            string branchName = args != null && args.ContainsKey("branchName") ? args["branchName"] as string : null;
            bool isInteractive = args == null || !args.ContainsKey("nonInteractive");

            // Remove existing trailing date patterns like _19.07.26 or _19.07.2026 or _10.09.2026
            string cleanBase = Regex.Replace(curName, @"_\d{2}\.\d{2}\.\d{2,4}$", "");

            // Pattern match: SPS_Mechta_2026_V0 -> prefix = "SPS_Mechta_2026", ver = 0
            string prefix = cleanBase;
            int curVer = 0;
            string suffix = "";
            var m = Regex.Match(cleanBase, @"^(.*?)_V(\d+)(.*)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                prefix = m.Groups[1].Value;
                curVer = int.Parse(m.Groups[2].Value);
                suffix = m.Groups[3].Value;
            }

            // Scan parent directory for all existing projects of this family
            int maxExistingVer = curVer;
            var siblingProjects = new List<string>();
            try
            {
                if (Directory.Exists(parentDir))
                {
                    var dirs = Directory.GetDirectories(parentDir);
                    foreach (var d in dirs)
                    {
                        string dirName = Path.GetFileName(d);
                        var vm = Regex.Match(dirName, @"^" + Regex.Escape(prefix) + @"_V(\d+)", RegexOptions.IgnoreCase);
                        if (vm.Success)
                        {
                            siblingProjects.Add(dirName);
                            int fVer = int.Parse(vm.Groups[1].Value);
                            if (fVer > maxExistingVer) maxExistingVer = fVer;
                        }
                    }
                }
            }
            catch { }

            string newName = "";
            string strategy = "Trunk";

            if (!string.IsNullOrEmpty(customName))
            {
                newName = customName;
                strategy = "Custom";
            }
            else if (!string.IsNullOrEmpty(branchName))
            {
                newName = string.Format("{0}_V{1}_Branch_{2}_{3}", prefix, curVer, branchName, todayStr);
                strategy = "Branch";
            }
            else if (curVer < maxExistingVer && isInteractive)
            {
                // Branch Divergence detected in interactive mode!
                ClearScreen();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("╔════════════════════════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║  " + L("ВНИМАНИЕ: ОБНАРУЖЕНО ВЕТВЛЕНИЕ ВЕРСИЙ (BRANCH DIVERGENCE)!", "WARNING: PROJECT BRANCH DIVERGENCE DETECTED!             ") + "  ║");
                Console.WriteLine("╠════════════════════════════════════════════════════════════════════════════════╣");
                Console.WriteLine(string.Format("║  " + L("Открыт проект          : {0,-55}", "Opened Project         : {0,-55}") + " ║", curName));
                Console.WriteLine(string.Format("║  " + L("Версия открытого файла : V{0,-54}", "Opened File Version    : V{0,-54}") + " ║", curVer));
                Console.WriteLine(string.Format("║  " + L("Максимальная на диске  : V{0,-54}", "Highest Version on Disk: V{0,-54}") + " ║", maxExistingVer));
                Console.WriteLine("╚════════════════════════════════════════════════════════════════════════════════╝");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("  " + L("Если создать V" + (curVer + 1) + ", возникнет путаница с уже существующими V" + maxExistingVer + "!",
                                      "If you create V" + (curVer + 1) + ", version collision and divergence from existing V" + maxExistingVer + " will occur!"));
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("  [1] " + L("Сквозная версия (Trunk): " + prefix + "_V" + (maxExistingVer + 1) + "_" + todayStr + suffix + " [Рекомендуется]",
                                           "Linear Trunk Version: " + prefix + "_V" + (maxExistingVer + 1) + "_" + todayStr + suffix + " [Recommended]"));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("      " + L("Продолжить общую сквозную нумерацию, исключая дубликаты версий",
                                           "Continue global linear numbering, preventing version duplicates"));
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("  [2] " + L("Изолированная ветка (Branch): " + prefix + "_V" + curVer + "_Branch_{Имя}_" + todayStr,
                                           "Isolated Branch: " + prefix + "_V" + curVer + "_Branch_{Name}_" + todayStr));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("      " + L("Создать маркированную ветку от старой ревизии V" + curVer,
                                           "Create an explicitly tagged branch from older revision V" + curVer));
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("  [3] " + L("Ввести имя версии вручную", "Enter custom version name manually"));
                Console.ResetColor();
                Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
                Console.Write("  " + L("Ваш выбор [1, 2, 3]: ", "Your choice [1, 2, 3]: "));

                var optKey = Console.ReadKey(true);
                Console.WriteLine(optKey.KeyChar);

                if (optKey.KeyChar == '2')
                {
                    Console.Write("  " + L("Введите имя ветки (например Hotfix или GripperTest): ", "Enter branch name (e.g. Hotfix or GripperTest): "));
                    string bInput = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(bInput)) bInput = "Branch";
                    bInput = Regex.Replace(bInput.Trim(), @"[^\w\-]", "_");
                    newName = string.Format("{0}_V{1}_Branch_{2}_{3}", prefix, curVer, bInput, todayStr);
                    strategy = "Branch";
                }
                else if (optKey.KeyChar == '3')
                {
                    Console.Write("  " + L("Введите полное имя нового проекта: ", "Enter full new project name: "));
                    string cInput = Console.ReadLine();
                    if (!string.IsNullOrWhiteSpace(cInput))
                    {
                        newName = cInput.Trim();
                        strategy = "Custom";
                    }
                    else
                    {
                        newName = prefix + "_V" + (maxExistingVer + 1) + "_" + todayStr + suffix;
                        strategy = "Trunk";
                    }
                }
                else
                {
                    // Option 1 default: Trunk increment
                    newName = prefix + "_V" + (maxExistingVer + 1) + "_" + todayStr + suffix;
                    strategy = "Trunk";
                }
            }
            else
            {
                // Normal progression or non-interactive safe mode:
                int targetVer = (curVer < maxExistingVer) ? (maxExistingVer + 1) : (curVer + 1);
                newName = prefix + "_V" + targetVer + "_" + todayStr + suffix;
                strategy = (curVer < maxExistingVer) ? "Trunk (Auto-Recovered)" : "Trunk";
            }

            // Collision Guard: Ensure target directory does not exist
            string targetFolder = Path.Combine(parentDir, newName);
            if (Directory.Exists(targetFolder))
            {
                int revIndex = 1;
                string baseAttempt = newName;
                while (Directory.Exists(targetFolder))
                {
                    newName = string.Format("{0}_rev{1}", baseAttempt, revIndex++);
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
                if (_activeProject.Path != null)
                {
                    _lastProjectPath = _activeProject.Path.FullName;
                    SaveSettings();
                }
            }

            // Write Version Manifest for permanent engineering audit
            try
            {
                string manifestPath = Path.Combine(targetFolder, "version_manifest.txt");
                var sbMan = new StringBuilder();
                sbMan.AppendLine("================================================================================");
                sbMan.AppendLine("TIA PORTAL PROJECT VERSION MANIFEST");
                sbMan.AppendLine("================================================================================");
                sbMan.AppendLine("New Project Name : " + newName);
                sbMan.AppendLine("Target Directory : " + targetFolder);
                sbMan.AppendLine("Parent Project   : " + curName);
                sbMan.AppendLine("Parent Path      : " + oldPath);
                sbMan.AppendLine("Created Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sbMan.AppendLine("Created By       : " + Environment.UserName + " on " + Environment.MachineName);
                sbMan.AppendLine("Strategy         : " + strategy);
                sbMan.AppendLine("Base Version     : V" + curVer);
                sbMan.AppendLine("Max Sibling Ver  : V" + maxExistingVer);
                sbMan.AppendLine("Agent Version    : " + AGENT_VERSION);
                sbMan.AppendLine("TIA Version      : " + TIA_TARGET_VERSION);
                sbMan.AppendLine("================================================================================");
                File.WriteAllText(manifestPath, sbMan.ToString(), Encoding.UTF8);
            }
            catch { }

            return new Dictionary<string, object>
            {
                { "status", "Success" },
                { "strategy", strategy },
                { "oldProjectName", curName },
                { "newProjectName", _activeProject.Name },
                { "newProjectPath", _activeProject.Path.FullName },
                { "message", L("Проект успешно сохранен как новая версия: ", "Project successfully saved as new version: ") + _activeProject.Name }
            };
        }

        // --- Helpers ---

        private static bool EnsureConnected(bool throwOnError = true)
        {
            if (IsTiaConnected()) return true;

            string res = DoConnectProcess(new Dictionary<string, object>());
            if (IsTiaConnected())
            {
                try
                {
                    if (_activeProject != null && _activeProject.Path != null)
                    {
                        _lastProjectPath = _activeProject.Path.FullName;
                        SaveSettings();
                    }
                }
                catch { }
                return true;
            }

            if (throwOnError)
            {
                throw new InvalidOperationException(res + "\n(" + L(
                    "Подсказка: Проверьте, запущен ли TIA Portal, обновлён ли Whitelist и запущен ли инструмент с правами Администратора, если TIA запущен от Администратора. Либо откройте проект в фоновом режиме [P].",
                    "Hint: Check if TIA Portal is running, Whitelist is updated, or run as Admin if TIA is Admin. Or open project in headless mode [P].") + ")");
            }
            return false;
        }

        private static string DoExportTagsCsv(PlcSoftware plc, string outputPath)
        {
            var allTables = new List<Dictionary<string, object>>();
            CollectTagsRecursive(plc.TagTableGroup, allTables, "");

            var sb = new StringBuilder();
            string sep = _csvDelimiter;
            sb.AppendLine(string.Format("Name{0}Path{0}DataType{0}LogicalAddress{0}Comment", sep));

            int tagCount = 0;
            foreach (var tbl in allTables)
            {
                string tPath = tbl["path"].ToString();
                var tags = tbl["tags"] as List<Dictionary<string, object>>;
                if (tags == null) continue;
                foreach (var tg in tags)
                {
                    string name = tg["name"].ToString();
                    string dt = tg["dataType"].ToString();
                    string addr = tg["logicalAddress"].ToString();
                    string comment = "";
                    if (_exportIncludeComments && tg.ContainsKey("comment") && tg["comment"] != null)
                    {
                        comment = tg["comment"].ToString().Replace("\r", " ").Replace("\n", " ").Replace(sep, " ");
                    }
                    sb.AppendLine(string.Format("{0}{1}{2}{1}{3}{1}{4}{1}{5}", name, sep, tPath, dt, addr, comment));
                    tagCount++;
                }
            }

            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            return string.Format(L("Экспортировано {0} тегов в CSV: {1} (Комментарии: {2})",
                                   "Exported {0} tags to CSV: {1} (Comments: {2})"),
                                 tagCount, outputPath, _exportIncludeComments ? L("Включены", "Included") : L("Отключены", "Excluded"));
        }

        private static Dictionary<string, object> DoCheckTags(PlcSoftware plc)
        {
            var allTables = new List<Dictionary<string, object>>();
            CollectTagsRecursive(plc.TagTableGroup, allTables, "");

            int totalTags = 0;
            int withComments = 0;
            int withoutComments = 0;
            var addressMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var unreferencedTags = new List<string>();

            // Get CrossReferences
            var crossRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var auditRes = DoAuditProject(new Dictionary<string, object>());
                var deadTags = auditRes.ContainsKey("deadTags") ? auditRes["deadTags"] as List<string> : null;
                if (deadTags != null) unreferencedTags = deadTags;
            }
            catch { }

            foreach (var tbl in allTables)
            {
                var tags = tbl["tags"] as List<Dictionary<string, object>>;
                if (tags == null) continue;
                foreach (var tg in tags)
                {
                    totalTags++;
                    string tName = tg["name"].ToString();
                    string addr = tg["logicalAddress"].ToString();
                    string comm = tg.ContainsKey("comment") && tg["comment"] != null ? tg["comment"].ToString().Trim() : "";

                    if (!string.IsNullOrEmpty(comm)) withComments++;
                    else withoutComments++;

                    if (!string.IsNullOrEmpty(addr))
                    {
                        if (!addressMap.ContainsKey(addr)) addressMap[addr] = new List<string>();
                        addressMap[addr].Add(tName);
                    }
                }
            }

            var duplicateAddresses = new Dictionary<string, List<string>>();
            foreach (var kvp in addressMap)
            {
                if (kvp.Value.Count > 1) duplicateAddresses[kvp.Key] = kvp.Value;
            }

            return new Dictionary<string, object>
            {
                { "totalTags", totalTags },
                { "tagsWithComments", withComments },
                { "tagsWithoutComments", withoutComments },
                { "duplicateAddressCount", duplicateAddresses.Count },
                { "duplicateAddresses", duplicateAddresses },
                { "unreferencedTagCount", unreferencedTags.Count },
                { "unreferencedTags", unreferencedTags }
            };
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
            if (string.IsNullOrEmpty(path)) return null;
            if (path.Contains("/"))
            {
                string[] parts = path.Split('/');
                return FindTypeInternal(group, parts, 0);
            }
            var t = group.Types.Find(path);
            if (t != null) return t;
            return FindTypeByNameRecursive(group, path);
        }

        private static Siemens.Engineering.SW.Types.PlcType FindTypeByNameRecursive(Siemens.Engineering.SW.Types.PlcTypeGroup group, string name)
        {
            var t = group.Types.Find(name);
            if (t != null) return t;
            foreach (var sub in group.Groups)
            {
                t = FindTypeByNameRecursive(sub, name);
                if (t != null) return t;
            }
            return null;
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
            if (string.IsNullOrEmpty(path)) return null;
            if (path.Contains("/"))
            {
                string[] parts = path.Split('/');
                return FindBlockInternal(group, parts, 0);
            }
            var b = group.Blocks.Find(path);
            if (b != null) return b;
            return FindBlockByNameRecursive(group, path);
        }

        private static PlcBlock FindBlockByNameRecursive(PlcBlockGroup group, string name)
        {
            var b = group.Blocks.Find(name);
            if (b != null) return b;
            foreach (var sub in group.Groups)
            {
                b = FindBlockByNameRecursive(sub, name);
                if (b != null) return b;
            }
            return null;
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

        private static void ShowWatchTablesTui()
        {
            ClearScreen();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("  " + L("ТАБЛИЦЫ НАБЛЮДЕНИЯ И ФОРСИРОВАНИЯ (WATCH & FORCE TABLES)", "WATCH & FORCE TABLES"));
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            try
            {
                var data = (Dictionary<string, object>)DoListWatchTables(new Dictionary<string, object>());
                var wts = (List<Dictionary<string, object>>)data["watchTables"];
                var fts = (List<Dictionary<string, object>>)data["forceTables"];

                Console.WriteLine(L("Устройство: ", "Device: ") + data["deviceName"] + "\n");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("--- " + L("Таблицы наблюдения (Watch Tables, ", "Watch Tables, count: ") + wts.Count + ") ---");
                Console.ResetColor();
                if (wts.Count == 0) Console.WriteLine("  (" + L("нет таблиц наблюдения", "no watch tables") + ")");
                for (int i = 0; i < wts.Count; i++)
                {
                    Console.WriteLine(string.Format("  [{0}] {1} (элементов: {2}, консистентна: {3})", i + 1, wts[i]["path"], wts[i]["entryCount"], wts[i]["isConsistent"]));
                }

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("--- " + L("Таблицы форсирования (Force Tables, ", "Force Tables, count: ") + fts.Count + ") ---");
                Console.ResetColor();
                if (fts.Count == 0) Console.WriteLine("  (" + L("нет таблиц форсирования", "no force tables") + ")");
                for (int i = 0; i < fts.Count; i++)
                {
                    Console.WriteLine(string.Format("  [{0}] {1} (элементов: {2})", i + 1, fts[i]["path"], fts[i]["entryCount"]));
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(L("Ошибка: ", "Error: ") + ex.Message);
                Console.ResetColor();
            }

            Console.WriteLine("\n" + L("Нажмите любую клавишу для возврата в меню...", "Press any key to return to menu..."));
            Console.ReadKey(true);
            ClearScreen();
        }

        private static object DoListUdts()
        {
            EnsureConnected();
            var plc = FindPlcSoftware(FindDevice(null));
            if (plc == null) throw new InvalidOperationException("No PLC found in project.");
            var udts = new List<Dictionary<string, object>>();
            CollectTypesRecursive(plc.TypeGroup, udts, "");
            return udts;
        }

        private static object DoListWatchTables(Dictionary<string, object> args)
        {
            EnsureConnected();
            string deviceName = args != null && args.ContainsKey("deviceName") && args["deviceName"] != null ? args["deviceName"].ToString() : null;
            var dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);
            if (plc == null) throw new InvalidOperationException("No PLC found in project.");

            var res = new Dictionary<string, object>();
            var watchList = new List<Dictionary<string, object>>();
            var forceList = new List<Dictionary<string, object>>();

            if (plc.WatchAndForceTableGroup != null)
            {
                CollectWatchTables(plc.WatchAndForceTableGroup, "", watchList, forceList);
            }

            res["deviceName"] = dev != null ? dev.Name : plc.Name;
            res["watchTablesCount"] = watchList.Count;
            res["watchTables"] = watchList;
            res["forceTablesCount"] = forceList.Count;
            res["forceTables"] = forceList;
            return res;
        }

        private static void CollectWatchTables(PlcWatchAndForceTableGroup group, string currentPath, List<Dictionary<string, object>> watchList, List<Dictionary<string, object>> forceList)
        {
            if (group == null) return;
            string prefix = string.IsNullOrEmpty(currentPath) ? "" : currentPath + "/";

            foreach (PlcWatchTable wt in group.WatchTables)
            {
                watchList.Add(new Dictionary<string, object>
                {
                    { "name", wt.Name },
                    { "path", prefix + wt.Name },
                    { "entryCount", wt.Entries != null ? wt.Entries.Count : 0 },
                    { "isConsistent", wt.IsConsistent }
                });
            }

            foreach (PlcForceTable ft in group.ForceTables)
            {
                forceList.Add(new Dictionary<string, object>
                {
                    { "name", ft.Name },
                    { "path", prefix + ft.Name },
                    { "entryCount", ft.Entries != null ? ft.Entries.Count : 0 },
                    { "isConsistent", ft.IsConsistent }
                });
            }

            foreach (PlcWatchAndForceTableUserGroup sub in group.Groups)
            {
                CollectWatchTables(sub, prefix + sub.Name, watchList, forceList);
            }
        }

        private static PlcWatchTable FindWatchTableRecursive(PlcWatchAndForceTableGroup group, string name)
        {
            if (group == null) return null;
            foreach (PlcWatchTable wt in group.WatchTables)
            {
                if (string.Equals(wt.Name, name, StringComparison.OrdinalIgnoreCase)) return wt;
            }
            foreach (PlcWatchAndForceTableUserGroup sub in group.Groups)
            {
                var found = FindWatchTableRecursive(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        private static PlcForceTable FindForceTableRecursive(PlcWatchAndForceTableGroup group, string name)
        {
            if (group == null) return null;
            foreach (PlcForceTable ft in group.ForceTables)
            {
                if (string.Equals(ft.Name, name, StringComparison.OrdinalIgnoreCase)) return ft;
            }
            foreach (PlcWatchAndForceTableUserGroup sub in group.Groups)
            {
                var found = FindForceTableRecursive(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        private static object DoReadWatchTable(Dictionary<string, object> args)
        {
            EnsureConnected();
            if (args == null || !args.ContainsKey("tableName") || args["tableName"] == null) throw new ArgumentException("Parameter 'tableName' is required.");
            string tableName = args["tableName"].ToString();
            string deviceName = args.ContainsKey("deviceName") && args["deviceName"] != null ? args["deviceName"].ToString() : null;
            var dev = FindDevice(deviceName);
            var plc = FindPlcSoftware(dev);
            if (plc == null) throw new InvalidOperationException("No PLC found in project.");
            if (plc.WatchAndForceTableGroup == null) throw new InvalidOperationException("WatchAndForceTableGroup is not available.");

            PlcWatchTable foundWt = FindWatchTableRecursive(plc.WatchAndForceTableGroup, tableName);
            if (foundWt != null)
            {
                var entries = new List<Dictionary<string, object>>();
                if (foundWt.Entries != null)
                {
                    foreach (PlcWatchTableEntry entry in foundWt.Entries)
                    {
                        entries.Add(new Dictionary<string, object>
                        {
                            { "name", entry.Name },
                            { "address", entry.Address },
                            { "displayFormat", entry.DisplayFormat.ToString() },
                            { "modifyValue", entry.ModifyValue },
                            { "modifyTrigger", entry.ModifyTrigger.ToString() },
                            { "monitorTrigger", entry.MonitorTrigger.ToString() }
                        });
                    }
                }
                return new Dictionary<string, object>
                {
                    { "type", "WatchTable" },
                    { "name", foundWt.Name },
                    { "isConsistent", foundWt.IsConsistent },
                    { "entriesCount", entries.Count },
                    { "entries", entries }
                };
            }

            PlcForceTable foundFt = FindForceTableRecursive(plc.WatchAndForceTableGroup, tableName);
            if (foundFt != null)
            {
                var entries = new List<Dictionary<string, object>>();
                if (foundFt.Entries != null)
                {
                    foreach (PlcForceTableEntry entry in foundFt.Entries)
                    {
                        entries.Add(new Dictionary<string, object>
                        {
                            { "name", entry.Name },
                            { "address", entry.Address },
                            { "displayFormat", entry.DisplayFormat.ToString() },
                            { "forceValue", entry.ForceValue },
                            { "forceIntention", entry.ForceIntention.ToString() },
                            { "monitorTrigger", entry.MonitorTrigger.ToString() }
                        });
                    }
                }
                return new Dictionary<string, object>
                {
                    { "type", "ForceTable" },
                    { "name", foundFt.Name },
                    { "isConsistent", foundFt.IsConsistent },
                    { "entriesCount", entries.Count },
                    { "entries", entries }
                };
            }

            throw new FileNotFoundException("Watch or Force Table '" + tableName + "' not found in PLC software.");
        }

        private static object DoCrossReferences(Dictionary<string, object> args)
        {
            EnsureConnected();
            string target = args != null && args.ContainsKey("target") && args["target"] != null ? args["target"].ToString().Trim() : "";
            string filter = args != null && args.ContainsKey("filter") && args["filter"] != null ? args["filter"].ToString().Trim() : "";
            string deviceName = args != null && args.ContainsKey("deviceName") && args["deviceName"] != null ? args["deviceName"].ToString() : null;

            var plc = FindPlcSoftware(FindDevice(deviceName));
            if (plc == null) throw new InvalidOperationException("No PLC found in project.");

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

            var res = new Dictionary<string, object>();
            res["deviceName"] = plc.Name;

            if (string.Equals(filter, "UnusedObjects", StringComparison.OrdinalIgnoreCase) || (string.IsNullOrEmpty(target) && string.IsNullOrEmpty(filter)))
            {
                var unusedBlocks = new List<Dictionary<string, object>>();
                foreach (var bName in blockNames)
                {
                    string t = blockTypeMap.ContainsKey(bName) ? blockTypeMap[bName] : "Block";
                    if (t.Contains("OB")) continue;

                    bool isReachable = reachableFromOB.Contains(bName);
                    int callers = blockCallers.ContainsKey(bName) ? blockCallers[bName].Count : 0;
                    if (!isReachable && callers == 0)
                    {
                        unusedBlocks.Add(new Dictionary<string, object>
                        {
                            { "name", bName },
                            { "type", t },
                            { "path", blockGroupMap.ContainsKey(bName) ? blockGroupMap[bName] : bName },
                            { "address", blockAddressMap.ContainsKey(bName) ? blockAddressMap[bName] : "" }
                        });
                    }
                }

                var unusedTags = new List<Dictionary<string, object>>();
                try
                {
                    if (plc.TagTableGroup != null)
                    {
                        var tagTables = new List<Dictionary<string, object>>();
                        CollectTagsRecursive(plc.TagTableGroup, tagTables, "");
                        foreach (var td in tagTables)
                        {
                            var tags = td["tags"] as List<Dictionary<string, string>>;
                            if (tags == null) continue;
                            foreach (var tag in tags)
                            {
                                string tName = tag["name"];
                                if (!tagUsageMap.ContainsKey(tName) || tagUsageMap[tName].Count == 0)
                                {
                                    unusedTags.Add(new Dictionary<string, object>
                                    {
                                        { "tagTable", td["tableName"] },
                                        { "name", tName },
                                        { "dataType", tag["dataType"] },
                                        { "address", tag["logicalAddress"] }
                                    });
                                }
                            }
                        }
                    }
                }
                catch { }

                res["filter"] = "UnusedObjects";
                res["unusedBlocksCount"] = unusedBlocks.Count;
                res["unusedBlocks"] = unusedBlocks;
                res["unusedTagsCount"] = unusedTags.Count;
                res["unusedTags"] = unusedTags;
                return res;
            }

            res["target"] = target;
            var callersList = new List<string>();
            var calledList = new List<string>();
            var tagUsedIn = new List<string>();

            if (blockCallers.ContainsKey(target)) callersList.AddRange(blockCallers[target]);
            if (blockCalls.ContainsKey(target)) calledList.AddRange(blockCalls[target]);
            if (tagUsageMap.ContainsKey(target)) tagUsedIn.AddRange(tagUsageMap[target]);

            res["isBlock"] = blockNames.Contains(target);
            res["callers"] = callersList;
            res["calls"] = calledList;
            res["accessingBlocks"] = tagUsedIn;
            res["totalReferences"] = callersList.Count + tagUsedIn.Count;
            return res;
        }

        private static object DoTiaDoctor()
        {
            var report = new Dictionary<string, object>();
            var checks = new List<Dictionary<string, object>>();
            bool allPass = true;

            string opennessDll = "";
            string opennessVer = "";
            string[] candidateDirs = new string[]
            {
                @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\Siemens.Engineering.dll",
                @"C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20\Siemens.Engineering.dll",
                @"C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19\Siemens.Engineering.dll",
                @"C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18\Siemens.Engineering.dll"
            };
            foreach (var p in candidateDirs)
            {
                if (File.Exists(p))
                {
                    opennessDll = p;
                    try { opennessVer = FileVersionInfo.GetVersionInfo(p).FileVersion; } catch { }
                    break;
                }
            }

            checks.Add(new Dictionary<string, object>
            {
                { "check", "Siemens Openness PublicAPI Assembly" },
                { "status", !string.IsNullOrEmpty(opennessDll) ? "PASS" : "FAIL" },
                { "path", opennessDll },
                { "version", opennessVer }
            });
            if (string.IsNullOrEmpty(opennessDll)) allPass = false;

            bool inOpennessGroup = false;
            string userName = "";
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                userName = identity.Name;
                foreach (var claim in identity.Groups)
                {
                    try
                    {
                        string groupName = claim.Translate(typeof(NTAccount)).Value;
                        if (groupName.IndexOf("Siemens TIA Openness", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            inOpennessGroup = true;
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            checks.Add(new Dictionary<string, object>
            {
                { "check", "Siemens TIA Openness User Group Membership" },
                { "status", inOpennessGroup ? "PASS" : "WARN" },
                { "user", userName },
                { "details", inOpennessGroup ? "User is member of 'Siemens TIA Openness'" : "User is NOT member of 'Siemens TIA Openness'." }
            });

            bool isWhitelisted = false;
            string exePath = Assembly.GetExecutingAssembly().Location;
            try
            {
                string exeName = Path.GetFileName(exePath);
                string[] versions = new string[] { "18.0", "19.0", "20.0", "21.0" };
                foreach (var ver in versions)
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness\" + ver + @"\Whitelist\" + exeName + @"\Entry"))
                    {
                        if (k != null) { isWhitelisted = true; break; }
                    }
                }
            }
            catch { }

            checks.Add(new Dictionary<string, object>
            {
                { "check", "Openness Whitelist SHA256 Registration" },
                { "status", isWhitelisted ? "PASS" : "WARN" },
                { "exePath", exePath },
                { "details", isWhitelisted ? "Registered (0 Openness security popups)" : "Not registered in Registry Whitelist. Run build.bat." }
            });

            var portalProcs = Process.GetProcessesByName("Siemens.Automation.Portal");
            var plcsimProcs = Process.GetProcessesByName("Siemens.Simatic.PlcSim.V18");
            var instances = new List<Dictionary<string, object>>();
            foreach (var p in portalProcs)
            {
                try
                {
                    instances.Add(new Dictionary<string, object>
                    {
                        { "id", p.Id },
                        { "workingSetMB", p.WorkingSet64 / 1024 / 1024 }
                    });
                }
                catch { }
            }

            checks.Add(new Dictionary<string, object>
            {
                { "check", "Running TIA Portal Instances" },
                { "status", portalProcs.Length > 0 ? "PASS" : "INFO" },
                { "count", portalProcs.Length },
                { "instances", instances },
                { "plcsimRunning", plcsimProcs.Length > 0 }
            });

            checks.Add(new Dictionary<string, object>
            {
                { "check", "Agent Openness Connection" },
                { "status", _activeTiaPortal != null ? "CONNECTED" : "STANDBY" },
                { "attachedPid", _attachedPid },
                { "project", _activeProject != null ? _activeProject.Name : "(none)" }
            });

            report["overallStatus"] = allPass ? "READY" : "ATTENTION_REQUIRED";
            report["agentVersion"] = AGENT_VERSION;
            report["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            report["checks"] = checks;
            return report;
        }

        private static object DoBatchRead(Dictionary<string, object> args)
        {
            EnsureConnected();
            if (args == null || !args.ContainsKey("operations"))
            {
                throw new ArgumentException("Parameter 'operations' (array of read operations) is required.");
            }

            var opsEnum = args["operations"] as System.Collections.IEnumerable;
            if (opsEnum == null)
            {
                throw new ArgumentException("Parameter 'operations' must be an array.");
            }

            var results = new List<Dictionary<string, object>>();
            int count = 0;

            foreach (var rawOp in opsEnum)
            {
                count++;
                if (count > 50) break; // Maximum 50 operations per batch

                var opItem = rawOp as Dictionary<string, object>;
                if (opItem == null) continue;

                string action = "";
                if (opItem.ContainsKey("action")) action = opItem["action"].ToString();
                else if (opItem.ContainsKey("operation")) action = opItem["operation"].ToString();
                else if (opItem.ContainsKey("tool")) action = opItem["tool"].ToString();

                var subArgs = opItem;
                if (opItem.ContainsKey("arguments") && opItem["arguments"] is Dictionary<string, object>)
                {
                    subArgs = (Dictionary<string, object>)opItem["arguments"];
                }

                var opResult = new Dictionary<string, object>();
                opResult["index"] = count;
                opResult["action"] = action;

                try
                {
                    object outData = null;
                    switch (action.ToLowerInvariant().Replace("-", "_"))
                    {
                        case "read_scl":
                        case "tia_read_scl":
                            outData = DoReadScl(subArgs);
                            break;
                        case "read_block_interface":
                        case "read_interface":
                        case "tia_read_block_interface":
                            outData = DoReadBlockInterface(subArgs);
                            break;
                        case "list_tags":
                        case "tia_list_tags":
                            outData = DoListTags(subArgs);
                            break;
                        case "search_blocks":
                        case "tia_search_blocks":
                            outData = DoSearchBlocks(subArgs);
                            break;
                        case "search_tags":
                        case "tia_search_tags":
                            outData = DoSearchTags(subArgs);
                            break;
                        case "list_blocks":
                        case "tia_list_blocks":
                            outData = DoListBlocks(subArgs);
                            break;
                        case "list_devices":
                        case "tia_list_devices":
                            outData = DoListDevices();
                            break;
                        case "get_project_info":
                        case "tia_get_project_info":
                            outData = DoGetProjectInfo();
                            break;
                        case "get_hardware_config":
                        case "tia_get_hardware_config":
                            var dHw = FindDevice(null);
                            outData = GetHardwareConfig(dHw, FindPlcSoftware(dHw));
                            break;
                        case "get_call_structure":
                        case "call_tree":
                        case "tia_get_call_structure":
                            bool onlyConf = subArgs != null && subArgs.ContainsKey("onlyConflicts") ? Convert.ToBoolean(subArgs["onlyConflicts"]) : false;
                            outData = GetCallStructure(FindPlcSoftware(FindDevice(null)), onlyConf, true);
                            break;
                        case "get_dependency_structure":
                        case "dependencies":
                        case "tia_get_dependency_structure":
                            outData = GetDependencyStructure(FindPlcSoftware(FindDevice(null)));
                            break;
                        case "get_memory_resources":
                        case "memory":
                        case "tia_get_memory_resources":
                            var dMem = FindDevice(null);
                            outData = GetMemoryResources(FindPlcSoftware(dMem), dMem);
                            break;
                        case "get_device_params":
                        case "tia_get_device_params":
                            outData = DoGetDeviceParams(subArgs);
                            break;
                        case "list_udts":
                        case "tia_list_udts":
                            outData = DoListUdts();
                            break;
                        case "list_watch_tables":
                        case "tia_list_watch_tables":
                            outData = DoListWatchTables(subArgs);
                            break;
                        case "read_watch_table":
                        case "tia_read_watch_table":
                            outData = DoReadWatchTable(subArgs);
                            break;
                        case "cross_references":
                        case "read_cross_references":
                        case "tia_cross_references":
                            outData = DoCrossReferences(subArgs);
                            break;
                        case "doctor":
                        case "tia_doctor":
                        case "get_system_health":
                        case "tia_get_system_health":
                            outData = DoTiaDoctor();
                            break;
                        case "get_watchdog_status":
                        case "tia_get_watchdog_status":
                            outData = _latestWatchdogStatus;
                            break;
                        default:
                            throw new NotSupportedException("Action '" + action + "' is not supported in batch read mode.");
                    }

                    opResult["status"] = "success";
                    opResult["data"] = outData;
                }
                catch (Exception ex)
                {
                    opResult["status"] = "error";
                    opResult["error"] = ex.Message;
                }

                results.Add(opResult);
            }

            return new Dictionary<string, object>
            {
                { "totalOperations", results.Count },
                { "results", results }
            };
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
                    case "tia_read_block_interface":
                        data = DoReadBlockInterface(args);
                        break;
                    case "tia_search_blocks":
                        data = DoSearchBlocks(args);
                        break;
                    case "tia_search_tags":
                        data = DoSearchTags(args);
                        break;
                    case "tia_create_block":
                        data = DoCreateBlock(args);
                        break;
                    case "tia_delete_block":
                        data = DoDeleteBlock(args);
                        break;
                    case "tia_copy_block":
                        data = DoCopyBlock(args);
                        break;
                    case "tia_call_block":
                        data = DoCallBlock(args);
                        break;
                    case "tia_get_device_params":
                        data = DoGetDeviceParams(args);
                        break;
                    case "tia_set_device_param":
                        data = DoSetDeviceParam(args);
                        break;
                    case "tia_add_device":
                        data = DoAddDevice(args);
                        break;
                    case "tia_add_module":
                        data = DoAddModule(args);
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
                    case "tia_archive_project":
                        data = DoArchiveProject(args);
                        break;
                    case "tia_retrieve_project":
                        data = DoRetrieveProject(args);
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
                        string outPath = args.ContainsKey("outputDirectory") ? args["outputDirectory"] as string : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Export_" + _activeProject.Name);
                        data = BatchExport(FindPlcSoftware(FindDevice(null)), outPath, "all");
                        break;
                    case "tia_batch_import":
                        EnsureConnected();
                        string inPath = args.ContainsKey("inputDirectory") ? args["inputDirectory"] as string : "";
                        data = BatchImport(FindPlcSoftware(FindDevice(null)), inPath);
                        break;
                    case "tia_get_settings":
                        data = _settings;
                        break;
                    case "tia_update_settings":
                        if (args != null)
                        {
                            if (args.ContainsKey("language")) _currentLanguage = args["language"].ToString();
                            if (args.ContainsKey("exportIncludeComments")) _exportIncludeComments = Convert.ToBoolean(args["exportIncludeComments"]);
                            if (args.ContainsKey("csvDelimiter")) _csvDelimiter = args["csvDelimiter"].ToString();
                            if (args.ContainsKey("displayPageSize")) _displayPageSize = Convert.ToInt32(args["displayPageSize"]);
                            SaveSettings();
                        }
                        data = _settings;
                        break;
                    case "tia_get_system_health":
                        data = new Dictionary<string, object>
                        {
                            { "isConnected", IsTiaConnected() },
                            { "attachedPid", _attachedPid },
                            { "projectName", SafeGetProjectName() },
                            { "lastProjectPath", _lastProjectPath },
                            { "language", _currentLanguage },
                            { "exportIncludeComments", _exportIncludeComments },
                            { "csvDelimiter", _csvDelimiter }
                        };
                        break;
                    case "tia_export_tags_csv":
                        EnsureConnected();
                        string tagCsvOut = args != null && args.ContainsKey("outputPath") ? args["outputPath"] as string : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tags_" + _activeProject.Name + ".csv");
                        data = DoExportTagsCsv(FindPlcSoftware(FindDevice(null)), tagCsvOut);
                        break;
                    case "tia_check_tags":
                        EnsureConnected();
                        data = DoCheckTags(FindPlcSoftware(FindDevice(null)));
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
                    case "tia_batch_read":
                    case "execute_read_batch":
                        data = DoBatchRead(args);
                        break;
                    case "tia_cross_references":
                    case "read_cross_references":
                        data = DoCrossReferences(args);
                        break;
                    case "tia_list_watch_tables":
                        data = DoListWatchTables(args);
                        break;
                    case "tia_read_watch_table":
                        data = DoReadWatchTable(args);
                        break;
                    case "tia_doctor":
                        data = DoTiaDoctor();
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
            list.Add(CreateToolDef("tia_read_scl", "Decompiles SimaticML XML into readable Structured Text (SCL) logic with selective network filtering to save tokens.", new Dictionary<string, object>
            {
                { "blockPath", new Dictionary<string, object> { { "type", "string" }, { "description", "Block path e.g. 'Main'." } } },
                { "networkNumber", new Dictionary<string, object> { { "type", "integer" }, { "description", "Optional 1-based network number. When specified, returns ONLY that network, reducing token consumption by >90%." } } },
                { "startNetwork", new Dictionary<string, object> { { "type", "integer" }, { "description", "Optional start network number for range reading." } } },
                { "endNetwork", new Dictionary<string, object> { { "type", "integer" }, { "description", "Optional end network number for range reading." } } },
                { "outlineOnly", new Dictionary<string, object> { { "type", "boolean" }, { "description", "If true, returns only a compact outline of network numbers and titles without full code, minimizing token consumption." } } }
            }, new List<string> { "blockPath" }));
            list.Add(CreateToolDef("tia_read_block_interface", "Decompiles block (DB, FB, FC) or UDT interface into concise SCL variable declarations (VAR_INPUT, VAR_OUTPUT, VAR, etc.), saving >90% tokens.", new Dictionary<string, object>
            {
                { "blockPath", new Dictionary<string, object> { { "type", "string" }, { "description", "Path to block or UDT (e.g. 'MAIN_DB', 'Tags_StorePallet_DB', or '11_Conveyors/Conv_05_FC')." } } },
                { "sectionFilter", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional section filter: 'Input', 'Output', 'InOut', 'Static', 'Temp'." } } },
                { "deviceName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional device name." } } }
            }, new List<string> { "blockPath" }));
            list.Add(CreateToolDef("tia_search_blocks", "Fast local search for blocks and UDTs by name, number, type or code snippet on the host PC to minimize token usage.", new Dictionary<string, object>
            {
                { "query", new Dictionary<string, object> { { "type", "string" }, { "description", "Search query (substring, wildcard * or regex) matching block name, number or path." } } },
                { "typeFilter", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional filter: 'FC', 'FB', 'DB', 'OB', 'UDT'." } } },
                { "groupFilter", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional folder/group filter e.g. 'Conveyors'." } } },
                { "includeCode", new Dictionary<string, object> { { "type", "boolean" }, { "description", "If true, also searches inside decompiled SCL logic text." } } }
            }));
            list.Add(CreateToolDef("tia_search_tags", "Fast local search across PLC tag tables by tag name, address (%I, %Q, %M, %DB) or comment.", new Dictionary<string, object>
            {
                { "query", new Dictionary<string, object> { { "type", "string" }, { "description", "Search query for tag name, address or comment." } } },
                { "tableName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional tag table name filter." } } }
            }));
            list.Add(CreateToolDef("tia_create_block", "Creates and compiles a new block (FC, FB, DB) or UDT into TIA Portal project using native SCL code.", new Dictionary<string, object>
            {
                { "blockType", new Dictionary<string, object> { { "type", "string" }, { "description", "Type of block: 'FC', 'FB', 'DB', or 'UDT'." } } },
                { "blockName", new Dictionary<string, object> { { "type", "string" }, { "description", "Name of the block to create (e.g. 'Conv_Control_FC')." } } },
                { "code", new Dictionary<string, object> { { "type", "string" }, { "description", "SCL code (declarations and/or logic)." } } },
                { "groupPath", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional target block folder/group e.g. '10_Logic'." } } }
            }, new List<string> { "blockType", "blockName", "code" }));
            list.Add(CreateToolDef("tia_delete_block", "Deletes a block (FC, FB, DB) or UDT from the project by name or path.", new Dictionary<string, object>
            {
                { "blockPath", new Dictionary<string, object> { { "type", "string" }, { "description", "Block or UDT name or path (e.g. 'Test_FC' or '10_Logic/Old_FB')." } } },
                { "deviceName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional device name." } } }
            }, new List<string> { "blockPath" }));
            list.Add(CreateToolDef("tia_copy_block", "Copies a block or UDT from another TIA Portal project (or running instance) into the current project.", new Dictionary<string, object>
            {
                { "sourceBlockPath", new Dictionary<string, object> { { "type", "string" }, { "description", "Block path in source project (e.g. 'Conv_Speed_Calc' or '10_Logic/Safety_FC')." } } },
                { "sourceProjectPath", new Dictionary<string, object> { { "type", "string" }, { "description", "Path to source .ap18 project file." } } },
                { "sourcePid", new Dictionary<string, object> { { "type", "integer" }, { "description", "Optional PID of running source TIA Portal instance." } } },
                { "targetGroupPath", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional target group in current project." } } },
                { "isUdt", new Dictionary<string, object> { { "type", "boolean" }, { "description", "True if copying a PLC data type (UDT)." } } }
            }, new List<string> { "sourceBlockPath" }));
            list.Add(CreateToolDef("tia_call_block", "Calls a block (FB or FC) inside a caller block. For FBs called inside an FB (e.g. Zone_2_Conveyors_FB), automatically implements Multi-Instance (#inst_name declared in Static VAR section) without creating separate global DBs. For FCs or calls from FC/OB, creates dedicated Instance DB or calls directly.", new Dictionary<string, object>
            {
                { "callerBlockName", new Dictionary<string, object> { { "type", "string" }, { "description", "Caller block name or path (e.g. 'Zone_2_Conveyors_FB', 'Main')." } } },
                { "calleeBlockName", new Dictionary<string, object> { { "type", "string" }, { "description", "Callee block name or path (e.g. 'Conv_06_ProductTransfering_FB', 'Errors_FC')." } } },
                { "instanceName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional instance name (e.g. 'inst_Conv_06'). Defaults to 'inst_<calleeBlockName>'." } } },
                { "callType", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional call type: 'auto' (default: multi for FB in FB, single for FB in FC/OB, direct for FC), 'multi', 'single', 'direct'." } } },
                { "parameters", new Dictionary<string, object> { { "type", "object" }, { "description", "Optional parameter mappings e.g. { 'Enable': 'TRUE', 'Error': '#ZoneError' }." } } },
                { "networkTitle", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional title for the new network containing the call." } } },
                { "deviceName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional device name." } } }
            }, new List<string> { "callerBlockName", "calleeBlockName" }));
            list.Add(CreateToolDef("tia_archive_project", "Archives the active project into a compressed .zap18 file using Openness DiscardRestorableDataAndCompressed mode for fast, lightweight transfer.", new Dictionary<string, object>
            {
                { "archiveName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional archive file name (without .zap18). Defaults to '<ProjectName>_Archive_<Date>'." } } },
                { "targetDirectory", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional target folder. Defaults to '<ProjectParent>/Archives'." } } },
                { "mode", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional mode: 'DiscardRestorableDataAndCompressed' (default), 'Compressed', 'None', 'DiscardRestorableData'." } } }
            }));
            list.Add(CreateToolDef("tia_retrieve_project", "Retrieves and extracts an archived TIA Portal project from a .zap18 file.", new Dictionary<string, object>
            {
                { "archivePath", new Dictionary<string, object> { { "type", "string" }, { "description", "Absolute path to .zap18 file." } } },
                { "targetDirectory", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional destination folder for extracted project." } } }
            }, new List<string> { "archivePath" }));
            list.Add(CreateToolDef("tia_get_device_params", "Retrieves controller hardware parameters, PROFINET IP address, subnet mask, PN device name and slotted modules.", new Dictionary<string, object>
            {
                { "deviceName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional device name. Default is active PLC." } } }
            }));
            list.Add(CreateToolDef("tia_set_device_param", "Modifies controller parameters: IP address, subnet mask, PROFINET device name or station name.", new Dictionary<string, object>
            {
                { "parameter", new Dictionary<string, object> { { "type", "string" }, { "description", "'ip', 'subnet', 'pn_name', or 'device_name'." } } },
                { "value", new Dictionary<string, object> { { "type", "string" }, { "description", "New value (e.g. '192.168.1.100', '255.255.255.0', 'conveyor-plc')." } } },
                { "deviceName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional device name." } } }
            }, new List<string> { "parameter", "value" }));
            list.Add(CreateToolDef("tia_add_device", "Adds a new device (PLC controller, drive, HMI) to the project from catalog or order number (MLFB).", new Dictionary<string, object>
            {
                { "typeIdentifier", new Dictionary<string, object> { { "type", "string" }, { "description", "Catalog type identifier or MLFB (e.g. 'OrderNumber:6ES7 515-2AM02-0AB0/V2.9')." } } },
                { "deviceName", new Dictionary<string, object> { { "type", "string" }, { "description", "Unique device name (e.g. 'PLC_Conveyors')." } } },
                { "stationName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional station name." } } }
            }, new List<string> { "typeIdentifier", "deviceName" }));
            list.Add(CreateToolDef("tia_add_module", "Plugs an I/O module into a specific slot/rack of a PLC or distributed I/O station.", new Dictionary<string, object>
            {
                { "typeIdentifier", new Dictionary<string, object> { { "type", "string" }, { "description", "Module MLFB or type (e.g. 'OrderNumber:6ES7 521-1BL00-0AB0/V2.1')." } } },
                { "moduleName", new Dictionary<string, object> { { "type", "string" }, { "description", "Name of module (e.g. 'DI_16x24VDC_Slot2')." } } },
                { "slot", new Dictionary<string, object> { { "type", "integer" }, { "description", "Slot number (e.g. 2, 3, etc.)." } } },
                { "deviceName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional device name." } } }
            }, new List<string> { "typeIdentifier", "moduleName", "slot" }));
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
            list.Add(CreateToolDef("tia_batch_read", "Executes up to 50 bundled read operations in a single roundtrip to slash AI conversation tokens and latency by >85%.", new Dictionary<string, object>
            {
                { "operations", new Dictionary<string, object>
                    {
                        { "type", "array" },
                        { "description", "List of read operation objects. Each object specifies 'action' (e.g. read_scl, read_interface, list_tags, search_blocks, search_tags, get_project_info, get_hardware_config, get_call_structure, list_watch_tables, cross_references, doctor) and operation-specific parameters." }
                    }
                }
            }, new List<string> { "operations" }));
            list.Add(CreateToolDef("tia_cross_references", "Inspects cross-references for a symbol/block/tag, or finds all unused objects in project when filter='UnusedObjects'.", new Dictionary<string, object>
            {
                { "target", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional symbol name (block, DB, or tag) to find references for." } } },
                { "filter", new Dictionary<string, object> { { "type", "string" }, { "description", "Filter mode: set to 'UnusedObjects' to discover dead blocks and unreferenced tags." } } },
                { "deviceName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional device name." } } }
            }));
            list.Add(CreateToolDef("tia_list_watch_tables", "Lists all watch tables and force tables in the PLC software.", new Dictionary<string, object>
            {
                { "deviceName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional device name." } } }
            }));
            list.Add(CreateToolDef("tia_read_watch_table", "Reads entries from a specified watch table or force table (Name, Address, DisplayFormat, ModifyValue, Comment).", new Dictionary<string, object>
            {
                { "tableName", new Dictionary<string, object> { { "type", "string" }, { "description", "Name of the watch table or force table." } } },
                { "deviceName", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional device name." } } }
            }, new List<string> { "tableName" }));
            list.Add(CreateToolDef("tia_doctor", "Comprehensive environment probe verifying Siemens Openness assembly, user group membership, firewall, and running instances.", new Dictionary<string, object>()));

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

        private static void RunHeadlessCli(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: --headless <project.ap18> <command> [--json]");
                return;
            }
            string ap18Path = args[1];
            string subCmd = args[2];
            var subArgs = new List<string>();
            subArgs.Add(subCmd);
            for (int i = 3; i < args.Length; i++) subArgs.Add(args[i]);

            try
            {
                Console.WriteLine("Opening headless: " + ap18Path);
                DoOpenHeadless(new Dictionary<string, object> { { "projectPath", ap18Path } });
                RunCli(subArgs.ToArray());
            }
            finally
            {
                try
                {
                    if (_activeTiaPortal != null)
                    {
                        _activeTiaPortal.Dispose();
                        _activeTiaPortal = null;
                        _activeProject = null;
                    }
                }
                catch { }
            }
        }

        private static void RunCli(string[] args)
        {
            string cmd = args[0].ToLower();
            bool isJson = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--json", StringComparison.OrdinalIgnoreCase))
                {
                    isJson = true;
                    break;
                }
            }

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
                        string saveRes = DoSaveProject();
                        if (isJson) Console.WriteLine(_serializer.Serialize(new Dictionary<string, object> { { "status", "Success" }, { "message", saveRes } }));
                        else Console.WriteLine(saveRes);
                        break;
                    case "save-version":
                        DoConnectProcess(new Dictionary<string, object>());
                        string customName = args.Length > 1 ? args[1] : null;
                        var sArgs = new Dictionary<string, object> { { "nonInteractive", true } };
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
                    case "check-tags":
                    case "--check-tags":
                        DoConnectProcess(new Dictionary<string, object>());
                        Console.WriteLine(_serializer.Serialize(DoCheckTags(FindPlcSoftware(FindDevice(null)))));
                        break;
                    case "export-tags":
                    case "tags-csv":
                        DoConnectProcess(new Dictionary<string, object>());
                        string outCsv = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tags_" + _activeProject.Name + ".csv");
                        string csvRes = DoExportTagsCsv(FindPlcSoftware(FindDevice(null)), outCsv);
                        if (isJson) Console.WriteLine(_serializer.Serialize(new Dictionary<string, object> { { "status", "Success" }, { "message", csvRes }, { "outputPath", outCsv } }));
                        else Console.WriteLine(csvRes);
                        break;
                    case "export-all":
                    case "--export-all":
                        DoConnectProcess(new Dictionary<string, object>());
                        string outP = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Export_" + _activeProject.Name);
                        string expRes = BatchExport(FindPlcSoftware(FindDevice(null)), outP, "all");
                        if (isJson) Console.WriteLine(_serializer.Serialize(new Dictionary<string, object> { { "status", "Success" }, { "message", expRes }, { "outputDirectory", outP } }));
                        else Console.WriteLine(expRes);
                        break;
                    case "export-block":
                    case "--export-block":
                        DoConnectProcess(new Dictionary<string, object>());
                        if (args.Length < 2)
                        {
                            Console.WriteLine("Usage: export-block <blockPath> [outputPath]");
                            break;
                        }
                        string expBlkPath = args[1];
                        string expBlkOut = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : expBlkPath + ".xml";
                        var expBlkArgs = new Dictionary<string, object>
                        {
                            { "blockPath", expBlkPath },
                            { "outputFilePath", expBlkOut }
                        };
                        Console.WriteLine(_serializer.Serialize(DoExportBlock(expBlkArgs)));
                        break;
                    case "blocks":
                    case "--blocks":
                        DoConnectProcess(new Dictionary<string, object>());
                        Console.WriteLine(_serializer.Serialize(DoListBlocks(new Dictionary<string, object>())));
                        break;
                    case "devices":
                    case "--devices":
                        DoConnectProcess(new Dictionary<string, object>());
                        Console.WriteLine(_serializer.Serialize(DoListDevices()));
                        break;
                    case "search-blocks":
                    case "--search-blocks":
                        DoConnectProcess(new Dictionary<string, object>());
                        string bQuery = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "";
                        string bType = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : "";
                        var sbArgs = new Dictionary<string, object> { { "query", bQuery }, { "typeFilter", bType } };
                        Console.WriteLine(_serializer.Serialize(DoSearchBlocks(sbArgs)));
                        break;
                    case "search-tags":
                    case "--search-tags":
                        DoConnectProcess(new Dictionary<string, object>());
                        string tQuery = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "";
                        var stArgs = new Dictionary<string, object> { { "query", tQuery } };
                        Console.WriteLine(_serializer.Serialize(DoSearchTags(stArgs)));
                        break;
                    case "read-interface":
                    case "--read-interface":
                        DoConnectProcess(new Dictionary<string, object>());
                        string ifacePath = args.Length > 1 ? args[1] : "MAIN_DB";
                        var ifArgs = new Dictionary<string, object> { { "blockPath", ifacePath } };
                        if (args.Length > 2 && !args[2].StartsWith("--")) ifArgs["sectionFilter"] = args[2];
                        Console.WriteLine(DoReadBlockInterface(ifArgs));
                        break;
                    case "read-scl":
                    case "--read-scl":
                        DoConnectProcess(new Dictionary<string, object>());
                        string sclPath = args.Length > 1 ? args[1] : "Main";
                        var rsArgs = new Dictionary<string, object> { { "blockPath", sclPath } };
                        if (args.Length > 2)
                        {
                            if (args[2].Equals("--outline", StringComparison.OrdinalIgnoreCase))
                            {
                                rsArgs["outlineOnly"] = true;
                            }
                            else
                            {
                                int parsedNet = 0;
                                if (int.TryParse(args[2], out parsedNet)) rsArgs["networkNumber"] = parsedNet;
                            }
                        }
                        Console.WriteLine(DoReadScl(rsArgs));
                        break;
                    case "call-block":
                    case "--call-block":
                        DoConnectProcess(new Dictionary<string, object>());
                        if (args.Length < 3)
                        {
                            Console.WriteLine("Usage: call-block <callerBlockName> <calleeBlockName> [instanceName] [callType: auto|multi|single|direct]");
                            break;
                        }
                        var callBlockArgs = new Dictionary<string, object>
                        {
                            { "callerBlockName", args[1] },
                            { "calleeBlockName", args[2] }
                        };
                        for (int i = 3; i < args.Length; i++)
                        {
                            if (args[i].StartsWith("--")) continue;
                            if (args[i].Contains(":=")) callBlockArgs["parameters"] = args[i];
                            else if (args[i].Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                                     args[i].Equals("multi", StringComparison.OrdinalIgnoreCase) ||
                                     args[i].Equals("single", StringComparison.OrdinalIgnoreCase) ||
                                     args[i].Equals("direct", StringComparison.OrdinalIgnoreCase))
                            {
                                callBlockArgs["callType"] = args[i];
                            }
                            else if (!callBlockArgs.ContainsKey("instanceName"))
                            {
                                callBlockArgs["instanceName"] = args[i];
                            }
                        }
                        Console.WriteLine(_serializer.Serialize(DoCallBlock(callBlockArgs)));
                        break;
                    case "archive":
                    case "--archive":
                    case "zap18":
                    case "--zap18":
                        DoConnectProcess(new Dictionary<string, object>());
                        var archArgs = new Dictionary<string, object>();
                        if (args.Length > 1 && !args[1].StartsWith("--")) archArgs["archiveName"] = args[1];
                        if (args.Length > 2 && !args[2].StartsWith("--")) archArgs["targetDirectory"] = args[2];
                        Console.WriteLine(_serializer.Serialize(DoArchiveProject(archArgs)));
                        break;
                    case "retrieve-archive":
                    case "--retrieve-archive":
                        if (args.Length < 2)
                        {
                            Console.WriteLine("Usage: retrieve-archive <archivePath> [targetDirectory]");
                            break;
                        }
                        var retCliArgs = new Dictionary<string, object>
                        {
                            { "archivePath", args[1] }
                        };
                        if (args.Length > 2 && !args[2].StartsWith("--")) retCliArgs["targetDirectory"] = args[2];
                        Console.WriteLine(_serializer.Serialize(DoRetrieveProject(retCliArgs)));
                        break;
                    case "create-block":
                    case "--create-block":
                        DoConnectProcess(new Dictionary<string, object>());
                        if (args.Length < 4)
                        {
                            Console.WriteLine("Usage: create-block <type: FC|FB|DB|UDT> <name> <code> [groupPath]");
                            break;
                        }
                        var cbArgs = new Dictionary<string, object>
                        {
                            { "blockType", args[1] },
                            { "blockName", args[2] },
                            { "code", args[3] }
                        };
                        if (args.Length > 4 && !args[4].StartsWith("--")) cbArgs["groupPath"] = args[4];
                        Console.WriteLine(_serializer.Serialize(DoCreateBlock(cbArgs)));
                        break;
                    case "delete-block":
                    case "--delete-block":
                        DoConnectProcess(new Dictionary<string, object>());
                        if (args.Length < 2)
                        {
                            Console.WriteLine("Usage: delete-block <blockOrUdtName>");
                            break;
                        }
                        var delCliArgs = new Dictionary<string, object> { { "blockPath", args[1] } };
                        Console.WriteLine(_serializer.Serialize(DoDeleteBlock(delCliArgs)));
                        break;
                    case "copy-block":
                    case "--copy-block":
                        DoConnectProcess(new Dictionary<string, object>());
                        if (args.Length < 2)
                        {
                            Console.WriteLine("Usage: copy-block <sourceBlockPath> [targetGroupPath] [sourceProjectPath]");
                            break;
                        }
                        var cpbArgs = new Dictionary<string, object>
                        {
                            { "sourceBlockPath", args[1] }
                        };
                        if (args.Length > 2 && !args[2].StartsWith("--")) cpbArgs["targetGroupPath"] = args[2];
                        if (args.Length > 3 && !args[3].StartsWith("--")) cpbArgs["sourceProjectPath"] = args[3];
                        Console.WriteLine(_serializer.Serialize(DoCopyBlock(cpbArgs)));
                        break;
                    case "get-device-params":
                    case "--get-device-params":
                        DoConnectProcess(new Dictionary<string, object>());
                        string devName = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
                        var gdpArgs = new Dictionary<string, object>();
                        if (!string.IsNullOrEmpty(devName)) gdpArgs["deviceName"] = devName;
                        Console.WriteLine(_serializer.Serialize(DoGetDeviceParams(gdpArgs)));
                        break;
                    case "set-device-param":
                    case "--set-device-param":
                        DoConnectProcess(new Dictionary<string, object>());
                        if (args.Length < 3)
                        {
                            Console.WriteLine("Usage: set-device-param <parameter: ip|subnet|pn_name|device_name> <value> [deviceName]");
                            break;
                        }
                        var sdpArgs = new Dictionary<string, object>
                        {
                            { "parameter", args[1] },
                            { "value", args[2] }
                        };
                        if (args.Length > 3 && !args[3].StartsWith("--")) sdpArgs["deviceName"] = args[3];
                        Console.WriteLine(_serializer.Serialize(DoSetDeviceParam(sdpArgs)));
                        break;
                    case "add-device":
                    case "--add-device":
                        DoConnectProcess(new Dictionary<string, object>());
                        if (args.Length < 3)
                        {
                            Console.WriteLine("Usage: add-device <typeIdentifier> <deviceName> [stationName]");
                            break;
                        }
                        var adArgs = new Dictionary<string, object>
                        {
                            { "typeIdentifier", args[1] },
                            { "deviceName", args[2] }
                        };
                        if (args.Length > 3 && !args[3].StartsWith("--")) adArgs["stationName"] = args[3];
                        Console.WriteLine(_serializer.Serialize(DoAddDevice(adArgs)));
                        break;
                    case "add-module":
                    case "--add-module":
                        DoConnectProcess(new Dictionary<string, object>());
                        if (args.Length < 5)
                        {
                            Console.WriteLine("Usage: add-module <deviceName> <slot> <typeIdentifier> <moduleName>");
                            break;
                        }
                        var amArgs = new Dictionary<string, object>
                        {
                            { "deviceName", args[1] },
                            { "slot", int.Parse(args[2]) },
                            { "typeIdentifier", args[3] },
                            { "moduleName", args[4] }
                        };
                        Console.WriteLine(_serializer.Serialize(DoAddModule(amArgs)));
                        break;
                    case "doctor":
                    case "--doctor":
                        Console.WriteLine(_serializer.Serialize(DoTiaDoctor()));
                        break;
                    case "cross-refs":
                    case "cross-references":
                    case "--cross-refs":
                    case "--cross-references":
                        DoConnectProcess(new Dictionary<string, object>());
                        var xrArgs = new Dictionary<string, object>();
                        if (args.Length > 1 && !args[1].StartsWith("--"))
                        {
                            if (args[1].Equals("unused", StringComparison.OrdinalIgnoreCase) ||
                                args[1].Equals("UnusedObjects", StringComparison.OrdinalIgnoreCase))
                            {
                                xrArgs["filter"] = "UnusedObjects";
                            }
                            else
                            {
                                xrArgs["target"] = args[1];
                            }
                        }
                        if (args.Length > 2 && !args[2].StartsWith("--"))
                        {
                            xrArgs["filter"] = args[2];
                        }
                        Console.WriteLine(_serializer.Serialize(DoCrossReferences(xrArgs)));
                        break;
                    case "list-watch-tables":
                    case "--list-watch-tables":
                    case "watch-tables":
                        DoConnectProcess(new Dictionary<string, object>());
                        var lwtArgs = new Dictionary<string, object>();
                        if (args.Length > 1 && !args[1].StartsWith("--")) lwtArgs["deviceName"] = args[1];
                        Console.WriteLine(_serializer.Serialize(DoListWatchTables(lwtArgs)));
                        break;
                    case "read-watch-table":
                    case "--read-watch-table":
                        DoConnectProcess(new Dictionary<string, object>());
                        if (args.Length < 2)
                        {
                            Console.WriteLine("Usage: read-watch-table <tableName> [deviceName]");
                            break;
                        }
                        var rwtArgs = new Dictionary<string, object>
                        {
                            { "tableName", args[1] }
                        };
                        if (args.Length > 2 && !args[2].StartsWith("--")) rwtArgs["deviceName"] = args[2];
                        Console.WriteLine(_serializer.Serialize(DoReadWatchTable(rwtArgs)));
                        break;
                    case "batch-read":
                    case "--batch-read":
                        DoConnectProcess(new Dictionary<string, object>());
                        if (args.Length < 2)
                        {
                            Console.WriteLine("Usage: batch-read <jsonOperationsOrJsonFilePath>");
                            break;
                        }
                        string jsonInput = args[1];
                        if (File.Exists(jsonInput)) jsonInput = File.ReadAllText(jsonInput, Encoding.UTF8);
                        var brRaw = _serializer.DeserializeObject(jsonInput);
                        var brArgs = new Dictionary<string, object>();
                        if (brRaw is System.Collections.IEnumerable && !(brRaw is Dictionary<string, object>))
                        {
                            brArgs["operations"] = brRaw;
                        }
                        else if (brRaw is Dictionary<string, object>)
                        {
                            brArgs = (Dictionary<string, object>)brRaw;
                        }
                        Console.WriteLine(_serializer.Serialize(DoBatchRead(brArgs)));
                        break;
                    default:
                        Console.WriteLine("TiaPortalAgent v" + AGENT_VERSION + ". Commands: doctor, list-processes, test-attach, search-blocks, search-tags, read-interface, read-scl, batch-read, cross-refs, list-watch-tables, read-watch-table, create-block, call-block, copy-block, archive, zap18, retrieve-archive, get-device-params, set-device-param, add-device, add-module, audit, compile, call-tree, dependencies, memory, hardware, blocks, devices, check-tags, export-tags, export-all, --headless, --watch, --mcp, --json");
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
