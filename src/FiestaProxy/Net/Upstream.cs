using System.Net.Sockets;

namespace FiestaProxy.Net;

/// <summary>
/// Upstream-dial helpers.
///
/// The proxy must stay "invisible" until the real upstream peer is reachable.
/// A co-located Fiesta exe treats its first s2s connect as a readiness probe
/// ("friendly check"): if it succeeds, the exe immediately spins up its full
/// parallel connection pool. If the proxy's listen port is open before the
/// real peer is up, that friendly check succeeds falsely, the exe dumps its
/// whole pool against a not-ready peer, and it wedges in a boot loop.
///
/// So a listener calls <see cref="WaitUntilReachableAsync"/> BEFORE binding:
/// the port stays closed (connections refused — a retryable state the exe
/// handles gracefully) until the upstream answers once. Per-connection dials
/// then use <see cref="DialAsync"/>, a single attempt with no holding/retry —
/// holding accepted connections while retrying pile them up and burst them
/// onto the peer when it recovers.
/// </summary>
internal static class Upstream
{
    /// <summary>
    /// Block until <paramref name="host"/>:<paramref name="port"/> accepts a
    /// TCP connection once. Retries every second; only returns early if the
    /// token cancels. Used to gate a listener's <c>Start()</c>.
    /// </summary>
    public static async Task WaitUntilReachableAsync(
        string host, int port, TimeSpan attemptTimeout, string logTag, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(attemptTimeout);
                await client.ConnectAsync(host, port, cts.Token);
                Log.Info($"{logTag} upstream {host}:{port} reachable after {attempt} probe(s) — opening listener");
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                if (attempt == 1)
                    Log.Info($"{logTag} upstream {host}:{port} not up yet — holding listener closed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Single upstream dial for one accepted connection. Returns a connected
    /// client, or null on failure — the caller then closes the accepted side,
    /// so the exe sees a normal disconnect and retries at its own pace.
    /// </summary>
    public static async Task<TcpClient?> DialAsync(
        string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(host, port, cts.Token);
            return client;
        }
        catch (Exception)
        {
            client.Dispose();
            return null;
        }
    }
}
