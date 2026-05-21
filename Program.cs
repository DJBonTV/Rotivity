using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Reflection;
using System.Threading;
using System.Diagnostics;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;
using DiscordRPC;
using DiscordButton = DiscordRPC.Button;

namespace Rotivity
{
    public class ActivityData
    {
        public long PlaceId { get; set; }
        public int UserId { get; set; }
        public string JobId { get; set; } = "";
        public string MachineAddress { get; set; } = "";
        public DateTime TimeJoined { get; set; }
    }

    // Custom color table to give context menus a more modern (Windows 11-like) look
    internal class ModernColorTable : ProfessionalColorTable
    {
        private readonly Color _background = Color.White;
        private readonly Color _hover = Color.FromArgb(230, 240, 255); // light blue hover
        private readonly Color _pressed = Color.FromArgb(210, 230, 255);
        private readonly Color _border = Color.FromArgb(220, 225, 230);
        private readonly Color _text = Color.FromArgb(32, 32, 32);

        public override Color ToolStripDropDownBackground => _background;
        public override Color MenuItemSelected => _hover;
        public override Color MenuItemSelectedGradientBegin => _hover;
        public override Color MenuItemSelectedGradientEnd => _hover;
        public override Color MenuItemPressedGradientBegin => _pressed;
        public override Color MenuItemPressedGradientEnd => _pressed;
        public override Color MenuItemBorder => _border;


        // This ensures the side margin matches the rest of the menu background
        public override Color ImageMarginGradientBegin => Color.FromArgb(249, 249, 249);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(249, 249, 249);
        public override Color ImageMarginGradientEnd => Color.FromArgb(249, 249, 249);

        public override Color ToolStripGradientBegin => _background;
        public override Color ToolStripGradientMiddle => _background;
        public override Color ToolStripGradientEnd => _background;

        public override Color MenuBorder => _border;
        public override Color ToolStripBorder => _border;
        public override Color SeparatorDark => Color.FromArgb(240, 240, 240);
        public override Color SeparatorLight => Color.FromArgb(250, 250, 250);

        public override Color CheckBackground => Color.FromArgb(230, 230, 230);
        public override Color CheckSelectedBackground => Color.FromArgb(200, 200, 200);
        public override Color CheckPressedBackground => Color.FromArgb(180, 180, 180);
    }

    public enum ServerType { Public, Private, Reserved }

    public class RobloxApi
    {
        private static readonly HttpClient _http = new();

        public static async Task<long> GetUniverseId(long placeId)
        {
            try
            {
                var json = await _http.GetStringAsync(
                    $"https://apis.roblox.com/universes/v1/places/{placeId}/universe");
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("universeId").GetInt64();
            }
            catch
            {
                return 0;
            }
        }

        public static async Task<string> GetGenre(long universeId)
        {
            try
            {
                var json = await _http.GetStringAsync(
                    $"https://games.roblox.com/v1/games?universeIds={universeId}");
                using var doc = JsonDocument.Parse(json);
                var genre = doc.RootElement.GetProperty("data")[0].GetProperty("genre_l1").GetString();
                if (genre != null) return genre; else return "";
            }
            catch
            {
                return "";
            }
        }

        public static async Task<string?> GetGameName(long universeId)
        {
            try
            {
                var json = await _http.GetStringAsync(
                    $"https://games.roblox.com/v1/games?universeIds={universeId}");
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("data")[0]
                    .GetProperty("name").GetString();
            }
            catch
            {
                return null;
            }
        }

        public static async Task<string?> GetUserName(int userid)
        {
            try
            {
                var json = await _http.GetStringAsync(
                    $"https://users.roblox.com/v1/users/{userid}");
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("name").GetString();
            }
            catch
            {
                return null;
            }
        }

        public static async Task<string?> GetDisplayName(int userid)
        {
            try
            {
                var json = await _http.GetStringAsync(
                    $"https://users.roblox.com/v1/users/{userid}");
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("displayName").GetString();
            }
            catch
            {
                return null;
            }
        }
    }

    public class ActivityWatcher : IDisposable
    {
        public event EventHandler? OnGameJoin;
        public event EventHandler? OnGameLeave;

        public bool InGame { get; private set; }
        public bool GameOpen { get; private set; }

        public ActivityData Data { get; private set; } = new();

        private FileSystemWatcher? _fsWatcher;
        private FileStream? _stream;
        private StreamReader? _reader;

