using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AGAMOTTO.WinTAKMIL
{
    internal class CoTHelper
    {
        internal string _takIP;
        internal int _takPort;

        /// <summary>Guards against absurd frames (matches listener default cap order-of-magnitude).</summary>
        internal const int MaxCotPayloadBytes = 10_000_000;

        public CoTHelper(string takIp, int takPort)
        {
            SetEndpoint(takIp, takPort);
        }

        public void SetEndpoint(string takIp, int takPort)
        {
            _takIP = string.IsNullOrWhiteSpace(takIp) ? "127.0.0.1" : takIp.Trim();
            _takPort = takPort;
        }

        /// <summary>
        /// Sends length-prefixed UTF-8 frames: 4-byte little-endian <b>byte</b> count (not character count), then payload.
        /// </summary>
        public void SendCotMessageList(List<XDocument> cotXmlList)
        {
            var (_, err) = SendCotMessageListCoreAsync(cotXmlList, CancellationToken.None, syncConnect: true).GetAwaiter().GetResult();
            if (err != null)
                System.Diagnostics.Trace.WriteLine("[AGAMOTTO CoTHelper] " + err);
        }

        public async Task<(bool ok, string error)> SendCotMessageListAsync(List<XDocument> cotXmlList, CancellationToken cancellationToken = default)
        {
            return await SendCotMessageListCoreAsync(cotXmlList, cancellationToken, syncConnect: false).ConfigureAwait(false);
        }

        async Task<(bool ok, string error)> SendCotMessageListCoreAsync(List<XDocument> cotXmlList, CancellationToken cancellationToken, bool syncConnect)
        {
            if (cotXmlList == null || cotXmlList.Count == 0)
                return (false, "No CoT messages to send.");

            try
            {
                using (var client = new TcpClient())
                {
                    if (syncConnect)
                        client.Connect(_takIP, _takPort);
                    else
                        await client.ConnectAsync(_takIP, _takPort).ConfigureAwait(false);

                    using (NetworkStream stream = client.GetStream())
                    {
                        foreach (var message in cotXmlList)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string cotMessage = message.Root.ToString();
                            byte[] bytes = Encoding.UTF8.GetBytes(cotMessage);
                            if (bytes.Length > MaxCotPayloadBytes)
                                return (false, $"CoT payload exceeds max size ({bytes.Length} bytes).");

                            byte[] sizeBuffer = BitConverter.GetBytes(bytes.Length);
                            await stream.WriteAsync(sizeBuffer, 0, 4, cancellationToken).ConfigureAwait(false);
                            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, $"Send failed: {ex.Message}");
            }
        }

        public void StartListening()
        {
            TcpListener listener = new TcpListener(IPAddress.Any, 57761);

            try
            {
                listener.Start();
                System.Diagnostics.Trace.WriteLine($"[AGAMOTTO CoTHelper] Listening for CoT messages on port 57761...");

                while (true)
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead = stream.Read(buffer, 0, buffer.Length);

                        if (bytesRead > 0)
                        {
                            string cotXml = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                            System.Diagnostics.Trace.WriteLine("[AGAMOTTO CoTHelper] Received CoT XML:\n" + cotXml);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("[AGAMOTTO CoTHelper] Error receiving: " + ex.Message);
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
