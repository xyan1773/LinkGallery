using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Forms;
using System.Xml.Linq;

namespace LinkGallery.E2E;

internal static class Program
{
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly List<CheckResult> Results = [];

    [STAThread]
    public static int Main(string[] args)
    {
        var options = Options.Parse(args);
        Directory.CreateDirectory(options.ArtifactsDirectory);
        Directory.CreateDirectory(options.DataDirectory);
        Directory.CreateDirectory(options.ImportDirectory);

        using var desktop = StartDesktop(options);
        try
        {
            RunCoreJourney(desktop, options);
        }
        catch (Exception exception)
        {
            Record("harness", false, exception.Message, 0, "blocking");
            CaptureScreen(Path.Combine(options.ArtifactsDirectory, "failure.png"));
        }
        finally
        {
            if (!desktop.HasExited)
            {
                desktop.CloseMainWindow();
                if (!desktop.WaitForExit(3000))
                {
                    desktop.Kill(entireProcessTree: true);
                }
            }
        }

        if (desktop.HasExited && desktop.ExitCode != 0)
        {
            Record("desktop-process-exit", false, $"Exit code {desktop.ExitCode}", 0, "blocking");
        }

        WriteReports(options);
        return Results.All(static result => result.Passed) ? 0 : 1;
    }

    private static Process StartDesktop(Options options)
    {
        var start = new ProcessStartInfo(options.DesktopExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(options.DesktopExecutable)
                ?? Environment.CurrentDirectory,
        };
        start.Environment["LINKGALLERY_E2E"] = "1";
        start.Environment["LINKGALLERY_E2E_DATA_DIRECTORY"] = options.DataDirectory;
        start.Environment["LINKGALLERY_E2E_IMPORT_DIRECTORY"] = options.ImportDirectory;
        start.Environment.TryGetValue("windir", out var windowsDirectory);
        if (string.IsNullOrWhiteSpace(windowsDirectory))
        {
            start.Environment.TryGetValue("SystemRoot", out var systemRoot);
            start.Environment["windir"] = string.IsNullOrWhiteSpace(systemRoot)
                ? Environment.SystemDirectory
                : systemRoot;
        }
        return Process.Start(start)
            ?? throw new InvalidOperationException("Unable to start LinkGallery desktop.");
    }

