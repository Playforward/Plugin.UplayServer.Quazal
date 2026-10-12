# Plugin.UplayServer.Quazal
Quazal plugin for Uplay / ServerEmus

## Local accounts

The plugin creates `Configs/UplayServer.QuazalConfig.json` on first start. Add
player accounts to the `Accounts` array; the server owns these records and does
not read a client `upc.json`.

```json
"Accounts": [
  { "Username": "Tracking", "Password": "JaDe!", "Pid": 100 },
  { "Username": "player", "Password": "server-side-password", "Pid": 101 }
]
```

For a local title, set `Username` to the account name sent by the client and
use the matching local password/key material. Every account needs a unique
non-zero PID. Keep Tracking at PID 100 so its existing telemetry flow remains
unchanged; use a different PID for the player.

The client must log in before `RequestTicket` is accepted. A ticket is sealed
with the matched account password, and secure CONNECT rejects tickets for
unconfigured PIDs.


## Playforward accounts

The server can authenticate players against
[Playforward](../../playforward) instead of the local `Accounts` array, so a
player uses one account across every title rather than a per-server login.

Off by default. A private LAN server keeps working exactly as it does today,
with local accounts and no accounts service involved.

The configuration is in two halves, because Playforward is per title: one
secret is one `title_id` there, with its own token audience, pid allocator,
blobs and scores. What is the same for every title this server hosts sits
under `Playforward`; what is each title's own sits under that title's entry
in `Titles`.

```json
"Playforward": {
  "Enabled": true,
  "PublicKeyBase64": "<the \"x\" value from /.well-known/jwks.json>",
  "Issuer": "playforward-accounts",
  "AudiencePrefix": "playforward-game-servers",
  "BaseUrl": "http://localhost:8080"
},
"Titles": {
  "farcry3": {
    "SecureServerPort": 6571,
    "Playforward": {
      "ServerSecret": "<issued once when an admin registered a server for this title>",
      "TemplateAccountUsername": "player",
      "SessionCapacity": 14,
      "LoginNameMaxLength": 16
    }
  },
  "splinter-cell-blacklist": {
    "SecureServerPort": 6573,
    "Playforward": { "ServerSecret": "<its own>", "TemplateAccountUsername": "player", "SessionCapacity": 8, "LoginNameMaxLength": 16 }
  }
}
```

The keys of `Titles` are slugs from the host's `Configs/Titles.json`, which is
where each title's ports live; listing a title here is what makes this plugin
serve it on those ports. Which service family answers a title (`farcry3`,
`overlord`) is a protocol fact, recorded once in the accounts service's admin
panel and fetched by the host on start (`TitleCatalog.Url` in
`ServerAppSettings.json`), never typed here; a title with no family, or one
this plugin has no table for, is refused at start. A config from before
`Titles` existed is migrated on first start: its flat `Ports`/`SecureServerPort`
become the entry of the title owning those ports, and the per-title Playforward
values move into the title the old `Audience` named.

`RequireConnectionSignature` (top-level, default `true`) is what makes a leaked
endpoint useless to an off-path attacker; see *What a leaked address does not
buy*. Leave it on outside of diagnosing an old capture.

