using System.Runtime.InteropServices;

namespace CaptureCardPlayer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new PreviewForm(ParseDeviceSelector(args)));
    }

    private static string? ParseDeviceSelector(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg.StartsWith("--device=", StringComparison.OrdinalIgnoreCase))
            {
                return arg["--device=".Length..].Trim('"');
            }

            if (arg.StartsWith("/device=", StringComparison.OrdinalIgnoreCase))
            {
                return arg["/device=".Length..].Trim('"');
            }
        }

        return null;
    }
}

internal sealed class PreviewForm : Form
{
    private string? currentDeviceSelector;
    private readonly PreviewPanel previewHost = new();
    private readonly Label statusLabel = new();
    private readonly ContextMenuStrip deviceMenu = new();
    private DirectShowPreviewGraph? graph;
    private long lastMenuOpenedTicks;
    private int volumePercent = 100;

    public PreviewForm(string? deviceSelector)
    {
        currentDeviceSelector = deviceSelector;

        KeyPreview = true;
        Text = "CaptureCardPlayer";
        BackColor = Color.Black;
        ClientSize = new Size(1280, 720);
        MinimumSize = new Size(320, 180);
        StartPosition = FormStartPosition.CenterScreen;

        previewHost.BackColor = Color.Black;
        previewHost.Dock = DockStyle.Fill;
        Controls.Add(previewHost);

        statusLabel.AutoSize = false;
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.BackColor = Color.Black;
        statusLabel.ForeColor = Color.White;
        statusLabel.TextAlign = ContentAlignment.MiddleCenter;
        statusLabel.Font = new Font(Font.FontFamily, 12.0f, FontStyle.Regular);
        statusLabel.Visible = false;
        Controls.Add(statusLabel);
        statusLabel.BringToFront();

        previewHost.Resize += (_, _) => graph?.Resize(previewHost.ClientSize);
        previewHost.MenuRequested += (_, point) => ShowDeviceMenu(point);
        previewHost.WheelRequested += (_, delta) => ChangeVolume(delta);
        previewHost.ShortcutRequested += (_, shortcut) => HandleShortcut(shortcut);
        MouseWheel += (_, e) => ChangeVolume(e.Delta);
        statusLabel.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowDeviceMenu(statusLabel.PointToScreen(e.Location));
            }
        };
        statusLabel.MouseWheel += (_, e) => ChangeVolume(e.Delta);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (HandleShortcut(keyData))
        {
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        BeginInvoke(StartPreview);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        graph?.Dispose();
        graph = null;
        base.OnFormClosed(e);
    }

    private void StartPreview()
    {
        try
        {
            DiagnosticLog.Write("Starting preview.");
            graph = DirectShowPreviewGraph.Start(previewHost.Handle, previewHost.ClientSize, currentDeviceSelector);
            graph.SetVolumePercent(volumePercent);
            statusLabel.Visible = false;
            UpdateWindowTitle();
            DiagnosticLog.Write($"Preview started: {graph.DeviceName}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex.ToString());
            statusLabel.Text = $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Log: {DiagnosticLog.Path}";
            statusLabel.Visible = true;
            graph?.Dispose();
            graph = null;
        }
    }

    private void ChangeVolume(int wheelDelta)
    {
        int direction = Math.Sign(wheelDelta);
        if (direction == 0)
        {
            return;
        }

        volumePercent = Math.Clamp(volumePercent + (direction * 5), 0, 150);
        graph?.SetVolumePercent(volumePercent);
        UpdateWindowTitle();
        DiagnosticLog.Write($"Volume changed: {volumePercent}%.");
    }

    private bool HandleShortcut(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.C))
        {
            CopyCurrentFrameToClipboard();
            return true;
        }

        if (keyData == (Keys.Control | Keys.E))
        {
            SaveCurrentFrameToPictures();
            return true;
        }

        return false;
    }

    private void CopyCurrentFrameToClipboard()
    {
        try
        {
            using Bitmap frame = CapturePreviewArea();
            Clipboard.SetImage((Bitmap)frame.Clone());
            DiagnosticLog.Write("Current frame copied to clipboard.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Failed to copy current frame: {ex}");
        }
    }

    private void SaveCurrentFrameToPictures()
    {
        try
        {
            using Bitmap frame = CapturePreviewArea();
            string picturesPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(picturesPath))
            {
                picturesPath = AppContext.BaseDirectory;
            }

            Directory.CreateDirectory(picturesPath);

            string fileName = $"CaptureCardPlayer_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            string filePath = Path.Combine(picturesPath, fileName);
            frame.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
            DiagnosticLog.Write($"Current frame saved: {filePath}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Failed to save current frame: {ex}");
        }
    }

    private Bitmap CapturePreviewArea()
    {
        Rectangle bounds = previewHost.RectangleToScreen(previewHost.ClientRectangle);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("Preview area is empty.");
        }

        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private void UpdateWindowTitle()
    {
        string deviceName = graph?.DeviceName ?? "No Device";
        Text = $"CaptureCardPlayer - {deviceName} - {volumePercent}%";
    }

    private void RestartPreview(string? selector)
    {
        currentDeviceSelector = selector;
        graph?.Dispose();
        graph = null;
        StartPreview();
    }

    private void ShowDeviceMenu(Point screenPoint)
    {
        deviceMenu.Items.Clear();

        try
        {
            IReadOnlyList<string> devices = DirectShowPreviewGraph.ListVideoCaptureDeviceNames();
            if (devices.Count == 0)
            {
                deviceMenu.Items.Add(new ToolStripMenuItem("No video capture devices") { Enabled = false });
            }

            for (int i = 0; i < devices.Count; i++)
            {
                string deviceName = devices[i];
                var item = new ToolStripMenuItem(deviceName)
                {
                    Checked = string.Equals(graph?.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase),
                    Tag = deviceName,
                };
                item.Click += (_, _) => RestartPreview((string)item.Tag);
                deviceMenu.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Failed to build device menu: {ex}");
            deviceMenu.Items.Add(new ToolStripMenuItem("Failed to read devices") { Enabled = false });
        }

        if (deviceMenu.Items.Count > 0)
        {
            deviceMenu.Items.Add(new ToolStripSeparator());
        }

        var refresh = new ToolStripMenuItem("Refresh");
        refresh.Click += (_, _) => ShowDeviceMenu(screenPoint);
        deviceMenu.Items.Add(refresh);

        long nowTicks = Environment.TickCount64;
        if (nowTicks - lastMenuOpenedTicks < 250)
        {
            return;
        }

        lastMenuOpenedTicks = nowTicks;
        deviceMenu.Show(previewHost, previewHost.PointToClient(screenPoint));
    }
}

