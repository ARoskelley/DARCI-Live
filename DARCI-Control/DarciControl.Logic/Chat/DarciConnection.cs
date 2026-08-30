#nullable enable

using Microsoft.AspNetCore.SignalR.Client;

namespace DarciControl.Logic.Chat;

/// <summary>A reply pushed by the core.</summary>
/// <param name="UserId">Who it is addressed to.</param>
/// <param name="Content">The reply text.</param>
/// <param name="CreatedAt">When the core produced it.</param>
/// <param name="InResponseToMessageId">
/// Which message it answers, or null for an unprompted notification. This is why D5 existed: the hub
/// broadcasts to every connected client, so without it this app cannot tell its own answer from one
/// meant for the phone.
/// </param>
public sealed record DarciReply(string? UserId, string? Content, string? CreatedAt, int? InResponseToMessageId);

/// <summary>
/// The app's link to a running core, over the SignalR hub.
///
/// <para>The hub rather than <c>POST /message</c> + polling: it PUSHES, it has always guarded empty input,
/// and it is the transport both existing clients already use, so a bug found here benefits all three.</para>
///
/// <para>Reconnection is the normal case, not an error case. The core is a process the user starts and
/// stops from this very app, so "not running" is an expected state to sit in calmly rather than something
/// to spam errors about.</para>
/// </summary>
public sealed class DarciConnection : IAsyncDisposable
{
    private readonly string _hubUrl;
    private HubConnection? _connection;

    public DarciConnection(string baseUrl = "http://localhost:5081")
        => _hubUrl = $"{baseUrl.TrimEnd('/')}/hub";

    public event Action<DarciReply>? ReplyReceived;
    public event Action<string>? Notification;
    public event Action<string, bool>? StateChanged;   // (description, isConnected)

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connection is not null) return;

        _connection = new HubConnectionBuilder()
            .WithUrl(_hubUrl)
            .WithAutomaticReconnect(new[]
            {
                // Retry promptly at first — the common case is the user just pressed "Start core" and the
                // core is seconds from being ready — then back off so an app left open overnight against
                // a stopped core is not hammering a dead port.
                TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
            })
            .Build();

        _connection.On<DarciReply>("ReceiveResponse", reply => ReplyReceived?.Invoke(reply));
        _connection.On<string>("Notification", text => Notification?.Invoke(text));

        _connection.Reconnecting += _ => { StateChanged?.Invoke("Reconnecting...", false); return Task.CompletedTask; };
        _connection.Reconnected += _ => { StateChanged?.Invoke("Connected", true); return Task.CompletedTask; };
        _connection.Closed += _ => { StateChanged?.Invoke("Disconnected", false); return Task.CompletedTask; };

        await StartAsync(ct);
    }

    /// <summary>
    /// Start, tolerating a core that is not up yet. Returns whether it connected — a failure here is
    /// ordinary and the caller shows it as a state, not an error.
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (_connection is null) return false;
        if (_connection.State == HubConnectionState.Connected) return true;

        try
        {
            StateChanged?.Invoke("Connecting...", false);
            await _connection.StartAsync(ct);
            StateChanged?.Invoke("Connected", true);
            return true;
        }
        catch (Exception)
        {
            StateChanged?.Invoke("Core not running", false);
            return false;
        }
    }

    /// <summary>
    /// Send a message. Returns false when there is no connection, so the caller can queue rather than
    /// silently dropping what the user typed.
    /// </summary>
    public async Task<bool> SendAsync(string message, string userId = "Tinman", bool urgent = false)
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected) return false;

        try
        {
            await _connection.InvokeAsync("SendMessage", message, userId, urgent);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
