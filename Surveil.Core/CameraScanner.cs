using System.Net;
using System.Net.Sockets;

namespace Surveil.Core;

public readonly record struct ScanProgress(int Scanned, int Total, int Found);

public interface ICameraScanner
{
    Task<IReadOnlyList<IPAddress>> ScanAsync(IReadOnlyCollection<IPAddress> addresses, int port,
        int concurrency = 256, TimeSpan? timeout = null, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class CameraScanner : ICameraScanner
{
    public async Task<IReadOnlyList<IPAddress>> ScanAsync(
        IReadOnlyCollection<IPAddress> addresses,
        int port,
        int concurrency = 256,
        TimeSpan? timeout = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65_535) throw new ArgumentOutOfRangeException(nameof(port));
        var probeTimeout = timeout ?? TimeSpan.FromMilliseconds(400);
        var found = new List<IPAddress>();
        var sync = new object();
        var scannedCount = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));

        var probes = addresses.Select(async address =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var connected = await ProbeAsync(address, port, probeTimeout, cancellationToken);
                int scanned;
                int foundCount;
                lock (sync)
                {
                    scanned = ++scannedCount;
                    if (connected) found.Add(address);
                    foundCount = found.Count;
                }
                // Report often enough that the progress bar moves smoothly, plus on every hit and at the end.
                if (connected || scanned % 32 == 0 || scanned == addresses.Count)
                    progress?.Report(new ScanProgress(scanned, addresses.Count, foundCount));
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(probes);
        return found.OrderBy(IpAddress.ToUInt32).ToArray();
    }

    internal static async Task<bool> ProbeAsync(
        IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(address, port, deadline.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