internal sealed class PreviewPanel : Panel
{
    private const int WmContextMenu = 0x007B;
    private const int WmKeyDown = 0x0100;
    private const int WmMouseWheel = 0x020A;

    public event EventHandler<Point>? MenuRequested;
    public event EventHandler<int>? WheelRequested;
    public event EventHandler<Keys>? ShortcutRequested;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmContextMenu)
        {
            Point point = GetContextMenuPoint(m.LParam);
            MenuRequested?.Invoke(this, point);
            return;
        }

        if (m.Msg == WmMouseWheel)
        {
            int delta = unchecked((short)(((long)m.WParam >> 16) & 0xFFFF));
            WheelRequested?.Invoke(this, delta);
            return;
        }

        if (m.Msg == WmKeyDown)
        {
            Keys shortcut = (Keys)(int)m.WParam | ModifierKeys;
            if (shortcut is (Keys.Control | Keys.C) or (Keys.Control | Keys.E))
            {
                ShortcutRequested?.Invoke(this, shortcut);
                return;
            }
        }

        base.WndProc(ref m);
    }

    private Point GetContextMenuPoint(IntPtr lParam)
    {
        long raw = lParam.ToInt64();
        if (raw == -1)
        {
            return PointToScreen(new Point(Width / 2, Height / 2));
        }

        int x = unchecked((short)(raw & 0xFFFF));
        int y = unchecked((short)((raw >> 16) & 0xFFFF));
        return new Point(x, y);
    }
}

internal sealed class DirectShowPreviewGraph : IDisposable
{
    private const int OaTrue = -1;
    private const int OaFalse = 0;
    private const int WsChild = 0x40000000;
    private const int WsClipSiblings = 0x04000000;
    private const int HrSuccess = 0;
    private const int HrFalse = 1;
    private const uint ClsctxInprocServer = 0x1;

    private IGraphBuilder? graphBuilder;
    private ICaptureGraphBuilder2? captureBuilder;
    private IBaseFilter? sourceFilter;
    private IBaseFilter? audioSourceFilter;
    private IBaseFilter? rendererFilter;
    private IMediaControl? mediaControl;
    private IVideoWindow? videoWindow;
    private IBasicAudio? basicAudio;

    private DirectShowPreviewGraph(string deviceName)
    {
        DeviceName = deviceName;
    }

    public string DeviceName { get; private set; }

    public static IReadOnlyList<string> ListVideoCaptureDeviceNames()
    {
        Application.OleRequired();
        return VideoCaptureDevices.ListNames();
    }