`BaseUrl` and each title's `ServerSecret` are required whenever Playforward
login is on, not only for session reporting: a player's PID and their saved
progress live in Playforward (see [Saved progress](#saved-progress)), and both
are reached with them. A title whose secret is empty is served without
Playforward (its players log in locally) rather than under another title's.

`PublicKeyBase64` is the raw 32-byte Ed25519 key, copied straight out of
Playforward's `/.well-known/jwks.json` — base64url, unpadded, exactly as that
endpoint prints it. Not a PEM.

The audience is **per title** and derived, never typed: `<AudiencePrefix>:<title_id>`,
exactly as Playforward mints it, so a token a player minted to join one game
is refused by every other title's server. Deriving it is what makes it
impossible to point two titles at one audience.

The key is configured rather than fetched so a server on a LAN with no route to
Playforward still verifies the tokens its players bring with them. The cost is
that a key rotation means editing every server by hand.

### How a player gets in

```
desktop client                     this server                    Playforward
     |                                  |                              |
     |-- POST /me/game-token -----------|----------------------------->|
     |<-- short-lived signed token -----|------------------------------|
     |                                  |
     |-- POST /playforward/pre-register |
     |     {"token": "..."}             |
     |                            verify signature, issuer,
     |                            audience, expiry, and that
     |                            this jti has not been used
     |<-- {"code": "...", "expires_in_seconds": n}
     |
     |-- (local pipe) --> patched game
     |                                  |
     |          game sends the code as its RendezVous username
     |                                  |
     |                            resolve code -> account -> stable PID
     |                            login proceeds exactly as before
```

The game never handles a token and performs no cryptography.

There are three ways the identity reaches a login. **Pre-bind is the one
proven with a real Far Cry 3 session**; the launch proof is what replaces it
once the shim speaks it:

- **Pre-bind by address** (`POST /playforward/pre-bind`). The client binds this
  machine's IP to the account ahead of the game connecting; the unmodified game
  then logs in from that IP and is recognised, with no code travelling through
  it. FC3 shows the `-login` launch value as the player's name, so it is set to
  the player's display name (`-login "Jason Brody"`), and the server **enforces**
  that the name matches the token-verified display name
  (`EnforceLoginName`, default on) — refusing a mismatch, which is what prevents
  impersonation. The name FC3 shows is the account's unique **console handle**
  (≤15 chars) when set, else the display name truncated to `LoginNameMaxLength`
  (default 16 — FC3's cap, confirmed by probing). The handle is what makes the
  in-game name both short enough and unique. Limitation: the binding is
  IP-based, so it cannot separate players behind one NAT.
- **Correlation code in the login** (`POST /playforward/pre-register`). The code
  arrives as the RendezVous username itself. No NAT limitation, but it needs the
  code injected into the login, which FC3's static launch options do not allow.
- **A launch proved on the game's socket** (`POST /playforward/pre-launch`).
  The client presents the token and gets back a launch: a public `id` and a
  secret `key`, which it hands the game's shim over a local pipe. Before the
  game logs in, the shim -- on the game's own UDP socket -- sends `HELLO id`,
  gets a random challenge back, and answers with
  `HMAC-SHA256(key, "playforward-launch-proof" | id | challenge)`. The server
  binds the **socket** the proof came from, and LoginEx arrives from that same
  socket. A NAT rewrites both the same way, so two players behind one address
  are two bindings; and nothing secret ever crosses the network -- a proof
  read off the wire answers a challenge that will never be issued again, and
  is refused from any socket but the one challenged. The username is still the
  player's name and still enforced. Wire layout in `Playforward/LaunchEnvelope.cs`;
  a launch stays provable for `LaunchTtlSeconds` (default 8 hours) so a
  reconnect mid-session re-proves with the same key.

  The key crosses the `pre-launch` response, so that endpoint must be served
  over TLS anywhere that is not a single trusted LAN. Nothing in this plugin
  can check that; it is the one deployment fact the design leans on.

A login from a socket looks up the socket's own binding first and falls back
to its address, so a game that proved its launch is never mistaken for
whichever machine behind the same address pre-bound last.

See `docs/END-TO-END-TESTING.md` for the full working setup and run steps.

A correlation code is single-use and expires in
`CorrelationCodeTtlSeconds` (default 120). A token is single-use too, across
all three endpoints: its `jti` is remembered until it expires, so a token
captured in flight cannot mint a second code, bind a second address, or issue a
second launch beside the real player.

### What a leaked address does not buy

An identity here is keyed to a source address (pre-bind) or address-and-port
(a proved launch), and the game's login carries only a name. So the obvious
worry is that anyone who learns a player's endpoint -- a leaked list of them,
say -- could send packets as that endpoint and be taken for the player.

They cannot, without being able to read that player's traffic live. Every
packet after the opening SYN must echo the **connection signature** the server
issued in its SYN-ACK (`RequireConnectionSignature`, default on). That value is
fresh random per session and is sent only to the real player; a packet that
does not carry it is dropped as an unknown session would be. So forging a
player's session needs the SYN-ACK, which means being on the path between them
and the server -- a sniffer on the same segment. That is the floor this game's
transport allows: PRUDP V0 has no session encryption, so anyone who can read
the live traffic can replay it, and no server-side check changes that. What is
ruled out is the cheap, remote version: knowing an endpoint without seeing its
traffic is worth nothing.

Two consequences of the same limit are worth stating plainly. An attacker with
full control of the player's own machine is already that player -- outside what
any of this defends. And a launch key reaches the game's shim over a local pipe
and is proven without being sent, so it is not on the wire to sniff in the
first place; the residual live-sniff risk is against the *session*, not the
launch.

### Session reporting

The server can also call *into* Playforward: registering the matches running on
it, saying who is in them, and collecting the moderation commands it must
enforce. Off by default, and separately from the login half above — a LAN server
can want one account across every title without appearing in a public directory.

```json
"Playforward": {
  "ReportingEnabled": true,
  "BaseUrl": "http://localhost:8080",
  "ReportIntervalSeconds": 15,
  "EnforceBans": true,
  "SeatPartiesTogether": true
},
"Titles": {
  "farcry3": {
    "Playforward": { "ServerSecret": "<this title's>", "ServerLabel": "Test FC3", "SessionCapacity": 14 }
  }
}
```

Each title reports through its own secret, under its own label and lobby
size, and only its own matches: a Blacklist session on the same host is
registered by the Blacklist title's reporter, under the Blacklist title, with
a Blacklist pid. An admin registers a server per title and is shown each
secret once:

```sh
curl -sX POST <playforward>/api/v1/admin/gameservers \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d '{"title_id":"farcry3","name":"Test FC3","region":"lan","operator":"<username>"}'
```

Registering is an admin's act rather than self-service: a listing is Playforward
vouching for a server — quick match sends strangers at it — and it makes the
named operator a moderator of it. That operator moderating their own server with
no further grant is the "federate, don't absorb" bargain made concrete.

What it buys, once on:

- The server's matches appear in quick match and the server browser.
- Every player's client shows the real lobby roster, 1:1 with the game's own
  session, because the roster is pushed from here.
- Moderators can kick, ban and mute in a live match, and players can report and
  vote-kick — all of which arrive here as signed commands.
- A ban outlives the reconnect it was issued about, because the login path asks
  Playforward whether an account is barred. In Far Cry 3 reconnecting is one
  keypress, so without this a kick lasts about five seconds.
- Party members are seated into the same match: a token carries a `party_ref`,
  and this server answers their ordinary `SearchSessions` with the session
  another member is already hosting. The game is untouched; only the answer to
  its own search changes.

Every push is the whole picture — registration is idempotent on this server's
own session id, and a roster push is "these, and only these" — so a failed pass
is repaired by the next one rather than leaving anything to reconcile. A change
nudges the loop to run sooner rather than sending anything itself.

Commands come back **signed**, with the same key already held to verify player
tokens. That is not redundant with the server's own secret: the secret proves to
Playforward who is calling, the signature proves to this server that "remove
this player" came from Playforward — which is the direction where a forgery does
damage.

An unreachable Playforward never stops a match: calls are logged at debug and
retried on the next pass, and a login is allowed rather than refused when the
ban check cannot be answered. Turning an accounts outage into an outage of the
game would be the wrong trade for a server whose point is that a LAN party keeps
working.

`docs/END-TO-END-TESTING.md` has the full setup and what to look for in the log.

### PIDs

An account's PID is allocated by Playforward, per title, on the account's
first login to that title, and never changes. A title keys everything it
stores by principal id, so an account that came back with a different PID
would come back as a stranger — and with more than one server of a title
behind quick match, "came back" includes arriving at the *other* server. That
is why the allocation is not this server's: every server of the title asks
`POST /api/v1/gameservers/principals` and gets the same answer.

This server keeps a copy for the session. If Playforward becomes unreachable,
an account resolved earlier this session still logs in from that copy; an
account never seen this session is refused with the reason logged, because a
PID this server made up would differ from Playforward's and split the
player's stats.

The floor is Playforward's `PLAYER_DATA_PID_START` (default 2,000,000), well
clear of the configured local accounts and the dynamic LAN range, so both
identity schemes coexist on one server. (The JSON map this replaced,
`Configs/UplayServer.QuazalPlayers.db`, is no longer read; its PIDs were per
server and cannot be carried over.)

## Saved progress

Everything a player builds up online — the stats, unlocks, challenge state and
wall messages Far Cry 3 stores through its online-data service — is kept by
**Playforward's player-data store**, keyed by title and PID, with the PID tied
to the account. Two servers of the title see the same player; a server
restart loses nothing; and the player's account is the thing it all hangs off.

The contract is Playforward's game-server API (documented in
`playforward/docs/session-sync-and-moderation.md`), and it is
title-agnostic: opaque bytes under a namespace and key. FC3's protocol 106
rows go under `onlinedata/<data id>` as the exact RowSet bytes the client
uploaded — the same reasoning as `OnlineDataColumn` copying flag bytes rather
than building them; the learned column vocabulary is title-wide state under
`onlinedata-columns`; wall message ids come from a title-wide counter so two
servers never issue the same one. Another Quazal title stores its own
protocol's values through the same surface under a namespace of its own.

**How the traffic looks.** A player's rows are loaded once, on first touch
after they log in, and served from this server's memory after that; every
store is written through; the rows are dropped when their binding ends. So
the wall poll every few seconds costs Playforward nothing, and the load it
does see is proportional to sessions, not to time online. An unreachable
Playforward degrades to memory for the session — a value that could not be
saved is still served, a player that could not be loaded is retried after a
pause rather than on every poll — and never stops a match.

**What persists.** A Playforward principal's rows. A configured `Accounts`
entry is a server-local identity Playforward has never heard of, and a
dynamic LAN PID (`AssignPlayerPidByAddress`) is a join-order number that means
somebody else after a restart; both keep their rows in memory for the
session, exactly as before.

### Resetting or deleting a player

Both are Playforward's operations, because the data is Playforward's:

- **Reset** (`DELETE /api/v1/me/titles/farcry3/progress`, or an operator's
  `DELETE /api/v1/gameservers/players/{account}/progress`) wipes the values.
  The player keeps their PID and comes back with fresh stats.
- **Delete** (`…/player`, `…/principal`) removes the values *and* the
  principal. The next login allocates a new PID; the old one is retired and
  never reissued. Deleting the account itself cascades the same way.

If the player is in a match on this title when they reset, Playforward tells
the server they are on with a signed `reset_progress` / `delete_player`
command on the same channel as kick and ban. This server's part is to drop
the session's copy (and, on delete, to end the binding and remove them from
the match) — because a client still connected would otherwise keep reading,
and possibly re-uploading, what was deleted. Both take effect for the
player's next session in any case.

### Legality

Far Cry 3's protocol audit is in
[`playforward/docs/title-protocol-audits.md`](../../playforward/docs/title-protocol-audits.md).
It sits in the **middle tier** of the §1201 test in
`playforward/docs/1201-compliance-guide.md`, which calls for attorney review
before anything in that tier is operated publicly. Building it is not the thing
that guide gates; running it for the public is.
