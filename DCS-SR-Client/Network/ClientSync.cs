﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Settings;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Singletons;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Easy.MessageHub;
using Newtonsoft.Json;
using NLog;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Network
{
    public class ClientSync
    {
        public delegate void ConnectCallback(bool result);
        public delegate void ExternalAWACSModeConnectCallback(bool result, int coalition);

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public static SyncedServerSettings ServerSettings = SyncedServerSettings.Instance;
        public static string ServerVersion = "Unknown";
        private readonly ConcurrentDictionary<string, SRClient> _clients;
        private readonly string _guid;
        private ConnectCallback _callback;
        private ExternalAWACSModeConnectCallback _externalAWACSModeCallback;
        private IPEndPoint _serverEndpoint;
        private TcpClient _tcpClient;

        private ClientStateSingleton _clientStateSingleton = ClientStateSingleton.Instance;

        private RadioDCSSyncServer _radioDCSSync = null;

        public ClientSync(ConcurrentDictionary<string, SRClient> clients, string guid)
        {
            _clients = clients;
            _guid = guid;
        }


        public void TryConnect(IPEndPoint endpoint, ConnectCallback callback)
        {
            _callback = callback;
            _serverEndpoint = endpoint;

            var tcpThread = new Thread(Connect);
            tcpThread.Start();
        }

        public void ConnectExternalAWACSMode(string password, ExternalAWACSModeConnectCallback callback)
        {
            if (_clientStateSingleton.InExternalAWACSMode)
            {
                return;
            }

            _externalAWACSModeCallback = callback;

            var sideInfo = _clientStateSingleton.DcsPlayerSideInfo;
            SendToServer(new NetworkMessage
            {
                Client = new SRClient
                {
                    Coalition = sideInfo.side,
                    Name = sideInfo.name,
                    Position = sideInfo.Position,
                    ClientGuid = _guid
                },
                ExternalAWACSModePassword = password,
                MsgType = NetworkMessage.MessageType.EXTERNAL_AWACS_MODE_PASSWORD
            });
        }

        public void DisconnectExternalAWACSMode()
        {
            if (!_clientStateSingleton.InExternalAWACSMode || _radioDCSSync == null)
            {
                return;
            }

            if (_tcpClient != null && _tcpClient.Connected)
            {
                SendToServer(new NetworkMessage
                {
                    Client = new SRClient
                    {
                        Coalition = 0,
                        Name = "",
                        Position = new DcsPosition { x = 0, y = 0, z = 0 },
                        ClientGuid = _guid
                    }
                });
            }

            _radioDCSSync.StopExternalAWACSModeLoop();

            CallExternalAWACSModeOnMain(false, 0);
        }

        private void Connect()
        {
            if (_radioDCSSync != null)
            {
                _radioDCSSync.Stop();
                _radioDCSSync = null;
            }

            _radioDCSSync = new RadioDCSSyncServer(ClientRadioUpdated, ClientCoalitionUpdate, _clients, _guid);
            using (_tcpClient = new TcpClient())
            {
                _tcpClient.SendTimeout = 10;
                try
                {
                    _tcpClient.NoDelay = true;

                    _tcpClient.Connect(_serverEndpoint);

                    if (_tcpClient.Connected)
                    {
                        _radioDCSSync.Listen();

                        _tcpClient.NoDelay = true;

                        CallOnMain(true);
                        ClientSyncLoop();
                    }
                }
                catch (SocketException ex)
                {
                    Logger.Error(ex, "error connecting to server");
                }
            }

            _radioDCSSync.Stop();

            //disconnect callback
            CallOnMain(false);
        }

        private void ClientRadioUpdated()
        {
            Logger.Debug("Sending Radio Update to Server");
            var sideInfo = _clientStateSingleton.DcsPlayerSideInfo;
            SendToServer(new NetworkMessage
            {
                Client = new SRClient
                {
                    Coalition = sideInfo.side,
                    Name = sideInfo.name,
                    ClientGuid = _guid,
                    RadioInfo = _clientStateSingleton.DcsPlayerRadioInfo,
                    Position = sideInfo.Position
                },
                MsgType = NetworkMessage.MessageType.RADIO_UPDATE
            });
        }

        private void ClientCoalitionUpdate()
        {
            var sideInfo = _clientStateSingleton.DcsPlayerSideInfo;
            SendToServer(new NetworkMessage
            {
                Client = new SRClient
                {
                    Coalition = sideInfo.side,
                    Name = sideInfo.name,
                    Position = sideInfo.Position,
                    ClientGuid = _guid
                },
                MsgType = NetworkMessage.MessageType.UPDATE
            });
        }

        private void CallOnMain(bool result)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(DispatcherPriority.Background,
                    new ThreadStart(delegate { _callback(result); }));
            }
            catch (Exception ex)
            {
            }
        }

        private void CallExternalAWACSModeOnMain(bool result, int coalition)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(DispatcherPriority.Background,
                    new ThreadStart(delegate { _externalAWACSModeCallback(result, coalition); }));
            }
            catch (Exception ex)
            {
            }
        }

        private void ClientSyncLoop()
        {
            //clear the clietns list
            _clients.Clear();

            using (var reader = new StreamReader(_tcpClient.GetStream(), Encoding.UTF8))
            {
                try
                {
                    var sideInfo = _clientStateSingleton.DcsPlayerSideInfo;
                    //start the loop off by sending a SYNC Request
                    SendToServer(new NetworkMessage
                    {
                        Client = new SRClient
                        {
                            Coalition = sideInfo.side,
                            Name = sideInfo.name,
                            Position = sideInfo.Position,
                            ClientGuid = _guid
                        },
                        MsgType = NetworkMessage.MessageType.SYNC
                    });

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        try
                        {
                            var serverMessage = JsonConvert.DeserializeObject<NetworkMessage>(line);
                            if (serverMessage != null)
                            {
                                switch (serverMessage.MsgType)
                                {
                                    case NetworkMessage.MessageType.PING:
                                        // Do nothing for now
                                        break;
                                    case NetworkMessage.MessageType.UPDATE:

                                        ServerSettings.Decode(serverMessage.ServerSettings);

                                        if (_clients.ContainsKey(serverMessage.Client.ClientGuid))
                                        {
                                            var srClient = _clients[serverMessage.Client.ClientGuid];
                                            var updatedSrClient = serverMessage.Client;
                                            if (srClient != null)
                                            {
                                                srClient.LastUpdate = DateTime.Now.Ticks;
                                                srClient.Name = updatedSrClient.Name;
                                                srClient.Coalition = updatedSrClient.Coalition;
                                                srClient.Position = updatedSrClient.Position;

//                                                Logger.Info("Recevied Update Client: " + NetworkMessage.MessageType.UPDATE + " From: " +
//                                                            srClient.Name + " Coalition: " +
//                                                            srClient.Coalition + " Pos: " + srClient.Position);
                                            }
                                        }
                                        else
                                        {
                                            var connectedClient = serverMessage.Client;
                                            connectedClient.LastUpdate = DateTime.Now.Ticks;

                                            //init with LOS true so you can hear them incase of bad DCS install where
                                            //LOS isnt working
                                            connectedClient.LineOfSightLoss = 0.0f;
                                            //0.0 is NO LOSS therefore full Line of sight

                                            _clients[serverMessage.Client.ClientGuid] = connectedClient;

//                                            Logger.Info("Recevied New Client: " + NetworkMessage.MessageType.UPDATE +
//                                                        " From: " +
//                                                        serverMessage.Client.Name + " Coalition: " +
//                                                        serverMessage.Client.Coalition);
                                        }
                                        break;
                                    case NetworkMessage.MessageType.SYNC:
                                        // Logger.Info("Recevied: " + NetworkMessage.MessageType.SYNC);

                                        //check server version
                                        if (serverMessage.Version == null)
                                        {
                                            Logger.Error("Disconnecting Unversioned Server");
                                            Disconnect();
                                            break;
                                        }

                                        var serverVersion = Version.Parse(serverMessage.Version);
                                        var protocolVersion = Version.Parse(UpdaterChecker.MINIMUM_PROTOCOL_VERSION);

                                        ServerVersion = serverMessage.Version;

                                        if (serverVersion < protocolVersion)
                                        {
                                            Logger.Warn(
                                                $"Disconnecting From Unsupported Server Version - Version {serverMessage.Version}");
                                            Disconnect();
                                            break;
                                        }

                                        if (serverMessage.Clients != null)
                                        {
                                            foreach (var client in serverMessage.Clients)
                                            {
                                                client.LastUpdate = DateTime.Now.Ticks;
                                                //init with LOS true so you can hear them incase of bad DCS install where
                                                //LOS isnt working
                                                client.LineOfSightLoss = 0.0f;
                                                //0.0 is NO LOSS therefore full Line of sight
                                                _clients[client.ClientGuid] = client;
                                            }
                                        }
                                        //add server settings
                                        ServerSettings.Decode(serverMessage.ServerSettings);

                                        break;

                                    case NetworkMessage.MessageType.SERVER_SETTINGS:

                                        //  Logger.Info("Recevied: " + NetworkMessage.MessageType.SERVER_SETTINGS);
                                        ServerSettings.Decode(serverMessage.ServerSettings);
                                        ServerVersion = serverMessage.Version;

                                        break;
                                    case NetworkMessage.MessageType.CLIENT_DISCONNECT:
                                        //   Logger.Info("Recevied: " + NetworkMessage.MessageType.CLIENT_DISCONNECT);

                                        SRClient outClient;
                                        _clients.TryRemove(serverMessage.Client.ClientGuid, out outClient);

                                        if (outClient != null)
                                        {
                                            MessageHub.Instance.Publish(outClient);
                                        }

                                        break;
                                    case NetworkMessage.MessageType.VERSION_MISMATCH:
                                        Logger.Error("Version Mismatch Between Client & Server - Disconnecting");
                                        Disconnect();
                                        break;
                                    case NetworkMessage.MessageType.EXTERNAL_AWACS_MODE_PASSWORD:
                                        if (serverMessage.Client.Coalition == 0)
                                        {
                                            Logger.Info("External AWACS mode authentication failed");

                                            CallExternalAWACSModeOnMain(false, 0);
                                        }
                                        else if (_radioDCSSync != null && _radioDCSSync.IsListening)
                                        {
                                            Logger.Info("External AWACS mode authentication succeeded");

                                            _radioDCSSync.StartExternalAWACSModeLoop();

                                            CallExternalAWACSModeOnMain(true, serverMessage.Client.Coalition);
                                        }
                                        break;
                                    default:
                                        Logger.Error("Recevied unknown " + line);
                                        break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Client exception reading from socket ");
                        }

                        // do something with line
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Client exception reading - Disconnecting ");
                }
            }

            //disconnected - reset DCS Info
            ClientStateSingleton.Instance.DcsPlayerRadioInfo.LastUpdate = 0;

            //clear the clietns list
            _clients.Clear();

            //disconnect callback
            CallOnMain(false);
        }

        private void SendToServer(NetworkMessage message)
        {
            try
            {
               
                message.Version = UpdaterChecker.VERSION;

                var json = (JsonConvert.SerializeObject(message) + "\n");

                if (message.MsgType == NetworkMessage.MessageType.RADIO_UPDATE)
                {
                    Logger.Debug("Sending Radio Update To Server: "+ (json));
                }


                var bytes = Encoding.UTF8.GetBytes(json);
                _tcpClient.GetStream().Write(bytes, 0, bytes.Length);
                //Need to flush?
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Client exception sending to server");

                Disconnect();
            }
        }

        //implement IDispose? To close stuff properly?
        public void Disconnect()
        {
            DisconnectExternalAWACSMode();

            try
            {
                if (_tcpClient != null)
                {
                    _tcpClient.Close(); // this'll stop the socket blocking
                }
            }
            catch (Exception ex)
            {
            }

            Logger.Error("Disconnecting from server");

            //CallOnMain(false);
        }
    }
}