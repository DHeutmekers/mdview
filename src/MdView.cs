// MdView — lightweight Markdown viewer for Windows (WinForms + WebView2).
// Usage: MdView.exe [file.md]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: System.Reflection.AssemblyTitle("MdView")]
[assembly: System.Reflection.AssemblyProduct("MdView")]
[assembly: System.Reflection.AssemblyDescription("Lightweight Markdown viewer")]
[assembly: System.Reflection.AssemblyCompany("Davy Heutmekers")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.1.0")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

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
                    break;
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
}
