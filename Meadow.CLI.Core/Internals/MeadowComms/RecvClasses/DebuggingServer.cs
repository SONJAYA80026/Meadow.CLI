﻿using System;
using System.Diagnostics;
using System.Net;  
using System.Net.Sockets;  
using System.Threading;
using System.Threading.Tasks;
using MeadowCLI.DeviceManagement;

namespace Meadow.CLI.Internals.MeadowComms.RecvClasses
{
    // This TCP server directly interacts with Visual Studio debugging.
    // What it receives from Visual Studio it forwards to Meadow.
    // What it receives from Meadow it forwards to Visual Studio.
    public class DebuggingServer
    {
        // VS 2019 - 4024
        // VS 2017 - 4022
        // VS 2015 - 4020
        int vsPort;
        ActiveClient activeClient;
        int activeClientCount = 0;

        // Constructor
        public DebuggingServer(int visualStudioPort)
        {
            vsPort = visualStudioPort;
        }

        public async void StartListening()
        {
            try
            {
                IPHostEntry ipHostInfo = Dns.GetHostEntry("localhost");
                IPAddress ipAddress = ipHostInfo.AddressList[0];
                IPEndPoint localEndPoint = new IPEndPoint(ipAddress, vsPort);

                TcpListener tcpListener = new TcpListener(localEndPoint);
                tcpListener.Start();
                Console.WriteLine("Listening for Visual Studio to connect");

                while(true)
                {
                    await Task.Run(async () =>
                    {
                        // Wait for client to connect
                        TcpClient tcpClient = await tcpListener.AcceptTcpClientAsync();

                        // tcpClient valid after connection
                        Console.WriteLine("Visual Studio has connected");
                        if (activeClientCount > 0)
                        {
                            Debug.Assert(activeClientCount == 1);
                            Debug.Assert(activeClient != null);
                            activeClient.Close();
                            activeClient = null;
                            activeClientCount = 0;
                        }
                        
                        activeClient = new ActiveClient(this, tcpClient);
                        activeClient.ReceiveVSDebugging();
                        activeClientCount++;
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        internal void CloseActiveClient()
        {
            activeClient.Close();
            activeClient = null;
            activeClientCount = 0;
        }

        public void SendToVisualStudio(byte[] byteData)
        {
            activeClient.SendToVisualStudio(byteData);
        }

        // Imbedded class
        private class ActiveClient
        {
            DebuggingServer debuggingServer;
            TcpClient tcpClient;
            NetworkStream networkStream;
            bool okayToRun;

            // Constructor
            internal ActiveClient(DebuggingServer _debuggingServer, TcpClient _tcpClient)
            {
                debuggingServer = _debuggingServer;
                okayToRun = true;
                tcpClient = _tcpClient;
                networkStream = tcpClient.GetStream();
            }

            internal void Close()
            {
                Console.WriteLine("ActiveClient:Close active client");
                okayToRun = false;
                tcpClient.Close();      // Closes NetworkStream too
            }

            internal async void ReceiveVSDebugging()
            {
                //Console.WriteLine("ActiveClient:Start receiving");
                try
                {
                    await Task.Run(async () =>
                    {
                        while (tcpClient.Connected && okayToRun)
                        {
                            var recvdBuffer = new byte[490];
                            var bytesRead = await networkStream.ReadAsync(recvdBuffer, 0, recvdBuffer.Length);
                            if (!okayToRun)
                                break;

                            if (bytesRead > 0)
                            {
                                //Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}-VS sent {bytesRead} will forward to Meadow");

                                // Need a buffer the exact size of received data to work with CLI
                                var meadowBuffer = new byte[bytesRead];
                                Array.Copy(recvdBuffer, 0, meadowBuffer, 0, bytesRead);

                                // Forward to Meadow
                                MeadowDeviceManager.ForwardVisualStudioDataToMono(meadowBuffer, MeadowDeviceManager.CurrentDevice, 0);
                                //Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}-Forwarded {bytesRead} from VS to Meadow");
                            }
                        }
                    });
                }
                catch (System.IO.IOException ioe)
                {
                    // VS client probably died
                    Console.WriteLine(ioe.ToString());
                    debuggingServer.CloseActiveClient();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    debuggingServer.CloseActiveClient();
                    if (okayToRun)
                        throw;
                }
            }

            public async void SendToVisualStudio(byte[] byteData)
            {
                //Console.WriteLine($"Forwarding {byteData.Length} bytes to VS");
                try
                {
                    if (!tcpClient.Connected)
                    {
                        Console.WriteLine($"Send attempt is not possible, Visual Studio not connected");
                        return;
                    }

                    await Task.Run(async () =>
                    {
                        await networkStream.WriteAsync(byteData, 0, byteData.Length);
                    });
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    if (okayToRun)
                        throw;
                }
            }
        }
    }
}