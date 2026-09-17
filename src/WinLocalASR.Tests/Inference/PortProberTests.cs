using System.Net;
using System.Net.Sockets;
using WinLocalASR.Core.Inference;
using Xunit;

namespace WinLocalASR.Tests.Inference;

public class PortProberTests
{
    [Fact]
    public void Skips_occupied_ports()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 18100);
        blocker.Start();
        try
        {
            Assert.Equal(18101, PortProber.FindFreePort());
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void Skips_reserved_control_server_port()
    {
        var first = new TcpListener(IPAddress.Loopback, 17844);
        var second = new TcpListener(IPAddress.Loopback, 17845);
        first.Start();
        second.Start();
        try
        {
            // 17844/17845 occupied, 17846 reserved (ControlServer) -> must land on 17847
            Assert.Equal(17847, PortProber.FindFreePort(basePort: 17844, reservedPort: PortProber.ReservedControlServerPort));
        }
        finally
        {
            second.Stop();
            first.Stop();
        }
    }
}
