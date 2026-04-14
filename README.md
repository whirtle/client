# Whirtle

A [Sendspin](https://www.sendspin-audio.com/spec/) client for Windows.

Whirtle connects to Sendspin audio servers on your local network and plays back streamed audio with low-latency, clock-synchronized playback. It ships as both a full WinUI desktop app and a CLI harness.

## Features

- **Audio playback** — Opus, FLAC, and PCM decoding via a jitter-buffered, clock-synchronized pipeline
- **mDNS discovery** — Advertises itself on the local network so Sendspin servers can find and connect to it automatically
- **Now-playing metadata** — Displays track title, artist, album, duration, and playback position
- **Album artwork** — Receives and displays binary artwork from the server
- **Remote control** — Accepts play, pause, skip, and volume commands from the server
- **Audio device selection** — Enumerates and selects Windows audio output devices
- **System tray** — Minimize to tray with quick restore
- **Firewall integration** — Prompts to add a Windows Firewall rule on first launch (required for server-initiated connections)
- **Modern UI** — Dark theme, Mica backdrop, WinUI 3

## Requirements

- Windows 10 (build 17763) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (build only)
- x86, x64, or ARM64

The distributed application bundles the Windows App SDK runtime — no separate runtime install needed.

## Building

```bash
git clone <repo-url>
cd whirtle/client
dotnet restore
dotnet build
```

### Running the desktop app

```bash
cd src/Whirtle.Client.UI
dotnet run
```

### Running the CLI harness

```bash
cd src/Whirtle.Client
dotnet run -- [--name <friendly-name>] [--port <port>]
```

| Flag | Default |
|------|---------|
| `--name` | `Whirtle (<hostname>)` |
| `--port` | `8928` |

#### CLI commands (interactive prompt)

| Command | Action |
|---------|--------|
| `play` | Resume playback |
| `pause` | Pause playback |
| `skip` | Skip to next track |
| `volume <0–100>` | Set volume |
| `status` | Show current metadata and artwork |
| `quit` / `exit` | Exit |

### Running tests

```bash
dotnet test
```

## Project structure

```
client/
├── src/
│   ├── Whirtle.Client/        # Core library + CLI entry point
│   │   ├── Audio/             # Device enumeration
│   │   ├── Clock/             # NTP-style clock sync
│   │   ├── Codec/             # Opus, FLAC, PCM decoders
│   │   ├── Discovery/         # mDNS advertisement
│   │   ├── Playback/          # Jitter buffer + WASAPI renderer
│   │   ├── Protocol/          # Sendspin protocol client
│   │   ├── Transport/         # WebSocket transport
│   │   └── role.*/            # Metadata, controller, artwork, player roles
│   │
│   └── Whirtle.Client.UI/     # WinUI 3 desktop application
│       ├── Pages/             # NowPlaying, Settings, Logs
│       └── ViewModels/        # MVVM view models
│
└── tests/
    ├── Whirtle.Client.Tests/               # Unit tests (xUnit)
    └── Whirtle.Client.IntegrationTests/    # Integration tests
```

## Protocol

Whirtle implements the [Sendspin protocol spec](https://www.sendspin-audio.com/spec/) and supports both connection modes:

- **Client-initiated** — Whirtle connects outbound to a known server
- **Server-initiated** — Whirtle advertises via mDNS (`_sendspin._tcp.local.`) and listens for incoming WebSocket connections on `/sendspin` (default port `8928`)

## License

GNU General Public License v3.0 — see [LICENSE](LICENSE).
