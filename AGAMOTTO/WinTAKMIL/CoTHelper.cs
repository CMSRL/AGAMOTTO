using MapEngine.Interop.Util;
using Prism.Logging;
using Spyglass.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using WinTak.Net.Chat;
using WinTak.Overlays.Services;

namespace AGAMOTTO.WinTAKMIL
{
    internal class CoTHelper
    {
        internal string _takIP;
        internal int _takPort;

        public CoTHelper(string takIp, int takPort) 
        {
            _takIP = takIp;
            _takPort = takPort;

        
        }

        public void SendCotMessageList(List<XDocument> cotXmlList)
        {
            //Log.i("Save/Update the value inside : ", cotXml);
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    client.Connect(_takIP, _takPort);
                    NetworkStream stream = client.GetStream();
                    foreach(var message in cotXmlList)
                    {
                        string cotMessage = message.Root.ToString();
                        int size = cotMessage.Length;
                        byte[] sizeBuffer = BitConverter.GetBytes(size);

                        byte[] bytes = Encoding.UTF8.GetBytes(cotMessage);
                        stream.Write(sizeBuffer, 0, 4);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }


                    Console.WriteLine("CoT message sent via TCP.");

                    stream.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending CoT message: {ex.Message}");
            }
        }


        public void StartListening()
        {
            TcpListener listener = new TcpListener(IPAddress.Any, 57761);

            try
            {
                listener.Start();
                Console.WriteLine($"Listening for CoT messages on port {57761}...");

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
                            Console.WriteLine("Received CoT XML:");
                            Console.WriteLine(cotXml);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error receiving CoT message: {ex.Message}");
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
