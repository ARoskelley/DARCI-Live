using Darci.Core;
using Darci.Research;
using Darci.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Darci.Api;

/// <summary>
/// SignalR hub — real-time bidirectional channel between DARCI and connected clients.
///
/// Connection URL:  ws(s)://{host}/hub
///
/// Client → Server methods (clients call these):
///   SendMessage(message, userId?, urgent?)  — queue a message for DARCI
///   RequestStatus()                         — ask for immediate status push
///   ListFiles(sessionId?)                   — request file list push
///
/// Server → Client methods (DARCI pushes these):
///   ReceiveResponse(OutgoingMessage)        — DARCI's reply to a user message
///   StatusUpdate(DarciStatusDto)            — periodic / on-demand status broadcast
///   FileReady(ResearchFileDto)              — a new file is available for download
///   Notification(string text)              — freeform push notification
/// </summary>
public sealed class DarciHub : Hub
{
    private readonly Awareness _awareness;
    private readonly Darci.Core.Darci _darci;
    private readonly IResearchStore _research;
    private readonly ILogger<DarciHub> _logger;

    public DarciHub(
        Awareness awareness,
        Darci.Core.Darci darci,
        IResearchStore research,
        ILogger<DarciHub> logger)
    {
        _awareness = awareness;
        _darci     = darci;
        _research  = research;
        _logger    = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Hub client connected: {ConnectionId}", Context.ConnectionId);

        // Push current status immediately on connect
        var status = _darci.GetStatus();
        await Clients.Caller.SendAsync("StatusUpdate", new
        {
            status.CurrentMood,
            status.Energy,
            status.IsAlive,
            status.Uptime,
            status.CycleCount,
            status.CurrentActivity,
            ConnectedAt = DateTime.UtcNow
        });

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Hub client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Queues a message for DARCI to respond to.
    /// Mirrors the POST /message REST endpoint.
    /// </summary>
    public async Task SendMessage(string message, string? userId = null, bool urgent = false)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var incoming = new IncomingMessage
        {
            Content     = message,
            UserId      = userId ?? "Tinman",
            Source      = "hub",
            Urgency     = urgent ? Urgency.Now : Urgency.Soon,
            ReceivedAt  = DateTime.UtcNow
        };

        await _awareness.NotifyMessage(incoming);
        _logger.LogInformation("Hub message queued from {UserId}: {Preview}",
            incoming.UserId, message.Length > 60 ? message[..60] + "…" : message);
    }

    /// <summary>
    /// Push DARCI's current status to the calling client on demand.
    /// </summary>
    public async Task RequestStatus()
    {
        var status = _darci.GetStatus();
        await Clients.Caller.SendAsync("StatusUpdate", new
        {
            status.CurrentMood,
            status.Energy,
            status.IsAlive,
            status.Uptime,
            status.CycleCount,
            status.CurrentActivity
        });
    }

    /// <summary>
    /// Push the file list to the calling client.
    /// Optionally scoped to a specific research session.
    /// </summary>
    public async Task ListFiles(string? sessionId = null)
    {
        var files = await _research.GetFilesAsync(sessionId);
        var dtos  = files.Select(f => new
        {
            f.Id,
            f.SessionId,
            f.Filename,
            f.ContentType,
            f.SizeBytes,
            CreatedAt     = f.CreatedAt.ToString("O"),
            DownloadUrl   = $"/research/files/{f.Id}/download"
        });
        await Clients.Caller.SendAsync("FileList", dtos);
    }
}

/// <summary>
/// Helper service that other parts of DARCI can inject to push messages
/// to all connected hub clients — e.g. when a response is ready or a
/// file is written.
/// </summary>
public sealed class DarciHubNotifier
{
    private readonly IHubContext<DarciHub> _hub;

    public DarciHubNotifier(IHubContext<DarciHub> hub) => _hub = hub;

    /// <summary>Broadcast a DARCI response to all connected clients.</summary>
    public Task BroadcastResponse(OutgoingMessage message) =>
        _hub.Clients.All.SendAsync("ReceiveResponse", new
        {
            message.UserId,
            message.Content,
            CreatedAt     = message.CreatedAt.ToString("O"),
            message.ExternalNotify
        });

    /// <summary>Notify clients that a new research file is ready to download.</summary>
    public Task NotifyFileReady(string fileId, string filename, string sessionId, string downloadUrl) =>
        _hub.Clients.All.SendAsync("FileReady", new
        {
            FileId      = fileId,
            Filename    = filename,
            SessionId   = sessionId,
            DownloadUrl = downloadUrl,
            ReadyAt     = DateTime.UtcNow.ToString("O")
        });

    /// <summary>Push a freeform text notification to all clients.</summary>
    public Task Notify(string text) =>
        _hub.Clients.All.SendAsync("Notification", text);
}
