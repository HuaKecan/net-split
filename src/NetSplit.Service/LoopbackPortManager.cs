using System.Net;
using System.Net.Sockets;

namespace NetSplit.Service;

public interface ILoopbackPortManager
{
    ILoopbackPortReservation ReserveAvailablePorts(int controllerPort, int mixedPort);
    ILoopbackPortReservation ReservePorts(int controllerPort, int mixedPort);
}

public interface ILoopbackPortReservation : IDisposable
{
    LoopbackPortSelection Ports { get; }
    void Release();
}

public sealed record LoopbackPortSelection(int ControllerPort, int MixedPort)
{
    public bool ControllerPortChanged(int previousPort)
    {
        return ControllerPort != previousPort;
    }

    public bool MixedPortChanged(int previousPort)
    {
        return MixedPort != previousPort;
    }
}

public sealed class LoopbackPortManager : ILoopbackPortManager
{
    internal const int FallbackPortStart = 20000;
    internal const int FallbackPortEnd = 39999;

    public ILoopbackPortReservation ReserveAvailablePorts(
        int controllerPort,
        int mixedPort)
    {
        ValidatePorts(controllerPort, mixedPort);

        PortReservation? controllerReservation = null;
        PortReservation? mixedReservation = null;
        try
        {
            controllerReservation = PortReservation.TryCreate(
                controllerPort,
                PortProtocols.Tcp);
            mixedReservation = PortReservation.TryCreate(
                mixedPort,
                PortProtocols.Tcp | PortProtocols.Udp);

            controllerReservation ??= ReserveAvailablePort(PortProtocols.Tcp);
            mixedReservation ??= ReserveAvailablePort(
                PortProtocols.Tcp | PortProtocols.Udp);

            var reservation = new LoopbackPortReservation(
                controllerReservation,
                mixedReservation);
            controllerReservation = null;
            mixedReservation = null;
            return reservation;
        }
        finally
        {
            mixedReservation?.Dispose();
            controllerReservation?.Dispose();
        }
    }

    public ILoopbackPortReservation ReservePorts(
        int controllerPort,
        int mixedPort)
    {
        ValidatePorts(controllerPort, mixedPort);

        var controllerReservation = PortReservation.TryCreate(
            controllerPort,
            PortProtocols.Tcp)
            ?? throw new InvalidOperationException(
                "Mihomo 控制端口在启动前被其他程序占用；透明分流保持关闭。");
        try
        {
            var mixedReservation = PortReservation.TryCreate(
                mixedPort,
                PortProtocols.Tcp | PortProtocols.Udp)
                ?? throw new InvalidOperationException(
                    "Mihomo 混合端口在启动前被其他程序占用；透明分流保持关闭。");
            return new LoopbackPortReservation(
                controllerReservation,
                mixedReservation);
        }
        catch
        {
            controllerReservation.Dispose();
            throw;
        }
    }

    private static PortReservation ReserveAvailablePort(PortProtocols protocols)
    {
        var portCount = FallbackPortEnd - FallbackPortStart + 1;
        var firstPort = Random.Shared.Next(
            FallbackPortStart,
            FallbackPortEnd + 1);
        for (var offset = 0; offset < portCount; offset++)
        {
            var port = FallbackPortStart
                + (firstPort - FallbackPortStart + offset) % portCount;
            var reservation = PortReservation.TryCreate(port, protocols);
            if (reservation is not null)
            {
                return reservation;
            }
        }

        throw new InvalidOperationException(
            "无法分配 Mihomo 所需的本机端口；透明分流保持关闭。");
    }

    private static void ValidatePorts(int controllerPort, int mixedPort)
    {
        if (controllerPort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(controllerPort));
        }

        if (mixedPort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(mixedPort));
        }

        if (controllerPort == mixedPort)
        {
            throw new ArgumentException("Mihomo 控制端口和混合端口必须不同。");
        }
    }

    [Flags]
    private enum PortProtocols
    {
        Tcp = 1,
        Udp = 2
    }

    private sealed class LoopbackPortReservation : ILoopbackPortReservation
    {
        private PortReservation? _controllerReservation;
        private PortReservation? _mixedReservation;

        public LoopbackPortReservation(
            PortReservation controllerReservation,
            PortReservation mixedReservation)
        {
            _controllerReservation = controllerReservation;
            _mixedReservation = mixedReservation;
            Ports = new LoopbackPortSelection(
                controllerReservation.Port,
                mixedReservation.Port);
        }

        public LoopbackPortSelection Ports { get; }

        public void Release()
        {
            Interlocked.Exchange(ref _mixedReservation, null)?.Dispose();
            Interlocked.Exchange(ref _controllerReservation, null)?.Dispose();
        }

        public void Dispose()
        {
            Release();
        }
    }

    private sealed class PortReservation : IDisposable
    {
        private readonly TcpListener? _tcpListener;
        private readonly UdpClient? _udpClient;

        private PortReservation(
            int port,
            TcpListener? tcpListener,
            UdpClient? udpClient)
        {
            Port = port;
            _tcpListener = tcpListener;
            _udpClient = udpClient;
        }

        public int Port { get; }

        public static PortReservation? TryCreate(
            int requestedPort,
            PortProtocols protocols)
        {
            TcpListener? tcpListener = null;
            UdpClient? udpClient = null;
            try
            {
                if (protocols.HasFlag(PortProtocols.Tcp))
                {
                    tcpListener = new TcpListener(
                        IPAddress.Loopback,
                        requestedPort);
                    tcpListener.Server.ExclusiveAddressUse = true;
                    tcpListener.Start();
                }

                var port = tcpListener is null
                    ? requestedPort
                    : ((IPEndPoint)tcpListener.LocalEndpoint).Port;
                if (protocols.HasFlag(PortProtocols.Udp))
                {
                    udpClient = new UdpClient(AddressFamily.InterNetwork);
                    udpClient.Client.ExclusiveAddressUse = true;
                    udpClient.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
                }

                return new PortReservation(port, tcpListener, udpClient);
            }
            catch (SocketException)
            {
                udpClient?.Dispose();
                tcpListener?.Stop();
                return null;
            }
        }

        public void Dispose()
        {
            _udpClient?.Dispose();
            _tcpListener?.Stop();
        }
    }
}
