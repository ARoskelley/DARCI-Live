#nullable enable

using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using DarciControl.Logic.Chat;
using DarciControl.Logic.Nodes;
using DarciControl.Logic.Packaging;
using DarciControl.Logic.Prerequisites;
using DarciControl.Logic.Runtime;

namespace DarciControl.App;

/// <summary>One line in the chat transcript.</summary>
public sealed class ChatEntry
{
    public required string Who { get; init; }
    public required string Text { get; init; }
    public string Note { get; set; } = "";
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public IBrush Background { get; init; } = new SolidColorBrush(Color.Parse("#2A2A2A"));
    public Avalonia.Layout.HorizontalAlignment Alignment { get; init; } = Avalonia.Layout.HorizontalAlignment.Left;

    /// <summary>Set for messages WE sent, so the matching reply can clear the pending note (D5).</summary>
    public int? SentMessageId { get; set; }
}

/// <summary>One prerequisite row.</summary>
public sealed class PrereqRow
{
    public required string Badge { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public string Remedy { get; init; } = "";
    public bool HasRemedy => !string.IsNullOrEmpty(Remedy);
    public required IBrush Colour { get; init; }
}

/// <summary>One selectable node in the builder.</summary>
public sealed class NodeRow
{
    public required string NodeId { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public required string Capabilities { get; init; }
    public required string Transport { get; init; }
    public string Problem { get; init; } = "";
    public bool HasProblem => !string.IsNullOrEmpty(Problem);
    public bool IsSelectable => !HasProblem;

    /// <summary>Bound two-way to the checkbox. Plain mutable state — the build reads it once, on click.</summary>
    public bool IsSelected { get; set; }
}

public partial class MainWindow : Window
{
    private readonly DarciConnection _connection = new();
    private readonly ObservableCollection<ChatEntry> _messages = new();
    private readonly ObservableCollection<PrereqRow> _prereqs = new();
    private readonly PrerequisiteChecker _checker;
    private readonly CoreLauncher _launcher;
    private readonly ObservableCollection<NodeRow> _nodes = new();
    private readonly DarciPaths _paths;

    public MainWindow()
    {
        // InitializeComponent, NOT AvaloniaXamlLoader.Load: the generated method both loads the XAML and
        // assigns the x:Name fields. Calling the loader directly builds and then dies on the first named
        // control, because every one of them is still null.
        InitializeComponent();

        Messages.ItemsSource = _messages;
        PrereqList.ItemsSource = _prereqs;

        _paths = new DarciPaths { Root = FindRepoRoot() };
        _checker = new PrerequisiteChecker(_paths);
        _launcher = new CoreLauncher(_paths, _checker);

        NodeList.ItemsSource = _nodes;
        LoadNodes();

        // A core this app started can outlive an app crash. Find it rather than leaving it invisible.
        if (_launcher.FindAdoptable() is { } orphan)
        {
            Log($"Adopted a core this app started earlier (pid {orphan.Pid}). Stop is available.");
            StopCoreButton.IsEnabled = true;
        }

        StartNeo4jButton.IsEnabled = Neo4jController.FindLauncher() is not null;

        _connection.ReplyReceived += OnReply;
        _connection.Notification += text => Post(() => AddSystem(text));
        _connection.StateChanged += (description, connected) => Post(() => SetStatus(description, connected));

        // Try immediately, but treat failure as an ordinary state: the core is a process this very app
        // starts, so "not running" is where a first launch legitimately begins.
        _ = _connection.ConnectAsync();
    }

    // ── chat ──

    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _ = SendAsync(); e.Handled = true; }
    }

    private void OnSendClick(object? sender, RoutedEventArgs e) => _ = SendAsync();

    private async Task SendAsync()
    {
        var text = MessageBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        MessageBox.Text = "";

        var entry = new ChatEntry
        {
            Who = "You",
            Text = text,
            Background = new SolidColorBrush(Color.Parse("#2D4A63")),
            Alignment = Avalonia.Layout.HorizontalAlignment.Right,
            Note = "sending...",
        };
        _messages.Add(entry);
        ScrollToEnd();

        if (await _connection.SendAsync(text))
        {
            // The hub's SendMessage does not hand back the id, so we cannot pin this to a specific reply
            // yet; the id arrives on the reply itself and is used to tell OUR answers from the phone's.
            entry.Note = "sent";
        }
        else
        {
            entry.Note = "not sent - core is not running. Start it from the Start DARCI tab.";
            ShowHint("DARCI is not running. Open the Start DARCI tab to bring it up, then send again.");
        }

        Refresh(entry);
    }

