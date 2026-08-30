#nullable enable

using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DarciControl.Logic.Chat;
using DarciControl.Logic.Prerequisites;

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

    public MainWindow()
    {
        // InitializeComponent, NOT AvaloniaXamlLoader.Load: the generated method both loads the XAML and
        // assigns the x:Name fields. Calling the loader directly builds and then dies on the first named
        // control, because every one of them is still null.
        InitializeComponent();

        Messages.ItemsSource = _messages;
        PrereqList.ItemsSource = _prereqs;

        _checker = new PrerequisiteChecker(new DarciPaths { Root = FindRepoRoot() });

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

    private void OnStartCoreClick(object? sender, RoutedEventArgs e) =>
        ShowHint("Starting the core from here lands in D3. For now, run Start-DARCI.ps1.");

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
