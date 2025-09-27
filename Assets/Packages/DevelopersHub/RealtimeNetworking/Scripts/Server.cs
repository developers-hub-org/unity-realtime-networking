using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace DevelopersHub.RealtimeNetworking
{
    public class Server : MonoBehaviour
    {

        #region Data Structures

        public enum ConnectionProtocol
        {
            TCP, UDP
        }

        private class ClientData
        {
            public TcpClient client;
            public NetworkStream stream;
            public byte[] receiveBuffer;
            public Packet receivedData;
            public string sendToken;
            public string receiveToken;
            public int id;
            public IPEndPoint endPoint;

            public ClientData(int clientId, TcpClient tcpClient)
            {
                id = clientId;
                client = tcpClient;
                stream = client.GetStream();
                receiveBuffer = new byte[_dataBufferSize];
                receivedData = new Packet();
                sendToken = Guid.NewGuid().ToString();
                receiveToken = null;
            }

            public void Dispose()
            {
                client?.Close();
                stream?.Close();
                receivedData?.Dispose();
            }
        }

        #endregion

        #region Server Variables
        private ushort _port = 0; public ushort Port { get { return _port; } }
        private bool _running = false; public bool IsRunning { get { return _running; } }
        private TcpListener _tcpListener = null;
        private UdpClient _udpListener = null;
        private readonly Dictionary<int, ClientData> _tcpClients = new Dictionary<int, ClientData>();
        private readonly SortedSet<int> _availableIds = new SortedSet<int>();
        private readonly Dictionary<IPAddress, int> _ipToClientId = new Dictionary<IPAddress, int>();
        private int _maxId = 0;
        private static int _dataBufferSize = 4096;
        private bool _initialized = false;
        #endregion

        #region Events

        public delegate void PacketCallback(Server server, int clientId, ConnectionProtocol protocol, Packet packet);
        public static event PacketCallback OnPacketReceived;

        public delegate void ConnectCallback(Server server, int clientId);
        public static event ConnectCallback OnClientConnected;

        public delegate void DisconnectedCallback(Server server, int clientId, string reason);
        public static event DisconnectedCallback OnClientDisconnected;

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

        public static Server CreateNewServer()
        {
            var server = new GameObject("Server").AddComponent<Server>();
            server.Initialize();
            return server;
        }

        public bool StartServer(ushort port, int maxConnections)
        {
            if (_running) { return false; }
            try
            {
                var servers = FindObjectsByType<Server>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (servers != null)
                {
                    for (int i = 0; i < servers.Length; i++)
                    {
                        Server server = servers[i];
                        if(server != this && server.IsRunning && server.Port == port)
                        {
                            Debug.LogWarning($"Another server is running on port {port} of this device.");
                            return false;
                        }
                    }
                }
                _port = port;
                _tcpListener = new TcpListener(IPAddress.Any, port);
                _tcpListener.Start(maxConnections);
                _tcpListener.BeginAcceptTcpClient(OnTcpClientConnected, null);
                _udpListener = new UdpClient(port);
                _udpListener.BeginReceive(OnUdpDataReceived, null);
                _running = true;
                return true;
            }
            catch (Exception e)
            {
                _running = false;
                Debug.LogWarning($"Server TCP start error:\n{e.Message}");
                return false;
            }
        }

        private void DisconnectClient(int clientId, string reason)
        {
            if (_tcpClients.TryGetValue(clientId, out ClientData clientData))
            {
                clientData.Dispose();
                _tcpClients.Remove(clientId);

                // Remove IP mapping
                if (clientData.endPoint != null)
                {
                    _ipToClientId.Remove(clientData.endPoint.Address);
                }

                ReleaseID(clientId);
            }
            Dispatcher.ExecuteOnMainThread(() => DisconnectClientFinalize(clientId, reason));
            // Debug.LogWarning($"Client {clientId} disconnected:\n{reason}");
        }

        private void DisconnectClientFinalize(int clientId, string reason)
        {
            OnClientDisconnected?.Invoke(this, clientId, reason);
        }

        public void StopServer()
        {
            if (!_running) { return; }
            _running = false;
            _tcpListener?.Stop();
            foreach (var client in _tcpClients.Values)
            {
                client.Dispose();
            }
            _tcpClients.Clear();
            _udpListener?.Close();
            _availableIds.Clear();
            _maxId = 0;
        }

        public void SendPacket(int clientId, Packet packet, ConnectionProtocol protocol)
        {
            SendPacket(clientId, packet, Packet.ID.CUSTOM, protocol);
        }

        public void SendPacketToAllClients(Packet packet, ConnectionProtocol protocol)
        {
            foreach (var clientId in _tcpClients.Keys)
            {
                using(Packet clone = new Packet(packet.ToArray()))
                {
                    clone.compress = packet.compress;
                    SendPacket(clientId, clone, Packet.ID.CUSTOM, protocol);
                }
            }
        }

        private void SendPacketInternal(int clientId, Packet packet, ConnectionProtocol protocol)
        {
            SendPacket(clientId, packet, Packet.ID.INTERNAL, protocol);
        }

        private void SendPacket(int clientId, Packet packet, Packet.ID id, ConnectionProtocol protocol)
        {
            if (!_running)
            {
                Debug.Log("Server is not running.");
                return;
            }
            if (!_tcpClients.TryGetValue(clientId, out ClientData clientData)) { return; }
            if (protocol == Server.ConnectionProtocol.TCP)
            {
                try
                {
                    packet.SetID((int)id);
                    packet.InternalSendInitialize();
                    packet.WriteLength();
                    clientData.stream.BeginWrite(packet.ToArray(), 0, packet.Length(), null, null);
                }
                catch (Exception e)
                {
                    DisconnectClient(clientId, $"Send TCP data error:\n{e.Message}");
                }
            }
            else if (protocol == Server.ConnectionProtocol.UDP)
            {
                try
                {
                    if(clientData.endPoint != null)
                    {
                        packet.SetID((int)id);
                        packet.InternalSendInitialize();
                        packet.WriteLength();
                        _udpListener.BeginSend(packet.ToArray(), packet.Length(), clientData.endPoint, null, null);
                    }
                    else
                    {
                        Debug.LogWarning("UDP is not initialized. Client should send a UDP message to server to be registered.");
                    }
                }
                catch (Exception e)
                {
                    DisconnectClient(clientId, $"Send UDP data error:\n{e.Message}");
                }
            }
        }

        public static Server Get(ushort port)
        {
            var servers = FindObjectsByType<Server>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (servers != null)
            {
                for (int i = 0; i < servers.Length; i++)
                {
                    var server = servers[i];
                    if (server._running && server._port == port)
                    {
                        return server;
                    }
                }
            }
            return null;
        }

        private void OnApplicationQuit()
        {
            StopServer();
        }

        #endregion

        #region ID Management Methods

        private int GetNextAvailableID()
        {
            lock (_availableIds)
            {
                // If we have available IDs, return the smallest one
                if (_availableIds.Count > 0)
                {
                    int id = _availableIds.Min;
                    _availableIds.Remove(id);
                    return id;
                }

                // Otherwise, increment our counter and return the new ID
                return ++_maxId;
            }
        }

        private void ReleaseID(int id)
        {
            lock (_availableIds)
            {
                // Add the ID back to the available pool
                _availableIds.Add(id);

                // If this was the highest ID and we have available IDs, we can try to optimize our max ID counter
                if (id == _maxId && _availableIds.Count > 0)
                {
                    // Find the new maximum ID that's actually in use
                    int newMax = 0;
                    foreach (int usedId in _tcpClients.Keys)
                    {
                        if (usedId > newMax) { newMax = usedId; }
                    }
                    _maxId = newMax;
                }
                else if (id == _maxId)
                {
                    // If no available IDs, decrement the max counter
                    _maxId--;
                }
            }
        }

        #endregion

        #region TCP Methods

        private void OnTcpClientConnected(IAsyncResult result)
        {
            if (!_running) { return; }
            int clientId = -1;
            try
            {
                TcpClient client = _tcpListener.EndAcceptTcpClient(result);
                clientId = GetNextAvailableID();

                var endPoint = client.Client.RemoteEndPoint as IPEndPoint;

                ClientData clientData = new ClientData(clientId, client);
                _tcpClients.Add(clientId, clientData);
                clientData.endPoint = endPoint;

                // Register IP address
                IPAddress clientIp = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                _ipToClientId[clientIp] = clientId;

                // Send connection token to client
                using (Packet packet = new Packet())
                {
                    packet.WriteInt(1); // Connection accepted
                    packet.WriteString(clientData.sendToken);
                    SendPacketInternal(clientId, packet, ConnectionProtocol.TCP);
                }

                // Begin reading from client
                clientData.stream.BeginRead(clientData.receiveBuffer, 0, _dataBufferSize, OnTcpDataReceived, clientId);

                // Notify main thread
                Dispatcher.ExecuteOnMainThread(() => TcpClientConnectedFinalize(clientId));

                // Continue accepting new clients
                if (_running)
                {
                    _tcpListener.BeginAcceptTcpClient(OnTcpClientConnected, null);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Client acceptance error:\n{e.Message}");
                if(clientId >= 0)
                {
                    ReleaseID(clientId);
                }
            }
        }

        private void TcpClientConnectedFinalize(int clientId)
        {
            OnClientConnected?.Invoke(this, clientId);
        }

        private void OnTcpDataReceived(IAsyncResult result)
        {
            int clientId = (int)result.AsyncState;
            if (!_tcpClients.TryGetValue(clientId, out ClientData clientData)) { return; }
            try
            {
                int byteLength = clientData.stream.EndRead(result);
                if (byteLength <= 0)
                {
                    DisconnectClient(clientId, "Connection closed.");
                    return;
                }

                byte[] data = new byte[byteLength];
                Array.Copy(clientData.receiveBuffer, data, byteLength);

                clientData.receivedData.Reset(TcpHandleData(clientId, data));

                // Continue reading
                clientData.stream.BeginRead(clientData.receiveBuffer, 0, _dataBufferSize, OnTcpDataReceived, clientId);
            }
            catch (Exception e)
            {
                DisconnectClient(clientId, $"Receive TCP data error:\n{e.Message}");
            }
        }

        private bool TcpHandleData(int clientId, byte[] data)
        {
            if (!_tcpClients.TryGetValue(clientId, out ClientData clientData)) { return false; }

            int length = 0;
            clientData.receivedData.SetBytes(data);

            if (clientData.receivedData.UnreadLength() >= 4)
            {
                length = clientData.receivedData.ReadInt();
                if (length <= 0) { return true; }
            }

            while (length > 0 && length <= clientData.receivedData.UnreadLength())
            {
                byte[] packetBytes = clientData.receivedData.ReadBytes(length);
                Dispatcher.ExecuteOnMainThread(() =>
                {
                    using (Packet packet = new Packet(packetBytes))
                    {
                        packet.InternalReceiveInitialize();
                        int packetId = packet.ReadInt();
                        if (packetId == (int)Packet.ID.INTERNAL)
                        {
                            TcpReceiveInternal(clientId, packet);
                        }
                        else if (packetId == (int)Packet.ID.CUSTOM)
                        {
                            TcpReceiveCustom(clientId, packet);
                        }
                    }
                });

                length = 0;
                if (clientData.receivedData.UnreadLength() >= 4)
                {
                    length = clientData.receivedData.ReadInt();
                    if (length <= 0) { return true; }
                }
            }
            if (length <= 1) { return true; }
            return false;
        }

        private void TcpReceiveInternal(int clientId, Packet packet)
        {
            try
            {
                if (!_tcpClients.TryGetValue(clientId, out ClientData clientData)) { return; }
                int id = packet.ReadInt();
                switch (id)
                {
                    case 1: // Client sent their token
                        clientData.receiveToken = packet.ReadString();

                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error receiving data from client via TCP:\n{e.Message}");
            }
        }

        private void TcpReceiveCustom(int clientId, Packet packet)
        {
            if (packet != null)
            {
                if (!_tcpClients.TryGetValue(clientId, out ClientData clientData)) { return; }
                OnPacketReceived?.Invoke(this, clientId, ConnectionProtocol.TCP, packet);
            }
        }

        #endregion

        #region UDP Methods

        private void OnUdpDataReceived(IAsyncResult result)
        {
            if (!_running) { return; }
            try
            {
                IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = _udpListener.EndReceive(result, ref endPoint);

                if (data.Length < 4)
                {
                    // Continue listening
                    _udpListener.BeginReceive(OnUdpDataReceived, null);
                    return;
                }

                using (Packet packet = new Packet(data))
                {
                    int length = packet.ReadInt();
                    if (length <= 0)
                    {
                        // Continue listening
                        _udpListener.BeginReceive(OnUdpDataReceived, null);
                        return;
                    }
                    data = packet.ReadBytes(length);
                }

                int clientId = FindClientIdByIpAddress(endPoint.Address);
                if (clientId >= 0)
                {
                    /*
                    if (_tcpClients.TryGetValue(clientId, out ClientData clientData) && clientData.endPoint == null) 
                    { 
                        clientData.endPoint = endPoint;
                    }
                    */
                    Dispatcher.ExecuteOnMainThread(() =>
                    {
                        using (Packet packet = new Packet(data))
                        {
                            packet.InternalReceiveInitialize();
                            int packetId = packet.ReadInt();
                            if (packetId == (int)Packet.ID.INTERNAL)
                            {
                                UdpReceiveInternal(clientId, packet);
                            }
                            else if (packetId == (int)Packet.ID.CUSTOM)
                            {
                                UdpReceiveCustom(clientId, packet);
                            }
                        }
                    });
                }
                else
                {
                    Debug.LogWarning($"No matching client found for UDP packet, endpoint: {endPoint}");
                }

                // Continue listening
                _udpListener.BeginReceive(OnUdpDataReceived, null);
            }
            catch (Exception e)
            {
                if (_running)
                {
                    // Continue listening even after error
                    _udpListener.BeginReceive(OnUdpDataReceived, null);
                    Debug.LogWarning($"Receive UDP data error: {e.Message}");
                }
            }
        }

        private int FindClientIdByIpAddress(IPAddress ipAddress)
        {
            if (_ipToClientId.TryGetValue(ipAddress, out int clientId))
            {
                return clientId;
            }
            return -1;
        }

        private void UdpReceiveInternal(int clientId, Packet packet)
        {
            try
            {
                if (!_tcpClients.TryGetValue(clientId, out ClientData clientData)) { return; }
                int id = packet.ReadInt();
                switch (id)
                {
                    case 1:
                        
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error receiving data from client via TCP:\n{e.Message}");
            }
        }

        private void UdpReceiveCustom(int clientId, Packet packet)
        {
            if (packet != null)
            {
                if (!_tcpClients.TryGetValue(clientId, out ClientData clientData)) { return; }
                OnPacketReceived?.Invoke(this, clientId, ConnectionProtocol.UDP, packet);
            }
        }

        #endregion

    }
}