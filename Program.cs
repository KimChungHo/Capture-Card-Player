using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

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
    private readonly string? deviceSelector;
    private readonly Panel previewHost = new();
    private readonly Label statusLabel = new();
    private DirectShowPreviewGraph? graph;

    public PreviewForm(string? deviceSelector)
    {
        this.deviceSelector = deviceSelector;

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
            graph = DirectShowPreviewGraph.Start(previewHost.Handle, previewHost.ClientSize, deviceSelector);
            statusLabel.Visible = false;
            Text = $"CaptureCardPlayer - {graph.DeviceName}";
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
    private IBaseFilter? rendererFilter;
    private IMediaControl? mediaControl;
    private IVideoWindow? videoWindow;

    private DirectShowPreviewGraph(string deviceName)
    {
        DeviceName = deviceName;
    }

    public string DeviceName { get; private set; }

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
            ReleaseComObject(mediaControl);
            ReleaseComObject(rendererFilter);
            ReleaseComObject(sourceFilter);
            ReleaseComObject(captureBuilder);
            ReleaseComObject(graphBuilder);

            videoWindow = null;
            mediaControl = null;
            rendererFilter = null;
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

        DiagnosticLog.Write("Attaching video window.");
        mediaControl = (IMediaControl)graphBuilder;
        videoWindow = (IVideoWindow)graphBuilder;

        CheckHr(videoWindow.put_Owner(owner), "Failed to attach the video output window.");
        CheckHr(videoWindow.put_WindowStyle(WsChild | WsClipSiblings), "Failed to configure the video output window.");
        _ = videoWindow.put_MessageDrain(owner);
        Resize(size);
        CheckHr(videoWindow.put_Visible(OaTrue), "Failed to show the video output window.");

        DiagnosticLog.Write("Running graph.");
        CheckHr(mediaControl.Run(), "Failed to start the video capture device.");
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
        public static IBaseFilter Bind(string? selector, out string deviceName)
        {
            ICreateDevEnum? createDevEnum = null;
            IEnumMoniker? enumMoniker = null;
            var monikers = new List<IMoniker>();

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
                var current = new IMoniker[1];
                while (enumMoniker.Next(1, current, IntPtr.Zero) == HrSuccess)
                {
                    DiagnosticLog.Write("Found video capture moniker.");
                    monikers.Add(current[0]);
                    current = new IMoniker[1];
                }

                if (monikers.Count == 0)
                {
                    throw new InvalidOperationException("No video capture device was found.");
                }

                DiagnosticLog.Write($"Selecting from {monikers.Count} video capture device(s).");
                List<string> failures = new();
                foreach (int selectedIndex in BuildCandidateOrder(monikers, selector))
                {
                    IMoniker selected = monikers[selectedIndex];
                    string candidateName = GetFriendlyName(selected) ?? $"Video Capture Device {selectedIndex}";
                    DiagnosticLog.Write($"Trying device {selectedIndex}: {candidateName}.");

                    try
                    {
                        Guid baseFilterId = typeof(IBaseFilter).GUID;
                        DiagnosticLog.Write("Binding selected moniker to IBaseFilter.");
                        selected.BindToObject(null!, null!, ref baseFilterId, out object sourceObject);
                        DiagnosticLog.Write("Selected moniker bound to IBaseFilter.");
                        deviceName = candidateName;
                        return (IBaseFilter)sourceObject;
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
                foreach (IMoniker moniker in monikers)
                {
                    ReleaseComObject(moniker);
                }

                ReleaseComObject(enumMoniker);
                ReleaseComObject(createDevEnum);
            }
        }

        private static IEnumerable<int> BuildCandidateOrder(IReadOnlyList<IMoniker> monikers, string? selector)
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

            for (int i = 0; i < monikers.Count; i++)
            {
                string? name = GetFriendlyName(monikers[i]);
                if (name?.Contains(selector, StringComparison.OrdinalIgnoreCase) == true)
                {
                    yield return i;
                }
            }
        }

        private static string? GetFriendlyName(IMoniker moniker)
        {
            object? bagObject = null;
            try
            {
                Guid propertyBagId = typeof(IPropertyBag).GUID;
                moniker.BindToStorage(null!, null!, ref propertyBagId, out bagObject);
                var propertyBag = (IPropertyBag)bagObject;
                int hr = propertyBag.Read("FriendlyName", out object value, IntPtr.Zero);
                return hr >= 0 ? value as string : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                ReleaseComObject(bagObject);
            }
        }
    }
}

internal static class DirectShowGuids
{
    public static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    public static readonly Guid PinCategoryPreview = new("FB6C4282-0353-11D1-905F-0000C0CC16BA");
    public static readonly Guid PinCategoryCapture = new("FB6C4281-0353-11D1-905F-0000C0CC16BA");
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
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
    int CreateClassEnumerator(ref Guid type, out IEnumMoniker? enumMoniker, int flags);
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
