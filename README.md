# MeshAI

A hybrid mesh-server file and message network for decentralized-friendly communication.

## Overview

MeshAI is a networking library that provides a hybrid topology combining the benefits of centralized servers with peer-to-peer mesh networking. It enables secure message and file transfer between clients using:

- **Hybrid Topology**: Star topology to server when reachable, mesh topology between clients otherwise
- **Hardware-Derived Identity**: Client IDs derived from hardware fingerprints (CPU, motherboard, MAC addresses)
- **Three-Tier Routing**: Direct P2P, relay through other clients, or server-mediated
- **End-to-End Encryption**: AES-256-GCM encryption with ECDH key exchange
- **File Transfer**: Chunked transfers with integrity verification via Merkle trees

## Architecture

```
                    ┌─────────────┐
                    │   Server    │
                    │  (Central)  │
                    └──────┬──────┘
                           │
           ┌───────────────┼───────────────┐
           │               │               │
      ┌────▼────┐     ┌────▼────┐     ┌────▼────┐
      │ Client  │◄────► Client  │◄────► Client  │
      │  Alice  │     │   Bob   │     │ Charlie │
      └─────────┘     └─────────┘     └─────────┘
                 (Mesh connections)
```

### Routing Priority

1. **Direct Peer-to-Peer**: Clients on the same subnet connect directly
2. **Relay**: Messages forwarded through relay-capable clients
3. **Server-Mediated**: Last resort when no direct path exists

## Project Structure

```
MeshAI/
├── Core/
│   ├── Configuration/   # Network configuration
│   ├── Crypto/          # Encryption, key exchange, hashing
│   ├── Identity/        # Hardware fingerprinting, client identity
│   └── Logging/         # Logging infrastructure
├── Network/
│   ├── Protocol/        # Message types, serialization, framing
│   ├── Routing/         # Route tables, message routing
│   └── Transport/       # TCP connections, listeners
├── Server/              # Mesh server implementation
├── Client/              # Mesh client and peer management
├── Transfer/            # File chunking and transfer management
├── MeshAI.Demo/         # Demonstration application
└── MeshAI.Tests/        # Unit and integration tests
```

## Getting Started

### Prerequisites

- .NET 10.0 SDK or later

### Building

```bash
dotnet build
```

### Running the Demo

The demo creates a server and three clients (Alice, Bob, Charlie) that exchange messages:

```bash
cd MeshAI.Demo
dotnet run
```

For verbose logging:

```bash
dotnet run -- --verbose
```

### Running as Server

```bash
dotnet run --project MeshAI -- server --port 9500
```

Options:
- `--port <port>`: Port to listen on (default: 9500)
- `--verbose`: Enable debug logging

### Running as Client

```bash
dotnet run --project MeshAI -- client --server 192.168.1.100:9500
```

Options:
- `--server <host:port>`: Server address to connect to
- `--port <port>`: Local listening port (default: 9500)
- `--relay`: Enable relay capability for other clients
- `--verbose`: Enable debug logging

### Identity Information

Display the hardware-derived identity:

```bash
dotnet run --project MeshAI -- identity
```

## Key Components

### Client Identity

Each client has a unique identity derived from hardware characteristics:

```csharp
var identity = ClientIdentity.Generate();
Console.WriteLine($"Client ID: {identity.ClientId}");
Console.WriteLine($"Short ID: {identity.ShortId}");
```

The identity is deterministic - the same hardware always produces the same ID.

### Message Exchange

```csharp
// Create and start client
var config = new NetworkConfig
{
    ServerEndPoint = new IPEndPoint(IPAddress.Parse("192.168.1.100"), 9500),
    ListenPort = 9501,
    CanRelay = true
};

await using var client = new MeshClient(config);

// Handle incoming messages
client.MessageReceived += async (sourceId, data) =>
{
    var text = Encoding.UTF8.GetString(data);
    Console.WriteLine($"From {sourceId}: {text}");
};

await client.StartAsync();

// Send a message
await client.SendMessageAsync(targetClientId, Encoding.UTF8.GetBytes("Hello!"));
```

