using System.Net;
using System.Net.Sockets;
using NetSplit.Service;

namespace NetSplit.Service.Tests;

public sealed class LoopbackPortManagerTests
{
    [Fact]
    public async Task OccupiedControllerPortIsReassignedWithoutDisturbingOwner()
    {
        using var owner = new TcpListener(IPAddress.Loopback, 0);
        owner.Server.ExclusiveAddressUse = true;
        owner.Start();
        var occupiedPort = ((IPEndPoint)owner.LocalEndpoint).Port;
        var mixedPort = FindAvailableTcpUdpPort();
        var manager = new LoopbackPortManager();

        using var reservation = manager.ReserveAvailablePorts(
            occupiedPort,
            mixedPort);
        var selection = reservation.Ports;

        Assert.NotEqual(occupiedPort, selection.ControllerPort);
        Assert.Equal(mixedPort, selection.MixedPort);
        Assert.InRange(
            selection.ControllerPort,
            LoopbackPortManager.FallbackPortStart,
            LoopbackPortManager.FallbackPortEnd);
        reservation.Release();
        using var exactReservation = manager.ReservePorts(
            selection.ControllerPort,
            selection.MixedPort);

        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(
            IPAddress.Loopback,
            occupiedPort,
            CancellationToken.None).ConfigureAwait(true);
        Assert.True(client.Connected);
    }

    [Fact]
    public void OccupiedMixedPortIsReassigned()
    {
        using var owner = new UdpClient(AddressFamily.InterNetwork);
        owner.Client.ExclusiveAddressUse = true;
        owner.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var occupiedPort = ((IPEndPoint)owner.Client.LocalEndPoint!).Port;
        var controllerPort = FindAvailableTcpPort();
        var manager = new LoopbackPortManager();

        using var reservation = manager.ReserveAvailablePorts(
            controllerPort,
            occupiedPort);
        var selection = reservation.Ports;

        Assert.Equal(controllerPort, selection.ControllerPort);
        Assert.NotEqual(occupiedPort, selection.MixedPort);
        Assert.InRange(
            selection.MixedPort,
            LoopbackPortManager.FallbackPortStart,
            LoopbackPortManager.FallbackPortEnd);
        Assert.Equal(
            occupiedPort,
            ((IPEndPoint)owner.Client.LocalEndPoint!).Port);
    }

    [Fact]
    public void UdpOnlyControllerPortUseDoesNotForceReassignment()
    {
        using var owner = new UdpClient(AddressFamily.InterNetwork);
        owner.Client.ExclusiveAddressUse = true;
        owner.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var controllerPort = ((IPEndPoint)owner.Client.LocalEndPoint!).Port;
        var mixedPort = FindAvailableTcpUdpPort();
        var manager = new LoopbackPortManager();

        using var reservation = manager.ReserveAvailablePorts(
            controllerPort,
            mixedPort);

        Assert.Equal(controllerPort, reservation.Ports.ControllerPort);
        Assert.Equal(mixedPort, reservation.Ports.MixedPort);
    }

    [Fact]
    public void StartTimeCheckRejectsNewPortCollision()
    {
        using var owner = new TcpListener(IPAddress.Loopback, 0);
        owner.Server.ExclusiveAddressUse = true;
        owner.Start();
        var occupiedPort = ((IPEndPoint)owner.LocalEndpoint).Port;
        var manager = new LoopbackPortManager();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            manager.ReservePorts(occupiedPort, FindAvailableTcpUdpPort()));

        Assert.Contains("控制端口", exception.Message, StringComparison.Ordinal);
    }

    private static int FindAvailableTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static int FindAvailableTcpUdpPort()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.ExclusiveAddressUse = true;
                udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException)
            {
            }
            finally
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException("Could not find an available TCP/UDP port.");
    }
}