    private static void RunCoreJourney(Process desktop, Options options)
    {
        var mainWindow = WaitForWindow(desktop.Id, "LinkGallery", ElementTimeout);
        var address = FindById(mainWindow, "AddressTextBox");
        ((ValuePattern)address.GetCurrentPattern(ValuePattern.Pattern)).SetValue(options.Address);

        var connect = FindById(mainWindow, "ConnectButton");
        var timer = Stopwatch.StartNew();
        Invoke(connect);
        if (options.ExpectConnectionFailure)
        {
            WaitUntil(
                () =>
                {
                    var text = ReadName(FindById(mainWindow, "StatusText"));
                    return text.Contains("无法连接", StringComparison.Ordinal) ||
                        text.Contains("网络不可达", StringComparison.Ordinal) ||
                        text.Contains("拒绝连接", StringComparison.Ordinal) ||
                        text.Contains("超时", StringComparison.Ordinal);
                },
                options.ConnectTimeout,
                "Desktop did not expose a connection failure state.");
            timer.Stop();
            Record(
                "connection-error-feedback",
                true,
                $"Actionable connection error after {timer.Elapsed.TotalMilliseconds:F0} ms",
                timer.Elapsed.TotalMilliseconds,
                "experience");
            CaptureScreen(Path.Combine(options.ArtifactsDirectory, "connection-error.png"));
            return;
        }

        WaitUntil(
            () => ReadName(FindById(mainWindow, "StatusText")).Contains("已连接", StringComparison.Ordinal),
            options.ConnectTimeout,
            "Desktop did not report a connected stage.");
        timer.Stop();
        Record(
            "connect-stage",
            timer.Elapsed <= TimeSpan.FromSeconds(1),
            $"Connected stage after {timer.Elapsed.TotalMilliseconds:F0} ms",
            timer.Elapsed.TotalMilliseconds,
            "performance");

        timer.Restart();
        var timeline = WaitForElementById(
            mainWindow,
            "TimelineList",
            options.FirstPageTimeout);
        var items = WaitForListItems(timeline, options.FirstPageTimeout);
        timer.Stop();
        Record(
            "initial-page",
            items.Count > 0 && timer.Elapsed <= TimeSpan.FromSeconds(3),
            $"{items.Count} realized items after {timer.Elapsed.TotalMilliseconds:F0} ms",
            timer.Elapsed.TotalMilliseconds,
            "performance");

        var selected = items.Cast<AutomationElement>()
            .FirstOrDefault(element => ReadName(element).EndsWith(".JPG", StringComparison.OrdinalIgnoreCase))
            ?? items[0];
        Select(selected);
        Invoke(FindById(selected, "OpenMediaButton"));

        var detailFileName = WaitForProcessElementById(
            desktop.Id,
            "FileNameText",
            ElementTimeout);
        var detail = FindWindowAncestor(detailFileName);
        var fileName = ReadName(FindById(detail, "FileNameText"));
        Record("open-detail", !string.IsNullOrWhiteSpace(fileName), $"Opened {fileName}", 0, "functional");

        TryInvoke(detail, "ZoomInButton");
        TryInvoke(detail, "ZoomResetButton");
        TryInvoke(detail, "NextButton");
        CloseWindow(detail);

        var refreshedItems = WaitForListItems(timeline, ElementTimeout);
        if (options.RequireVideo)
        {
            refreshedItems = RunVideoJourney(
                desktop,
                mainWindow,
                timeline,
                refreshedItems);
        }

        if (!options.SkipImport)
        {
            selected = refreshedItems[0];
            Select(selected);
            timer.Restart();
            Invoke(FindById(mainWindow, "ImportSelectedButton"));
            WaitUntil(
                () => ReadName(FindById(mainWindow, "StatusText")).Contains("加入导入中心", StringComparison.Ordinal),
                ElementTimeout,
                "Import did not enter the queue.");
            timer.Stop();
            Record(
                "import-feedback",
                timer.Elapsed <= TimeSpan.FromMilliseconds(500),
                $"Queue feedback after {timer.Elapsed.TotalMilliseconds:F0} ms",
                timer.Elapsed.TotalMilliseconds,
                "experience");

            TryInvoke(mainWindow, "PauseAllButton");
            WaitUntil(
                () =>
                {
                    var status = ReadName(FindById(mainWindow, "TransferStatusText"));
                    return status.Contains("已暂停", StringComparison.Ordinal) ||
                        status.Contains("已完成", StringComparison.Ordinal);
                },
                ElementTimeout,
                "Import did not settle after pause.");
            var transferStatus = ReadName(FindById(mainWindow, "TransferStatusText"));
            if (transferStatus.Contains("已暂停", StringComparison.Ordinal))
            {
                TryInvoke(mainWindow, "ResumeAllButton");
            }
            timer.Restart();
            WaitUntil(
                () => ReadName(FindById(mainWindow, "ImportSummaryText"))
                    .Contains("1/1 完成", StringComparison.Ordinal),
                options.ImportTimeout,
                "Import did not complete before the timeout.");
            timer.Stop();
            var importedFiles = Directory.GetFiles(options.ImportDirectory);
            Record(
                "import-complete",
                importedFiles.Length == 1,
                $"{importedFiles.Length} completed file(s) after {timer.Elapsed.TotalMilliseconds:F0} ms",
                timer.Elapsed.TotalMilliseconds,
                "functional");
        }

        for (var iteration = 0; iteration < options.ConnectionIterations; iteration++)
        {
            TryInvoke(mainWindow, "NavDevicesButton");
            Invoke(FindById(mainWindow, "DisconnectButton"));
            Invoke(FindById(mainWindow, "ConnectButton"));
            WaitUntil(
                () => ReadName(FindById(mainWindow, "StatusText"))
                    .Contains("已连接", StringComparison.Ordinal),
                options.ConnectTimeout,
                $"Reconnect iteration {iteration + 1} failed.");
        }
        Record(
            "connection-cycle",
            true,
            $"{options.ConnectionIterations} reconnect iterations completed",
            0,
            "stability");

        if (options.SoakDuration > TimeSpan.Zero)
        {
            RunSoak(desktop, mainWindow, options);
        }

        CaptureScreen(Path.Combine(options.ArtifactsDirectory, "core-journey.png"));
    }

