#nullable enable

using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DarciControl.Logic.Chat;
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

public partial class MainWindow : Window
{
    private readonly DarciConnection _connection = new();
    private readonly ObservableCollection<ChatEntry> _messages = new();
    private readonly ObservableCollection<PrereqRow> _prereqs = new();
    private readonly PrerequisiteChecker _checker;
    private readonly CoreLauncher _launcher;

    public MainWindow()
    {
        // InitializeComponent, NOT AvaloniaXamlLoader.Load: the generated method both loads the XAML and
        // assigns the x:Name fields. Calling the loader directly builds and then dies on the first named
        // control, because every one of them is still null.
        InitializeComponent();

        Messages.ItemsSource = _messages;
        PrereqList.ItemsSource = _prereqs;

        var paths = new DarciPaths { Root = FindRepoRoot() };
        _checker = new PrerequisiteChecker(paths);
        _launcher = new CoreLauncher(paths, _checker);

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
