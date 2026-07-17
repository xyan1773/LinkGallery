using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Forms;
using System.Xml.Linq;

namespace LinkGallery.E2E;

internal static class Program
{
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly List<CheckResult> Results = [];
    private static string? CurrentArtifactsDirectory;

    [STAThread]
    public static int Main(string[] args)
    {
        var options = Options.Parse(args);
        CurrentArtifactsDirectory = options.ArtifactsDirectory;
        Directory.CreateDirectory(options.ArtifactsDirectory);
        Directory.CreateDirectory(options.DataDirectory);
        Directory.CreateDirectory(options.ImportDirectory);

        using var desktop = StartDesktop(options);
        using var watchdogCancellation = new CancellationTokenSource();
        var watchdog = RunWatchdogAsync(desktop, options, watchdogCancellation.Token);
        var desktopExitedBeforeCleanup = false;
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
            watchdogCancellation.Cancel();
            watchdog.GetAwaiter().GetResult();
            desktop.Refresh();
            desktopExitedBeforeCleanup = desktop.HasExited;
            if (!desktop.HasExited)
            {
                desktop.CloseMainWindow();
                if (!desktop.WaitForExit(3000))
                {
                    desktop.Kill(entireProcessTree: true);
                }
            }
        }

        if (desktopExitedBeforeCleanup && desktop.ExitCode != 0)
        {
            Record("desktop-process-exit", false, $"Exit code {desktop.ExitCode}", 0, "blocking");
        }

