using System.Net;
using System.Net.Sockets;

namespace WinLocalASR.Core.Inference;

/// <summary>
/// Free-port probe for the resident llama-server (docs/rfc.md anchor decision): bind-test ports
/// from <see cref="DefaultBasePort"/> upward, skipping the ControlServer port (Task 12).
/// </summary>
public static class PortProber
{
    public const int DefaultBasePort = 18100;

    /// <summary>Reserved for the opt-in ControlServer (localhost, Task 12). Never chosen here.</summary>
    public const int ReservedControlServerPort = 17846;

    public static int FindFreePort(int basePort = DefaultBasePort, int reservedPort = ReservedControlServerPort, int maxAttempts = 1000)
    {
        for (int port = basePort; port < basePort + maxAttempts; port++)
        {
            if (port == reservedPort)
            {
                continue;
            }

            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                return port;
            }
            catch (SocketException)
            {
                // port in use — keep probing
            }
            finally
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException($"No free port found in [{basePort}, {basePort + maxAttempts}).");
    }
}
