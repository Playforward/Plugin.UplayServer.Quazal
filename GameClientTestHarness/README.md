# Game client test harness

Stands in for the patched Far Cry 3 binary so the whole Playforward login chain
can be run without the Windows game. It does exactly what the patch will do:
obtain a Playforward correlation code and use it as the RendezVous username.

Not shipped. It exists to test the integration.

## selftest runs anywhere, self-contained

Everything in one process: start a Playforward gateway, obtain a code, and log
in through the real server login path (real PRUDP framing and RMC via the
server's own serializer, real `FindPlayforwardAccount` resolution). No Windows,
nothing else running required.

```sh
dotnet run --project GameClientTestHarness -- selftest
```

With a real Playforward instead of a self-signed token — proves the actual
account API, consent record and token minting, not just the game-server half:

```sh
dotnet run --project GameClientTestHarness -- selftest \
  --playforward-url http://localhost:8080 --account NAME --password PW
```

The account must have enabled the title first
(`PUT /api/v1/me/titles/farcry3`), or the game-token request is refused — which
is the per-title consent gate doing its job.

**What it does not cover:** the desktop client's HTTP hop and the named-pipe
handoff. Those are the bridge mode's job, and each has its own tests
(`GameSessionServiceTests`, `GameBridgeTests`). The seam this covers is the one
that otherwise had no end-to-end test: a real token becoming a logged-in player
with a stable PID.

## bridge the true end-to-end, against running services

Asks a real desktop-client `GameBridge` for a code (which drives the client ->
Playforward -> Quazal HTTP path for real), then logs in over a UDP socket to a
running Quazal server. Needs the desktop client host and the Quazal server
actually up, so it is not self-contained. It is the check to run once they are,
including on Windows later with this harness in place of the real game.

```sh
dotnet run --project GameClientTestHarness -- bridge \
  --pipe playforward-game-bridge --server 127.0.0.1:61455
```

## What the eventual FC3 binary patch must do

The harness is the executable specification of the patch. On the wire the patch
has to produce exactly what `LoginExClient` produces, and its only new
behaviour over stock FC3 is where the username comes from:

1. Connect to the named pipe `playforward-game-bridge`.
2. Write the title id and a newline (`farcry3\n`).
3. Read one line: the correlation code, or `ERROR <reason>`.
4. Use that code as the RendezVous username in LoginEx — the field a hardcoded
   username already flows into. Nothing else about the game's networking
   changes; the game handles no token and does no cryptography.
