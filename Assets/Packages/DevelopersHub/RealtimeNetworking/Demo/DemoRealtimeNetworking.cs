using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.UI;
using DevelopersHub.RealtimeNetworking;

public class DemoRealtimeNetworking : MonoBehaviour
{

    [SerializeField] private TextMeshProUGUI _logText = null;
    [SerializeField] private int _maxLogEntries = 20;

    [SerializeField] private TMP_InputField _serverPortField = null;
    [SerializeField] private Button _serverStartButton = null;
    [SerializeField] private GameObject _serverItem = null;
    [SerializeField] private Transform _serverItemsContainer = null;

    [SerializeField] private TMP_InputField _clientIpField = null;
    [SerializeField] private TMP_InputField _clientPortField = null;
    [SerializeField] private Button _clientConnectButton = null;
    [SerializeField] private GameObject _clientItem = null;
    [SerializeField] private Transform _clientItemsContainer = null;

    private List<string> _logEntries = new List<string>();

    public enum PacketID
    {
        HelloWorld = 0
    }

    private void Awake()
    {
        ClearItems();
        _serverStartButton.onClick.AddListener(StartServer);
        _clientConnectButton.onClick.AddListener(StartClient);
        Client.OnConnectionAttempt += OnClientConnectionAttempt;
        Client.OnDisconnected += OnClientDisconnected;
        Client.OnPacketReceived += OnPacketReceivedForClient;
        Server.OnClientConnected += OnClientConnected;
        Server.OnClientDisconnected += ClientDisconnected;
        Server.OnPacketReceived += OnPacketReceivedForServer;
    }

    private void OnDestroy()
    {
        Client.OnConnectionAttempt -= OnClientConnectionAttempt;
        Client.OnDisconnected -= OnClientDisconnected;
        Client.OnPacketReceived -= OnPacketReceivedForClient;
        Server.OnClientConnected -= OnClientConnected;
        Server.OnClientDisconnected -= ClientDisconnected;
        Server.OnPacketReceived -= OnPacketReceivedForServer;
    }

    private void StartServer()
    {
        string portStr = _serverPortField.text;
        if (!string.IsNullOrEmpty(portStr) && ushort.TryParse(portStr, out ushort port))
        {
            var server = Server.CreateNewServer();
            if(server.StartServer(port, 10000))
            {
                CreateServerItem(port);
            }
            else
            {
                Destroy(server.gameObject);
            }
        }
    }

    private void OnClientConnected(Server server, int clientId)
    {
        Log($"Server: Client connected to server {server.Port} with ID {clientId}", Color.green);
    }

    private void ClientDisconnected(Server server, int clientId, string reason)
    {
        Log($"Server: Client with ID {clientId} disconnected from server {server.Port}. Reason: {reason}", Color.red);
    }

    private void OnPacketReceivedForServer(Server server, int clientId, Server.ConnectionProtocol protocol, Packet packet)
    {
        var id = (PacketID)packet.ReadInt();
        switch (id)
        {
            case PacketID.HelloWorld:
                var message = packet.ReadString();
                Log($"Server: Received from client {clientId} on server {server.Port}: {message}", Color.cyan);
                break;
        }
    }

    private void StartClient()
    {
        string portStr = _clientPortField.text;
        string ip = _clientIpField.text;
        if (!string.IsNullOrEmpty(portStr) && ushort.TryParse(portStr, out ushort port) && !string.IsNullOrEmpty(ip))
        {
            var client = Client.CreateNewClient();
            client.ConnectToServer(ip, port);
        }
    }