    private void OnReply(DarciReply reply)
    {
        Post(() =>
        {
            if (string.IsNullOrWhiteSpace(reply.Content)) return;

            // The hub broadcasts to every connected client, so this may be an answer to the phone. Say so
            // rather than pretending it was ours.
            var note = reply.InResponseToMessageId is { } id ? $"in reply to message {id}" : "unprompted";

            _messages.Add(new ChatEntry
            {
                Who = "DARCI",
                Text = reply.Content!,
                Note = note,
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
            });
            ScrollToEnd();
        });
    }

    private void AddSystem(string text) =>
        _messages.Add(new ChatEntry
        {
            Who = "System",
            Text = text,
            Background = new SolidColorBrush(Color.Parse("#3A3320")),
        });

    private void ShowHint(string text)
    {
        ChatHint.Text = text;
        ChatHintPanel.IsVisible = true;
    }

    private void Refresh(ChatEntry entry)
    {
        // ChatEntry is a plain object, so nudge the list to re-render the changed row.
        var index = _messages.IndexOf(entry);
        if (index >= 0) { _messages.RemoveAt(index); _messages.Insert(index, entry); }
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);

    // ── prerequisites ──

    private async void OnCheckClick(object? sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        _prereqs.Clear();

        try
        {
            var report = await _checker.CheckAllAsync();
            foreach (var result in report.Results) _prereqs.Add(ToRow(result));

            StartCoreButton.IsEnabled = report.CanStart && !_connection.IsConnected;
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private static PrereqRow ToRow(PrereqResult r) => new()
    {
        Badge = r.State switch
        {
            PrereqState.Ok => "OK",
            PrereqState.Warning => "WARN",
            PrereqState.Failed => "FAIL",
            _ => "?",
        },
        Colour = new SolidColorBrush(r.State switch
        {
            PrereqState.Ok => Color.Parse("#5CB85C"),
            PrereqState.Warning => Color.Parse("#D9A441"),
            PrereqState.Failed => Color.Parse("#D9534F"),
            _ => Colors.Gray,
        }),
        Name = r.Name,
        Detail = r.Detail,
        Remedy = r.Remedy ?? "",
    };

    // ── core lifecycle ──

    private async void OnStartCoreClick(object? sender, RoutedEventArgs e)
    {
        StartCoreButton.IsEnabled = false;
        Log("Starting DARCI...");

        try
        {
            // Neo4j is checked but never required: the core falls back to SQLite on its own, so a missing
            // graph database must not stop a start.
            if (await Neo4jController.IsListeningAsync())
                Log("  Neo4j is listening - the core will use it.");
            else
                Log("  Neo4j is not running - the core will use SQLite (a valid setup).");

            var result = await _launcher.StartAsync(onOutput: line =>
            {
                // Surface only what an operator would act on; the full log is the core's own.
                if (line.Contains("warn:", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Registered node", StringComparison.Ordinal) ||
                    line.Contains("knowledge graph", StringComparison.OrdinalIgnoreCase))
                    Post(() => Log("  " + line.Trim()));
            });

            Log(result.Ready ? "  " + result.Detail : "  FAILED: " + result.Detail);

            if (result.Ready)
            {
                StopCoreButton.IsEnabled = true;
                await _connection.StartAsync();
            }
        }
        catch (Exception ex)
        {
            Log("  FAILED: " + ex.GetBaseException().Message);
        }
        finally
        {
            StartCoreButton.IsEnabled = true;
        }
    }

    private async void OnStopCoreClick(object? sender, RoutedEventArgs e)
    {
        StopCoreButton.IsEnabled = false;
        Log(await _launcher.StopAsync() ? "Stopped the core." : "Nothing to stop.");
    }

    private async void OnStartNeo4jClick(object? sender, RoutedEventArgs e)
    {
        StartNeo4jButton.IsEnabled = false;
        Log("Starting Neo4j...");

        try
        {
            Log(await Neo4jController.StartAsync()
                ? "  Neo4j is listening on bolt://localhost:7687."
                : "  Could not start Neo4j. DARCI will run on SQLite, which is fine.");
        }
        finally
        {
            StartNeo4jButton.IsEnabled = true;
        }
    }

    private void Log(string line)
    {
        StartLog.Text = string.IsNullOrEmpty(StartLog.Text) ? line : StartLog.Text + "\n" + line;
    }

    // ── build a distributable ──

    private void LoadNodes()
    {
        _nodes.Clear();

        foreach (var entry in NodeCatalog.Scan(_paths.NodesPath))
        {
            _nodes.Add(new NodeRow
            {
                NodeId = entry.NodeId,
                DisplayName = entry.DisplayName,
                Version = "v" + entry.Version,
                Capabilities = entry.Capabilities.Count > 0
                    ? string.Join(", ", entry.Capabilities)
                    : "(no capabilities declared)",
                Transport = entry.IsOutOfProcess ? "[http]" : "[in-process]",
                Problem = entry.Problem ?? "",
                // Everything that CAN ship is ticked by default: the common case is "package what I have",
                // and a bare core is then one deliberate action away rather than the accidental outcome of
                // not noticing a list.
                IsSelected = entry.IsSelectable,
            });
        }
    }

    private void OnRefreshNodesClick(object? sender, RoutedEventArgs e)
    {
        LoadNodes();
        BuildLog.Text = $"Found {_nodes.Count} node(s) in {_paths.NodesPath}.";
    }

    private async void OnBuildZipClick(object? sender, RoutedEventArgs e)
    {
        var platform = TargetCombo.SelectedIndex == 1 ? TargetPlatform.Linux : TargetPlatform.Windows;
        var selected = _nodes.Where(n => n.IsSelected && n.IsSelectable).Select(n => n.NodeId).ToList();

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save distributable",
            SuggestedFileName = $"darci-{platform.Os.ToString().ToLowerInvariant()}.zip",
            DefaultExtension = "zip",
        });

        if (file is null) return;   // cancelled

        BuildZipButton.IsEnabled = false;
        BuildLog.Text = "";

        try
        {
            var request = new ZipBuildRequest
            {
                RepoRoot = _paths.Root,
                OutputPath = file.Path.LocalPath,
                SelectedNodeIds = selected,
                IncludeOnnxModels = OnnxCheck.IsChecked == true,
                Platform = platform,
            };

            BuildLine(selected.Count == 0
                ? $"Building a BARE CORE for {platform.Os} - valid, and it will say so honestly."
                : $"Building for {platform.Os} with: {string.Join(", ", selected)}");
            BuildLine($"Publishing self-contained ({platform.Rid}) - this takes a few minutes...");

            var publishDir = Path.Combine(Path.GetTempPath(), $"darci-publish-{platform.Rid}");
            var publish = await new CorePublisher().PublishAsync(request.RepoRoot, publishDir, request.Runtime);
            if (!publish.Success)
            {
                BuildLine("FAILED to publish: " + publish.Error);
                return;
            }

            BuildLine("Publish OK. Assembling the zip...");

            var catalog = NodeCatalog.Scan(_paths.NodesPath);
            var plan = ZipPlan.Create(request, catalog, publishDir);
            var readme = ZipAssembler.BuildReadme(request, plan, _paths.HostProfilePath);
            var result = ZipAssembler.Write(plan, request.OutputPath, readme, platform);

            foreach (var warning in result.Warnings) BuildLine("  note: " + warning);

            BuildLine(result.Success
                ? $"DONE: {result.ZipPath} ({result.Bytes / 1024 / 1024} MB), nodes: "
                  + (result.IncludedNodeIds.Count == 0 ? "(bare core)" : string.Join(", ", result.IncludedNodeIds))
                : "FAILED: " + result.Error);
        }
        catch (Exception ex)
        {
            BuildLine("FAILED: " + ex.GetBaseException().Message);
        }
        finally
        {
            BuildZipButton.IsEnabled = true;
        }
    }

    private void BuildLine(string line) =>
        BuildLog.Text = string.IsNullOrEmpty(BuildLog.Text) ? line : BuildLog.Text + "\n" + line;

    /// <summary>
    /// Stop a core this app started, so closing the window does not leave one holding port 5081 and
    /// writing to the database with nobody owning it.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _launcher.StopOnExit();
        base.OnClosed(e);
    }

    // ── plumbing ──

    private void SetStatus(string description, bool connected)
    {
        StatusText.Text = description;
        StatusDot.Fill = new SolidColorBrush(connected ? Color.Parse("#5CB85C") : Color.Parse("#888888"));
        StatusDetail.Text = connected ? "http://localhost:5081" : "";
        if (connected) ChatHintPanel.IsVisible = false;
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>Walk up to the repo root so the checker finds host-profile.json and nodes/.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "DARCI-v4")))
            dir = dir.Parent;

        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
