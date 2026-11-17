using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Length-prefixed CoT TCP listener (same framing as the WinTAK plugin).
/// Unity Play binds this so TAK can send map data with no Python orchestrator.
/// Frame: 4-byte little-endian payload size, then UTF-8 CoT XML.
/// </summary>
public sealed class AGAMOTTO_CotTcpListener
{
    public const int DefaultPort = 65432;
    const int MaxPayloadBytes = 10_000_000;

    readonly int _port;
    readonly Action<string> _onXml;
    TcpListener _listener;
    Thread _thread;
    volatile bool _running;

    public int BoundPort { get; private set; }
    public bool IsRunning => _running;

    public AGAMOTTO_CotTcpListener(int port, Action<string> onXml)
    {
        _port = port > 0 ? port : DefaultPort;
        _onXml = onXml ?? throw new ArgumentNullException(nameof(onXml));
    }

    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _thread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "AGAMOTTO_CotTcpListener"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try
        {
            _listener?.Stop();
        }
        catch
        {
            /* ignore */
        }
        _listener = null;
    }

    void ListenLoop()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            BoundPort = _port;
            Debug.Log($"AGAMOTTO_CotTcpListener: listening on 0.0.0.0:{_port} (WinTAK Send Map Data → this Unity instance).");
        }
        catch (Exception ex)
        {
            _running = false;
            Debug.LogError($"AGAMOTTO_CotTcpListener: could not bind port {_port}: {ex.Message}");
            return;
        }

        while (_running)
        {
            TcpClient client = null;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                if (!_running)
                    break;
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (client == null)
                continue;

            try
            {
                HandleClient(client);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AGAMOTTO_CotTcpListener: client error: {ex.Message}");
            }
            finally
            {
                try { client.Close(); } catch { /* ignore */ }
            }
        }
    }

    void HandleClient(TcpClient client)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        {
            stream.ReadTimeout = 15000;
            while (_running && client.Connected)
            {
                byte[] sizeBuf = new byte[4];
                if (!ReadExact(stream, sizeBuf, 4))
                    break;
                int size = BitConverter.ToInt32(sizeBuf, 0);
                if (size <= 0 || size > MaxPayloadBytes)
                {
                    Debug.LogWarning($"AGAMOTTO_CotTcpListener: invalid frame size {size}");
                    break;
                }

                byte[] payload = new byte[size];
                if (!ReadExact(stream, payload, size))
                    break;

                string xml = Encoding.UTF8.GetString(payload);
                if (string.IsNullOrWhiteSpace(xml))
                    continue;
                _onXml(xml);
            }
        }
    }

    static bool ReadExact(NetworkStream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int n;
            try
            {
                n = stream.Read(buffer, offset, count - offset);
            }
            catch
            {
                return false;
            }
            if (n <= 0)
                return false;
            offset += n;
        }
        return true;
    }
}