    public static DirectShowPreviewGraph Start(IntPtr owner, Size size, string? deviceSelector)
    {
        Application.OleRequired();

        var preview = new DirectShowPreviewGraph(deviceSelector ?? "Video Capture Device");
        try
        {
            preview.Build(owner, size, deviceSelector);
            return preview;
        }
        catch
        {
            preview.Dispose();
            throw;
        }
    }

    public void Resize(Size size)
    {
        if (videoWindow is null)
        {
            return;
        }

        int width = Math.Max(1, size.Width);
        int height = Math.Max(1, size.Height);
        _ = videoWindow.SetWindowPosition(0, 0, width, height);
    }

    public void Dispose()
    {
        try
        {
            _ = mediaControl?.Stop();
            if (videoWindow is not null)
            {
                _ = videoWindow.put_Visible(OaFalse);
                _ = videoWindow.put_Owner(IntPtr.Zero);
            }
        }
        finally
        {
            ReleaseComObject(videoWindow);
            ReleaseComObject(basicAudio);
            ReleaseComObject(mediaControl);
            ReleaseComObject(rendererFilter);
            ReleaseComObject(audioSourceFilter);
            ReleaseComObject(sourceFilter);
            ReleaseComObject(captureBuilder);
            ReleaseComObject(graphBuilder);

            videoWindow = null;
            basicAudio = null;
            mediaControl = null;
            rendererFilter = null;
            audioSourceFilter = null;
            sourceFilter = null;
            captureBuilder = null;
            graphBuilder = null;
        }
    }

    private void Build(IntPtr owner, Size size, string? deviceSelector)
    {
        DiagnosticLog.Write("Creating FilterGraph.");
        graphBuilder = CreateComObject<IGraphBuilder>(DirectShowGuids.FilterGraph);
        DiagnosticLog.Write("Creating CaptureGraphBuilder2.");
        captureBuilder = CreateComObject<ICaptureGraphBuilder2>(DirectShowGuids.CaptureGraphBuilder2);

        CheckHr(captureBuilder.SetFiltergraph(graphBuilder), "Failed to initialize the DirectShow graph.");

        DiagnosticLog.Write("Binding video capture device.");
        sourceFilter = VideoCaptureDevices.Bind(deviceSelector, out string deviceName);
        DeviceName = deviceName;
        DiagnosticLog.Write($"Selected device: {deviceName}");

        var filterGraph = (IFilterGraph)graphBuilder;
        CheckHr(filterGraph.AddFilter(sourceFilter, deviceName), "Failed to add the capture device to the graph.");

        DiagnosticLog.Write("Creating VideoRenderer.");
        rendererFilter = CreateComObject<IBaseFilter>(DirectShowGuids.VideoRenderer);
        CheckHr(filterGraph.AddFilter(rendererFilter, "Video Renderer"), "Failed to add the video renderer.");

        Guid category = DirectShowGuids.PinCategoryPreview;
        Guid mediaType = DirectShowGuids.MediaTypeVideo;
        int hr = captureBuilder.RenderStream(ref category, ref mediaType, sourceFilter, null, rendererFilter);
        if (hr < 0)
        {
            category = DirectShowGuids.PinCategoryCapture;
            hr = captureBuilder.RenderStream(ref category, ref mediaType, sourceFilter, null, rendererFilter);
        }

        CheckHr(hr, "Failed to render the video capture stream.");

        RenderAudioStream();

        DiagnosticLog.Write("Attaching video window.");
        mediaControl = (IMediaControl)graphBuilder;
        videoWindow = (IVideoWindow)graphBuilder;
        basicAudio = graphBuilder as IBasicAudio;

        CheckHr(videoWindow.put_Owner(owner), "Failed to attach the video output window.");
        CheckHr(videoWindow.put_WindowStyle(WsChild | WsClipSiblings), "Failed to configure the video output window.");
        _ = videoWindow.put_MessageDrain(owner);
        Resize(size);
        CheckHr(videoWindow.put_Visible(OaTrue), "Failed to show the video output window.");

        DiagnosticLog.Write("Running graph.");
        CheckHr(mediaControl.Run(), "Failed to start the video capture device.");
    }

    public void SetVolumePercent(int percent)
    {
        int clampedPercent = Math.Clamp(percent, 0, 150);
        if (basicAudio is null)
        {
            return;
        }

        int directShowVolume = ConvertToDirectShowVolume(clampedPercent);
        int hr = basicAudio.put_Volume(directShowVolume);
        if (hr < 0 && clampedPercent > 100)
        {
            hr = basicAudio.put_Volume(0);
        }

        if (hr < 0)
        {
            DiagnosticLog.Write($"Failed to set volume {clampedPercent}%: 0x{hr:X8}");
        }
    }