    private void CreateServerItem(ushort port)
    {
        var item = Instantiate(_serverItem, _serverItemsContainer);
        var portText = item.transform.Find("TextPort").GetComponent<TextMeshProUGUI>();
        var buttons = item.GetComponentsInChildren<Button>();
        portText.text = $"Running On: {port}";
        buttons[0].onClick.AddListener(() => 
        {
            var server = Server.Get(port);
            if (server != null)
            {
                using(Packet packet = new Packet())
                {
                    packet.WriteInt((int)PacketID.HelloWorld);
                    packet.WriteString("Hello World");
                    server.SendPacketToAllClients(packet, Server.ConnectionProtocol.UDP);
                }
            }
        });
        buttons[1].onClick.AddListener(() =>
        {
            var server = Server.Get(port);
            if (server != null)
            {
                server.StopServer();
                Destroy(server.gameObject);
            }
            Destroy(item);
        });
    }

    private void CreateClientItem(string ip, ushort port)
    {
        var item = Instantiate(_clientItem, _clientItemsContainer);
        var ipText = item.transform.Find("TextIP").GetComponent<TextMeshProUGUI>();
        var buttons = item.GetComponentsInChildren<Button>();
        ipText.text = $"Connected To: {ip}:{port}";
        buttons[0].onClick.AddListener(() =>
        {
            var client = Client.Get(ip, port);
            if (client != null)
            {
                using (Packet packet = new Packet())
                {
                    packet.WriteInt((int)PacketID.HelloWorld);
                    packet.WriteString("Hello World");
                    client.SendPacket(packet, Server.ConnectionProtocol.UDP);
                }
            }
        });
        buttons[1].onClick.AddListener(() => 
        {
            var client = Client.Get(ip, port);
            if (client != null)
            {
                client.Disconnect();
                Destroy(client.gameObject);
            }
            Destroy(item);
        });
    }

    private void OnClientConnectionAttempt(Client client, bool connected, string error)
    {
        if (connected)
        {
            CreateClientItem(client.IP, client.Port);
        }
        else
        {
            Destroy(client.gameObject);
        }
    }

    private void OnClientDisconnected(Client client, string reason)
    {
        Log($"Client disconnected: {reason}", Color.red);
        for (int i = 0; i < _clientItemsContainer.childCount; i++)
        {
            var ipText = _clientItemsContainer.GetChild(i).transform.Find("TextIP").GetComponent<TextMeshProUGUI>();
            string ip = ipText.text.Split(':')[1].Trim();
            ushort port = ushort.Parse(ipText.text.Split(':')[2].Trim());
            if (ip == client.IP && port == client.Port)
            {
                Destroy(_clientItemsContainer.GetChild(i).gameObject);
                break;
            }
        }
    }

    private void OnPacketReceivedForClient(Client client, Server.ConnectionProtocol protocol, Packet packet)
    {
        var id = (PacketID)packet.ReadInt();
        switch (id)
        {
            case PacketID.HelloWorld:
                var message = packet.ReadString();
                Log($"Client: Received from server {client.IP}:{client.Port}: {message}", Color.yellow);
                break;
        }
    }

    private void ClearItems()
    {
        for (int i = _serverItemsContainer.childCount - 1; i >= 0; i--)
        {
            Destroy(_serverItemsContainer.GetChild(i).gameObject);
        }
        for (int i = _clientItemsContainer.childCount - 1; i >= 0; i--)
        {
            Destroy(_clientItemsContainer.GetChild(i).gameObject);
        }
    }

    private void Log(string message, Color color)
    {
        // Create colored message with timestamp
        string timestamp = System.DateTime.Now.ToString("HH:mm:ss");
        string coloredMessage = $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>[{timestamp}] {message}</color>";

        // Add to the beginning of the list (top of log)
        _logEntries.Insert(0, coloredMessage);

        // Limit the number of log entries
        if (_logEntries.Count > _maxLogEntries)
        {
            _logEntries.RemoveAt(_logEntries.Count - 1);
        }

        // Update the log text
        UpdateLogText();
    }

    private void UpdateLogText()
    {
        if (_logText != null)
        {
            _logText.text = string.Join("\n", _logEntries);
        }
    }

    public void ClearLog()
    {
        _logEntries.Clear();
        UpdateLogText();
        Log("Log cleared.", Color.white);
    }

}
