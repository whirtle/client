# Whirtle Client

## Project Overview

Whirtle is a Sendspin client for Windows.

The Sendspin protocol is documented at https://www.sendspin-audio.com/spec/.

## Tech Stack

- **Language:** C#
- **Framework:** .NET (see `.gitignore` for tooling configuration)
- **Package Manager:** NuGet
- Target OS is Windows 11

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
│   ├── Whirtle.Client/
│   │   ├── Whirtle.Client.csproj
│   │   ├── Program.cs
│   │   ├── AppLogger.cs
│   │   ├── Properties/
│   │   │   └── AssemblyInfo.cs
│   │   ├── Audio/
│   │   │   ├── IAudioDeviceEnumerator.cs
│   │   │   ├── AudioDeviceEnumerator.cs
│   │   │   ├── AudioDeviceInfo.cs
│   │   │   ├── AudioDeviceKind.cs
│   │   │   ├── NullAudioDeviceEnumerator.cs
│   │   │   └── WindowsAudioDeviceEnumerator.cs
│   │   ├── Clock/
│   │   │   ├── ISystemClock.cs           # Internal clock seam
│   │   │   ├── SystemClock.cs            # Production DateTime.UtcNow wrapper
│   │   │   ├── ClockSyncResult.cs        # Offset + RTT result record
│   │   │   └── ClockSynchronizer.cs      # NTP-style sync over ProtocolClient
│   │   ├── Codec/
│   │   │   ├── IAudioDecoder.cs
│   │   │   ├── AudioDecoderFactory.cs
│   │   │   ├── AudioFormat.cs
│   │   │   ├── AudioFrame.cs
│   │   │   ├── FlacAudioDecoder.cs
│   │   │   ├── OpusAudioDecoder.cs
│   │   │   └── PcmAudioDecoder.cs
│   │   ├── Discovery/
│   │   │   ├── ServiceEndpoint.cs        # Discovered host + port
│   │   │   ├── IMulticastSocket.cs       # Internal UDP socket seam
│   │   │   ├── SystemMulticastSocket.cs  # Production UdpClient wrapper
│   │   │   ├── DnsMessage.cs             # DNS wire-format encoder/decoder (PTR/SRV/A)
│   │   │   └── MdnsAdvertiser.cs         # mDNS advertisement
│   │   ├── Playback/
│   │   │   ├── IWasapiRenderer.cs
│   │   │   ├── WasapiRenderer.cs
│   │   │   ├── PlaybackEngine.cs
│   │   │   ├── PlaybackState.cs
│   │   │   ├── JitterBuffer.cs
│   │   │   └── SampleInterpolator.cs
│   │   ├── Protocol/
│   │   │   ├── Message.cs                # All message records + polymorphic JSON
│   │   │   ├── MessageSerializer.cs      # Internal JSON encoder/decoder
│   │   │   ├── HandshakeException.cs     # Thrown on handshake failure
│   │   │   ├── ProtocolClient.cs         # Handshake + send/receive over ITransport
│   │   │   ├── ConnectionManager.cs
│   │   │   └── IncomingFrame.cs
│   │   ├── Transport/
│   │   │   ├── ITransport.cs             # Transport abstraction
│   │   │   ├── IClientWebSocket.cs       # Internal WebSocket seam
│   │   │   ├── SystemClientWebSocket.cs  # Wraps ClientWebSocket
│   │   │   ├── WebSocketTransport.cs     # WebSocket implementation
│   │   │   ├── AcceptedWebSocket.cs
│   │   │   └── WebSocketListener.cs
│   │   ├── role.artwork/
│   │   │   └── ArtworkReceiver.cs
│   │   ├── role.controller/
│   │   │   └── ControllerClient.cs
│   │   └── role.metadata/
│   │       └── NowPlayingState.cs
│   └── Whirtle.Client.UI/
│       ├── Whirtle.Client.UI.csproj
│       ├── App.xaml
│       ├── App.xaml.cs
│       ├── MainWindow.xaml
│       ├── MainWindow.xaml.cs
│       ├── LogsWindow.xaml
│       ├── LogsWindow.xaml.cs
│       ├── SettingsWindow.xaml
│       ├── SettingsWindow.xaml.cs
│       ├── Converters/
│       │   └── SecondsToTimeConverter.cs
│       ├── Logging/
│       │   ├── InMemorySink.cs
│       │   └── LogEntry.cs
│       ├── Pages/
│       │   ├── LogsPage.xaml
│       │   ├── LogsPage.xaml.cs
│       │   ├── NowPlayingPage.xaml
│       │   ├── NowPlayingPage.xaml.cs
│       │   ├── SettingsPage.xaml
│       │   └── SettingsPage.xaml.cs
│       └── ViewModels/
│           ├── ConnectionMode.cs
│           ├── LogsViewModel.cs
│           ├── NowPlayingViewModel.cs
│           └── SettingsViewModel.cs
└── tests/
    ├── Whirtle.Client.Tests/
    │   ├── Whirtle.Client.Tests.csproj
    │   ├── Audio/
    │   │   ├── AudioDeviceEnumeratorTests.cs
    │   │   └── FakeAudioDeviceEnumerator.cs
    │   ├── Clock/
    │   │   ├── FakeClock.cs                  # Test double
    │   │   └── ClockSynchronizerTests.cs
    │   ├── Codec/
    │   │   ├── AudioDecoderContractTests.cs
    │   │   └── PcmAudioDecoderTests.cs
    │   ├── Discovery/
    │   │   ├── FakeMulticastSocket.cs        # Test double
    │   │   ├── DnsMessageTests.cs
    │   │   └── MdnsAdvertiserTests.cs
    │   ├── Playback/
    │   │   ├── FakeWasapiRenderer.cs         # Test double
    │   │   ├── JitterBufferTests.cs
    │   │   ├── PlaybackEngineTests.cs
    │   │   └── SampleInterpolatorTests.cs
    │   ├── Protocol/
    │   │   ├── FakeTransport.cs              # Test double
    │   │   ├── MessageSerializerTests.cs
    │   │   ├── ProtocolClientTests.cs
    │   │   ├── ConnectionManagerTests.cs
    │   │   └── ReceiveAllAsyncTests.cs
    │   ├── Transport/
    │   │   ├── FakeClientWebSocket.cs        # Test double
    │   │   └── WebSocketTransportTests.cs
    │   ├── role.artwork/
    │   │   └── ArtworkReceiverTests.cs
    │   ├── role.controller/
    │   │   └── ControllerClientTests.cs
    │   └── role.metadata/
    │       └── NowPlayingStateTests.cs
    └── Whirtle.Client.IntegrationTests/
        ├── Whirtle.Client.IntegrationTests.csproj
        ├── SendspinIntegrationTests.cs
        └── SendspinServerFixture.cs
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
- The `gh` CLI is not available. Push branches with `git push -u origin <branch>` and open a PR at:
  https://github.com/whirtle/client/compare/<branch>?expand=1

## Before pushing

Run all unit tests.