    private void RenderAudioStream()
    {
        DiagnosticLog.Write("Rendering audio capture stream from video device.");
        int hr = RenderAudioFromFilter(sourceFilter!);
        if (hr >= 0)
        {
            DiagnosticLog.Write("Audio capture stream rendered from video device.");
            return;
        }

        DiagnosticLog.Write($"Audio stream render from video device failed: 0x{hr:X8}");

        if (!VideoCaptureDevices.TryBindMatchingAudioDevice(DeviceName, out audioSourceFilter, out string? audioDeviceName))
        {
            DiagnosticLog.Write("No matching separate audio capture device was found.");
            return;
        }

        DiagnosticLog.Write($"Rendering matching separate audio device: {audioDeviceName}");
        var filterGraph = (IFilterGraph)graphBuilder!;
        hr = filterGraph.AddFilter(audioSourceFilter!, audioDeviceName!);
        if (hr < 0)
        {
            DiagnosticLog.Write($"Failed to add separate audio device: 0x{hr:X8}");
            return;
        }

        hr = RenderAudioFromFilter(audioSourceFilter!);
        if (hr >= 0)
        {
            DiagnosticLog.Write("Audio capture stream rendered from separate audio device.");
            return;
        }

        DiagnosticLog.Write($"Separate audio stream render failed: 0x{hr:X8}");
    }

    private int RenderAudioFromFilter(IBaseFilter filter)
    {
        Guid audioCategory = DirectShowGuids.PinCategoryCapture;
        Guid audioType = DirectShowGuids.MediaTypeAudio;
        int hr = captureBuilder!.RenderStream(ref audioCategory, ref audioType, filter, null, null);
        if (hr >= 0)
        {
            return hr;
        }

        audioCategory = DirectShowGuids.PinCategoryPreview;
        audioType = DirectShowGuids.MediaTypeAudio;
        return captureBuilder.RenderStream(ref audioCategory, ref audioType, filter, null, null);
    }

    private static int ConvertToDirectShowVolume(int percent)
    {
        if (percent <= 0)
        {
            return -10000;
        }

        double linear = percent / 100.0;
        double decibels = 20.0 * Math.Log10(linear);
        int hundredthsOfDb = (int)Math.Round(decibels * 100.0);
        return Math.Clamp(hundredthsOfDb, -10000, 1000);
    }

    private static T CreateComObject<T>(Guid classId)
    {
        Guid interfaceId = typeof(T).GUID;
        int hr = CoCreateInstance(ref classId, IntPtr.Zero, ClsctxInprocServer, ref interfaceId, out IntPtr instance);
        CheckHr(hr, $"Failed to create COM object {classId}.");

        try
        {
            return (T)Marshal.GetObjectForIUnknown(instance);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        out IntPtr instance);

    private static void CheckHr(int hr, string message)
    {
        if (hr >= 0)
        {
            return;
        }

        Exception exception = Marshal.GetExceptionForHR(hr) ?? new COMException(message, hr);
        throw new InvalidOperationException($"{message}{Environment.NewLine}{exception.Message}", exception);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Ignoring COM release failure: {ex}");
        }
    }

    private static class VideoCaptureDevices
    {
        public static IReadOnlyList<string> ListNames()
        {
            ICreateDevEnum? createDevEnum = null;
            INativeEnumMoniker? enumMoniker = null;
            var monikers = new List<INativeMoniker>();
            var names = new List<string>();

            try
            {
                createDevEnum = CreateComObject<ICreateDevEnum>(DirectShowGuids.CreateDevEnum);
                Guid category = DirectShowGuids.VideoInputDeviceCategory;
                int hr = createDevEnum.CreateClassEnumerator(ref category, out enumMoniker, 0);
                if (hr == HrFalse || enumMoniker is null)
                {
                    return names;
                }

                CheckHr(hr, "Failed to enumerate video capture devices.");

                while (enumMoniker.Next(1, out INativeMoniker current, IntPtr.Zero) == HrSuccess)
                {
                    monikers.Add(current);
                    names.Add(GetFriendlyName(current, releasePropertyBag: true) ?? $"Video Capture Device {names.Count}");
                }

                return names;
            }
            finally
            {
                foreach (INativeMoniker moniker in monikers)
                {
                    ReleaseComObject(moniker);
                }

                ReleaseComObject(enumMoniker);
                ReleaseComObject(createDevEnum);
            }
        }

