using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.UIElements;

namespace DevelopersHub.RealtimeNetworking
{
    public class Client : MonoBehaviour
    {

        #region Variables
        private string _ip = ""; public string IP { get { return _ip; } }
        private ushort _port = 0; public ushort Port { get { return _port; } }
        private bool _connecting = false; public bool IsConnecting { get { return _connecting; } }
        private bool _connected = false; public bool IsConnected { get { return _connected; } }
        private string _receiveToken = "xxxxx"; public string ReceiveToken { get { return _receiveToken; } }
        private string _sendToken = "xxxxx"; public string SendToken { get { return _sendToken; } }
        private TcpClient _tcpSocket = null;
        private NetworkStream _tcpStream = null;
        private Packet _tcpReceivedData = null;
        private byte[] _tcpReceiveBuffer = null;
        private static Dictionary<int, PacketHandler> _tcpPacketHandlers;
        private UdpClient _udpSocket = null;
        private IPEndPoint _udpEndPoint = null;
        private static Dictionary<int, PacketHandler> _udpPacketHandlers;
        private static int _dataBufferSize = 4096;
        private static int _connectTimeout = 5000;
        private bool _initialized = false;
        private delegate void PacketHandler(Packet packet);
        #endregion

        #region Events

        public delegate void PacketCallback(Client client, Server.ConnectionProtocol protocol, Packet packet);
        public static event PacketCallback OnPacketReceived;

        public delegate void ConnectCallback(Client client, bool connected, string error);
        public static event ConnectCallback OnConnectionAttempt;

        public delegate void DisconnectCallback(Client client, string reason);
        public static event DisconnectCallback OnDisconnected;

        #endregion

        #region General Methods

        private void Awake()
        {
            Initialize();
        }

        private void Initialize()
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;
            DontDestroyOnLoad(gameObject);
            Application.runInBackground = true;
        }

        public static Client CreateNewClient()
        {
            var client = new GameObject("Client").AddComponent<Client>();
            client.Initialize();
            return client;
        }

        public void SendPacket(Packet packet, Server.ConnectionProtocol protocol)
        {
            SendPacket(packet, Packet.ID.CUSTOM, protocol);
        }

        private void SendPacketInternal(Packet packet, Server.ConnectionProtocol protocol)
        {
            SendPacket(packet, Packet.ID.INTERNAL, protocol);
        }

