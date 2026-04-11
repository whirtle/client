# Whirtle Client

## Project Overview

Whirtle is a Sendspin client for Windows.

The Sendspin protocol is documented at https://www.sendspin-audio.com/spec/.

## Tech Stack

- **Language:** C#
- **Framework:** .NET (see `.gitignore` for tooling configuration)
- **Package Manager:** NuGet

## Common Commands

Once project files are in place, standard .NET CLI commands apply:

```bash
# Build the project
dotnet build

# Run the application
dotnet run

# Run tests
dotnet test

# Restore dependencies
dotnet restore
```

## Project Structure

```
client/
├── CLAUDE.md
├── LICENSE
├── .gitignore
├── Whirtle.Client.slnx
├── src/
│   └── Whirtle.Client/
│       ├── Whirtle.Client.csproj
│       ├── Program.cs
│       ├── Properties/
│       │   └── AssemblyInfo.cs
│       ├── Transport/
│       │   ├── ITransport.cs             # Transport abstraction
│       │   ├── IClientWebSocket.cs       # Internal WebSocket seam
│       │   ├── SystemClientWebSocket.cs  # Wraps ClientWebSocket
│       │   └── WebSocketTransport.cs     # WebSocket implementation
│       ├── Protocol/
│       │   ├── Message.cs                # All message records + polymorphic JSON
│       │   ├── MessageSerializer.cs      # Internal JSON encoder/decoder
│       │   ├── HandshakeException.cs     # Thrown on handshake failure
│       │   └── ProtocolClient.cs         # Handshake + send/receive over ITransport
│       ├── Clock/
│       │   ├── ISystemClock.cs           # Internal clock seam
│       │   ├── SystemClock.cs            # Production DateTime.UtcNow wrapper
│       │   ├── ClockSyncResult.cs        # Offset + RTT result record
│       │   └── ClockSynchronizer.cs      # NTP-style sync over ProtocolClient
│       └── Discovery/
│           ├── ServiceEndpoint.cs        # Discovered host + port
│           ├── IMulticastSocket.cs       # Internal UDP socket seam
│           ├── SystemMulticastSocket.cs  # Production UdpClient wrapper
│           ├── DnsMessage.cs             # DNS wire-format encoder/decoder (PTR/SRV/A)
│           └── MdnsDiscovery.cs          # mDNS PTR query + response parsing
└── tests/
    └── Whirtle.Client.Tests/
        ├── Whirtle.Client.Tests.csproj
        ├── Transport/
        │   ├── FakeClientWebSocket.cs        # Test double
        │   └── WebSocketTransportTests.cs
        ├── Protocol/
        │   ├── FakeTransport.cs              # Test double
        │   ├── MessageSerializerTests.cs
        │   └── ProtocolClientTests.cs
        ├── Clock/
        │   ├── FakeClock.cs                  # Test double
        │   └── ClockSynchronizerTests.cs
        └── Discovery/
            ├── FakeMulticastSocket.cs        # Test double
            ├── DnsMessageTests.cs
            └── MdnsDiscoveryTests.cs
```

## Development Notes

- Respect the GPLv3 license — all contributions must be compatible.
- Follow standard C# naming conventions: `PascalCase` for types and members, `camelCase` for local variables and parameters.
- Keep `bin/`, `obj/`, and other build artifacts out of version control (covered by `.gitignore`).

## C# style notes
Use _ as leading character on instance variables.

## Git Workflow

- Development happens on feature branches; merge to `main` via pull request.
- Write clear, descriptive commit messages.

## Before pushing

Run all unit tests.