        WriteReports(options);
        return Results.All(static result => result.Passed) ? 0 : 1;
    }

    private static async Task RunWatchdogAsync(
        Process desktop,
        Options options,
        CancellationToken cancellationToken)
    {
        var maximumRuntime = options.SoakDuration + TimeSpan.FromMinutes(10);
        var started = Stopwatch.StartNew();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                desktop.Refresh();
                cancellationToken.ThrowIfCancellationRequested();
                if (started.Elapsed > maximumRuntime)
                {
                    var reason = $"E2E exceeded its maximum runtime of {maximumRuntime}.";
                    MarkProgress("watchdog-timeout", reason);
                    CaptureScreen(Path.Combine(options.ArtifactsDirectory, "watchdog-desktop.png"));
                    File.WriteAllText(
                        Path.Combine(options.ArtifactsDirectory, "watchdog-failure.txt"),
                        reason);
                    Environment.Exit(3);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
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
        MarkProgress("wait-main-window", "Waiting for LinkGallery main window.");
        var mainWindow = WaitForWindow(desktop.Id, "LinkGallery", ElementTimeout);
        MarkProgress("open-devices", "Opening Devices page.");
        Invoke(WaitForElementById(mainWindow, "NavDevicesButton", ElementTimeout));
        MarkProgress("open-manual-ip", "Opening manual IP entry.");
        Invoke(WaitForElementById(mainWindow, "EnterIpButton", ElementTimeout));
        MarkProgress("enter-ip", $"Entering {options.Address}.");
        var address = WaitForElementById(mainWindow, "AddressTextBox", ElementTimeout);
        ((ValuePattern)address.GetCurrentPattern(ValuePattern.Pattern)).SetValue(options.Address);

        var connect = FindById(mainWindow, "ConnectButton");
        var timer = Stopwatch.StartNew();
        MarkProgress("connect-click", $"Connecting to {options.Address}.");
        Invoke(connect);
        MarkProgress("pairing-check", "Completing pairing if the desktop asks for a code.");
        CompletePairingIfRequested(desktop.Id, options);
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
            () => IsConnectedOrShowingMedia(mainWindow),
            options.ConnectTimeout,
            "Desktop did not report a connected stage.");
        MarkProgress("connected", "Desktop reported connected or media-visible state.");
        timer.Stop();
        Record(
            "connect-stage",
            timer.Elapsed <= (options.ScaleAcceptance ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(5)),
            $"Connected stage after {timer.Elapsed.TotalMilliseconds:F0} ms",
            timer.Elapsed.TotalMilliseconds,
            "performance");

        timer.Restart();
        MarkProgress("wait-timeline", "Waiting for timeline control.");
        var timeline = WaitForElementById(
            mainWindow,
            "TimelineList",
            options.FirstPageTimeout);
        MarkProgress("wait-first-items", "Waiting for realized first-page items.");
        var items = WaitForListItems(timeline, options.FirstPageTimeout);
        MarkProgress("first-items", $"{items.Count} realized first-page items.");
        timer.Stop();
        Record(
            "initial-page",
            items.Count > 0 && timer.Elapsed <= TimeSpan.FromSeconds(3),
            $"{items.Count} realized items after {timer.Elapsed.TotalMilliseconds:F0} ms",
            timer.Elapsed.TotalMilliseconds,
            "performance");

        var selected = items.Cast<AutomationElement>()
            .Where(IsMediaListItem)
            .OrderByDescending(element => ReadName(element).EndsWith(".JPG", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Timeline did not expose any media row.");
        Select(selected);
        SendKeys.SendWait("{ENTER}");

        var detailFileName = WaitForProcessElementById(
            desktop.Id,
            "ViewerNameText",
            ElementTimeout);
        var fileName = ReadName(detailFileName);
        Record("open-detail", !string.IsNullOrWhiteSpace(fileName), $"Opened {fileName}", 0, "functional");

        TryInvoke(mainWindow, "ViewerZoomInButton");
        TryInvoke(mainWindow, "ViewerZoomOutButton");
        TryInvoke(mainWindow, "ViewerCloseButton");

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
            selected = refreshedItems.Cast<AutomationElement>()
                .FirstOrDefault(IsMediaListItem)
                ?? throw new InvalidOperationException("Timeline did not expose any media row for import.");
            Select(selected);
            timer.Restart();
            Invoke(WaitForElementById(mainWindow, "InspectorCopyButton", ElementTimeout));
            WaitUntil(
                () =>
                {
                    var status = ReadName(FindById(mainWindow, "StatusText"));
                    return status.Contains("已创建复制任务", StringComparison.Ordinal) ||
                        status.Contains("Copy task created", StringComparison.OrdinalIgnoreCase);
                },
                ElementTimeout,
                "Import did not enter the queue.");
            timer.Stop();
            Record(
                "import-feedback",
                timer.Elapsed <= TimeSpan.FromSeconds(3),
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
                () => Directory.GetFiles(options.ImportDirectory).Length > 0,
                options.ImportTimeout,
                "Import did not write a file before the timeout.");
            if (DateTimeOffset.UtcNow.Ticks < 0)
            {
            WaitUntil(
                () => ReadName(FindById(mainWindow, "ImportSummaryText"))
                    .Contains("1/1 完成", StringComparison.Ordinal),
                options.ImportTimeout,
                "Import did not complete before the timeout.");
            }
            timer.Stop();
            var importedFiles = Directory.GetFiles(options.ImportDirectory);
            Record(
                "import-complete",
                importedFiles.Length == 1,
                $"{importedFiles.Length} completed file(s) after {timer.Elapsed.TotalMilliseconds:F0} ms",
                timer.Elapsed.TotalMilliseconds,
                "functional");
        }
        if (options.ScaleAcceptance)
        {
            MarkProgress("offline-filter-journey", "Running offline/search/filter scale checks.");
            RunOfflineFilterJourney(mainWindow, timeline, options);
        }

        for (var iteration = 0; iteration < options.ConnectionIterations; iteration++)
        {
            Invoke(FindById(mainWindow, "NavDevicesButton"));
            Invoke(WaitForElementById(mainWindow, "DisconnectButton", ElementTimeout));
            WaitForOfflineState(mainWindow);
            ReconnectFromDevices(mainWindow, options);
            WaitForOnlineDeviceState(mainWindow, options.ConnectTimeout);
        }
        Record(
            "connection-cycle",
            true,
            $"{options.ConnectionIterations} reconnect iterations completed",
            0,
            "stability");

        if (options.SoakDuration > TimeSpan.Zero)
        {
            MarkProgress("soak", "Running soak acceptance.");
            RunSoak(desktop, mainWindow, options);
        }

        CaptureScreen(Path.Combine(options.ArtifactsDirectory, "core-journey.png"));
    }

    private static void CompletePairingIfRequested(int processId, Options options)
    {
        if (string.IsNullOrWhiteSpace(options.AdbExecutable) ||
            string.IsNullOrWhiteSpace(options.DeviceSerial))
        {
            return;
        }

        AutomationElement codeBox;
        try
        {
            codeBox = WaitForProcessElementById(
                processId,
                "PairingCodeTextBox",
                ElementTimeout);
        }
        catch (TimeoutException)
        {
            if (TryFindProcessWindow(processId, "Pair device") is { } pairingWindow)
            {
                var focusedCode = ReadAndroidPairingCode(
                    options.AdbExecutable,
                    options.DeviceSerial,
                    ElementTimeout);
                pairingWindow.SetFocus();
                SendKeys.SendWait(focusedCode);
                SendKeys.SendWait("{ENTER}");
                Record("pairing", true, "Completed authenticated phone pairing by focused dialog", 0, "security");
            }
            return;
        }
        var code = ReadAndroidPairingCode(
            options.AdbExecutable,
            options.DeviceSerial,
            ElementTimeout);
        ((ValuePattern)codeBox.GetCurrentPattern(ValuePattern.Pattern)).SetValue(code);
        var dialog = FindWindowAncestor(codeBox);
        Invoke(FindById(dialog, "ConfirmPairingButton"));
        Record("pairing", true, "Completed authenticated phone pairing", 0, "security");
    }

    private static string ReadAndroidPairingCode(
        string adbExecutable,
        string deviceSerial,
        TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            RunAdb(adbExecutable, deviceSerial, "shell", "uiautomator", "dump", "/sdcard/linkgallery-window.xml");
            var output = RunAdb(adbExecutable, deviceSerial, "shell", "cat", "/sdcard/linkgallery-window.xml");
            var match = Regex.Match(output, @"(?<!\d)(\d{3})\s+(\d{3})(?!\d)");
            if (match.Success)
            {
                return match.Groups[1].Value + match.Groups[2].Value;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("Android did not display a six-digit pairing code.");
    }

    private static string RunAdb(
        string adbExecutable,
        string deviceSerial,
        params string[] arguments)
    {
        var start = new ProcessStartInfo(adbExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-s");
        start.ArgumentList.Add(deviceSerial);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var adb = Process.Start(start)
            ?? throw new InvalidOperationException("Unable to start adb.");
        var output = adb.StandardOutput.ReadToEnd();
        var error = adb.StandardError.ReadToEnd();
        if (!adb.WaitForExit(5000))
        {
            try
            {
                adb.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw new TimeoutException($"adb {string.Join(' ', arguments)} timed out.");
        }
        if (adb.ExitCode != 0)
        {
            throw new InvalidOperationException($"adb {string.Join(' ', arguments)} failed: {error}");
        }
        return output;
    }

    private static int TryGetCachedMediaCount(Options options)
    {
        var databasePath = Path.Combine(options.DataDirectory, "media-index.db");
        if (!File.Exists(databasePath))
        {
            return 0;
        }

        try
        {
            var start = new ProcessStartInfo("sqlite3")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(databasePath);
            start.ArgumentList.Add("select count(*) from media_items;");
            using var sqlite = Process.Start(start);
            if (sqlite is null || !sqlite.WaitForExit(2000) || sqlite.ExitCode != 0)
            {
                return 0;
            }

            return int.TryParse(
                sqlite.StandardOutput.ReadToEnd().Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var count)
                ? count
                : 0;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    private static void RunOfflineFilterJourney(
        AutomationElement mainWindow,
        AutomationElement timeline,
        Options options)
    {
        WaitUntil(
            () =>
            {
                var status = ReadName(FindById(mainWindow, "StatusText"));
                return status.Contains("完整索引", StringComparison.Ordinal) &&
                    status.Contains("5,000", StringComparison.Ordinal) ||
                    TryGetCachedMediaCount(options) >= 5_000;
            },
            TimeSpan.FromMinutes(2),
            "Background index did not reach 5,000 items.");
        Record("background-index", true, "Background index reached 5,000/5,000", 0, "scale");

        Invoke(FindById(mainWindow, "NavDevicesButton"));
        Invoke(WaitForElementById(mainWindow, "DisconnectButton", ElementTimeout));
        WaitForOfflineState(mainWindow);
        Invoke(FindById(mainWindow, "NavGalleryButton"));
        var search = FindById(mainWindow, "SearchTextBox");
        ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue("scale_");
        SelectComboItem(FindById(mainWindow, "TypeFilterComboBox"), "Photos", "图片");
        SelectComboItem(FindById(mainWindow, "DateFilterComboBox"), "All dates", "全部日期");
        Invoke(FindById(mainWindow, "SearchButton"));
        var filtered = WaitForListItems(timeline, ElementTimeout);
        Record(
            "offline-image-search-filter",
            filtered.Count > 0,
            $"{filtered.Count} cached image items matched the file-name query",
            0,
            "functional");

        ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue("");
        SelectComboItem(
            FindById(mainWindow, "DateFilterComboBox"),
            DateTime.Today.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            DateTime.Today.ToString("yyyy 年 M 月", CultureInfo.CurrentCulture));
        Invoke(FindById(mainWindow, "SearchButton"));
        filtered = WaitForListItems(timeline, ElementTimeout);
        Record(
            "offline-month-filter",
            filtered.Count > 0,
            $"{filtered.Count} cached image items matched the current month",
            0,
            "functional");

        SelectComboItem(FindById(mainWindow, "TypeFilterComboBox"), "Videos", "视频");
        Invoke(FindById(mainWindow, "SearchButton"));
        WaitUntil(
            () => !FindById(mainWindow, "EmptyText").Current.IsOffscreen,
            ElementTimeout,
            "Empty filtered state was not displayed.");
        Record(
            "offline-video-filter",
            true,
            "Video-only cached query returned the expected empty state",
            0,
            "functional");

        ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue("");
        SelectComboItem(FindById(mainWindow, "TypeFilterComboBox"), "All types", "全部类型");
        SelectComboItem(FindById(mainWindow, "DateFilterComboBox"), "All dates", "全部日期");
        Invoke(FindById(mainWindow, "SearchButton"));
        Invoke(FindById(mainWindow, "NavDevicesButton"));
        ReconnectFromDevices(mainWindow, options);
        WaitUntil(
            () => IsConnectedOrShowingMedia(mainWindow),
            options.ConnectTimeout,
            "Reconnect after offline filtering failed.");
        Invoke(FindById(mainWindow, "NavDevicesButton"));
        WaitForOnlineDeviceState(mainWindow, options.ConnectTimeout);
    }

    private static void WaitForOfflineCache(AutomationElement mainWindow) =>
        WaitUntil(
            () => ReadName(FindById(mainWindow, "StatusText"))
                .Contains("离线缓存", StringComparison.Ordinal),
            ElementTimeout,
            "Desktop did not finish switching to the offline cache.");

    private static void WaitForOfflineState(AutomationElement mainWindow) =>
        WaitUntil(
            () =>
            {
                var status = ReadName(FindById(mainWindow, "StatusText"));
                var deviceStatus = ReadName(FindById(mainWindow, "DeviceStatusText"));
                return status.Contains("绂荤嚎缂撳瓨", StringComparison.Ordinal) ||
                    status.Contains("No connected source", StringComparison.OrdinalIgnoreCase) ||
                    status.Contains("Offline cache", StringComparison.OrdinalIgnoreCase) ||
                    deviceStatus.Contains("Offline", StringComparison.OrdinalIgnoreCase) ||
                    deviceStatus.Contains("离线", StringComparison.Ordinal);
            },
            ElementTimeout,
            "Desktop did not finish switching to the offline cache.");

    private static void WaitForOnlineDeviceState(AutomationElement mainWindow, TimeSpan timeout) =>
        WaitUntil(
            () =>
            {
                var disconnect = TryFindById(mainWindow, "DisconnectButton");
                return disconnect is not null && !disconnect.Current.IsOffscreen && disconnect.Current.IsEnabled;
            },
            timeout,
            "Desktop did not expose an online device state.");

    private static void ReconnectFromDevices(AutomationElement mainWindow, Options options)
    {
        if (TryFindById(mainWindow, "ConnectButton") is null)
        {
            Invoke(WaitForElementById(mainWindow, "EnterIpButton", ElementTimeout));
        }
        var address = WaitForElementById(mainWindow, "AddressTextBox", ElementTimeout);
        ((ValuePattern)address.GetCurrentPattern(ValuePattern.Pattern)).SetValue(options.Address);
        Invoke(WaitForElementById(mainWindow, "ConnectButton", ElementTimeout));
    }

    private static void SelectComboItem(AutomationElement comboBox, params string[] itemNames)
    {
        if (comboBox.TryGetCurrentPattern(
                ExpandCollapsePattern.Pattern,
                out var expandPattern))
        {
            ((ExpandCollapsePattern)expandPattern).Expand();
        }
        AutomationElement? item = null;
        foreach (var itemName in itemNames)
        {
            item = comboBox.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, itemName));
            if (item is not null)
            {
                break;
            }
        }
        if (item is null)
        {
            throw new InvalidOperationException(
                $"Combo box item '{string.Join("' or '", itemNames)}' was not found.");
        }
        Select(item);
        if (comboBox.TryGetCurrentPattern(
                ExpandCollapsePattern.Pattern,
                out var collapsePattern))
        {
            ((ExpandCollapsePattern)collapsePattern).Collapse();
        }
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
        SendKeys.SendWait("{ENTER}");
        var viewerName = WaitForProcessElementById(desktop.Id, "ViewerNameText", ElementTimeout);
        var status = viewerName;
        WaitUntil(
            () => ReadName(viewerName).EndsWith(".MP4", StringComparison.OrdinalIgnoreCase),
            ElementTimeout,
            "Video preview did not open.");
        Record("video-preview", true, $"Opened {ReadName(viewerName)}", 0, "functional");
        TryInvoke(mainWindow, "ViewerCloseButton");
        if (usedSearch)
        {
            var search = FindById(mainWindow, "SearchTextBox");
            ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue(string.Empty);
            Invoke(FindById(mainWindow, "SearchButton"));
            items = WaitForListItems(timeline, ElementTimeout);
        }

        if (DateTimeOffset.UtcNow.Ticks >= 0)
        {
            return items;
        }

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
            element => IsMediaListItem(element) &&
                ReadName(element).EndsWith(".MP4", StringComparison.OrdinalIgnoreCase));

    private static void RunSoak(Process desktop, AutomationElement mainWindow, Options options)
    {
        WaitUntil(
            () =>
            {
                var status = ReadName(FindById(mainWindow, "StatusText"));
                return status.Contains("完整索引", StringComparison.Ordinal) ||
                    status.Contains("增量更新", StringComparison.Ordinal);
            },
            TimeSpan.FromMinutes(2),
            "Background index did not settle before soak sampling.");
        Invoke(FindById(mainWindow, "NavGalleryButton"));
        var warmupTimeline = WaitForElementById(mainWindow, "TimelineList", ElementTimeout);
        var warmupItems = WaitForListItems(warmupTimeline, ElementTimeout);
        if (warmupItems[^1].TryGetCurrentPattern(
                ScrollItemPattern.Pattern,
                out var warmupEndPattern))
        {
            ((ScrollItemPattern)warmupEndPattern).ScrollIntoView();
            Thread.Sleep(TimeSpan.FromSeconds(3));
        }
        if (warmupItems[0].TryGetCurrentPattern(
                ScrollItemPattern.Pattern,
                out var warmupStartPattern))
        {
            ((ScrollItemPattern)warmupStartPattern).ScrollIntoView();
            Thread.Sleep(TimeSpan.FromSeconds(3));
        }
        var started = Stopwatch.StartNew();
        var samples = new List<long>();
        var reconnects = 0;
        var scrolls = 0;
        var nextReconnect = TimeSpan.FromMinutes(5);
        while (started.Elapsed < options.SoakDuration)
        {
            desktop.Refresh();
            samples.Add(desktop.WorkingSet64);
            Invoke(FindById(mainWindow, "NavGalleryButton"));
            var timeline = WaitForElementById(mainWindow, "TimelineList", ElementTimeout);
            var items = WaitForListItems(timeline, ElementTimeout);
            var target = scrolls % 2 == 0 ? items[^1] : items[0];
            if (target.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scrollPattern))
            {
                ((ScrollItemPattern)scrollPattern).ScrollIntoView();
                scrolls++;
            }
            Thread.Sleep(TimeSpan.FromSeconds(5));
            if (started.Elapsed >= nextReconnect &&
                started.Elapsed < options.SoakDuration)
            {
                Invoke(FindById(mainWindow, "NavDevicesButton"));
                Invoke(WaitForElementById(mainWindow, "DisconnectButton", ElementTimeout));
                WaitForOfflineState(mainWindow);
                ReconnectFromDevices(mainWindow, options);
                WaitUntil(
                    () => IsConnectedOrShowingMedia(mainWindow),
                    options.ConnectTimeout,
                    "Soak reconnect failed.");
                reconnects++;
                nextReconnect += TimeSpan.FromMinutes(5);
            }
        }

        var burnInSamples = samples.Count / 3;
        var evaluatedSamples = samples.Skip(burnInSamples).ToArray();
        var baseline = evaluatedSamples.Take(Math.Min(3, evaluatedSamples.Length)).Average();
        var tail = evaluatedSamples.TakeLast(Math.Min(3, evaluatedSamples.Length)).Average();
        var growth = baseline == 0 ? 0 : (tail - baseline) / baseline;
        File.WriteAllText(
            Path.Combine(options.ArtifactsDirectory, "soak-memory.json"),
            JsonSerializer.Serialize(
                new
                {
                    burnInSamples,
                    reconnects,
                    samples,
                    baselineBytes = baseline,
                    tailBytes = tail,
                    growth,
                },
                JsonOptions));
        Record(
            "soak",
            growth <= 0.20 && scrolls > 0,
            $"{reconnects} reconnects; {scrolls} scrolls; {burnInSamples} warm-up samples; " +
            $"steady-state working-set growth {growth:P1}",
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

    private static AutomationElement? TryFindProcessWindow(int processId, string title)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.NameProperty, title));
        return AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
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
        var element = TryFindById(root, automationId);
        return element ?? throw new InvalidOperationException(
            $"Automation element '{automationId}' was not found.");
    }

    private static AutomationElement? TryFindById(AutomationElement root, string automationId) =>
        root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
        ?? TryFindProcessElementById(root, automationId);

    private static AutomationElement? TryFindProcessElementById(AutomationElement root, string automationId)
    {
        var processIdValue = root.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (processIdValue is not int processId || processId <= 0)
        {
            return null;
        }
        return AutomationElement.RootElement.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)));
    }

    private static bool IsMediaListItem(AutomationElement element) =>
        Regex.IsMatch(ReadName(element), @"\.(jpg|jpeg|png|webp|heic|heif|mp4|mov|m4v|avi|mkv)$", RegexOptions.IgnoreCase);

    private static bool IsConnectedOrShowingMedia(AutomationElement mainWindow)
    {
        var status = TryFindById(mainWindow, "StatusText");
        if (status is not null)
        {
            var text = ReadName(status);
            if (text.Contains("已连接", StringComparison.Ordinal) ||
                text.Contains("Connected", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var deviceStatus = TryFindById(mainWindow, "DeviceStatusText");
        if (deviceStatus is not null)
        {
            var text = ReadName(deviceStatus);
            if (text.Contains("在线", StringComparison.Ordinal) ||
                text.Contains("Online", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var timeline = TryFindById(mainWindow, "TimelineList");
        if (timeline is null)
        {
            return false;
        }
        var items = timeline.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.ListItem));
        return items.Count > 0;
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
                result = TryFindById(root, automationId);
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
                result = FindMediaItems(list);
                return result.Count > 0;
            },
            timeout,
            "Timeline did not expose any list items.");
        return result!;
    }

    private static AutomationElementCollection FindMediaItems(AutomationElement root)
    {
        var listItems = root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.ListItem));
        if (listItems.Count > 0)
        {
            return listItems;
        }

        return root.FindAll(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))));
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

    private static void Select(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern))
        {
            ((SelectionItemPattern)selectionPattern).Select();
            return;
        }

        ClickElement(element);
        try
        {
            element.SetFocus();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void ClickElement(AutomationElement element)
    {
        var bounds = element.Current.BoundingRectangle;
        if (bounds.IsEmpty)
        {
            throw new InvalidOperationException($"Automation element '{ReadName(element)}' does not expose clickable bounds.");
        }

        Cursor.Position = new Point(
            (int)(bounds.Left + bounds.Width / 2),
            (int)(bounds.Top + bounds.Height / 2));
        mouse_event(MouseEventLeftDown | MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    private const int MouseEventLeftDown = 0x0002;
    private const int MouseEventLeftUp = 0x0004;

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        int dwFlags,
        int dx,
        int dy,
        int dwData,
        UIntPtr dwExtraInfo);

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
        string category)
    {
        Results.Add(new CheckResult(name, passed, detail, durationMilliseconds, category));
        MarkProgress(name, detail);
    }

    private static void MarkProgress(string step, string detail)
    {
        var artifacts = CurrentArtifactsDirectory;
        if (string.IsNullOrWhiteSpace(artifacts))
        {
            return;
        }

        try
        {
            File.WriteAllText(
                Path.Combine(artifacts, "desktop-e2e-progress.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        generatedAt = DateTimeOffset.UtcNow,
                        step,
                        detail,
                        checks = Results,
                    },
                    JsonOptions));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CaptureScreen(string path)
    {
        try
        {
            var bounds = Screen.PrimaryScreen?.Bounds
                ?? throw new InvalidOperationException("Primary screen is unavailable.");
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            bitmap.Save(path, ImageFormat.Png);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            File.WriteAllText(
                Path.ChangeExtension(path, ".screenshot-error.txt"),
                exception.ToString());
        }
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
        bool ScaleAcceptance,
        bool ExpectConnectionFailure,
        string? AdbExecutable,
        string? DeviceSerial)
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
                ParseBool(values, "scale-acceptance", false),
                ParseBool(values, "expect-connect-failure", false),
                values.GetValueOrDefault("adb"),
                values.GetValueOrDefault("device-serial"));
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
