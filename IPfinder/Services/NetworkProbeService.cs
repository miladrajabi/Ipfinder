using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;

namespace IPfinder.Services;

public sealed class NetworkProbeService
{
    public async Task<ScanResult> ProbeAsync(
        IPAddress ip,
        int port,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var ping = await TryPingAsync(ip, timeoutMs, cancellationToken);
        var tcp = await TryTcpAsync(ip, port, timeoutMs, cancellationToken);
        var latencyMs = ping.RoundTripMs > 0 ? ping.RoundTripMs : tcp.ConnectMs;

        return new ScanResult(ip.ToString(), ping.Succeeded, tcp.Succeeded, latencyMs);
    }

    private static async Task<PingProbeResult> TryPingAsync(
        IPAddress ip,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsAndroid())
        {
            return new PingProbeResult(false, 0);
        }

        try
        {
            using var ping = new Ping();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            var pingTask = ping.SendPingAsync(ip, timeoutMs);
            var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
            var completedTask = await Task.WhenAny(pingTask, timeoutTask);

            if (completedTask != pingTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new PingProbeResult(false, 0);
            }

            var reply = await pingTask;
            return new PingProbeResult(reply.Status == IPStatus.Success, reply.RoundtripTime);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new PingProbeResult(false, 0);
        }
    }

    private static async Task<TcpProbeResult> TryTcpAsync(
        IPAddress ip,
        int port,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();

        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            await client.ConnectAsync(ip, port, timeoutCts.Token);
            stopwatch.Stop();
            return new TcpProbeResult(true, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new TcpProbeResult(false, 0);
        }
        catch (SocketException)
        {
            return new TcpProbeResult(false, 0);
        }
        catch (ObjectDisposedException)
        {
            return new TcpProbeResult(false, 0);
        }
    }

    private readonly record struct PingProbeResult(bool Succeeded, long RoundTripMs);
    private readonly record struct TcpProbeResult(bool Succeeded, long ConnectMs);
}