    private static AutomationElementCollection RunVideoJourney(
        Process desktop,
        AutomationElement mainWindow,
        AutomationElement timeline,
        AutomationElementCollection items)
    {
        var video = FindVideo(items);
        var usedSearch = false;
        if (video is null)
        {
            usedSearch = true;
            var search = FindById(mainWindow, "SearchTextBox");
            ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue(".MP4");
            var searchButton = FindById(mainWindow, "SearchButton");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            do
            {
                Invoke(searchButton);
                Thread.Sleep(2000);
                items = timeline.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.ListItem));
                video = FindVideo(items);
            } while (video is null && DateTimeOffset.UtcNow < deadline);
            if (video is null)
            {
                throw new InvalidOperationException(
                    "No MP4 item was found after waiting for the local index.");
            }
        }

        Select(video);
        Invoke(FindById(video, "OpenMediaButton"));
        var status = WaitForProcessElementById(desktop.Id, "VideoStatusText", ElementTimeout);
        var timer = Stopwatch.StartNew();
        WaitUntil(
            () => ReadName(status).Contains("已就绪", StringComparison.Ordinal),
            ElementTimeout,
            "Video did not become ready.");
        timer.Stop();
        Record(
            "video-ready",
            timer.Elapsed <= TimeSpan.FromSeconds(5),
            $"Video ready after {timer.Elapsed.TotalMilliseconds:F0} ms",
            timer.Elapsed.TotalMilliseconds,
            "performance");

        var detail = FindWindowAncestor(status);
        Invoke(FindById(detail, "PlayPauseButton"));
        WaitUntil(
            () => ReadName(status).Contains("正在播放", StringComparison.Ordinal),
            ElementTimeout,
            "Video did not enter the playing state.");

        var progress = FindById(detail, "VideoProgress");
        if (progress.TryGetCurrentPattern(RangeValuePattern.Pattern, out var pattern))
        {
            var range = (RangeValuePattern)pattern;
            range.SetValue(range.Current.Maximum / 2);
            WaitUntil(
                () =>
                {
                    var text = ReadName(status);
                    return text.Contains("已定位", StringComparison.Ordinal) ||
                        text.Contains("正在播放", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(2),
                "Video seek did not settle.");
        }

        Record("video-play-seek", true, "Play and midpoint seek completed", 0, "functional");
        CloseWindow(detail);
        if (usedSearch)
        {
            var search = FindById(mainWindow, "SearchTextBox");
            ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue(string.Empty);
            Invoke(FindById(mainWindow, "SearchButton"));
            items = WaitForListItems(timeline, ElementTimeout);
        }

        return items;
    }

    private static AutomationElement? FindVideo(AutomationElementCollection items) =>
        items.Cast<AutomationElement>().FirstOrDefault(
            element => ReadName(element).EndsWith(".MP4", StringComparison.OrdinalIgnoreCase));

    private static void RunSoak(Process desktop, AutomationElement mainWindow, Options options)
    {
        var started = Stopwatch.StartNew();
        var samples = new List<long>();
        var cycles = 0;
        while (started.Elapsed < options.SoakDuration)
        {
            desktop.Refresh();
            samples.Add(desktop.WorkingSet64);
            Invoke(FindById(mainWindow, "DisconnectButton"));
            Invoke(FindById(mainWindow, "ConnectButton"));
            WaitUntil(
                () => ReadName(FindById(mainWindow, "StatusText"))
                    .Contains("已连接", StringComparison.Ordinal),
                options.ConnectTimeout,
                "Soak reconnect failed.");
            cycles++;
        }

        var baseline = samples.Take(Math.Min(3, samples.Count)).Average();
        var tail = samples.TakeLast(Math.Min(3, samples.Count)).Average();
        var growth = baseline == 0 ? 0 : (tail - baseline) / baseline;
        Record(
            "soak",
            growth <= 0.20,
            $"{cycles} cycles; working-set growth {growth:P1}",
            started.Elapsed.TotalMilliseconds,
            "stability");
    }

    private static AutomationElement WaitForWindow(
        int processId,
        string title,
        TimeSpan timeout)
    {
        AutomationElement? result = null;
        WaitUntil(
            () =>
            {
                var condition = new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                    new PropertyCondition(AutomationElement.NameProperty, title));
                result = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
                return result is not null;
            },
            timeout,
            $"Window '{title}' was not found.");
        return result!;
    }

    private static AutomationElement WaitForProcessElementById(
        int processId,
        string automationId,
        TimeSpan timeout)
    {
        AutomationElement? result = null;
        WaitUntil(
            () =>
            {
                result = AutomationElement.RootElement.FindFirst(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(
                            AutomationElement.ProcessIdProperty,
                            processId),
                        new PropertyCondition(
                            AutomationElement.AutomationIdProperty,
                            automationId)));
                return result is not null;
            },
            timeout,
            $"Process element '{automationId}' was not found.");
        return result!;
    }

    private static AutomationElement FindWindowAncestor(AutomationElement element)
    {
        for (var current = element; current is not null; current = TreeWalker.ControlViewWalker.GetParent(current))
        {
            if (current.Current.ControlType == ControlType.Window)
            {
                return current;
            }
        }

        throw new InvalidOperationException("The media detail window ancestor was not found.");
    }

    private static AutomationElement FindById(AutomationElement root, string automationId)
    {
        var element = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        return element ?? throw new InvalidOperationException(
            $"Automation element '{automationId}' was not found.");
    }

    private static AutomationElement WaitForElementById(
        AutomationElement root,
        string automationId,
        TimeSpan timeout)
    {
        AutomationElement? result = null;
        WaitUntil(
            () =>
            {
                result = root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        automationId));
                return result is not null;
            },
            timeout,
            $"Automation element '{automationId}' was not found.");
        return result!;
    }

    private static AutomationElementCollection WaitForListItems(
        AutomationElement list,
        TimeSpan timeout)
    {
        AutomationElementCollection? result = null;
        WaitUntil(
            () =>
            {
                result = list.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.ListItem));
                return result.Count > 0;
            },
            timeout,
            "Timeline did not expose any list items.");
        return result!;
    }

    private static void WaitUntil(Func<bool> predicate, TimeSpan timeout, string failureMessage)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException(failureMessage);
    }

    private static string ReadName(AutomationElement element) =>
        element.GetCurrentPropertyValue(AutomationElement.NameProperty) as string ?? string.Empty;

    private static void Invoke(AutomationElement element) =>
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static void Select(AutomationElement element) =>
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    private static void TryInvoke(AutomationElement root, string automationId)
    {
        var element = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        if (element is not null &&
            element.Current.IsEnabled &&
            element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
        {
            ((InvokePattern)pattern).Invoke();
        }
    }

    private static void CloseWindow(AutomationElement window) =>
        ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();

    private static void Record(
        string name,
        bool passed,
        string detail,
        double durationMilliseconds,
        string category) =>
        Results.Add(new CheckResult(name, passed, detail, durationMilliseconds, category));

    private static void CaptureScreen(string path)
    {
        var bounds = Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("Primary screen is unavailable.");
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static void WriteReports(Options options)
    {
        var report = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            address = options.Address,
            passed = Results.All(static result => result.Passed),
            checks = Results,
        };
        File.WriteAllText(
            Path.Combine(options.ArtifactsDirectory, "desktop-e2e.json"),
            JsonSerializer.Serialize(report, JsonOptions));

        var failures = Results.Count(static result => !result.Passed);
        var suite = new XElement(
            "testsuite",
            new XAttribute("name", "LinkGallery desktop E2E"),
            new XAttribute("tests", Results.Count),
            new XAttribute("failures", failures),
            Results.Select(result =>
            {
                var test = new XElement(
                    "testcase",
                    new XAttribute("name", result.Name),
                    new XAttribute(
                        "time",
                        (result.DurationMilliseconds / 1000).ToString("F3", CultureInfo.InvariantCulture)));
                if (!result.Passed)
                {
                    test.Add(new XElement(
                        "failure",
                        new XAttribute("message", result.Detail),
                        result.Detail));
                }

                return test;
            }));
        new XDocument(suite).Save(Path.Combine(options.ArtifactsDirectory, "desktop-e2e.xml"));
    }

    private sealed record CheckResult(
        string Name,
        bool Passed,
        string Detail,
        double DurationMilliseconds,
        string Category);

    private sealed record Options(
        string DesktopExecutable,
        string Address,
        string ArtifactsDirectory,
        string DataDirectory,
        string ImportDirectory,
        TimeSpan ConnectTimeout,
        TimeSpan FirstPageTimeout,
        TimeSpan ImportTimeout,
        int ConnectionIterations,
        TimeSpan SoakDuration,
        bool RequireVideo,
        bool SkipImport,
        bool ExpectConnectionFailure)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Arguments must use --name value pairs.");
                }

                values[args[index][2..]] = args[index + 1];
            }

            string Required(string key) => values.TryGetValue(key, out var value)
                ? Path.GetFullPath(value)
                : throw new ArgumentException($"Missing --{key}.");

            var artifacts = Required("artifacts");
            return new Options(
                Required("desktop"),
                values.GetValueOrDefault("address", "127.0.0.1:39570"),
                artifacts,
                values.TryGetValue("data", out var data)
                    ? Path.GetFullPath(data)
                    : Path.Combine(artifacts, "desktop-data"),
                values.TryGetValue("imports", out var imports)
                    ? Path.GetFullPath(imports)
                    : Path.Combine(artifacts, "imports"),
                TimeSpan.FromSeconds(ParseInt(values, "connect-timeout", 15)),
                TimeSpan.FromSeconds(ParseInt(values, "first-page-timeout", 30)),
                TimeSpan.FromSeconds(ParseInt(values, "import-timeout", 120)),
                ParseInt(values, "iterations", 1),
                TimeSpan.FromMinutes(ParseInt(values, "soak-minutes", 0)),
                ParseBool(values, "require-video", true),
                ParseBool(values, "skip-import", false),
                ParseBool(values, "expect-connect-failure", false));
        }

        private static int ParseInt(
            Dictionary<string, string> values,
            string key,
            int defaultValue) =>
            values.TryGetValue(key, out var value)
                ? int.Parse(value, CultureInfo.InvariantCulture)
                : defaultValue;

        private static bool ParseBool(
            Dictionary<string, string> values,
            string key,
            bool defaultValue) =>
            values.TryGetValue(key, out var value)
                ? bool.Parse(value)
                : defaultValue;
    }
}