        public static bool TryBindMatchingAudioDevice(
            string videoDeviceName,
            out IBaseFilter? audioFilter,
            out string? audioDeviceName)
        {
            audioFilter = null;
            audioDeviceName = null;

            ICreateDevEnum? createDevEnum = null;
            INativeEnumMoniker? enumMoniker = null;
            var monikers = new List<INativeMoniker>();

            try
            {
                createDevEnum = CreateComObject<ICreateDevEnum>(DirectShowGuids.CreateDevEnum);
                Guid category = DirectShowGuids.AudioInputDeviceCategory;
                int hr = createDevEnum.CreateClassEnumerator(ref category, out enumMoniker, 0);
                if (hr == HrFalse || enumMoniker is null)
                {
                    return false;
                }

                CheckHr(hr, "Failed to enumerate audio capture devices.");

                var candidates = new List<(int Index, string Name, int Score)>();
                while (enumMoniker.Next(1, out INativeMoniker current, IntPtr.Zero) == HrSuccess)
                {
                    int index = monikers.Count;
                    monikers.Add(current);

                    string name = GetFriendlyName(current) ?? $"Audio Capture Device {index}";
                    int score = ScoreAudioDeviceMatch(videoDeviceName, name);
                    DiagnosticLog.Write($"Audio device candidate {index}: {name}, score {score}.");
                    if (score > 0)
                    {
                        candidates.Add((index, name, score));
                    }
                }

                if (candidates.Count == 0)
                {
                    return false;
                }

                (int selectedIndex, string selectedName, _) = candidates
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.Index)
                    .First();

                INativeMoniker selected = monikers[selectedIndex];
                Guid baseFilterId = typeof(IBaseFilter).GUID;
                CheckHr(
                    selected.BindToObject(IntPtr.Zero, IntPtr.Zero, ref baseFilterId, out IntPtr sourcePointer),
                    $"Failed to bind {selectedName} to IBaseFilter.");

                try
                {
                    audioFilter = (IBaseFilter)Marshal.GetObjectForIUnknown(sourcePointer);
                    audioDeviceName = selectedName;
                    return true;
                }
                finally
                {
                    Marshal.Release(sourcePointer);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                DiagnosticLog.Write($"Failed to bind matching audio device: {ex}");
                return false;
            }
            finally
            {
                foreach (INativeMoniker moniker in monikers)
                {
                    ReleaseComObject(moniker);
                }

                ReleaseComObject(enumMoniker);
                ReleaseComObject(createDevEnum);
            }
        }

        private static int ScoreAudioDeviceMatch(string videoDeviceName, string audioDeviceName)
        {
            string video = videoDeviceName.ToLowerInvariant();
            string audio = audioDeviceName.ToLowerInvariant();

            if (video == audio)
            {
                return 1000;
            }

            if (video.Contains(audio, StringComparison.OrdinalIgnoreCase) ||
                audio.Contains(video, StringComparison.OrdinalIgnoreCase))
            {
                return 500;
            }

            int score = 0;
            foreach (string token in GetDistinctiveTokens(video))
            {
                if (audio.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    score += token.Length >= 6 ? 50 : 25;
                }
            }

            return score;
        }

