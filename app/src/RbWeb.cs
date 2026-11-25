// ScriptRecycleBin.exe - High-performance Project Recycle Bin Triage Console.
// C# 5 compatible (.NET Framework 4.x csc.exe) with WebView2 Core.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try { SetProcessDPIAware(); }
        catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Task<CoreWebView2Environment> envTask = null;
        try
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecycleBinWeb");
            Directory.CreateDirectory(dataDir);
            envTask = CoreWebView2Environment.CreateAsync(null, dataDir, null);
        }
        catch { }

        Application.Run(new RecycleBinWindow(envTask));
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}

internal sealed class RecycleBinWindow : Form
{
    private const string UniqueHashSentinel = "~unique";
    private const int ScanChunkFirst = 1500;
    private const int ScanChunkNext = 4000;
    private const int HashBatchSize = 250;
    private const int MaxThumbCache = 400;

    private CoreWebView2Controller _ctl;
    private readonly System.Windows.Forms.Timer _boundsSync = new System.Windows.Forms.Timer { Interval = 200 };
    private readonly List<Item> _items = new List<Item>();
    private readonly Dictionary<string, Item> _itemsByPath = new Dictionary<string, Item>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _thumbCache = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly LinkedList<string> _thumbLru = new LinkedList<string>();
    private readonly Task<CoreWebView2Environment> _envTask;
    private readonly Label _splash = new Label();

    private bool _scanInProgress;
    private bool _actionInProgress;
    private int _scanGeneration;
    private CancellationTokenSource _hashCts;
    private CancellationTokenSource _previewCts;
    private CancellationTokenSource _thumbCts;

    private sealed class Item
    {
        public string Path;
        public string MetaPath;
        public string Original;
        public string Name;
        public string Type;
        public string Size;
        public long Bytes;
        public long DeletedTs;
        public long ModifiedTs;
        public string IconDataUrl = null;
        public string PreviewDataUrl = null;
        public string Hash;
        public string Category;
        public bool Child;
    }

    public RecycleBinWindow(Task<CoreWebView2Environment> envTask)
    {
        _envTask = envTask;

        Text = "项目文件回收站";
        ClientSize = new Size(1280, 780);
        MinimumSize = new Size(820, 500);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(17, 19, 24);
        AutoScaleMode = AutoScaleMode.None;
        Icon = BinIcon(true) ?? SystemIcons.Application;

        _splash.Dock = DockStyle.Fill;
        _splash.BackColor = Color.FromArgb(17, 19, 24);
        _splash.ForeColor = Color.FromArgb(148, 163, 184);
        _splash.TextAlign = ContentAlignment.MiddleCenter;
        _splash.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
        _splash.Text = "正在启动空间清理工作站…";
        Controls.Add(_splash);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme(true);
    }

    private void ApplyTitleBarTheme(bool dark)
    {
        try
        {
            int on = dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
            Color bg = dark ? Color.FromArgb(17, 19, 24) : Color.FromArgb(244, 246, 250);
            BackColor = bg;
            _splash.BackColor = bg;
            if (_ctl != null)
            {
                try { _ctl.DefaultBackgroundColor = bg; } catch { }
            }
        }
        catch { }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SyncBounds();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_actionInProgress && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }
        CancelToken(ref _hashCts);
        CancelToken(ref _previewCts);
        CancelToken(ref _thumbCts);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        float k = 1f;
        using (var g = CreateGraphics()) k = g.DpiX / 96f;
        Rectangle wa = Screen.FromControl(this).WorkingArea;
        int targetW = Math.Min((int)Math.Round(1280 * k), wa.Width - 40);
        int targetH = Math.Min((int)Math.Round(780 * k), wa.Height - 40);
        ClientSize = new Size(Math.Max(820, targetW), Math.Max(500, targetH));
        CenterToScreen();
        SyncBounds();