        private string? _currentLogFile;
        private bool _readingLog;
        private bool _disposed;

        private const string GameJoiningEntry = "[FLog::Output] ! Joining game";
        private const string GameJoinedEntry = "[FLog::Network] serverId:";
        private const string GameLeavingEntry = "[FLog::Network] Time to disconnect replication data:";
        private const string GameClosedEntry = "[FLog::SingleSurfaceApp] destroyLuaApp:"; //"[FLog::ClientMemStatus] 2367380423";
        private const string UserIdEntry = "[FLog::GameJoinLoadTime] Report game_join_loadtime:";

        public void Start()
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox\\logs");

            if (!Directory.Exists(logDir))
                return;

            _fsWatcher = new FileSystemWatcher
            {
                Path = logDir,
                Filter = "*.log",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            _fsWatcher.Created += OnLogEvent;
            _fsWatcher.Changed += OnLogEvent;

            Console.WriteLine("Waiting for Roblox log files...");

            // One-time check at startup: if RobloxPlayerBeta is already running, attach to the latest log
            try
            {
                var procs = Process.GetProcessesByName("RobloxPlayerBeta");
                if (procs.Length > 0)
                {
                    GameOpen = true;
                    Console.WriteLine("Attaching to last log");
                    TryAttachToLatestLog();
                }
            }
            catch { }
        }

        private void TryAttachToLatestLog()
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Roblox\\logs");

                if (!Directory.Exists(logDir))
                    return;

                // Find log files sorted by last write (newest first)
                var files = Directory.GetFiles(logDir, "*.log");
                if (files.Length == 0) return;

                Array.Sort(files, (a, b) =>
                {
                    var ta = File.GetLastWriteTimeUtc(a);
                    var tb = File.GetLastWriteTimeUtc(b);
                    return tb.CompareTo(ta);
                });

                string? candidate = null;