        private static IEnumerable<string> GetDistinctiveTokens(string value)
        {
            string[] commonWords =
            [
                "audio",
                "broadcaster",
                "camera",
                "capture",
                "device",
                "directshow",
                "input",
                "microphone",
                "virtual",
                "video",
            ];

            foreach (string token in value.Split([' ', '-', '_', '(', ')', '[', ']', '.', ':'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length < 4 || commonWords.Contains(token))
                {
                    continue;
                }

                yield return token;
            }
        }

        public static IBaseFilter Bind(string? selector, out string deviceName)
        {
            ICreateDevEnum? createDevEnum = null;
            INativeEnumMoniker? enumMoniker = null;
            var monikers = new List<INativeMoniker>();

            try
            {
                DiagnosticLog.Write("Creating System Device Enumerator.");
                createDevEnum = CreateComObject<ICreateDevEnum>(DirectShowGuids.CreateDevEnum);
                Guid category = DirectShowGuids.VideoInputDeviceCategory;
                DiagnosticLog.Write("Calling CreateClassEnumerator.");
                int hr = createDevEnum.CreateClassEnumerator(ref category, out enumMoniker, 0);
                if (hr == HrFalse || enumMoniker is null)
                {
                    throw new InvalidOperationException("No video capture device was found.");
                }

                CheckHr(hr, "Failed to enumerate video capture devices.");

                DiagnosticLog.Write("Reading monikers.");
                while (enumMoniker.Next(1, out INativeMoniker current, IntPtr.Zero) == HrSuccess)
                {
                    DiagnosticLog.Write("Found video capture moniker.");
                    monikers.Add(current);
                }

                if (monikers.Count == 0)
                {
                    throw new InvalidOperationException("No video capture device was found.");
                }

                DiagnosticLog.Write($"Selecting from {monikers.Count} video capture device(s).");
                List<string> failures = new();
                foreach (int selectedIndex in BuildCandidateOrder(monikers, selector))
                {
                    INativeMoniker selected = monikers[selectedIndex];
                    string candidateName = GetFriendlyName(selected) ?? $"Video Capture Device {selectedIndex}";
                    DiagnosticLog.Write($"Trying device {selectedIndex}: {candidateName}.");

                    try
                    {
                        Guid baseFilterId = typeof(IBaseFilter).GUID;
                        DiagnosticLog.Write("Binding selected moniker to IBaseFilter.");
                        CheckHr(
                            selected.BindToObject(IntPtr.Zero, IntPtr.Zero, ref baseFilterId, out IntPtr sourcePointer),
                            $"Failed to bind {candidateName} to IBaseFilter.");
                        DiagnosticLog.Write("Selected moniker bound to IBaseFilter.");

                        try
                        {
                            object sourceObject = Marshal.GetObjectForIUnknown(sourcePointer);
                            deviceName = candidateName;
                            return (IBaseFilter)sourceObject;
                        }
                        finally
                        {
                            Marshal.Release(sourcePointer);
                        }
                    }
                    catch (Exception ex) when (ex is COMException or InvalidComObjectException)
                    {
                        string failure = $"{candidateName}: {ex.Message}";
                        failures.Add(failure);
                        DiagnosticLog.Write($"Device bind failed: {failure}");
                    }
                }

                throw new InvalidOperationException(
                    "No usable video capture device was found." +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, failures));
            }
            finally
            {
                foreach (INativeMoniker moniker in monikers)
                {
                    ReleaseComObject(moniker);
                }

                ReleaseComObject(enumMoniker);
                ReleaseComObject(createDevEnum);
            }
        }

        private static IEnumerable<int> BuildCandidateOrder(IReadOnlyList<INativeMoniker> monikers, string? selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                for (int i = 0; i < monikers.Count; i++)
                {
                    yield return i;
                }

                yield break;
            }

            if (int.TryParse(selector, out int index) && index >= 0 && index < monikers.Count)
            {
                yield return index;
                yield break;
            }

            var yielded = new HashSet<int>();

            for (int i = 0; i < monikers.Count; i++)
            {
                string? name = GetFriendlyName(monikers[i]);
                if (string.Equals(name, selector, StringComparison.OrdinalIgnoreCase))
                {
                    yielded.Add(i);
                    yield return i;
                }
            }

            for (int i = 0; i < monikers.Count; i++)
            {
                if (yielded.Contains(i))
                {
                    continue;
                }

                string? name = GetFriendlyName(monikers[i]);
                if (name?.Contains(selector, StringComparison.OrdinalIgnoreCase) == true)
                {
                    yielded.Add(i);
                    yield return i;
                }
            }
        }

        private static string? GetFriendlyName(INativeMoniker moniker, bool releasePropertyBag = false)
        {
            object? bagObject = null;
            try
            {
                Guid propertyBagId = typeof(IPropertyBag).GUID;
                int hr = moniker.BindToStorage(IntPtr.Zero, IntPtr.Zero, ref propertyBagId, out IntPtr bagPointer);
                if (hr < 0)
                {
                    DiagnosticLog.Write($"BindToStorage failed: 0x{hr:X8}");
                    return null;
                }

                try
                {
                    bagObject = Marshal.GetObjectForIUnknown(bagPointer);
                }
                finally
                {
                    Marshal.Release(bagPointer);
                }

                var propertyBag = (IPropertyBag)bagObject;
                int readHr = propertyBag.Read("FriendlyName", out object value, IntPtr.Zero);
                return readHr >= 0 ? value as string : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (releasePropertyBag)
                {
                    ReleaseComObject(bagObject);
                }
                else
                {
                    // Some DirectShow monikers expose IPropertyBag on the same COM identity.
                    // FinalReleaseComObject here can disconnect the moniker RCW before BindToObject.
                }
            }
        }
    }
}

