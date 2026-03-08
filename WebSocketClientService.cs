using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace remote_operations;

public class WebSocketClientService(IConfiguration config, ILogger<WebSocketClientService> logger)
    : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var serverUrl = config["WebSocket:ServerUrl"];
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            logger.LogWarning("WebSocket:ServerUrl is not configured — client disabled");
            return;
        }

        var delay = TimeSpan.FromSeconds(5);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndMaintainAsync(serverUrl, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "WebSocket error — reconnecting in {Delay}s", delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
                // Back off up to 60s between retries
                if (delay < TimeSpan.FromSeconds(60))
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }

    private async Task ConnectAndMaintainAsync(string serverUrl, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(serverUrl), ct);

        logger.LogInformation("Connected to {Url}", serverUrl);

        // Send current OS info immediately on connect
        await SendAsync(ws, new
        {
            status      = "online",
            os          = RuntimeInformation.OSDescription,
            platform    = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "linux",
            macAddr     = (
                            from nic in NetworkInterface.GetAllNetworkInterfaces()
                            where nic.OperationalStatus == OperationalStatus.Up
                            select nic.GetPhysicalAddress().ToString()
                          ).FirstOrDefault(),
            ipAddress = (
                            from nic in NetworkInterface.GetAllNetworkInterfaces()
                            where nic.OperationalStatus == OperationalStatus.Up
                            && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            from addr in nic.GetIPProperties().UnicastAddresses
                            where addr.Address.AddressFamily == AddressFamily.InterNetwork
                            select addr.Address.ToString()
                        ).FirstOrDefault()
        }, ct);

        // Run receive loop and heartbeat concurrently; stop both if either exits
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiveTask   = ReceiveLoopAsync(ws, linked.Token);
        var heartbeatTask = HeartbeatLoopAsync(ws, linked.Token);

        await Task.WhenAny(receiveTask, heartbeatTask);
        linked.Cancel();

        // Propagate any real exception (ignore cancellation)
        await Task.WhenAll(
            Unwrap(receiveTask),
            Unwrap(heartbeatTask));
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                logger.LogInformation("Server closed the connection");
                return;
            }
            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            logger.LogDebug("Received: {Message}", message);
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(
            config.GetValue("WebSocket:HeartbeatIntervalSeconds", 30));

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);
            if (ws.State == WebSocketState.Open)
                await SendAsync(ws, new { type = "ping" }, ct);
        }
    }

    private async Task SendAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json  = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        logger.LogDebug("Sent: {Json}", json);
    }

    private static async Task Unwrap(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }
}