                // Look for the most recent file that contains a game join entry
                foreach (var f in files)
                {
                    try
                    {
                        using var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(stream);
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.Contains(GameJoinedEntry) || line.Contains(GameJoiningEntry) || line.Contains(UserIdEntry))
                            {
                                ProcessLine(line);
                                candidate = f;
                                Thread.Sleep(500);
                            }
                        }
                        if (candidate != null) break;
                    }
                    catch { }
                }

                // If none found, fall back to the newest file
                if (candidate == null) candidate = files[0];

                AttachToLog(candidate);
            }
            catch { }
        }

        private void OnLogEvent(object sender, FileSystemEventArgs e)
        {
            if (_disposed)
                return;

            // Only attach to NEW logs when Roblox is not running
            if (!GameOpen && e.ChangeType != WatcherChangeTypes.Created)
                return;

            if (_currentLogFile != null && e.FullPath != _currentLogFile)
            {
                DetachFromLog();
            }

            AttachToLog(e.FullPath);
        }

        private void AttachToLog(string path)
        {
            if (_readingLog)
                return;

            _readingLog = true;
            _currentLogFile = path;
            GameOpen = true;

            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _reader = new StreamReader(_stream);

            _stream.Seek(0, SeekOrigin.End);

            Console.WriteLine($"Attached to log: {Path.GetFileName(path)}");

            Task.Run(ReadLoop);
        }

        private async Task ReadLoop()
        {
            while (!_disposed && GameOpen)
            {
                var line = await _reader!.ReadLineAsync();
                if (line != null)
                {
                    ProcessLine(line);
                }
                else
                {
                    await Task.Delay(500);
                }
            }
        }

        private async void DetachFromLog()
        {
            _reader?.Dispose();
            _stream?.Dispose();

            _reader = null;
            _stream = null;
            _currentLogFile = null;

            _readingLog = false;
            GameOpen = false;

            Console.WriteLine("Detached from log. Attempting relaunch...");
            Start();
        }

        private void ProcessLine(string line)
        {
            if (line.Contains(GameJoiningEntry))
            {
                var match = Regex.Match(
                    line,
                    @"! Joining game '([0-9a-f\-]{36})' place ([0-9]+) at ([0-9\.]+)");

                if (match.Success)
                {
                    Data.PlaceId = long.Parse(match.Groups[2].Value);
                    Data.JobId = match.Groups[1].Value;
                    Data.MachineAddress = match.Groups[3].Value;
                }
            }
            else if (line.Contains(GameJoinedEntry))
            {
                InGame = true;
                Data.TimeJoined = DateTime.Now;
                OnGameJoin?.Invoke(this, EventArgs.Empty);
            }
            else if (line.Contains(UserIdEntry))
            {
                string userIdSection = line.Split("userid:")[1];
                string userIdStr = userIdSection.Split(",")[0];
                Data.UserId = int.TryParse(userIdStr, out int userId) ? userId : 0;
            }
            else if (line.Contains(GameLeavingEntry))
            {
                InGame = false;
                OnGameLeave?.Invoke(this, EventArgs.Empty);
                Data = new ActivityData();
            }
            else if (line.Contains(GameClosedEntry))
            {
                Console.WriteLine("Disconnected from experience...");
                Task.Run(DetachFromLog);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _fsWatcher?.Dispose();
            _reader?.Dispose();
            _stream?.Dispose();
        }
    }

    public class RobloxRichPresence : IDisposable
    {
        private readonly DiscordRpcClient _client;
        private readonly ActivityWatcher _watcher;
        private readonly HttpClient _http = new();

        private readonly Dictionary<long, string> _gameThumbCache = new();
        private readonly Dictionary<long, string> _userThumbCache = new();

        public RobloxRichPresence(ActivityWatcher watcher, string appId)
        {
            _watcher = watcher;
            _client = new DiscordRpcClient(appId);
            _client.Initialize();

            _watcher.OnGameJoin += async (_, _) => await SetPresenceAsync();
            _watcher.OnGameLeave += (_, _) => _client.ClearPresence();
            Program.OnEnablePresenceChanged += async (_, _) => await SetPresenceAsync();
            Program.OnShowUserChanged += async (_, _) => await SetPresenceAsync();
            Program.OnGameLinkShownChanged += async (_, _) => await SetPresenceAsync();
        }

        private async Task<string> GetGameThumbnail(long universeId)
        {
            if (_gameThumbCache.TryGetValue(universeId, out var cached))
                return cached;

            var json = await _http.GetStringAsync(
                $"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}&size=128x128&format=Png");

            using var doc = JsonDocument.Parse(json);
            var url = doc.RootElement.GetProperty("data")[0]
                .GetProperty("imageUrl").GetString()!;

            _gameThumbCache[universeId] = url;
            return url;
        }

        private async Task<string> GetUserThumbnail(int userId)
        {
            if (_userThumbCache.TryGetValue(userId, out var cached))
                return cached;

            var json = await _http.GetStringAsync(
                $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=75x75&format=Png&isCircular=false");

            using var doc = JsonDocument.Parse(json);
            var url = doc.RootElement.GetProperty("data")[0]
                .GetProperty("imageUrl").GetString()!;

            _userThumbCache[userId] = url;
            return url;
        }

        private async Task SetPresenceAsync()
        {
            if (!_watcher.InGame)
                return;

            if (!Program.EnablePresence)
            {
                _client.ClearPresence();
                return;
            }

            var universeId = await RobloxApi.GetUniverseId(_watcher.Data.PlaceId);
            var gameName = await RobloxApi.GetGameName(universeId) ?? "Unknown";
            var image = await GetGameThumbnail(universeId);
            var userImage = await GetUserThumbnail(_watcher.Data.UserId);
            var userName = await RobloxApi.GetUserName(_watcher.Data.UserId) ?? "Unknown";
            var displayName = await RobloxApi.GetDisplayName(_watcher.Data.UserId) ?? "Unknown";

            if (Program.EnablePresence && Program.ShowUser && !Program.GameLinkShown)
            {
                _client.SetPresence(new RichPresence
                {
                    Details = $"Playing {gameName}",
                    State = $"In Game as {displayName}",
                    Timestamps = new Timestamps
                    {
                        Start = _watcher.Data.TimeJoined.ToUniversalTime()
                    },
                    Assets = new Assets
                    {
                        LargeImageKey = image,
                        LargeImageText = gameName,
                        SmallImageKey = userImage,
                        SmallImageText = $"@{userName}"
                    }
                });
            }
            else if (Program.EnablePresence && !Program.ShowUser && !Program.GameLinkShown)
            {
                _client.SetPresence(new RichPresence
                {
                    Details = $"Playing {gameName}",
                    State = "In Game",
                    Timestamps = new Timestamps
                    {
                        Start = _watcher.Data.TimeJoined.ToUniversalTime()
                    },
                    Assets = new Assets
                    {
                        LargeImageKey = image,
                        LargeImageText = gameName,
                        SmallImageKey = "Hidden",
                        SmallImageText = "Hidden"
                    }
                });
            }
            else if (Program.EnablePresence && Program.ShowUser && Program.GameLinkShown)
            {
                _client.SetPresence(new RichPresence
                {
                    Details = $"Playing {gameName}",
                    State = $"In Game as {displayName}",
                    Timestamps = new Timestamps
                    {
                        Start = _watcher.Data.TimeJoined.ToUniversalTime()
                    },
                    Assets = new Assets
                    {
                        LargeImageKey = image,
                        LargeImageText = gameName,
                        SmallImageKey = userImage,
                        SmallImageText = $"@{userName}"
                    },
                    Buttons = new DiscordButton[]
                    {
                        new DiscordButton()
                        {
                            Label = "View on web",
                            Url = $"https://www.roblox.com/games/{_watcher.Data.PlaceId}/"
                        }
                    }
                });
            }
            else if (Program.EnablePresence && !Program.ShowUser && Program.GameLinkShown)
            {
                _client.SetPresence(new RichPresence
                {
                    Details = $"Playing {gameName}",
                    State = "In Game",
                    Timestamps = new Timestamps
                    {
                        Start = _watcher.Data.TimeJoined.ToUniversalTime()
                    },
                    Assets = new Assets
                    {
                        LargeImageKey = image,
                        LargeImageText = gameName,
                        SmallImageKey = "Hidden",
                        SmallImageText = "Hidden"
                    },
                    Buttons = new DiscordButton[]
                    {
                        new DiscordButton()
                        {
                            Label = "View on web",
                            Url = $"https://www.roblox.com/games/{_watcher.Data.PlaceId}/"
                        }
                    }
                });
            };
        }

        public void Dispose()
        {
            _client.Dispose();
            _http.Dispose();
        }
    }

    class Program
    {
        // Controls whether Rich Presence is sent. Toggled from the tray menu.
        public static bool EnablePresence { get; set; } = true;
        public static event EventHandler? OnEnablePresenceChanged;
        public static bool ShowUser { get; set; } = false;
        public static event EventHandler? OnShowUserChanged;
        public static bool GameLinkShown { get; set; } = false;
        public static event EventHandler? OnGameLinkShownChanged;

        // New notifier method: events can only be invoked from within their declaring type.
        // External callers should use this to raise the event.
        public static void NotifyEnablePresenceChanged()
        {
            OnEnablePresenceChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void NotifyShowUserChanged()
        {
            OnShowUserChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void NotifyGameLinkShownChanged()
        {
            OnGameLinkShownChanged?.Invoke(null, EventArgs.Empty);
        }


        static async Task Main()
        {
            Console.WriteLine("Rotivity is starting...");

            var exitTcs = new TaskCompletionSource<bool>();

            using var watcher = new ActivityWatcher();
            using var rpc = new RobloxRichPresence(watcher, "1463609931029545094");

            watcher.Start();

            using var tray = new TrayIcon(() => exitTcs.TrySetResult(true));

            Console.WriteLine("Application minimized to tray. Right-click the tray icon to exit.");

            await exitTcs.Task;
        }
    }

    public sealed class TrayIcon : IDisposable
    {
        private readonly Thread _thread;
        private NotifyIcon? _notifyIcon;
        private readonly Action _onExit;

        public TrayIcon(Action onExit)
        {
            _onExit = onExit ?? throw new ArgumentNullException(nameof(onExit));

            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void Run()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                _notifyIcon = new NotifyIcon
                {
                    Text = "Rotivity",
                    Visible = true
                };

                var cms = new ContextMenuStrip();
                // Modernize appearance
                cms.ShowImageMargin = true;
                cms.Padding = new Padding(5, 5, 5, 5);
                cms.BackColor = Color.FromArgb(249, 249, 249);
                cms.DropShadowEnabled = true;

                // Use a modern renderer with a custom color table
                ToolStripManager.Renderer = new ToolStripProfessionalRenderer(new ModernColorTable());

                var separatortop = new ToolStripSeparator();
                var separatorbottom = new ToolStripSeparator();

                var restartItem = new ToolStripMenuItem("Restart")
                {
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                    ForeColor = Color.FromArgb(32, 32, 32)
                };

                var startupItem = new ToolStripMenuItem("Start with Windows")
                {
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                    ForeColor = Color.FromArgb(32, 32, 32),
                    CheckOnClick = true
                };

                // initialize checked state from registry
                try { startupItem.Checked = IsStartupEnabled(); } catch { startupItem.Checked = false; }

                // Handle the toggle event: add/remove Run key
                startupItem.CheckedChanged += (s, e) =>
                {
                    try
                    {
                        if (startupItem.Checked) EnableStartup(); else DisableStartup();
                    }
                    catch { }
                };

                var ShowUserItem = new ToolStripMenuItem("Show Username")
                {
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                    ForeColor = Color.FromArgb(32, 32, 32),
                    CheckOnClick = true
                };

                // initialize checked state from Program setting
                try { ShowUserItem.Checked = Program.ShowUser; } catch { ShowUserItem.Checked = false; }

                // Toggle program presence flag
                ShowUserItem.CheckedChanged += (s, e) =>
                {
                    try { Program.ShowUser = ShowUserItem.Checked; } catch { }
                    try { Program.NotifyShowUserChanged(); } catch { }
                };

                var gamelinkItem = new ToolStripMenuItem("Show Game Link")
                {
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                    ForeColor = Color.FromArgb(32, 32, 32),
                    CheckOnClick = true
                };

                // initialize checked state from Program setting
                try { gamelinkItem.Checked = Program.GameLinkShown; } catch { gamelinkItem.Checked = false; }

                // Toggle program presence flag
                gamelinkItem.CheckedChanged += (s, e) =>
                {
                    try { Program.GameLinkShown = gamelinkItem.Checked; } catch { }
                    try { Program.NotifyGameLinkShownChanged(); } catch { }
                };

                var enablepresenceItem = new ToolStripMenuItem("Enable Presence")
                {
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                    ForeColor = Color.FromArgb(32, 32, 32),
                    CheckOnClick = true
                };

                // initialize checked state from Program setting
                try { enablepresenceItem.Checked = Program.EnablePresence; } catch { enablepresenceItem.Checked = false; }

                // Toggle program presence flag
                enablepresenceItem.CheckedChanged += (s, e) =>
                {
                    try { Program.EnablePresence = enablepresenceItem.Checked; } catch { }
                    try { Program.NotifyEnablePresenceChanged(); } catch { }
                };

                var quitItem = new ToolStripMenuItem("Quit")
                {
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                    ForeColor = Color.FromArgb(32, 32, 32)
                };

                restartItem.Click += (s, e) =>
                {
                    try
                    {
                        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exe))
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = exe,
                                UseShellExecute = true
                            });
                    }
                    catch { }
                    try { _onExit(); } catch { }
                    Application.ExitThread();
                };

                quitItem.Click += (s, e) => { try { _onExit(); } catch { } Application.ExitThread(); };

                cms.Items.Add(enablepresenceItem);
                cms.Items.Add(separatortop);
                cms.Items.Add(gamelinkItem);
                cms.Items.Add(ShowUserItem);
                cms.Items.Add(startupItem);
                cms.Items.Add(separatorbottom);
                cms.Items.Add(restartItem);
                cms.Items.Add(quitItem);

                

                _notifyIcon.ContextMenuStrip = cms;

                try
                {
                    var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Rotivity.ico");
                    if (File.Exists(icoPath))
                    {
                        _notifyIcon.Icon = new Icon(icoPath);
                    }
                    else
                    {
                        _notifyIcon.Icon = SystemIcons.Application;
                    }
                }
                catch
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }

                //_notifyIcon.BalloonTipTitle = "Rotivity";
                //_notifyIcon.BalloonTipText = "Running in system tray";
                //try { _notifyIcon.ShowBalloonTip(1000); } catch { }

                Application.Run();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            try
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }

                try { Application.ExitThread(); } catch { }

                if (_thread.IsAlive)
                    _thread.Join(500);
            }
            catch { }
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", false);
                if (key == null) return false;
                var val = key.GetValue("Rotivity") as string;
                return !string.IsNullOrEmpty(val);
            }
            catch { return false; }
        }

        private static void EnableStartup()
        {
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return;
                using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                key?.SetValue("Rotivity", $"\"{exe}\"");
            }
            catch { }
        }

        private static void DisableStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                key?.DeleteValue("Rotivity", false);
            }
            catch { }
        }
    }
}