internal static class DirectShowGuids
{
    public static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    public static readonly Guid AudioInputDeviceCategory = new("33D9A762-90C8-11D0-BD43-00A0C911CE86");
    public static readonly Guid PinCategoryPreview = new("FB6C4282-0353-11D1-905F-0000C0CC16BA");
    public static readonly Guid PinCategoryCapture = new("FB6C4281-0353-11D1-905F-0000C0CC16BA");
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid FilterGraph = new("E436EBB3-524F-11CE-9F53-0020AF0BA770");
    public static readonly Guid CaptureGraphBuilder2 = new("BF87B6E1-8C27-11D0-B3F0-00AA003761C5");
    public static readonly Guid CreateDevEnum = new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
    public static readonly Guid VideoRenderer = new("70E102B0-5556-11CE-97C0-00AA0055595A");
}

internal static class DiagnosticLog
{
    public static string Path { get; } = System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "CaptureCardPlayer.log");

    public static void Write(string message)
    {
        try
        {
            File.AppendAllText(
                Path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never stop preview startup or cleanup.
        }
    }
}

[ComImport]
[Guid("56A868A9-0AD4-11CE-B03A-0020AF0BA770")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphBuilder;

[ComImport]
[Guid("56A8689F-0AD4-11CE-B03A-0020AF0BA770")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFilterGraph
{
    [PreserveSig]
    int AddFilter(IBaseFilter filter, [MarshalAs(UnmanagedType.LPWStr)] string name);

    [PreserveSig]
    int RemoveFilter(IBaseFilter filter);

    [PreserveSig]
    int EnumFilters(out IntPtr filters);

    [PreserveSig]
    int FindFilterByName([MarshalAs(UnmanagedType.LPWStr)] string name, out IBaseFilter filter);

    [PreserveSig]
    int ConnectDirect(IntPtr outputPin, IntPtr inputPin, IntPtr mediaType);

    [PreserveSig]
    int Reconnect(IntPtr pin);

    [PreserveSig]
    int Disconnect(IntPtr pin);

    [PreserveSig]
    int SetDefaultSyncSource();
}

[ComImport]
[Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IBaseFilter;

[ComImport]
[Guid("56A868B1-0AD4-11CE-B03A-0020AF0BA770")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IMediaControl
{
    [PreserveSig]
    int Run();

    [PreserveSig]
    int Pause();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int GetState(int timeoutMs, out int filterState);

    [PreserveSig]
    int RenderFile([MarshalAs(UnmanagedType.BStr)] string fileName);

    [PreserveSig]
    int AddSourceFilter([MarshalAs(UnmanagedType.BStr)] string fileName, out object filterInfo);

    [PreserveSig]
    int get_FilterCollection(out object collection);

    [PreserveSig]
    int get_RegFilterCollection(out object collection);

    [PreserveSig]
    int StopWhenReady();
}

[ComImport]
[Guid("56A868B3-0AD4-11CE-B03A-0020AF0BA770")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IBasicAudio
{
    [PreserveSig]
    int put_Volume(int volume);

    [PreserveSig]
    int get_Volume(out int volume);

    [PreserveSig]
    int put_Balance(int balance);

    [PreserveSig]
    int get_Balance(out int balance);
}

[ComImport]
[Guid("56A868B4-0AD4-11CE-B03A-0020AF0BA770")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IVideoWindow
{
    [PreserveSig]
    int put_Caption([MarshalAs(UnmanagedType.BStr)] string caption);

    [PreserveSig]
    int get_Caption([MarshalAs(UnmanagedType.BStr)] out string caption);

    [PreserveSig]
    int put_WindowStyle(int windowStyle);

    [PreserveSig]
    int get_WindowStyle(out int windowStyle);

    [PreserveSig]
    int put_WindowStyleEx(int windowStyleEx);

    [PreserveSig]
    int get_WindowStyleEx(out int windowStyleEx);

    [PreserveSig]
    int put_AutoShow(int autoShow);

    [PreserveSig]
    int get_AutoShow(out int autoShow);

    [PreserveSig]
    int put_WindowState(int windowState);

    [PreserveSig]
    int get_WindowState(out int windowState);

    [PreserveSig]
    int put_BackgroundPalette(int backgroundPalette);

    [PreserveSig]
    int get_BackgroundPalette(out int backgroundPalette);

    [PreserveSig]
    int put_Visible(int visible);

    [PreserveSig]
    int get_Visible(out int visible);

    [PreserveSig]
    int put_Left(int left);

    [PreserveSig]
    int get_Left(out int left);

    [PreserveSig]
    int put_Width(int width);

    [PreserveSig]
    int get_Width(out int width);

    [PreserveSig]
    int put_Top(int top);

    [PreserveSig]
    int get_Top(out int top);

    [PreserveSig]
    int put_Height(int height);

    [PreserveSig]
    int get_Height(out int height);

    [PreserveSig]
    int put_Owner(IntPtr owner);

    [PreserveSig]
    int get_Owner(out IntPtr owner);

    [PreserveSig]
    int put_MessageDrain(IntPtr drain);

    [PreserveSig]
    int get_MessageDrain(out IntPtr drain);

    [PreserveSig]
    int get_BorderColor(out int color);

    [PreserveSig]
    int put_BorderColor(int color);

    [PreserveSig]
    int get_FullScreenMode(out int fullScreenMode);

    [PreserveSig]
    int put_FullScreenMode(int fullScreenMode);

    [PreserveSig]
    int SetWindowForeground(int focus);

    [PreserveSig]
    int NotifyOwnerMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [PreserveSig]
    int SetWindowPosition(int left, int top, int width, int height);

    [PreserveSig]
    int GetWindowPosition(out int left, out int top, out int width, out int height);

    [PreserveSig]
    int GetMinIdealImageSize(out int width, out int height);

    [PreserveSig]
    int GetMaxIdealImageSize(out int width, out int height);

    [PreserveSig]
    int GetRestorePosition(out int left, out int top, out int width, out int height);

    [PreserveSig]
    int HideCursor(int hideCursor);

    [PreserveSig]
    int IsCursorHidden(out int cursorHidden);
}

[ComImport]
[Guid("93E5A4E0-2D50-11D2-ABFA-00A0C9C6E38D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICaptureGraphBuilder2
{
    [PreserveSig]
    int SetFiltergraph(IGraphBuilder graphBuilder);

    [PreserveSig]
    int GetFiltergraph(out IGraphBuilder graphBuilder);

    [PreserveSig]
    int SetOutputFileName(
        ref Guid type,
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        out IBaseFilter filter,
        out IntPtr sink);

    [PreserveSig]
    int FindInterface(ref Guid category, ref Guid type, IBaseFilter filter, ref Guid interfaceId, out IntPtr value);

    [PreserveSig]
    int RenderStream(
        ref Guid category,
        ref Guid type,
        [MarshalAs(UnmanagedType.IUnknown)] object source,
        IBaseFilter? compressor,
        IBaseFilter? renderer);

    [PreserveSig]
    int ControlStream(
        ref Guid category,
        ref Guid type,
        IBaseFilter filter,
        long start,
        long stop,
        short startCookie,
        short stopCookie);

    [PreserveSig]
    int AllocCapFile([MarshalAs(UnmanagedType.LPWStr)] string fileName, long size);

    [PreserveSig]
    int CopyCaptureFile(
        [MarshalAs(UnmanagedType.LPWStr)] string oldFileName,
        [MarshalAs(UnmanagedType.LPWStr)] string newFileName,
        int allowEscAbort,
        IntPtr callback);

    [PreserveSig]
    int FindPin(
        IBaseFilter source,
        int pinDirection,
        ref Guid category,
        ref Guid type,
        [MarshalAs(UnmanagedType.Bool)] bool unconnected,
        int index,
        out IntPtr pin);
}

[ComImport]
[Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICreateDevEnum
{
    [PreserveSig]
    int CreateClassEnumerator(ref Guid type, out INativeEnumMoniker? enumMoniker, int flags);
}

[ComImport]
[Guid("00000102-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeEnumMoniker
{
    [PreserveSig]
    int Next(int count, out INativeMoniker moniker, IntPtr fetched);

    [PreserveSig]
    int Skip(int count);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone(out INativeEnumMoniker enumMoniker);
}

[ComImport]
[Guid("0000000F-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INativeMoniker
{
    [PreserveSig]
    int GetClassID(out Guid classId);

    [PreserveSig]
    int IsDirty();

    [PreserveSig]
    int Load(IntPtr stream);

    [PreserveSig]
    int Save(IntPtr stream, [MarshalAs(UnmanagedType.Bool)] bool clearDirty);

    [PreserveSig]
    int GetSizeMax(out long size);

    [PreserveSig]
    int BindToObject(IntPtr bindContext, IntPtr monikerToLeft, ref Guid interfaceId, out IntPtr result);

    [PreserveSig]
    int BindToStorage(IntPtr bindContext, IntPtr monikerToLeft, ref Guid interfaceId, out IntPtr result);
}

[ComImport]
[Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyBag
{
    [PreserveSig]
    int Read(
        [MarshalAs(UnmanagedType.LPWStr)] string propertyName,
        [MarshalAs(UnmanagedType.Struct)] out object value,
        IntPtr errorLog);

    [PreserveSig]
    int Write(
        [MarshalAs(UnmanagedType.LPWStr)] string propertyName,
        [MarshalAs(UnmanagedType.Struct)] ref object value);
}