### Server

```csharp
var serverConfig = NetworkConfig.CreateServerConfig(9500);
await using var server = new MeshServer(serverConfig);
server.Start();

// Server runs until stopped
Console.WriteLine($"Clients connected: {server.Registry.ConnectedCount}");
Console.WriteLine($"Relay nodes: {server.Registry.RelayCount}");

await server.StopAsync();
```

## Security

### Encryption

- **Payload Encryption**: AES-256-GCM authenticated encryption
- **Key Exchange**: ECDH using NIST P-256 curve
- **Key Derivation**: HKDF for deriving session keys
- **Hardware Binding**: Optional encryption bound to hardware fingerprint

### Identity

Client IDs are SHA-256 hashes of hardware fingerprints containing:
- CPU identifier
- Motherboard UUID
- Primary network adapter MAC address

This provides deterministic identity without requiring account registration.

## Protocol

### Message Format

```
┌────────────────────────────────────┐
│           Message Header           │
│            (32 bytes)              │
├────────────────────────────────────┤
│             Payload                │
│          (variable size)           │
└────────────────────────────────────┘
```

Header fields:
- Version (1 byte)
- Message Type (1 byte)
- Flags (2 bytes)
- Message ID (4 bytes)
- Timestamp (8 bytes)
- Payload Length (4 bytes)
- Checksum (4 bytes)
- Reserved (8 bytes)

### Message Types

| Type | Name | Description |
|------|------|-------------|
| 0x01 | Register | Client registration request |
| 0x02 | RegisterAck | Server acknowledgment |
| 0x10 | PeerListRequest | Request peer list |
| 0x11 | PeerListResponse | Peer list response |
| 0x20 | DirectMessage | Direct P2P message |
| 0x30 | RelayRequest | Request relay route |
| 0x31 | RelayData | Relayed data packet |
| 0x40 | FileTransferInit | Initiate file transfer |
| 0x41 | FileChunk | File data chunk |
| 0x42 | FileTransferAck | Chunk acknowledgment |
| 0xF0 | Ping | Heartbeat ping |
| 0xF1 | Pong | Heartbeat response |
| 0xFF | Error | Error message |

## File Transfer

File transfers use chunked transmission with integrity verification:

1. **Chunking**: Files split into configurable chunk sizes (default 64KB)
2. **Hashing**: Each chunk hashed with SHA-256
3. **Merkle Tree**: Root hash computed for integrity verification
4. **Acknowledgment**: Each chunk acknowledged before sending next
5. **Resume**: Transfers can resume from last acknowledged chunk

## Testing

Run all tests:

```bash
dotnet test
```

Run unit tests only:

```bash
dotnet test --filter "FullyQualifiedName~Unit"
```

Run integration tests only:

```bash
dotnet test --filter "FullyQualifiedName~Integration"
```

## Configuration

### NetworkConfig Options

| Property | Default | Description |
|----------|---------|-------------|
| ServerEndPoint | null | Server address for clients |
| ListenPort | 9500 | Port to listen for connections |
| CanRelay | false | Allow relaying for other clients |
| MaxConnections | 100 | Maximum concurrent connections |
| ConnectionTimeout | 30s | Connection attempt timeout |
| HeartbeatInterval | 30s | Heartbeat ping interval |
| HeartbeatTimeout | 90s | Connection timeout if no heartbeat |
| MaxMessageSize | 16MB | Maximum message payload size |
| ChunkSize | 64KB | File transfer chunk size |

## Connection States

```
Disconnected ──► Connecting ──► KeyExchange ──► Connected
                     │              │              │
                     ▼              ▼              ▼
                  Failed ◄────── Failed ◄───── Disconnecting
```

- **Disconnected**: No connection
- **Connecting**: TCP connection in progress
- **KeyExchange**: ECDH key exchange in progress
- **Connected**: Ready for communication
- **Disconnecting**: Graceful shutdown in progress
- **Failed**: Connection error occurred

## License

[Add your license here]