        private void SendPacket(Packet packet, Packet.ID id, Server.ConnectionProtocol protocol)
        {
            if (protocol == Server.ConnectionProtocol.TCP)
            {
                try
                {
                    if (_tcpSocket != null && packet != null)
                    {
                        packet.SetID((int)id);
                        packet.InternalSendInitialize();
                        packet.WriteLength();
                        _tcpStream.BeginWrite(packet.ToArray(), 0, packet.Length(), null, null);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Error sending data to server via TCP:\n{e.Message}");
                }
            }
            else if (protocol == Server.ConnectionProtocol.UDP)
            {
                try
                {
                    if (_udpSocket != null && packet != null)
                    {
                        packet.SetID((int)id);
                        packet.InternalSendInitialize();
                        packet.WriteLength();
                        _udpSocket.BeginSend(packet.ToArray(), packet.Length(), null, null);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Error sending data to server via UDP:\n{e.Message}");
                }
            }
        }

        private void OnApplicationQuit()
        {
            TcpDisconnect("Application quit.");
        }

        public void Disconnect()
        {
            TcpDisconnect("Manually by the user.");
        }

        private void DisconnectFinalize(string reason)
        {
            OnDisconnected?.Invoke(this, reason);
        }

        public static Client Get(string ip, ushort port)
        {
            var clients = FindObjectsByType<Client>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (clients != null)
            {
                for (int i = 0; i < clients.Length; i++)
                {
                    var client = clients[i];
                    if ((client._connected || client._connecting) && client._ip == ip && client._port == port)
                    {
                        return client;
                    }
                }
            }
            return null;
        }

        #endregion

        #region TCP Methods

        public void ConnectToServer(string ip, ushort port)
        {
            if (_connecting || _connected || string.IsNullOrEmpty(ip))
            {
                return;
            }

            ip = ip.Trim();

            var client = Get(ip, port);
            if (client != null && client != this)
            {
                Debug.LogWarning($"Another client is connected to ip {ip} and port {port} from this device.");
                return;
            }

            _connecting = true;

            _tcpPacketHandlers = new Dictionary<int, PacketHandler>()
            {
                { (int)Packet.ID.INTERNAL, TcpReceiveInternal },
                { (int)Packet.ID.CUSTOM, TcpReceiveCustom },
            };

            _tcpSocket = new TcpClient { ReceiveBufferSize = _dataBufferSize, SendBufferSize = _dataBufferSize };
            _tcpReceiveBuffer = new byte[_dataBufferSize];
            IAsyncResult result = null;
            bool waiting = false;
            try
            {
                _ip = ip;
                _port = port;
                result = _tcpSocket.BeginConnect(ip, port, TcpConnectionAttemptCallback, _tcpSocket);
                waiting = result.AsyncWaitHandle.WaitOne(_connectTimeout, false);
            }
            catch (Exception e)
            {
                _connecting = false;
                OnConnectionAttempt?.Invoke(this, false, $"Unable to connect to server:\n{e.Message}");
                return;
            }
            if (!waiting || !_tcpSocket.Connected)
            {
                _connecting = false;
                OnConnectionAttempt?.Invoke(this, false, "Connection timed out.");
                return;
            }
        }

        private void TcpReceiveInternal(Packet packet)
        {
            try
            {
                int id = packet.ReadInt();
                switch (id)
                {
                    case 1: // Connection Accepted
                        _receiveToken = packet.ReadString();
                        _sendToken = Guid.NewGuid().ToString();
                        using (Packet response = new Packet())
                        {
                            response.Write((int)1);
                            response.Write(_sendToken);
                            SendPacketInternal(response, Server.ConnectionProtocol.TCP);
                        }
                        OnConnectionAttempt.Invoke(this, true, null);
                        UdpInitialize((ushort)((IPEndPoint)_tcpSocket.Client.LocalEndPoint).Port);
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error receiving data from server via TCP:\n{e.Message}");
            }
        }

        private void TcpReceiveCustom(Packet packet)
        {
            if (packet != null)
            {
                OnPacketReceived?.Invoke(this, Server.ConnectionProtocol.TCP, packet);
            }
        }

        private void TcpConnectionAttemptCallback(IAsyncResult result)
        {
            _tcpSocket.EndConnect(result);
            if (!_tcpSocket.Connected)
            {
                return;
            }
            _connecting = false;
            _connected = true;
            _tcpStream = _tcpSocket.GetStream();
            _tcpReceivedData = new Packet();
            _tcpStream.BeginRead(_tcpReceiveBuffer, 0, _dataBufferSize, TcpReceiveCallback, null);
        }

        private void TcpReceiveCallback(IAsyncResult result)
        {
            try
            {
                int length = _tcpStream.EndRead(result);
                if (length <= 0)
                {
                    TcpDisconnect("Failed to receive data.");
                    return;
                }
                byte[] data = new byte[length];
                Array.Copy(_tcpReceiveBuffer, data, length);
                _tcpReceivedData.Reset(TcpHandleData(data));
                _tcpStream.BeginRead(_tcpReceiveBuffer, 0, _dataBufferSize, TcpReceiveCallback, null);
            }
            catch (Exception e)
            {
                TcpDisconnect($"Failed to receive data:\n{e.Message}");
            }
        }

        private bool TcpHandleData(byte[] _data)
        {
            int length = 0;
            _tcpReceivedData.SetBytes(_data);
            if (_tcpReceivedData.UnreadLength() >= 4)
            {
                length = _tcpReceivedData.ReadInt();
                if (length <= 0)
                {
                    return true;
                }
            }
            while (length > 0 && length <= _tcpReceivedData.UnreadLength())
            {
                byte[] bytes = _tcpReceivedData.ReadBytes(length);
                Dispatcher.ExecuteOnMainThread(() =>
                {
                    using (Packet packet = new Packet(bytes))
                    {
                        packet.InternalReceiveInitialize();
                        int id = packet.ReadInt();
                        _tcpPacketHandlers[id](packet);
                    }
                });
                length = 0;
                if (_tcpReceivedData.UnreadLength() >= 4)
                {
                    length = _tcpReceivedData.ReadInt();
                    if (length <= 0)
                    {
                        return true;
                    }
                }
            }
            if (length <= 1)
            {
                return true;
            }
            return false;
        }

        private void TcpDisconnect(string reason)
        {
            if (_connected)
            {
                _connected = false;
                if (_tcpSocket != null)
                {
                    _tcpSocket.Close();
                }
                Dispatcher.ExecuteOnMainThread(() => DisconnectFinalize(reason));
                _tcpStream = null;
                _tcpReceivedData = null;
                _tcpReceiveBuffer = null;
                _tcpSocket = null;
                UdpDisconnect();
            }
        }

        #endregion

        #region UDP Methods

        public void UdpInitialize(ushort port)
        {
            _udpPacketHandlers = new Dictionary<int, PacketHandler>()
            {
                { (int)Packet.ID.INTERNAL, UdpReceiveInternal },
                { (int)Packet.ID.CUSTOM, UdpReceiveCustom },
            };

            try
            {
                _udpEndPoint = new IPEndPoint(IPAddress.Parse(_ip), _port);
                _udpSocket = new UdpClient(port);
                _udpSocket.Connect(_udpEndPoint);
                _udpSocket.BeginReceive(UdpReceiveCallback, null);
            }
            catch (Exception e)
            {
                _udpSocket = null;
                Debug.LogWarning($"Error initializing UDP:\n{e.Message}");
            }
        }

        private void UdpReceiveInternal(Packet packet)
        {
            try
            {
                int id = packet.ReadInt();
                switch (id)
                {
                    case 1:

                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error receiving data from server via UDP:\n{e.Message}");
            }
        }

        private void UdpReceiveCustom(Packet packet)
        {
            if (packet != null)
            {
                OnPacketReceived?.Invoke(this, Server.ConnectionProtocol.UDP, packet);
            }
        }

        private void UdpReceiveCallback(IAsyncResult result)
        {
            try
            {
                byte[] data = _udpSocket.EndReceive(result, ref _udpEndPoint);
                _udpSocket.BeginReceive(UdpReceiveCallback, null);
                if (data.Length < 4)
                {
                    TcpDisconnect("Failed to receive UDP data.");
                    return;
                }
                UdpHandleData(data);
            }
            catch (Exception e)
            {
                TcpDisconnect($"Failed to receive UDP data:\n{e.Message}");
            }
        }

        private void UdpHandleData(byte[] data)
        {
            using (Packet packet = new Packet(data))
            {
                int length = packet.ReadInt();
                data = packet.ReadBytes(length);
            }
            Dispatcher.ExecuteOnMainThread(() =>
            {
                using (Packet packet = new Packet(data))
                {
                    packet.InternalReceiveInitialize();
                    int packetId = packet.ReadInt();
                    _udpPacketHandlers[packetId](packet);
                }
            });
        }

        private void UdpDisconnect()
        {
            if (_udpSocket != null)
            {
                _udpSocket.Close();
            }
            _udpEndPoint = null;
            _udpSocket = null;
        }

        #endregion

    }
}