        await InitWebView();
    }

    private async Task InitWebView()
    {
        try
        {
            CoreWebView2Environment env = _envTask != null
                ? await _envTask
                : await CoreWebView2Environment.CreateAsync(null,
                    Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData), "RecycleBinWeb"), null);

            _ctl = await env.CreateCoreWebView2ControllerAsync(Handle);
            _ctl.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            _ctl.IsVisible = false;
            try { _ctl.DefaultBackgroundColor = BackColor; } catch { }

            CoreWebView2Settings s = _ctl.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = true;
            try { s.AreBrowserAcceleratorKeysEnabled = false; } catch { }

            _ctl.CoreWebView2.WebMessageReceived += OnWebMessage;
            _ctl.CoreWebView2.NavigationCompleted += delegate
            {
                try { _ctl.IsVisible = true; } catch { }
                try { _splash.Visible = false; } catch { }
            };

            string page = Path.Combine(
                Path.GetDirectoryName(typeof(Program).Assembly.Location), "ui", "index.html");

            if (File.Exists(page))
                _ctl.CoreWebView2.NavigateToString(File.ReadAllText(page, Encoding.UTF8));
            else
                _ctl.CoreWebView2.NavigateToString(
                    "<body style='background:#111318;color:#cbd5e1;font:14px sans-serif;padding:24px'>缺少 ui\\index.html</body>");

            _boundsSync.Tick += delegate { SyncBounds(); };
            _boundsSync.Start();
        }
        catch (Exception ex)
        {
            try { _splash.Text = "回收站打开失败：" + ex.Message; } catch { }
        }
    }

    private void SyncBounds()
    {
        if (_ctl == null) return;
        var want = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        try
        {
            if (_ctl.Bounds != want)
            {
                _ctl.Bounds = want;
                if (_ctl.CoreWebView2 != null)
                    _ctl.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'))");
            }
        }
        catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SyncBounds();
    }

    // ------------------------------------------------------- web bridge

    private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string cmd = null, previewPath = null, state = null, theme = null, mode = null, text = null, defaultName = null, ext = null;
        string[] paths = new string[0];
        int delta = 0, reqId = 0;
        bool confirmed = false, tail = false;

        try
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = 67108864 };
            var map = ser.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);

            if (map != null)
            {
                object v;
                if (map.TryGetValue("cmd", out v) && v != null) cmd = v.ToString();
                if (map.TryGetValue("path", out v) && v != null) previewPath = v.ToString();
                if (map.TryGetValue("state", out v) && v != null) state = v.ToString();
                if (map.TryGetValue("theme", out v) && v != null) theme = v.ToString();
                if (map.TryGetValue("mode", out v) && v != null) mode = v.ToString();
                if (map.TryGetValue("text", out v) && v != null) text = v.ToString();
                if (map.TryGetValue("defaultName", out v) && v != null) defaultName = v.ToString();
                if (map.TryGetValue("ext", out v) && v != null) ext = v.ToString();
                if (map.TryGetValue("delta", out v) && v != null) int.TryParse(v.ToString(), out delta);
                if (map.TryGetValue("reqId", out v) && v != null) int.TryParse(v.ToString(), out reqId);
                if (map.TryGetValue("confirmed", out v) && v is bool) confirmed = (bool)v;
                if (map.TryGetValue("tail", out v) && v is bool) tail = (bool)v;
                paths = ReadActionPaths(map);
            }
        }
        catch { }

        switch (cmd)
        {
            case "ready":
                SendState();
                SendItems();
                break;
            case "saveState":
                SaveState(state);
                break;
            case "setTheme":
                ApplyTitleBarTheme(theme != "light");
                break;
            case "refresh":
                SendItems();
                break;
            case "restore":
                SafeAction(paths, "restore", null, confirmed);
                break;
            case "restoreTo":
                SafeAction(paths, "restoreTo", mode, confirmed);
                break;
            case "delete":
                SafeAction(paths, "delete", null, confirmed);
                break;
            case "copyTo":
                SafeAction(paths, "export", null, confirmed);
                break;
            case "emptyBin":
                EmptyBinAsync(confirmed);
                break;
            case "zoom":
                Zoom(delta);
                break;
            case "preview":
                SendPreviewAsync(previewPath, tail, reqId);
                break;
            case "thumbnails":
                SendThumbnailsAsync(paths, reqId);
                break;
            case "pickProjectFolder":
                PickProjectFolder();
                break;
            case "openLocation":
                OpenLocation(paths);
                break;
            case "copyPaths":
                CopyTextToClipboard(text, paths);
                break;
            case "showProperties":
                ShowProperties(paths);
                break;
            case "checkConflicts":
                CheckConflictsAsync(paths, reqId);
                break;
            case "exportReport":
                ExportReport(text, defaultName, ext);
                break;
        }
    }

    private static string[] ReadActionPaths(Dictionary<string, object> message)
    {
        object value;
        if (!message.TryGetValue("paths", out value)) return new string[0];
        var list = value as System.Collections.IList;
        if (list == null) return new string[0];

        var paths = new List<string>(list.Count);
        foreach (object entry in list)
        {
            string path = entry as string;
            if (!string.IsNullOrEmpty(path)) paths.Add(path);
        }
        return paths.ToArray();
    }

    private static string AppDataDir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptRecycleAssistant");
    }

    private static string StateFile() { return Path.Combine(AppDataDir(), "settings.json"); }
    private static string HashCacheFile() { return Path.Combine(AppDataDir(), "hash-cache.tsv"); }

    private void SaveState(string state)
    {
        try
        {
            if (state == null || state.Length > 4194304) return;
            new JavaScriptSerializer { MaxJsonLength = 4194304 }.DeserializeObject(state);
            Directory.CreateDirectory(AppDataDir());
            File.WriteAllText(StateFile(), state, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            PostToast("error", "无法保存设置：" + ex.Message);
        }
    }

    private void SendState()
    {
        try
        {
            var payload = new Dictionary<string, object>();
            payload["type"] = "state";
            payload["data"] = File.Exists(StateFile()) ? File.ReadAllText(StateFile(), Encoding.UTF8) : "{}";
            PostJson(new JavaScriptSerializer { MaxJsonLength = 4194304 }.Serialize(payload));
        }
        catch { }
    }

    // ------------------------------------------------------- persistent hash cache

    private sealed class CachedHashEntry
    {
        public long Bytes;
        public long ModifiedTs;
        public long DeletedTs;
        public string Hash;
    }

    private static Dictionary<string, CachedHashEntry> LoadDiskHashCache()
    {
        var map = new Dictionary<string, CachedHashEntry>(StringComparer.Ordinal);
        try
        {
            string file = HashCacheFile();
            if (!File.Exists(file)) return map;
            using (var reader = new StreamReader(file, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length < 5) continue;
                    long bytes, mod, del;
                    if (long.TryParse(parts[1], out bytes) && long.TryParse(parts[2], out mod) && long.TryParse(parts[3], out del))
                    {
                        if (!string.IsNullOrEmpty(parts[4]))
                        {
                            map[parts[0]] = new CachedHashEntry
                            {
                                Bytes = bytes,
                                ModifiedTs = mod,
                                DeletedTs = del,
                                Hash = parts[4]
                            };
                        }
                    }
                }
            }
        }
        catch { }
        return map;
    }

    private static void SaveDiskHashCache(List<Item> items)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir());
            string tmp = HashCacheFile() + ".tmp";
            using (var writer = new StreamWriter(tmp, false, Encoding.UTF8))
            {
                foreach (Item item in items)
                {
                    if (string.IsNullOrEmpty(item.Hash) || item.Hash == UniqueHashSentinel) continue;
                    if (item.Path.IndexOf('\t') >= 0 || item.Path.IndexOf('\n') >= 0) continue;
                    writer.Write(item.Path);
                    writer.Write('\t');
                    writer.Write(item.Bytes);
                    writer.Write('\t');
                    writer.Write(item.ModifiedTs);
                    writer.Write('\t');
                    writer.Write(item.DeletedTs);
                    writer.Write('\t');
                    writer.WriteLine(item.Hash);
                }
            }
            string dest = HashCacheFile();
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
        }
        catch { }
    }

    // ------------------------------------------------------- file categories

    private static readonly string[] FileCategories = {
        "缓存与临时文件|.tmp .temp .cache .bak .old .part .partial .crdownload .swp .pyc .pyo",
        "日志|.log .out .err .trace",
        "脚本与源码|.ps1 .psm1 .py .bat .cmd .js .jsx .mjs .cjs .ts .tsx .sh .bash .zsh .rb .php .lua .sql .cs .cpp .c .h .hpp .go .rs .java .kt .swift .vue .svelte .ipynb .html .htm .css .scss .less",
        "文档|.txt .md .markdown .rst .pdf .doc .docx .odt .rtf .ppt .pptx readme license",
        "数据与配置|.json .jsonl .ndjson .csv .tsv .yaml .yml .toml .ini .xml .env .gitignore .config .conf .db .sqlite .sqlite3 .parquet .npy .xls .xlsx",
        "图片|.png .jpg .jpeg .gif .bmp .webp .svg .ico .tif .tiff .avif .heic .psd",
        "音频与视频|.mp3 .wav .flac .m4a .ogg .aac .mp4 .webm .mov .mkv .avi .wmv .flv",
        "压缩包|.zip .7z .rar .tar .gz .xz .bz2 .tgz .iso",
        "构建产物与模型|.exe .dll .msi .obj .pdb .whl .onnx .safetensors .gguf .pt .pth .bin .apk .jar"
    };
    private static readonly Dictionary<string, string> CategoryByExtension = BuildCategoryIndex();
    private static readonly HashSet<string> TextTypes = new HashSet<string>(
        (".ps1 .psm1 .py .bat .cmd .js .jsx .mjs .cjs .ts .tsx .sh .bash .zsh .rb .php .lua .sql .cs .cpp .c .h .hpp .go .rs .java .kt .swift .vue .svelte .ipynb .html .htm .css .scss .less " +
         ".txt .md .markdown .rst readme license .json .jsonl .ndjson .csv .tsv .yaml .yml .toml .ini .xml .env .gitignore .config .conf .log .out .err .trace .svg").Split(' '));
    private static readonly HashSet<string> DirectImageTypes = new HashSet<string>(
        (".png .jpg .jpeg .gif .bmp .tif .tiff").Split(' '));
    private static readonly HashSet<string> ShellThumbTypes = new HashSet<string>(
        (".webp .ico .heic .avif .psd .mp4 .webm .mov .mkv .avi .wmv .flv").Split(' '));

    private static Dictionary<string, string> BuildCategoryIndex()
    {
        var index = new Dictionary<string, string>();
        foreach (string entry in FileCategories)
        {
            string[] parts = entry.Split('|');
            foreach (string extension in parts[1].Split(' ')) index[extension] = parts[0];
        }
        return index;
    }

    private static string FileKind(string name)
    {
        string ext = Path.GetExtension(name);
        return (string.IsNullOrEmpty(ext) ? name : ext).ToLowerInvariant();
    }

    // ------------------------------------------------------- scan snapshot (ScanTest compatible)

    private sealed class ScanSnapshot
    {
        public readonly List<Item> Items = new List<Item>();
        public readonly List<string> Errors = new List<string>();
        private readonly Dictionary<string, Item> _previous;
        private readonly Dictionary<string, CachedHashEntry> _diskCache;

        public ScanSnapshot(IEnumerable<Item> previous) : this(previous, null) { }

        public ScanSnapshot(IEnumerable<Item> previous, Dictionary<string, CachedHashEntry> diskCache)
        {
            _previous = new Dictionary<string, Item>(StringComparer.Ordinal);
            foreach (Item item in previous)
            {
                if (!string.IsNullOrEmpty(item.Hash) && item.Hash != UniqueHashSentinel)
                    _previous[item.Path] = item;
            }
            _diskCache = diskCache;
        }

        internal void AddRecycledFiles(Item root)
        {
            try
            {
                if ((File.GetAttributes(root.Path) & FileAttributes.ReparsePoint) != 0)
                {
                    Errors.Add(root.Original + "：跳过链接或重解析点");
                    return;
                }
                if (!Directory.Exists(root.Path))
                {
                    AddFile(root);
                    return;
                }
                AddFolderFiles(root, root.Path, root.Original);
            }
            catch (Exception ex) { Errors.Add(root.Original + "：" + ex.Message); }
        }

        private void AddFolderFiles(Item root, string path, string original)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(path))
                {
                    try
                    {
                        if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) continue;
                        var fi = new FileInfo(f);
                        AddFile(new Item
                        {
                            Path = f,
                            MetaPath = root.MetaPath,
                            Original = Path.Combine(original, fi.Name),
                            Name = fi.Name,
                            Type = fi.Extension.ToUpperInvariant(),
                            Size = HumanSize(fi.Length),
                            Bytes = fi.Length,
                            DeletedTs = root.DeletedTs,
                            ModifiedTs = fi.LastWriteTimeUtc.Ticks,
                            Child = true
                        });
                    }
                    catch (Exception ex) { Errors.Add(f + "：" + ex.Message); }
                }
                foreach (string d in Directory.EnumerateDirectories(path))
                {
                    try
                    {
                        if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0)
                            AddFolderFiles(root, d, Path.Combine(original, Path.GetFileName(d)));
                    }
                    catch (Exception ex) { Errors.Add(d + "：" + ex.Message); }
                }
            }
            catch (Exception ex) { Errors.Add(original + "：" + ex.Message); }
        }

        private void AddFile(Item item)
        {
            string category;
            item.Category = CategoryByExtension.TryGetValue(FileKind(item.Name), out category) ? category : "其他";
            Item previous;
            if (_previous.TryGetValue(item.Path, out previous) && previous.Original == item.Original
                && previous.DeletedTs == item.DeletedTs && previous.Bytes == item.Bytes
                && previous.ModifiedTs == item.ModifiedTs)
            {
                item.Hash = previous.Hash;
            }
            else if (_diskCache != null)
            {
                CachedHashEntry cached;
                if (_diskCache.TryGetValue(item.Path, out cached) && cached.Bytes == item.Bytes
                    && cached.ModifiedTs == item.ModifiedTs && cached.DeletedTs == item.DeletedTs)
                {
                    item.Hash = cached.Hash;
                }
            }
            Items.Add(item);
        }

        // Preserved for ScanTest.cs compatibility: computes SHA-256 for all unhashed items.
        public string VerifyNewFiles()
        {
            var pending = new List<Item>();
            foreach (Item item in Items)
            {
                if (item.Hash == null) pending.Add(item);
            }
            if (pending.Count == 0) return null;

            var hashes = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var errs = new ConcurrentBag<string>();
            var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount)) };

            Parallel.ForEach(pending, opts, item =>
            {
                try
                {
                    item.Hash = ContentHash(item.Path);
                }
                catch (Exception ex)
                {
                    item.Hash = string.Empty;
                    errs.Add(item.Original + "：无法计算哈希：" + ex.Message);
                }
                hashes[item.Path] = item.Hash;
            });

            foreach (string err in errs) Errors.Add(err);
            return SerializeHashesBatch(hashes, Errors, 100, true);
        }
    }

    private static string ContentHash(string path)
    {
        using (var hash = System.Security.Cryptography.SHA256.Create())
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 131072, FileOptions.SequentialScan))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
    }

    // Fast 8KB head+tail fingerprint to avoid reading huge files that only share the same byte length.
    private static string QuickFingerprint(string path, long bytes)
    {
        using (var hash = System.Security.Cryptography.SHA256.Create())
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192))
        {
            byte[] buf = new byte[8192];
            int headRead = stream.Read(buf, 0, Math.Min(4096, buf.Length));
            int tailRead = 0;
            if (bytes > 4096)
            {
                stream.Seek(Math.Max(0, bytes - 4096), SeekOrigin.Begin);
                tailRead = stream.Read(buf, 4096, 4096);
            }
            hash.TransformBlock(buf, 0, headRead, null, 0);
            hash.TransformFinalBlock(buf, 4096, tailRead);
            return BitConverter.ToString(hash.Hash).Replace("-", "");
        }
    }

    // ------------------------------------------------------- fast manual JSON writers

    private static void AppendJsonString(StringBuilder sb, string value)
    {
        if (value == null) { sb.Append("null"); return; }
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4"));
                    }
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static string SerializeItemsChunk(List<Item> items, int offset, int count, int total, bool isFirst, bool isLast, List<string> errors)
    {
        var sb = new StringBuilder(Math.Min(count * 260 + 512, 8 * 1024 * 1024));
        sb.Append("{\"type\":\"itemsChunk\",\"first\":");
        sb.Append(isFirst ? "true" : "false");
        sb.Append(",\"last\":");
        sb.Append(isLast ? "true" : "false");
        sb.Append(",\"total\":");
        sb.Append(total);
        sb.Append(",\"items\":[");
        int end = Math.Min(items.Count, offset + count);
        for (int i = offset; i < end; i++)
        {
            if (i > offset) sb.Append(',');
            Item item = items[i];
            sb.Append("{\"path\":"); AppendJsonString(sb, item.Path);
            sb.Append(",\"name\":"); AppendJsonString(sb, item.Name);
            sb.Append(",\"original\":"); AppendJsonString(sb, item.Original);
            sb.Append(",\"hash\":"); AppendJsonString(sb, item.Hash);
            sb.Append(",\"category\":"); AppendJsonString(sb, item.Category);
            sb.Append(",\"size\":"); AppendJsonString(sb, item.Size);
            sb.Append(",\"bytes\":"); sb.Append(item.Bytes);
            sb.Append(",\"deletedTs\":"); sb.Append(item.DeletedTs);
            sb.Append(",\"modifiedTs\":"); sb.Append(item.ModifiedTs);
            sb.Append('}');
        }
        sb.Append(']');
        if (isLast && errors != null)
        {
            sb.Append(",\"errors\":[");
            for (int i = 0; i < errors.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendJsonString(sb, errors[i]);
            }
            sb.Append(']');
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string SerializeHashesBatch(IDictionary<string, string> hashes, List<string> errors, int percent, bool done)
    {
        var sb = new StringBuilder(hashes.Count * 120 + 256);
        sb.Append("{\"type\":\"hashes\",\"done\":");
        sb.Append(done ? "true" : "false");
        sb.Append(",\"percent\":");
        sb.Append(percent);
        sb.Append(",\"hashes\":{");
        bool first = true;
        foreach (KeyValuePair<string, string> kv in hashes)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendJsonString(sb, kv.Key);
            sb.Append(':');
            AppendJsonString(sb, kv.Value);
        }
        sb.Append('}');
        if (done && errors != null)
        {
            sb.Append(",\"errors\":[");
            for (int i = 0; i < errors.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendJsonString(sb, errors[i]);
            }
            sb.Append(']');
        }
        sb.Append('}');
        return sb.ToString();
    }

    // ------------------------------------------------------- high-perf scanning & duplicate verification

    private async void SendItems()
    {
        if (_scanInProgress || _actionInProgress || IsDisposed) return;
        _scanInProgress = true;
        int gen = Interlocked.Increment(ref _scanGeneration);
        CancelToken(ref _hashCts);
        PostWorkStatus(true, "正在扫描回收站…", "scan");
        string error = null;

        try
        {
            List<Item> previousSnapshot = new List<Item>(_items);
            ScanSnapshot scan = await Task.Run(() =>
            {
                var diskCache = LoadDiskHashCache();
                var snap = new ScanSnapshot(previousSnapshot, diskCache);
                ScanRecycleBinInto(snap);
                return snap;
            });

            if (IsDisposed || gen != _scanGeneration) return;

            _items.Clear();
            _items.AddRange(scan.Items);
            RebuildPathIndex();
            ClearThumbCache();
            UpdateBinIcon();

            // Stream items in chunks so UI renders first screen immediately without giant JSON freeze
            int total = scan.Items.Count;
            if (total == 0)
            {
                PostJson(SerializeItemsChunk(scan.Items, 0, 0, 0, true, true, scan.Errors));
            }
            else
            {
                int offset = 0;
                bool first = true;
                while (offset < total)
                {
                    if (IsDisposed || gen != _scanGeneration) return;
                    int chunkSize = first ? ScanChunkFirst : ScanChunkNext;
                    int take = Math.Min(chunkSize, total - offset);
                    bool last = (offset + take) >= total;
                    string chunkJson = SerializeItemsChunk(scan.Items, offset, take, total, first, last, last ? scan.Errors : null);
                    PostJson(chunkJson);
                    offset += take;
                    first = false;
                    if (!last) await Task.Delay(8);
                }
            }

            _scanInProgress = false;
            PostWorkStatus(false, null, "scan");

            // Start non-blocking background duplicate check (size pre-filter + 8KB quick hash + parallel SHA-256)
            RunBackgroundDuplicateCheck(scan, gen);
            return;
        }
        catch (Exception ex)
        {
            error = "读取回收站失败：" + ex.GetBaseException().Message;
        }
        finally
        {
            if (_scanInProgress)
            {
                _scanInProgress = false;
                PostWorkStatus(false, error, "scan");
            }
        }
    }

    private void RebuildPathIndex()
    {
        _itemsByPath.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            _itemsByPath[_items[i].Path] = _items[i];
        }
    }

    private void RunBackgroundDuplicateCheck(ScanSnapshot scan, int gen)
    {
        var cts = new CancellationTokenSource();
        _hashCts = cts;
        CancellationToken token = cts.Token;

        Task.Run(() =>
        {
            try
            {
                var items = scan.Items;
                if (items.Count == 0) return;

                // 1. Group by exact byte size: unique sizes cannot have duplicate content
                var bySize = new Dictionary<long, List<Item>>();
                for (int i = 0; i < items.Count; i++)
                {
                    Item it = items[i];
                    List<Item> bucket;
                    if (!bySize.TryGetValue(it.Bytes, out bucket))
                    {
                        bucket = new List<Item>();
                        bySize[it.Bytes] = bucket;
                    }
                    bucket.Add(it);
                }

                var immediateUpdates = new Dictionary<string, string>(StringComparer.Ordinal);
                var candidates = new List<Item>();
                foreach (KeyValuePair<long, List<Item>> kv in bySize)
                {
                    if (token.IsCancellationRequested) return;
                    if (kv.Value.Count == 1)
                    {
                        Item only = kv.Value[0];
                        if (only.Hash == null)
                        {
                            only.Hash = UniqueHashSentinel;
                            immediateUpdates[only.Path] = UniqueHashSentinel;
                        }
                    }
                    else
                    {
                        for (int j = 0; j < kv.Value.Count; j++)
                        {
                            Item cand = kv.Value[j];
                            if (cand.Hash == null || cand.Hash == UniqueHashSentinel)
                            {
                                cand.Hash = null;
                                candidates.Add(cand);
                            }
                        }
                    }
                }

                // Flush unique-size markers immediately so UI knows they aren't duplicates
                if (immediateUpdates.Count > 0 && !token.IsCancellationRequested)
                {
                    string uJson = SerializeHashesBatch(immediateUpdates, null, candidates.Count == 0 ? 100 : 10, candidates.Count == 0);
                    BeginInvokeSafe(() => { if (gen == _scanGeneration) PostJson(uJson); });
                }

                if (candidates.Count == 0) return;

                BeginInvokeSafe(() =>
                {
                    if (gen == _scanGeneration)
                        PostJson(SerializeMessage(new { type = "hashProgress", active = true, percent = 10, total = candidates.Count, done = 0 }));
                });

                // 2. For large files (>64KB) sharing size, do an 8KB head+tail quick pre-hash to skip non-matching pairs
                var toFullHash = new List<Item>();
                var quickBuckets = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
                var quickUnique = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (Item cand in candidates)
                {
                    if (token.IsCancellationRequested) return;
                    if (cand.Bytes <= 65536)
                    {
                        toFullHash.Add(cand);
                    }
                    else
                    {
                        try
                        {
                            string qk = cand.Bytes.ToString() + ":" + QuickFingerprint(cand.Path, cand.Bytes);
                            List<Item> qb;
                            if (!quickBuckets.TryGetValue(qk, out qb))
                            {
                                qb = new List<Item>();
                                quickBuckets[qk] = qb;
                            }
                            qb.Add(cand);
                        }
                        catch (Exception ex)
                        {
                            cand.Hash = string.Empty;
                            quickUnique[cand.Path] = string.Empty;
                            lock (scan.Errors) { scan.Errors.Add(cand.Original + "：无法读取文件：" + ex.Message); }
                        }
                    }
                }

                foreach (KeyValuePair<string, List<Item>> kv in quickBuckets)
                {
                    if (kv.Value.Count == 1)
                    {
                        kv.Value[0].Hash = UniqueHashSentinel;
                        quickUnique[kv.Value[0].Path] = UniqueHashSentinel;
                    }
                    else
                    {
                        toFullHash.AddRange(kv.Value);
                    }
                }

                if (quickUnique.Count > 0 && !token.IsCancellationRequested)
                {
                    bool allDone = toFullHash.Count == 0;
                    string qJson = SerializeHashesBatch(quickUnique, allDone ? scan.Errors : null, allDone ? 100 : 25, allDone);
                    BeginInvokeSafe(() => { if (gen == _scanGeneration) PostJson(qJson); });
                }

                if (toFullHash.Count == 0)
                {
                    BeginInvokeSafe(() =>
                    {
                        if (gen == _scanGeneration)
                            PostJson(SerializeMessage(new { type = "hashProgress", active = false, percent = 100, total = candidates.Count, done = candidates.Count }));
                    });
                    return;
                }

                // 3. Parallel full SHA-256 on genuine duplicate candidates, streaming batches to UI
                int processed = 0;
                int totalWork = toFullHash.Count;
                var batch = new Dictionary<string, string>(StringComparer.Ordinal);
                object batchLock = new object();
                var opts = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount)),
                    CancellationToken = token
                };

                try
                {
                    Parallel.ForEach(toFullHash, opts, item =>
                    {
                        if (token.IsCancellationRequested) return;
                        try
                        {
                            item.Hash = ContentHash(item.Path);
                        }
                        catch (Exception ex)
                        {
                            item.Hash = string.Empty;
                            lock (scan.Errors) { scan.Errors.Add(item.Original + "：无法计算哈希：" + ex.Message); }
                        }

                        int doneNow = Interlocked.Increment(ref processed);
                        Dictionary<string, string> flush = null;
                        lock (batchLock)
                        {
                            batch[item.Path] = item.Hash;
                            if (batch.Count >= HashBatchSize)
                            {
                                flush = new Dictionary<string, string>(batch, StringComparer.Ordinal);
                                batch.Clear();
                            }
                        }

                        if (flush != null && !token.IsCancellationRequested)
                        {
                            int pct = 25 + (int)(doneNow * 70L / Math.Max(1, totalWork));
                            string json = SerializeHashesBatch(flush, null, pct, false);
                            BeginInvokeSafe(() => { if (gen == _scanGeneration) PostJson(json); });
                        }
                    });
                }
                catch (OperationCanceledException) { return; }

                if (token.IsCancellationRequested) return;

                Dictionary<string, string> remaining;
                lock (batchLock)
                {
                    remaining = new Dictionary<string, string>(batch, StringComparer.Ordinal);
                    batch.Clear();
                }

                string finalJson = SerializeHashesBatch(remaining, scan.Errors, 100, true);
                BeginInvokeSafe(() =>
                {
                    if (gen == _scanGeneration)
                    {
                        PostJson(finalJson);
                        PostJson(SerializeMessage(new { type = "hashProgress", active = false, percent = 100, total = totalWork, done = totalWork }));
                    }
                });

                // Persist computed hashes to disk for near-instant future startups
                SaveDiskHashCache(items);
            }
            catch { }
        }, token);
    }

    private static void ScanRecycleBinInto(ScanSnapshot scan)
    {
        string sid;
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            sid = identity.User.Value;
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable
                    && drive.DriveType != DriveType.Ram) continue;
                if (!drive.IsReady) continue;
                string directory = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
                if (!Directory.Exists(directory)) continue;
                foreach (string path in Directory.EnumerateFileSystemEntries(directory, "$R*"))
                {
                    Item item = ReadItem(path);
                    if (item != null) scan.AddRecycledFiles(item);
                }
            }
            catch (Exception ex) { scan.Errors.Add(drive.Name + "：" + ex.Message); }
        }
    }

    // ------------------------------------------------------- file actions (incremental, non-blocking)

    private async void SafeAction(string[] paths, string action, string conflictMode, bool confirmed)
    {
        if (paths.Length == 0 || _scanInProgress || _actionInProgress) return;

        var selected = new List<Item>(paths.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            Item item;
            if (seen.Add(path) && _itemsByPath.TryGetValue(path, out item))
                selected.Add(item);
        }

        if (selected.Count == 0)
        {
            PostToast("warn", "选中的文件已不在当前列表中，请刷新后重试。");
            return;
        }

        _actionInProgress = true;
        try
        {
            if (action == "delete" && !confirmed)
            {
                var names = new StringBuilder();
                int limit = Math.Min(selected.Count, 20);
                for (int i = 0; i < limit; i++) names.AppendLine(selected[i].Original);
                if (selected.Count > limit) names.AppendLine("… 等共 " + selected.Count + " 个文件");
                if (MessageBox.Show(this, "永久删除以下 " + selected.Count + " 个文件？\n\n" + names, "确认永久删除",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }

            string folder = null;
            if (action == "export") folder = PickFolder("选择复制导出目标文件夹");
            else if (action == "restoreTo") folder = PickFolder("选择恢复到的目标文件夹");
            if ((action == "export" || action == "restoreTo") && string.IsNullOrEmpty(folder)) return;

            string verb = action == "delete" ? "永久删除"
                : action == "restore" ? "恢复"
                : action == "restoreTo" ? "恢复到指定目录"
                : "复制导出";

            PostWorkStatus(true, "正在" + verb + " (0/" + selected.Count + ")…", "action");
            var removed = new HashSet<string>(StringComparer.Ordinal);
            var failures = new List<string>();
            int completed = 0;
            int total = selected.Count;
            long freedBytes = 0;

            await Task.Run(() =>
            {
                foreach (Item item in selected)
                {
                    try
                    {
                        if (action == "restoreTo")
                            ApplyRestoreToAction(item, folder, conflictMode ?? "keepboth");
                        else
                            ApplyFileAction(item, action, folder);

                        if (action != "export") freedBytes += item.Bytes;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(item.Original + " — " + ex.Message);
                    }

                    if (action != "export" && !File.Exists(item.Path))
                        removed.Add(item.Path);

                    completed++;
                    if (completed % 25 == 0 || completed == total)
                    {
                        int c = completed;
                        BeginInvokeSafe(() => PostWorkStatus(true, "正在" + verb + " (" + c + "/" + total + ")…", "action"));
                    }
                }
            });

            if (IsDisposed) return;

            // Incremental in-memory removal — NO full disk rescan!
            if (removed.Count > 0)
            {
                _items.RemoveAll(item => removed.Contains(item.Path));
                foreach (string rp in removed) _itemsByPath.Remove(rp);
                UpdateBinIcon();
                ThreadPool.QueueUserWorkItem(state => NotifyRecycleBinChanged());
            }

            int okCount = selected.Count - failures.Count;
            string summary = verb + "完成：成功 " + okCount + " 项"
                + (freedBytes > 0 && action == "delete" ? "，释放 " + HumanSize(freedBytes) : "")
                + (failures.Count > 0 ? "，失败 " + failures.Count + " 项" : "。");

            PostJson(SerializeMessage(new
            {
                type = "actionResult",
                action = action,
                removed = new List<string>(removed),
                failures = failures,
                okCount = okCount,
                freedBytes = freedBytes,
                message = summary
            }));
        }
        catch (Exception ex)
        {
            PostJson(SerializeMessage(new
            {
                type = "actionResult",
                action = action,
                removed = new string[0],
                failures = new[] { ex.GetBaseException().Message },
                okCount = 0,
                freedBytes = 0L,
                message = "处理文件时发生错误。"
            }));
        }
        finally
        {
            _actionInProgress = false;
            PostWorkStatus(false, null, "action");
        }
    }

    // Preserved signature for ScanTest.cs reflection tests
    private static void ApplyFileAction(Item item, string action, string folder)
    {
        VerifyItemBeforeAction(item);

        if (action == "export")
        {
            string destination = UniqueDestinationPath(folder, item.Name);
            File.Copy(item.Path, destination, false);
            return;
        }

        if (action == "restore")
        {
            if (File.Exists(item.Original) || Directory.Exists(item.Original))
                throw new IOException("目标已存在，未覆盖");
            string parentDir = Path.GetDirectoryName(item.Original);
            if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);
            File.Move(item.Path, item.Original);
        }
        else if (action == "delete")
        {
            FileAttributes attributes = File.GetAttributes(item.Path);
            bool readOnly = (attributes & FileAttributes.ReadOnly) != 0;
            if (readOnly) File.SetAttributes(item.Path, attributes & ~FileAttributes.ReadOnly);
            try { File.Delete(item.Path); }
            catch
            {
                if (readOnly && File.Exists(item.Path))
                {
                    try { File.SetAttributes(item.Path, attributes); }
                    catch { }
                }
                throw;
            }
        }
        else throw new ArgumentOutOfRangeException("action");

        if (!item.Child && !ForceDelete(item.MetaPath))
            throw new IOException((action == "restore" ? "已恢复" : "文件已删除") + "，但回收站元数据未能删除");
    }

    private static void ApplyRestoreToAction(Item item, string folder, string conflictMode)
    {
        VerifyItemBeforeAction(item);
        string dest = Path.Combine(folder, item.Name);
        if (File.Exists(dest) || Directory.Exists(dest))
        {
            if (conflictMode == "skip") throw new IOException("目标目录已存在同名文件，已跳过");
            if (conflictMode == "overwrite")
            {
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                else File.Delete(dest);
            }
            else
            {
                dest = UniqueDestinationPath(folder, item.Name);
            }
        }
        Directory.CreateDirectory(folder);
        File.Move(item.Path, dest);
        if (!item.Child && !ForceDelete(item.MetaPath))
            throw new IOException("已恢复到指定目录，但回收站元数据未能删除");
    }

    private static void VerifyItemBeforeAction(Item item)
    {
        if (!File.Exists(item.Path)) throw new IOException("回收站中的文件已不存在，请刷新");
        FileAttributes attributes = File.GetAttributes(item.Path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("文件已变为链接，请刷新后重新核对");

        // If full SHA-256 was computed, verify exact content integrity;
        // if skipped as unique-size/pending, verify size & timestamp haven't drifted.
        if (item.Hash == UniqueHashSentinel || item.Hash == null)
        {
            var fi = new FileInfo(item.Path);
            if (fi.Length != item.Bytes || fi.LastWriteTimeUtc.Ticks != item.ModifiedTs)
                throw new IOException("文件内容已变化，请刷新后重新核对");
            return;
        }
        if (string.IsNullOrEmpty(item.Hash))
            throw new IOException("无法验证文件内容，请刷新后重试");

        string actual = ContentHash(item.Path);
        if (!string.Equals(actual, item.Hash, StringComparison.Ordinal))
            throw new IOException("文件内容已变化，请刷新后重新核对");
    }

    private static string UniqueDestinationPath(string folder, string fileName)
    {
        string destination = Path.Combine(folder, fileName);
        string extension = Path.GetExtension(fileName);
        string name = Path.GetFileNameWithoutExtension(fileName);
        int copy = 2;
        while (File.Exists(destination) || Directory.Exists(destination))
            destination = Path.Combine(folder, name + " (" + copy++ + ")" + extension);
        return destination;
    }

    private async void EmptyBinAsync(bool confirmed)
    {
        if (_scanInProgress || _actionInProgress || _items.Count == 0) return;
        if (!confirmed)
        {
            if (MessageBox.Show(this, "确实要清空整个回收站（共 " + _items.Count + " 项）吗？此操作不可撤销。", "清空回收站",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
        }

        _actionInProgress = true;
        CancelToken(ref _hashCts);
        PostWorkStatus(true, "正在清空回收站…", "action");
        long totalBytes = 0;
        int totalCount = _items.Count;
        foreach (Item it in _items) totalBytes += it.Bytes;

        try
        {
            int hr = -1;
            try
            {
                hr = SHEmptyRecycleBin(Handle, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            }
            catch { }

            if (hr != 0)
            {
                var snapshot = new List<Item>(_items);
                await Task.Run(() =>
                {
                    foreach (Item it in snapshot) Purge(it);
                    SweepOrphanMetadata();
                });
            }

            if (IsDisposed) return;
            _items.Clear();
            _itemsByPath.Clear();
            ClearThumbCache();
            UpdateBinIcon();
            ThreadPool.QueueUserWorkItem(state => NotifyRecycleBinChanged());

            PostJson(SerializeMessage(new
            {
                type = "binEmptied",
                count = totalCount,
                freedBytes = totalBytes,
                message = "已清空回收站，共清理 " + totalCount + " 项，释放 " + HumanSize(totalBytes) + "。"
            }));
        }
        catch (Exception ex)
        {
            PostToast("error", "清空回收站失败：" + ex.Message);
        }
        finally
        {
            _actionInProgress = false;
            PostWorkStatus(false, null, "action");
        }
    }

    // ------------------------------------------------------- async preview & thumbnails

    private void SendPreviewAsync(string path, bool tail, int reqId)
    {
        CancelToken(ref _previewCts);
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        CancellationToken token = cts.Token;

        Item item;
        if (string.IsNullOrEmpty(path) || !_itemsByPath.TryGetValue(path, out item))
        {
            PostJson(SerializeMessage(new { type = "preview", path = path, reqId = reqId, data = "文件不存在或已被移除。", image = (string)null, originalExists = false }));
            return;
        }

        Task.Run(() =>
        {
            if (token.IsCancellationRequested) return;
            string data = "此文件类型暂不支持内容预览，仅显示元数据信息。";
            string image = null;
            string mediaKind = "info";
            int imgW = 0, imgH = 0;
            bool truncated = false;
            bool originalExists = false;

            try { originalExists = File.Exists(item.Original) || Directory.Exists(item.Original); }
            catch { }

            try
            {
                string kind = FileKind(item.Name);
                if (TextTypes.Contains(kind))
                {
                    mediaKind = "text";
                    if (tail && item.Bytes > 131072)
                    {
                        using (var fs = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            long startPos = Math.Max(0, fs.Length - 131072);
                            fs.Seek(startPos, SeekOrigin.Begin);
                            using (var reader = new StreamReader(fs, Encoding.UTF8, true))
                            {
                                data = reader.ReadToEnd();
                                truncated = startPos > 0;
                            }
                        }
                    }
                    else
                    {
                        using (var fs = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(fs, Encoding.UTF8, true))
                        {
                            char[] chars = new char[262144];
                            int length = reader.ReadBlock(chars, 0, chars.Length);
                            truncated = reader.Peek() >= 0;
                            data = new string(chars, 0, length);
                        }
                    }
                }
                else if (DirectImageTypes.Contains(kind))
                {
                    mediaKind = "image";
                    image = HighResImagePreview(item.Path, 960, out imgW, out imgH);
                    if (image == null) data = "无法解码此图片文件。";
                }
                else if (ShellThumbTypes.Contains(kind))
                {
                    mediaKind = (kind == ".mp4" || kind == ".webm" || kind == ".mov" || kind == ".mkv" || kind == ".avi" || kind == ".wmv" || kind == ".flv")
                        ? "video" : "image";
                    image = MakeThumbnailOnSta(item.Path, 512, out imgW, out imgH);
                    if (image == null) data = "系统未能为此媒体文件生成封面预览。";
                }
            }
            catch (Exception ex) { data = "无法读取：" + ex.Message; }

            if (token.IsCancellationRequested) return;

            var payload = new Dictionary<string, object>();
            payload["type"] = "preview";
            payload["path"] = path;
            payload["reqId"] = reqId;
            payload["mediaKind"] = mediaKind;
            payload["data"] = data;
            payload["image"] = image;
            payload["imgW"] = imgW;
            payload["imgH"] = imgH;
            payload["truncated"] = truncated;
            payload["tail"] = tail;
            payload["originalExists"] = originalExists;

            string json = new JavaScriptSerializer { MaxJsonLength = 8388608 }.Serialize(payload);
            BeginInvokeSafe(() =>
            {
                if (!token.IsCancellationRequested) PostJson(json);
            });
        }, token);
    }

    private void SendThumbnailsAsync(string[] paths, int reqId)
    {
        if (paths == null || paths.Length == 0) return;
        CancelToken(ref _thumbCts);
        var cts = new CancellationTokenSource();
        _thumbCts = cts;
        CancellationToken token = cts.Token;

        var todo = new List<string>();
        var cachedHits = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_thumbCache)
        {
            for (int i = 0; i < paths.Length; i++)
            {
                string p = paths[i];
                string cached;
                if (_thumbCache.TryGetValue(p, out cached))
                {
                    cachedHits[p] = cached;
                }
                else if (_itemsByPath.ContainsKey(p))
                {
                    todo.Add(p);
                }
            }
        }

        if (cachedHits.Count > 0)
        {
            PostJson(SerializeMessage(new { type = "thumbnails", reqId = reqId, map = cachedHits }));
        }
        if (todo.Count == 0) return;

        var thread = new Thread(() =>
        {
            var batch = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string p in todo)
            {
                if (token.IsCancellationRequested) return;
                string url = null;
                try
                {
                    url = MakeThumbnail(p, 220);
                }
                catch { }
                if (url == null) url = "";

                PutThumbCache(p, url);
                batch[p] = url;

                if (batch.Count >= 8)
                {
                    var send = new Dictionary<string, string>(batch, StringComparer.Ordinal);
                    batch.Clear();
                    string json = SerializeMessage(new { type = "thumbnails", reqId = reqId, map = send });
                    BeginInvokeSafe(() => { if (!token.IsCancellationRequested) PostJson(json); });
                }
            }
            if (batch.Count > 0 && !token.IsCancellationRequested)
            {
                string json = SerializeMessage(new { type = "thumbnails", reqId = reqId, map = batch });
                BeginInvokeSafe(() => { if (!token.IsCancellationRequested) PostJson(json); });
            }
        });
        thread.IsBackground = true;
        try { thread.SetApartmentState(ApartmentState.STA); } catch { }
        thread.Start();
    }

    private void PutThumbCache(string path, string url)
    {
        lock (_thumbCache)
        {
            if (_thumbCache.ContainsKey(path)) return;
            _thumbCache[path] = url;
            _thumbLru.AddLast(path);
            while (_thumbLru.Count > MaxThumbCache)
            {
                string oldest = _thumbLru.First.Value;
                _thumbLru.RemoveFirst();
                _thumbCache.Remove(oldest);
            }
        }
    }

    private void ClearThumbCache()
    {
        lock (_thumbCache)
        {
            _thumbCache.Clear();
            _thumbLru.Clear();
        }
    }

    private static string MakeThumbnailOnSta(string path, int size, out int w, out int h)
    {
        string result = null;
        int rw = 0, rh = 0;
        var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try { result = MakeThumbnailWithDims(path, size, out rw, out rh); }
            catch { }
            finally { done.Set(); }
        });
        thread.IsBackground = true;
        try { thread.SetApartmentState(ApartmentState.STA); } catch { }
        thread.Start();
        done.Wait(4000);
        w = rw;
        h = rh;
        return result;
    }

    // ------------------------------------------------------- utility actions

    private void CheckConflictsAsync(string[] paths, int reqId)
    {
        var originals = new List<string>();
        foreach (string p in paths)
        {
            Item it;
            if (_itemsByPath.TryGetValue(p, out it)) originals.Add(it.Original);
        }
        Task.Run(() =>
        {
            var conflicts = new List<string>();
            for (int i = 0; i < originals.Count; i++)
            {
                string orig = originals[i];
                try
                {
                    if (File.Exists(orig) || Directory.Exists(orig))
                        conflicts.Add(orig);
                }
                catch { }
            }
            var sample = conflicts.Count > 10 ? conflicts.GetRange(0, 10) : conflicts;
            string json = SerializeMessage(new { type = "conflictCheck", reqId = reqId, count = conflicts.Count, sample = sample });
            BeginInvokeSafe(() => PostJson(json));
        });
    }

    private void PickProjectFolder()
    {
        string folder = PickFolder("选择要添加的项目根目录");
        if (!string.IsNullOrEmpty(folder))
            PostJson(SerializeMessage(new { type = "pickedFolder", path = folder }));
    }

    private void ExportReport(string content, string defaultName, string ext)
    {
        if (string.IsNullOrEmpty(content)) return;
        try
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "导出清理清单报告";
                dlg.FileName = string.IsNullOrEmpty(defaultName) ? "recycle-bin-report.csv" : defaultName;
                dlg.Filter = ext == "json"
                    ? "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
                    : "CSV 表格 (*.csv)|*.csv|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    File.WriteAllText(dlg.FileName, content, new UTF8Encoding(true));
                    PostToast("success", "报告已导出至：" + dlg.FileName);
                }
            }
        }
        catch (Exception ex) { PostToast("error", "导出失败：" + ex.Message); }
    }

    private void OpenLocation(string[] paths)
    {
        if (paths.Length == 0) return;
        Item e;
        if (!_itemsByPath.TryGetValue(paths[0], out e)) return;

        string folder = Path.GetDirectoryName(e.Original);
        string probe = folder;
        while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
        {
            probe = Path.GetDirectoryName(probe);
        }

        try
        {
            if (!string.IsNullOrEmpty(probe) && Directory.Exists(probe))
            {
                System.Diagnostics.Process.Start("explorer.exe", "\"" + probe + "\"");
                if (!string.Equals(probe, folder, StringComparison.OrdinalIgnoreCase))
                    PostToast("info", "原目录已不存在，已打开最近的上级目录：" + probe);
            }
            else
            {
                PostToast("warn", "原路径所在目录及上级目录均不存在。");
            }
        }
        catch (Exception ex) { PostToast("error", "无法打开目录：" + ex.Message); }
    }

    private void CopyTextToClipboard(string text, string[] paths)
    {
        try
        {
            string value = text;
            if (string.IsNullOrEmpty(value) && paths != null && paths.Length > 0)
            {
                var sb = new StringBuilder();
                foreach (string p in paths)
                {
                    Item e;
                    if (_itemsByPath.TryGetValue(p, out e)) sb.AppendLine(e.Original);
                }
                value = sb.ToString().TrimEnd();
            }
            if (!string.IsNullOrEmpty(value))
            {
                Clipboard.SetText(value);
                PostToast("success", "已复制到剪贴板");
            }
        }
        catch (Exception ex) { PostToast("error", "复制失败：" + ex.Message); }
    }

    private void ShowProperties(string[] paths)
    {
        if (paths.Length == 0) return;
        try { SHObjectProperties(Handle, SHOP_FILEPATH, paths[0], null); }
        catch { }
    }

    private void Zoom(int delta)
    {
        if (_ctl == null) return;
        try
        {
            double f = _ctl.ZoomFactor;
            f = delta == 0 ? 1.0 : f + delta * 0.1;
            _ctl.ZoomFactor = Math.Max(0.5, Math.Min(3.0, f));
        }
        catch { }
    }

    // ------------------------------------------------------- reading & helpers

    private static Item ReadItem(string dataPath)
    {
        if (string.IsNullOrEmpty(dataPath)) return null;

        string meta = MetaPathFor(dataPath);
        if (meta == null || !File.Exists(meta)) return null;

        string original = null;
        long deletedTs = 0;

        try
        {
            byte[] b = File.ReadAllBytes(meta);
            if (b.Length >= 28)
            {
                int chars = BitConverter.ToInt32(b, 24);
                if (chars > 0 && 28 + chars * 2 <= b.Length)
                {
                    string s = Encoding.Unicode.GetString(b, 28, chars * 2);
                    int nul = s.IndexOf('\0');
                    if (nul >= 0) s = s.Substring(0, nul);
                    original = s.Trim();
                }
                deletedTs = DateTime.FromFileTimeUtc(BitConverter.ToInt64(b, 16)).Ticks;
            }
        }
        catch { }

        if (string.IsNullOrEmpty(original)) return null;

        var e = new Item
        {
            Path = dataPath,
            MetaPath = meta,
            Original = original,
            DeletedTs = deletedTs
        };

        try
        {
            string name = Path.GetFileName(original);
            e.Name = string.IsNullOrEmpty(name) ? original : name;

            if (Directory.Exists(dataPath))
            {
                e.Type = "文件夹";
                e.ModifiedTs = Directory.GetLastWriteTimeUtc(dataPath).Ticks;
            }
            else if (File.Exists(dataPath))
            {
                var info = new FileInfo(dataPath);
                e.Bytes = info.Length;
                e.ModifiedTs = info.LastWriteTimeUtc.Ticks;
                string ext = Path.GetExtension(original);
                e.Type = string.IsNullOrEmpty(ext) ? "文件" : ext.TrimStart('.').ToUpperInvariant() + " 文件";
                e.Size = HumanSize(info.Length);
            }
            else return null;
        }
        catch { return null; }

        return e;
    }

    private static string HumanSize(long bytes)
    {
        double v = bytes;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? bytes + " B" : v.ToString("0.0") + " " + u[i];
    }

    private static string MetaPathFor(string dataPath)
    {
        try
        {
            string dir = Path.GetDirectoryName(dataPath);
            string file = Path.GetFileName(dataPath);
            if (dir == null || file.Length < 3) return null;
            if (file[0] != '$' || (file[1] != 'R' && file[1] != 'r')) return null;
            return Path.Combine(dir, "$I" + file.Substring(2));
        }
        catch { return null; }
    }

    private static bool Purge(Item e)
    {
        try
        {
            if (Directory.Exists(e.Path)) Directory.Delete(e.Path, true);
            else if (File.Exists(e.Path))
            {
                try { File.SetAttributes(e.Path, FileAttributes.Normal); } catch { }
                File.Delete(e.Path);
            }
            ForceDelete(e.MetaPath);
            return !File.Exists(e.Path) && !Directory.Exists(e.Path) && !File.Exists(e.MetaPath);
        }
        catch { return false; }
    }

    private static void SweepOrphanMetadata()
    {
        try
        {
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                string bin;
                try { bin = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin"); }
                catch { continue; }
                if (!Directory.Exists(bin)) continue;
                string[] subs;
                try { subs = Directory.GetDirectories(bin); }
                catch { continue; }
                foreach (string sub in subs)
                {
                    string[] metas;
                    try { metas = Directory.GetFiles(sub, "$I*"); }
                    catch { continue; }
                    foreach (string meta in metas)
                    {
                        try
                        {
                            string name = Path.GetFileName(meta);
                            if (name.Length < 3) continue;
                            string payload = Path.Combine(sub, "$R" + name.Substring(2));
                            if (File.Exists(payload) || Directory.Exists(payload)) continue;
                            ForceDelete(meta);
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    private static bool ForceDelete(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (!File.Exists(path)) return true;
            try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            File.Delete(path);
            return !File.Exists(path);
        }
        catch { return false; }
    }

    private static void CancelToken(ref CancellationTokenSource cts)
    {
        if (cts != null)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { }
            cts = null;
        }
    }

    private void BeginInvokeSafe(Action action)
    {
        try
        {
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(action);
        }
        catch { }
    }

    private void UpdateBinIcon()
    {
        Icon icon = BinIcon(_items.Count == 0);
        if (icon == null) return;
        Icon old = Icon;
        Icon = icon;
        if (old != null && old != SystemIcons.Application) old.Dispose();
    }

    private static string SerializeMessage(object payload)
    {
        return new JavaScriptSerializer { MaxJsonLength = 67108864 }.Serialize(payload);
    }

    private void PostJson(string json)
    {
        try
        {
            if (json != null && !IsDisposed && _ctl != null && _ctl.CoreWebView2 != null)
                _ctl.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (InvalidOperationException) { }
    }

    private void PostWorkStatus(bool busy, string message, string phase)
    {
        PostJson(SerializeMessage(new { type = "workStatus", busy = busy, message = message, phase = phase }));
    }

    private void PostToast(string level, string message)
    {
        PostJson(SerializeMessage(new { type = "toast", level = level, message = message }));
    }

    // ------------------------------------------------------- image & thumbnail helpers

    private static string HighResImagePreview(string path, int maxSide, out int naturalW, out int naturalH)
    {
        naturalW = 0;
        naturalH = 0;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (var inputMs = new MemoryStream(bytes))
            using (var src = Image.FromStream(inputMs))
            {
                naturalW = src.Width;
                naturalH = src.Height;
                double k = Math.Min(1.0, Math.Min((double)maxSide / src.Width, (double)maxSide / src.Height));
                int w = Math.Max(1, (int)Math.Round(src.Width * k));
                int h = Math.Max(1, (int)Math.Round(src.Height * k));

                using (var outBmp = new Bitmap(w, h))
                {
                    using (var g = Graphics.FromImage(outBmp))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, 0, 0, w, h);
                    }
                    using (var ms = new MemoryStream())
                    {
                        outBmp.Save(ms, ImageFormat.Png);
                        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
        }
        catch { return null; }
    }

    private static string MakeThumbnail(string path, int size)
    {
        int w, h;
        return MakeThumbnailWithDims(path, size, out w, out h);
    }

    private static string MakeThumbnailWithDims(string path, int size, out int w, out int h)
    {
        w = 0;
        h = 0;
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            IShellItemImageFactory factory;
            Guid iid = IID_IShellItemImageFactory;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null)
                return null;

            var sz = new SIZE { cx = size, cy = size };
            IntPtr hbm;
            if (factory.GetImage(sz, SIIGBF_BIGGERSIZEOK, out hbm) != 0 || hbm == IntPtr.Zero)
                return null;

            try
            {
                using (var src = Image.FromHbitmap(hbm))
                {
                    w = src.Width;
                    h = src.Height;
                    using (var outBmp = new Bitmap(src.Width, src.Height))
                    {
                        using (var g = Graphics.FromImage(outBmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
                        }
                        using (var ms = new MemoryStream())
                        {
                            outBmp.Save(ms, ImageFormat.Png);
                            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }
            }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
    }

    private string PickFolder(string title)
    {
        try
        {
            var dlg = (IFileOpenDialog)new FileOpenDialogRCW();
            uint opts;
            dlg.GetOptions(out opts);
            dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
            dlg.SetTitle(title);
            dlg.SetOkButtonLabel("选择此文件夹");
            if (dlg.Show(Handle) != 0) return null;

            IShellItem item;
            dlg.GetResult(out item);
            string path;
            item.GetDisplayName(SIGDN_FILESYSPATH, out path);
            return path;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------- icons & shell plumbing

    private static Icon BinIcon(bool empty)
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(
                @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\DefaultIcon"))
            {
                if (key == null) return null;
                object raw = key.GetValue(empty ? "Empty" : "Full")
                          ?? key.GetValue(empty ? "Full" : "Empty")
                          ?? key.GetValue(string.Empty);
                string spec = raw as string;
                if (string.IsNullOrEmpty(spec)) return null;

                int comma = spec.LastIndexOf(',');
                string file = Environment.ExpandEnvironmentVariables(comma > 0 ? spec.Substring(0, comma) : spec);
                int index = 0;
                if (comma > 0) int.TryParse(spec.Substring(comma + 1), out index);

                Icon multi = BuildMultiSizeIcon(file, index);
                if (multi != null) return multi;

                IntPtr large, small;
                if (SHDefExtractIcon(file, index, 0, out large, out small, 0) != 0) return null;
                IntPtr h = large != IntPtr.Zero ? large : small;
                if (h == IntPtr.Zero) return null;

                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally
                {
                    if (large != IntPtr.Zero) DestroyIcon(large);
                    if (small != IntPtr.Zero && small != large) DestroyIcon(small);
                }
            }
        }
        catch { return null; }
    }

    private static Icon BuildMultiSizeIcon(string file, int index)
    {
        int[] sizes = { 16, 32, 48, 256 };
        var frames = new List<byte[]>();
        var frameSizes = new List<int>();

        foreach (int s in sizes)
        {
            IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
            try
            {
                uint packed = (uint)(s | (s << 16));
                if (SHDefExtractIcon(file, index, 0, out large, out small, packed) != 0) continue;
                IntPtr h = large != IntPtr.Zero ? large : small;
                if (h == IntPtr.Zero) continue;

                using (Icon one = (Icon)Icon.FromHandle(h).Clone())
                using (var bmp = one.ToBitmap())
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    frames.Add(ms.ToArray());
                    frameSizes.Add(bmp.Width);
                }
            }
            catch { }
            finally
            {
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero && small != large) DestroyIcon(small);
            }
        }

        if (frames.Count == 0) return null;

        try
        {
            int srcIdx = frameSizes.IndexOf(256);
            if (srcIdx < 0) srcIdx = frames.Count - 1;
            using (var srcMs = new MemoryStream(frames[srcIdx]))
            using (var src = new Bitmap(srcMs))
            {
                int[] extra = { 20, 24, 40, 64 };
                foreach (int s in extra)
                {
                    if (frameSizes.Contains(s)) continue;
                    using (var bmp = new Bitmap(s, s))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.Clear(Color.Transparent);
                            g.DrawImage(src, new Rectangle(0, 0, s, s));
                        }
                        using (var ms = new MemoryStream())
                        {
                            bmp.Save(ms, ImageFormat.Png);
                            frames.Add(ms.ToArray());
                            frameSizes.Add(s);
                        }
                    }
                }
            }
        }
        catch { }

        try
        {
            using (var ico = new MemoryStream())
            using (var w = new BinaryWriter(ico))
            {
                w.Write((ushort)0);
                w.Write((ushort)1);
                w.Write((ushort)frames.Count);

                int offset = 6 + 16 * frames.Count;
                for (int i = 0; i < frames.Count; i++)
                {
                    int s = frameSizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((ushort)1);
                    w.Write((ushort)32);
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (byte[] f in frames) w.Write(f);
                ico.Position = 0;
                return new Icon(ico);
            }
        }
        catch { return null; }
    }

    private static void NotifyRecycleBinChanged()
    {
        try
        {
            IntPtr pidl;
            if (SHGetFolderLocation(IntPtr.Zero, CSIDL_BITBUCKET, IntPtr.Zero, 0, out pidl) == 0 && pidl != IntPtr.Zero)
            {
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_IDLIST, pidl, IntPtr.Zero);
                CoTaskMemFree(pidl);
            }
        }
        catch { }

        try
        {
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    string p = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin");
                    if (Directory.Exists(p))
                        SHChangeNotifyPath(SHCNE_UPDATEDIR, SHCNF_PATHW, p, IntPtr.Zero);
                }
                catch { }
            }
        }
        catch { }
    }

    private const int CSIDL_BITBUCKET = 0x000a;
    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetFolderLocation(IntPtr hwndOwner, int nFolder, IntPtr hToken,
        uint dwReserved, out IntPtr ppidl);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHChangeNotify")]
    private static extern void SHChangeNotifyPath(int wEventId, uint uFlags,
        [MarshalAs(UnmanagedType.LPWStr)] string dwItem1, IntPtr dwItem2);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string pszIconFile, int iIndex, uint uFlags,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW { }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    private const uint SHOP_FILEPATH = 0x00000002;
    private static readonly Guid IID_IShellItemImageFactory = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType,
        string pszObjectName, string pszPropertyPage);
}
