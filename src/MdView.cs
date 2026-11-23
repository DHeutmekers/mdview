// MdView — lightweight Markdown viewer for Windows (WinForms + WebView2).
// Usage: MdView.exe [file.md]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: System.Reflection.AssemblyTitle("MdView")]
[assembly: System.Reflection.AssemblyProduct("MdView")]
[assembly: System.Reflection.AssemblyDescription("Lightweight Markdown viewer")]
[assembly: System.Reflection.AssemblyCompany("Davy Heutmekers")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
// Version attributes live in BuildInfo.cs, which build.sh generates from the release tag.

namespace MdView
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        public static void Log(string msg)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n";
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdView");
                Directory.CreateDirectory(dir);
                var log = Path.Combine(dir, "mdview.log");
                if (File.Exists(log) && new FileInfo(log).Length > 256 * 1024) File.Delete(log);
                File.AppendAllText(log, line);
            }
            catch { }
        }

        static void Fail(string where, object ex)
        {
            Log("ERROR (" + where + "): " + ex);
            try { MessageBox.Show(where + ":\n\n" + ex, "MdView error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            Log("start  exe=" + Application.ExecutablePath + "  args=" + string.Join(" ", args) +
                "  64bit=" + Environment.Is64BitProcess + "  clr=" + Environment.Version + "  os=" + Environment.OSVersion);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Fail("Unhandled exception", e.ExceptionObject);
            Application.ThreadException += (s, e) => Fail("UI thread exception", e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 /* TLS 1.2 */; } catch { }
            TaskScheduler.UnobservedTaskException += (s, e) => { Log("Unobserved task exception: " + e.Exception); e.SetObserved(); };
            try { Run(args); }
            catch (Exception ex) { Fail("Startup failed", ex); }
            Log("exit");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Run(string[] args)
        {
            // Per-monitor v2 DPI (Win10 1703+), falling back to system DPI awareness.
            try { if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware(); }
            catch { try { SetProcessDPIAware(); } catch { } }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ViewerForm(args.Length > 0 ? args[0] : null));
        }
    }

    class ViewerForm : Form
    {
        const string AppHost = "app.mdview";
        const string DocHost = "doc.mdview";
        static readonly string[] MarkdownExts = { ".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdx", ".txt" };

        static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdView");

        readonly WebView2 web = new WebView2();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly Timer reloadTimer = new Timer { Interval = 200 };
        readonly Stack<string> history = new Stack<string>();

        FileSystemWatcher watcher;
        string currentPath;
        string pendingFragment;
        bool pageReady;
        int readRetries;
        bool autoCheckDone;
        bool updateBusy;
        Updater.Release updateInfo;

        public ViewerForm(string path)
        {
            Text = "MdView";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Color.White;
            StartPosition = FormStartPosition.Manual;
            RestoreBounds_();
            web.Dock = DockStyle.Fill;
            Controls.Add(web);
            reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); SendDoc(true); };
            FormClosing += (s, e) => SaveBounds();
            if (!string.IsNullOrEmpty(path)) currentPath = Path.GetFullPath(path);
            Program.Log("window created");
            Shown += async (s, e) =>
            {
                Program.Log("window shown");
                try { await InitAsync(); }
                catch (Exception ex) { Program.Log("InitAsync failed: " + ex); MessageBox.Show(this, ex.ToString(), "MdView error"); }
            };
        }

        async Task InitAsync()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") == "ARM64" ? "arm64"
                    : Environment.Is64BitProcess ? "x64" : "x86";
                var loaderDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtimes", "win-" + arch, "native");
                if (File.Exists(Path.Combine(loaderDir, "WebView2Loader.dll")))
                    CoreWebView2Environment.SetLoaderDllFolderPath(loaderDir);
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"), null);
                Program.Log("webview2 env created, version " + env.BrowserVersionString);
                await web.EnsureCoreWebView2Async(env);
                Program.Log("webview2 ready");
            }
            catch (WebView2RuntimeNotFoundException)
            {
                Program.Log("WebView2 runtime not found");
                if (MessageBox.Show(this,
                    "MdView needs the Microsoft Edge WebView2 Runtime, which isn't installed.\n\nOpen the download page now?",
                    "MdView", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    Shell("https://developer.microsoft.com/microsoft-edge/webview2/#download-section");
                Close();
                return;
            }
            catch (Exception ex)
            {
                Program.Log("webview2 init failed: " + ex);
                MessageBox.Show(this, "Could not start the viewer:\n\n" + ex.Message, "MdView",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            var core = web.CoreWebView2;
            var webDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");
            core.SetVirtualHostNameToFolderMapping(AppHost, webDir, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("MDVIEW_DEVTOOLS") == "1";
            core.Settings.IsZoomControlEnabled = true;

            core.WebMessageReceived += OnWebMessage;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += (s, e) => { e.Handled = true; HandleUri(e.Uri); };

            if (currentPath != null) MapDocFolder();
            core.Navigate("https://" + AppHost + "/viewer.html");
        }

        // ---------------- page -> host ----------------
        void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> msg;
            try { msg = json.Deserialize<Dictionary<string, object>>(e.TryGetWebMessageAsString()); }
            catch { return; }
            object t; if (!msg.TryGetValue("type", out t)) return;
            switch (t as string)
            {
                case "ready":
                    Program.Log("page ready, file=" + currentPath);
                    pageReady = true;
                    if (currentPath != null) LoadFile(currentPath, pendingFragment, false);
                    Post(new Dictionary<string, object> { { "type", "version" }, { "version", Updater.CurrentString } });
                    if (!autoCheckDone)
                    {
                        autoCheckDone = true;
                        if (Updater.AutoCheckEnabled && Updater.DueForCheck()) CheckForUpdate(false);
                    }
                    else if (updateInfo != null) PostUpdate("available");
                    if (DefaultApp.ShouldPrompt()) Post(new Dictionary<string, object> { { "type", "defaultPrompt" } });
                    break;
                case "defaultPromptSeen": DefaultApp.MarkPrompted(); break;
                case "makeDefault": DefaultApp.MarkPrompted(); DefaultApp.OpenSettings(); break;
                case "checkUpdate": CheckForUpdate(true); break;
                case "installUpdate": InstallUpdate(); break;
                case "reload": SendDoc(true); break;
                case "openDialog": ShowOpenDialog(); break;
                case "external": Shell(Str(msg, "url")); break;
                case "open": OpenRelative(Str(msg, "href")); break;
                case "back": GoBack(); break;
                case "close": Close(); break;
                case "theme": ApplyTitleBarTheme(msg.ContainsKey("dark") && msg["dark"] is bool && (bool)msg["dark"]); break;
            }
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) ? v as string ?? "" : "";
        }

        // Block navigation away from the viewer; dropped files arrive here as file:// URIs.
        void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (e.Uri.StartsWith("https://" + AppHost + "/", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            BeginInvoke(new Action(() => HandleUri(e.Uri)));
        }

        void HandleUri(string uri)
        {
            Uri u;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out u)) return;
            if (u.IsFile) OpenPath(u.LocalPath, u.Fragment.TrimStart('#'));
            else if (u.Host.Equals(DocHost, StringComparison.OrdinalIgnoreCase) && currentPath != null)
                OpenRelative(Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/')) + u.Fragment);
            else if (u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "mailto") Shell(uri);
        }

        // ---------------- file handling ----------------
        void OpenRelative(string href)
        {
            if (string.IsNullOrEmpty(href) || currentPath == null) return;
            string fragment = null;
            int hash = href.IndexOf('#');
            if (hash >= 0) { fragment = href.Substring(hash + 1); href = href.Substring(0, hash); }
            int q = href.IndexOf('?');
            if (q >= 0) href = href.Substring(0, q);
            if (href.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                Uri fu; if (Uri.TryCreate(href, UriKind.Absolute, out fu)) href = fu.LocalPath;
            }
            else href = Uri.UnescapeDataString(href).Replace('/', Path.DirectorySeparatorChar);
            if (href.Length == 0) return;
            string full;
            try { full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(currentPath), href)); }
            catch { return; }
            OpenPath(full, fragment);
        }

        void OpenPath(string full, string fragment)
        {
            if (Directory.Exists(full)) { Shell(full); return; }
            if (!File.Exists(full))
            {
                MessageBox.Show(this, "File not found:\n" + full, "MdView", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (IsMarkdown(full))
            {
                if (currentPath != null && !PathEquals(full, currentPath)) history.Push(currentPath);
                LoadFile(full, fragment, false);
            }
            else Shell(full);
        }

        static bool IsMarkdown(string p)
        {
            var ext = Path.GetExtension(p).ToLowerInvariant();
            return MarkdownExts.Contains(ext);
        }

        static bool PathEquals(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        void GoBack()
        {
            if (history.Count > 0) LoadFile(history.Pop(), null, false);
        }

        void ShowOpenDialog()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "Markdown files|*.md;*.markdown;*.mdown;*.mkd;*.mkdn;*.mdx|Text files|*.txt|All files|*.*";
                if (currentPath != null) dlg.InitialDirectory = Path.GetDirectoryName(currentPath);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    if (currentPath != null) history.Push(currentPath);
                    LoadFile(dlg.FileName, null, false);
                }
            }
        }

        void LoadFile(string path, string fragment, bool reload)
        {
            bool sameFile = currentPath != null && PathEquals(path, currentPath);
            currentPath = Path.GetFullPath(path);
            pendingFragment = fragment;
            if (!pageReady) return;
            MapDocFolder();
            Watch();
            SendDoc(reload && sameFile);
        }

        void MapDocFolder()
        {
            if (web.CoreWebView2 == null) return;
            try { web.CoreWebView2.ClearVirtualHostNameToFolderMapping(DocHost); } catch { }
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(DocHost, Path.GetDirectoryName(currentPath),
                CoreWebView2HostResourceAccessKind.Allow);
        }

        void SendDoc(bool reload)
        {
            if (currentPath == null || !pageReady) return;
            string text;
            try
            {
                using (var fs = new FileStream(currentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, true))
                    text = sr.ReadToEnd();
                readRetries = 0;
            }
            catch (IOException ex)
            {
                // Editors sometimes hold the file briefly while saving: retry a few times.
                if (reload && readRetries++ < 5) { reloadTimer.Start(); return; }
                Post(new Dictionary<string, object> { { "type", "error" }, { "message", ex.Message } });
                return;
            }
            catch (Exception ex)
            {
                Post(new Dictionary<string, object> { { "type", "error" }, { "message", ex.Message } });
                return;
            }

            Text = Path.GetFileName(currentPath) + " — MdView";
            Post(new Dictionary<string, object> {
                { "type", "doc" },
                { "name", Path.GetFileName(currentPath) },
                { "path", currentPath },
                { "text", text },
                { "baseUrl", "https://" + DocHost + "/" },
                { "fragment", pendingFragment ?? "" },
                { "reload", reload }
            });
            pendingFragment = null;
        }

        void Post(object o)
        {
            if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsString(json.Serialize(o));
        }

        // ---------------- live reload ----------------
        void Watch()
        {
            if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
            try
            {
                watcher = new FileSystemWatcher(Path.GetDirectoryName(currentPath))
                {
                    Filter = Path.GetFileName(currentPath),
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    SynchronizingObject = this
                };
                FileSystemEventHandler changed = (s, e) => { reloadTimer.Stop(); reloadTimer.Start(); };
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Renamed += (s, e) =>
                {
                    if (string.Equals(e.FullPath, currentPath, StringComparison.OrdinalIgnoreCase))
                    { reloadTimer.Stop(); reloadTimer.Start(); }
                };
                watcher.EnableRaisingEvents = true;
            }
            catch { /* network paths etc. — live reload just won't work */ }
        }

        // ---------------- misc ----------------
        // ---------------- updates ----------------
        void PostUpdate(string state, string message = null, int percent = -1)
        {
            var d = new Dictionary<string, object> { { "type", "update" }, { "state", state } };
            if (updateInfo != null) { d["version"] = updateInfo.VersionString; d["url"] = updateInfo.PageUrl; }
            if (message != null) d["message"] = message;
            if (percent >= 0) d["percent"] = percent;
            Post(d);
        }

        async void CheckForUpdate(bool manual)
        {
            if (updateBusy) return;
            updateBusy = true;
            if (manual) PostUpdate("checking");
            try
            {
                var rel = await Updater.GetLatestAsync();
                Updater.MarkChecked();
                if (rel != null && rel.Version > Updater.Current)
                {
                    updateInfo = rel;
                    Program.Log("update available: " + rel.VersionString);
                    PostUpdate("available");
                }
                else if (manual) PostUpdate("uptodate", "You're on the latest version (" + Updater.CurrentString + ")");
            }
            catch (Exception ex)
            {
                Program.Log("update check failed: " + ex.Message);
                if (manual) PostUpdate("checkfailed", "Couldn't check for updates: " + ex.Message);
            }
            finally { updateBusy = false; }
        }

        async void InstallUpdate()
        {
            if (updateInfo == null || updateBusy) return;
            var installDir = Updater.InstalledDir();
            if (installDir == null)
            {
                // Portable copy (not installed via the setup): just open the release page.
                Shell(updateInfo.PageUrl);
                return;
            }
            updateBusy = true;
            try
            {
                PostUpdate("downloading", null, 0);
                var setup = await Updater.DownloadAsync(updateInfo, p => PostUpdate("downloading", null, p));
                PostUpdate("installing");
                Program.Log("running installer " + setup);
                Updater.RunInstallerAndRelaunch(setup, Path.Combine(installDir, "MdView.exe"), currentPath);
                Application.Exit();
            }
            catch (Exception ex)
            {
                Program.Log("update failed: " + ex);
                PostUpdate("error", "Update failed: " + ex.Message);
            }
            finally { updateBusy = false; }
        }

        static void Shell(string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void ApplyTitleBarTheme(bool dark)
        {
            BackColor = dark ? Color.FromArgb(13, 17, 23) : Color.White;
            try
            {
                int v = dark ? 1 : 0;
                if (DwmSetWindowAttribute(Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref v, 4);
            }
            catch { }
        }

        protected override bool ProcessCmdKey(ref Message m, Keys keyData)
        {
            if (keyData == (Keys.Alt | Keys.Left) || keyData == Keys.BrowserBack) { GoBack(); return true; }
            if (keyData == (Keys.Control | Keys.W)) { Close(); return true; }
            return base.ProcessCmdKey(ref m, keyData);
        }

        string BoundsFile { get { return Path.Combine(DataDir, "window.txt"); } }

        void RestoreBounds_()
        {
            var r = new Rectangle(0, 0, 1100, 800);
            bool max = false;
            try
            {
                var p = File.ReadAllText(BoundsFile).Split(',');
                r = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                max = p.Length > 4 && p[4] == "1";
            }
            catch
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                r.Width = Math.Min(r.Width, wa.Width); r.Height = Math.Min(r.Height, wa.Height);
                r.X = wa.X + (wa.Width - r.Width) / 2; r.Y = wa.Y + (wa.Height - r.Height) / 2;
            }
            // Make sure the window is on a visible screen.
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)))
                r.Location = Screen.PrimaryScreen.WorkingArea.Location;
            Bounds = r;
            if (max) WindowState = FormWindowState.Maximized;
        }

        void SaveBounds()
        {
            try
            {
                var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(BoundsFile, string.Format("{0},{1},{2},{3},{4}",
                    r.X, r.Y, r.Width, r.Height, WindowState == FormWindowState.Maximized ? "1" : "0"));
            }
            catch { }
        }
    }

    /// <summary>
    /// One-time offer to make MdView the default app for .md files.
    /// Windows 10/11 don't let apps set defaults themselves, so this opens the Default apps settings.
    /// </summary>
    static class DefaultApp
    {
        static string MarkerFile
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MdView", "default-app-prompted.txt");
            }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int AssocQueryString(int flags, int str, string assoc, string extra,
            StringBuilder outBuf, ref uint outLen);

        /// <summary>Path of the program Windows uses to open .md files, or null if none.</summary>
        static string MdHandler()
        {
            const int ASSOCF_NOTRUNCATE = 0x20, ASSOCSTR_EXECUTABLE = 2;
            uint len = 1024;
            var sb = new StringBuilder((int)len);
            return AssocQueryString(ASSOCF_NOTRUNCATE, ASSOCSTR_EXECUTABLE, ".md", "open", sb, ref len) == 0
                ? sb.ToString() : null;
        }

        public static bool IsDefault()
        {
            try
            {
                var h = MdHandler();
                return h != null && string.Equals(Path.GetFullPath(h), Path.GetFullPath(Application.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Only for an installed copy (a portable copy isn't registered as a .md handler),
        /// only once, and only when MdView isn't already the default.
        /// MDVIEW_NODEFAULTPROMPT=1 turns it off.
        /// </summary>
        public static bool ShouldPrompt()
        {
            if (Environment.GetEnvironmentVariable("MDVIEW_NODEFAULTPROMPT") == "1") return false;
            if (File.Exists(MarkerFile) || Updater.InstalledDir() == null) return false;
            var isDefault = IsDefault();
            Program.Log("default .md handler is MdView: " + isDefault);
            if (isDefault) { MarkPrompted(); return false; }
            return true;
        }

        public static void MarkPrompted()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerFile));
                File.WriteAllText(MarkerFile, DateTime.UtcNow.ToString("o"));
            }
            catch { }
        }

        public static void OpenSettings()
        {
            // Windows 11 opens MdView's own page (its RegisteredApplications name); Windows 10 ignores
            // the parameter and opens the general Default apps page.
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppMachine=MdView") { UseShellExecute = true });
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch { }
            }
        }
    }

    /// <summary>Checks GitHub Releases for a newer version and installs it.</summary>
    static class Updater
    {
        public const string Repo = "DHeutmekers/mdview";

        public class Release
        {
            public Version Version;
            public string VersionString;
            public string Tag;
            public string PageUrl;
            public string SetupName { get { return "MdView-Setup-" + VersionString + ".exe"; } }
            public string DownloadBase { get { return "https://github.com/" + Repo + "/releases/download/" + Tag + "/"; } }
        }

        static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdView"); }
        }

        public static Version Current { get { return Norm(typeof(Updater).Assembly.GetName().Version); } }
        public static string CurrentString { get { var v = Current; return v.Major + "." + v.Minor + "." + v.Build; } }

        static Version Norm(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }

        public static bool AutoCheckEnabled
        {
            // Dev builds (0.0.0) and MDVIEW_NOUPDATE=1 never check automatically.
            get { return Current > new Version(0, 0, 0, 0) && Environment.GetEnvironmentVariable("MDVIEW_NOUPDATE") != "1"; }
        }

        static string StampFile { get { return Path.Combine(DataDir, "last-update-check.txt"); } }

        public static bool DueForCheck()
        {
            try
            {
                var t = DateTime.Parse(File.ReadAllText(StampFile).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind);
                return (DateTime.UtcNow - t.ToUniversalTime()).TotalHours >= 20;
            }
            catch { return true; }
        }

        public static void MarkChecked()
        {
            try { Directory.CreateDirectory(DataDir); File.WriteAllText(StampFile, DateTime.UtcNow.ToString("o")); } catch { }
        }

        static HttpWebRequest Request(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "MdView/" + CurrentString;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 30000;
            return req;
        }

        /// <summary>
        /// Uses the github.com/{repo}/releases/latest redirect instead of the REST API,
        /// so there is no API rate limit and no token is needed.
        /// </summary>
        public static async Task<Release> GetLatestAsync()
        {
            var req = Request("https://github.com/" + Repo + "/releases/latest");
            req.Method = "HEAD";
            req.AllowAutoRedirect = false;
            using (var resp = (HttpWebResponse)await req.GetResponseAsync())
            {
                var loc = resp.Headers["Location"];
                if (string.IsNullOrEmpty(loc) || loc.IndexOf("/releases/tag/", StringComparison.Ordinal) < 0) return null;
                var tag = Uri.UnescapeDataString(loc.Substring(loc.LastIndexOf('/') + 1));
                Version v;
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out v)) return null;
                v = Norm(v);
                return new Release
                {
                    Version = v,
                    VersionString = v.Major + "." + v.Minor + "." + v.Build,
                    Tag = tag,
                    PageUrl = "https://github.com/" + Repo + "/releases/tag/" + Uri.EscapeDataString(tag)
                };
            }
        }

        static async Task<string> GetStringAsync(string url)
        {
            using (var resp = await Request(url).GetResponseAsync())
            using (var sr = new StreamReader(resp.GetResponseStream()))
                return await sr.ReadToEndAsync();
        }

        /// <summary>Downloads the installer to %TEMP% and checks it against the release's SHA256SUMS.txt.</summary>
        public static async Task<string> DownloadAsync(Release rel, Action<int> progress)
        {
            var sums = await GetStringAsync(rel.DownloadBase + "SHA256SUMS.txt");
            string expected = null;
            foreach (var raw in sums.Split('\n'))
            {
                var line = raw.Trim();
                if (line.EndsWith(rel.SetupName, StringComparison.OrdinalIgnoreCase))
                    expected = line.Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
            }
            if (expected == null) throw new Exception(rel.SetupName + " is not listed in SHA256SUMS.txt");

            var target = Path.Combine(Path.GetTempPath(), rel.SetupName);
            using (var resp = await Request(rel.DownloadBase + rel.SetupName).GetResponseAsync())
            using (var input = resp.GetResponseStream())
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
            {
                long total = resp.ContentLength, done = 0;
                var buf = new byte[81920];
                int n, last = -1;
                while ((n = await input.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await output.WriteAsync(buf, 0, n);
                    done += n;
                    if (total > 0)
                    {
                        int pct = (int)(done * 100 / total);
                        if (pct != last) { last = pct; progress(pct); }
                    }
                }
            }

            string actual;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(target))
                actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (actual != expected)
            {
                try { File.Delete(target); } catch { }
                throw new Exception("Checksum mismatch for the downloaded installer");
            }
            return target;
        }

        /// <summary>Folder MdView was installed to by the setup, or null for a portable copy.</summary>
        public static string InstalledDir()
        {
            try
            {
                using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\MdView"))
                {
                    var loc = k == null ? null : k.GetValue("InstallLocation") as string;
                    if (string.IsNullOrEmpty(loc)) return null;
                    var a = Path.GetFullPath(loc).TrimEnd('\\');
                    var b = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
                    return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? a : null;
                }
            }
            catch { return null; }
        }

        static string Ps(string s) { return "'" + s.Replace("'", "''") + "'"; }

        /// <summary>
        /// Starts a hidden, non-elevated PowerShell that runs the installer elevated (UAC prompt),
        /// waits for it, then starts MdView again as the normal user, with the same file.
        /// If the UAC prompt is cancelled, the existing version is simply started again.
        /// </summary>
        public static void RunInstallerAndRelaunch(string setup, string exe, string file)
        {
            var relaunch = "Start-Process -FilePath " + Ps(exe) +
                (string.IsNullOrEmpty(file) ? "" : " -ArgumentList " + Ps("\"" + file + "\""));
            var script =
                "try { Start-Process -FilePath " + Ps(setup) + " -ArgumentList '/S' -Verb RunAs -Wait -ErrorAction Stop } catch { }\n" +
                "Start-Sleep -Milliseconds 500\n" +
                relaunch + "\n" +
                "Remove-Item -LiteralPath " + Ps(setup) + " -ErrorAction SilentlyContinue\n";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
    }
}
