# Lab 6: realtime updates

Live auctions: bidders bid, and everyone watching a lot should see each new price within a moment. The baseline makes watchers poll: "any bids after 41?" every second. It works, at a price. 2,000 watchers sent 60,000 requests in 30 seconds to learn about 151 bids, and still saw each bid half a second late. The five stages push bids to watchers instead, then make that work across two app instances and across dropped connections.

| Stage | Technique | What it costs you | Your code |
|---|---|---|---|
| 1 | Long polling | a request held open per watcher; a race between "read" and "wait" | `Live/LongPoll.cs` |
| 2 | Server-Sent Events | one open response per watcher; server-to-client only | `Live/BidStream.cs` |
| 3 | WebSocket | a stateful connection both ways; your own message protocol | `Live/BidSocket.cs` |
| 4 | Redis pub/sub between instances | another moving part; messages sent while nobody listens are gone | `Bus/RedisBidBus.cs` |
| 5 | Resume with `Last-Event-ID` | a replay from the database on every reconnect | `Live/BidStream.cs` again |

Everything else (bidding, the polling endpoint, the in-process bus, endpoints, tests, k6) is written. The stubs throw `NotImplementedException`, so stages 1-5 start red. Four baseline tests are green and must stay green.

## The API

| | |
|---|---|
| `GET /auctions/{id}` | the auction: current price, leader |
| `POST /auctions/{id}/bids` `{"bidderId", "amount"}` | 201 with the bid; 409 if it does not beat the current price |
| `GET /auctions/{id}/bids?after=41` | the baseline poll: bids after 41, at once |
| `GET /auctions/{id}/bids?after=41&waitSeconds=25` | a long poll (stage 1) |
| `GET /auctions/{id}/stream` | Server-Sent Events (stages 2 and 5); `Last-Event-ID` header or `?after=` to resume |
| `GET /auctions/{id}/ws` | WebSocket (stage 3), messages in `Live/Messages.cs` |
| `GET /stats`, `POST /stats/reset` | requests by kind, open connections and subscriptions, delivery latency |

Delivery latency is the time from a bid's `placed_at` to the moment the app writes it to a watcher.

## Setup

```
cd ~/code/system-design-patterns/06-realtime
docker compose up -d --wait                     # postgres :55932, redis :56879
dotnet run -c Release --project src/Realtime    # applies migrations, listens on :5600
docker compose exec postgres psql -U postgres -d auctions -f /db/seed.sql -c "CALL seed(10)"
```

Watch an auction from a terminal while you bid from another:

```
curl -N localhost:5600/auctions/1/stream                                   # stage 2 on
curl -s localhost:5600/auctions/1/bids -H 'Content-Type: application/json' -d '{"bidderId": 1, "amount": 150}'
```

Change behavior with environment variables, no code edits: `Realtime__Bus=Redis` (stage 4), `Realtime__HeartbeatSeconds=5`, `Realtime__MaxWaitSeconds=30`. All settings are in `src/Realtime/appsettings.json` and `Options.cs`.

Tests run on their own containers (Testcontainers), not the compose stack. Each test gets its own database, its own Redis channel prefix, and a fake clock. Stage 3's tests run the app on Kestrel with real sockets; the others use the in-memory test server.

```
dotnet test                                # everything, about 30 s
dotnet test --filter-namespace "*Stage2"   # one stage
```

Load test (`k6/watch.js` lists every setting): watchers follow 10 auctions while bidders place 5 bids a second.

```
k6 run -e MODE=poll k6/watch.js                  # 200 watchers polling every second
k6 run -e MODE=sse -e WATCHERS=2000 k6/watch.js
```

It prints bids placed, bids the watchers saw (each bid should reach every watcher of its auction, about 20 per bid with the defaults), and the watchers' instance's stats.

## How to run each stage

1. Answer the "before you code" questions in writing. Predict the numbers.
2. Run the k6 command for the stage on the current code and write the result in the table at the bottom.
3. Write the code until the stage's tests pass.
4. Run the same k6 command again. Compare with your prediction.
5. Try the mutation listed: break your code on purpose and check a test goes red.

---

## Stage 1: long polling

**Before you code**

1. Run `k6 run -e MODE=poll k6/watch.js`. How many requests did the app answer, and how many carried a bid? What is the average delay between a bid and a watcher seeing it, and why that number?
2. A long poll holds the request until there is news. What does each waiting request hold on the server (a thread? a connection? memory?) in ASP.NET Core? What about in a thread-per-request server?
3. You read the database, find nothing, then start waiting. A bid commits between those two steps. What happens to it? What order fixes that?

**Do**

- `Live/LongPoll.cs`. The class comment lists what the tests check.
- `dotnet test --filter-namespace "*Stage1"`.
- `k6 run -e MODE=longpoll k6/watch.js`. Compare requests and latency with polling.

**Mutation:** subscribe after reading the database instead of before. Which test catches it? Then time the wait with the real clock instead of the `TimeProvider`.

**Afterwards:** proxies and load balancers close idle requests (nginx in lab 5 used 30 s). What should `MaxWaitSeconds` be, and what does the client do when a long poll comes back empty?

---

## Stage 2: Server-Sent Events

**Before you code**

1. A long poll costs a request per bid per watcher. What does an SSE stream cost per bid?
2. A proxy between you and the client buffers responses until they finish. What does the watcher see? Which headers ask proxies not to (`Cache-Control`, nginx's `X-Accel-Buffering`)?
3. Nobody bids for 10 minutes. How do the client and every proxy in between know the stream is still alive, and how does the server find out the client left?

**Do**

- Stage 2's part of `Live/BidStream.cs`.
- `dotnet test --filter-namespace "*Stage2"`.
- `curl -N localhost:5600/auctions/1/stream`, bid from another terminal, and wait for a heartbeat. Then `k6 run -e MODE=sse k6/watch.js`.

**Mutation:** leave out the flush before waiting for the first bid. Which tests catch it, and what would a browser's `EventSource` see?

**Afterwards:** browsers allow 6 HTTP/1.1 connections per host. A user opens 7 auction tabs. What happens, and what changes under HTTP/2?

---

## Stage 3: WebSocket

**Before you code**

1. SSE already pushes bids. What does a WebSocket add, and what does it cost (proxies, load balancers, reconnects, your own protocol on top)?
2. Bids from the bus and results of the client's own bids both need sending on one socket. What is the rule for concurrent `SendAsync` calls, and how do you keep it?
3. The client sends garbage. Close the socket or answer with an error? What would you do if one client sent 1,000 messages a second?

**Do**

- `Live/BidSocket.cs`. Message shapes are in `Live/Messages.cs`.
- `dotnet test --filter-namespace "*Stage3"`.
- `k6 run -e MODE=ws k6/watch.js`. Compare with SSE: requests, latency, and the work your code did.

**Mutation:** when the client closes, return without completing the close handshake. Which test catches it? Then close the socket when a message does not parse.

**Afterwards:** for this app, which would you pick, SSE or WebSocket, and why? Name a feature that would change your answer.

---

## Stage 4: more than one instance

**Before you code**

1. Start a second instance (`dotnet run -c Release --project src/Realtime --urls http://localhost:5601`) and run `k6 run -e MODE=sse -e BID_URL=http://localhost:5601 k6/watch.js`: watchers on one instance, bids on the other. Predict `bids_seen`, then check.
2. Redis pub/sub delivers to whoever is subscribed at that moment, and drops the message if nobody is. What does a watcher lose if its instance's Redis connection blips for a second? Which stage covers that?
3. 10,000 watchers on one instance follow 50 auctions. How many Redis subscriptions should that instance hold?

**Do**

- `Bus/RedisBidBus.cs`.
- `dotnet test --filter-namespace "*Stage4"`. These use the bus directly, not your stages 1-3.
- Restart both instances with `Realtime__Bus=Redis` and run the k6 command from question 1 again.

**Mutation:** hand the bid to this instance's subscribers directly as well as publishing it. Which test catches it? Then unsubscribe the Redis channel whenever any watcher leaves.

**Afterwards:** compare Redis pub/sub with Redis Streams or Kafka for this. When would you want the bus to keep messages?

---

## Stage 5: reconnect without gaps

**Before you code**

1. A phone switches from Wi-Fi to cellular and the stream drops for 4 seconds. `EventSource` reconnects by itself with `Last-Event-ID`. What must the server send, and in what order?
2. You subscribe, then read the missed bids from the database. A bid can be in both. Another bid can commit between the read and going live. How do you send each exactly once?
3. A client comes back after an hour, 5,000 bids behind. Should the stream replay all of them?

**Do**

- Stage 5's part of `Live/BidStream.cs`.
- `dotnet test --filter-namespace "*Stage5"`. The tests place bids just before and just after your database read.
- `curl -N -H 'Last-Event-ID: 3' localhost:5600/auctions/1/stream`.

**Mutation:** subscribe after the replay instead of before. Which test catches it? Then drop the duplicate check, and then replay only the first page.

**Afterwards:** WebSocket has no built-in resume. Sketch how a client of stage 3 would resume, and what the server would need to keep.

---

## Results

Record your own numbers. Same machine, defaults unless noted (200 watchers, 10 auctions, 5 bids/s, 30 s).

| Run | k6 command | requests | bids_seen | latency p50 / p95 | notes |
|---|---|---|---|---|---|
| baseline | `-e MODE=poll` | | | | |
| baseline, 2,000 watchers | `-e MODE=poll -e WATCHERS=2000` | | | | |
| stage 1 | `-e MODE=longpoll` | | | | |
| stage 2 | `-e MODE=sse` | | | | |
| stage 2, 2,000 watchers | `-e MODE=sse -e WATCHERS=2000` | | | | |
| stage 3 | `-e MODE=ws` | | | | |
| two instances, local bus | `-e MODE=sse -e BID_URL=http://localhost:5601` | | | | |
| two instances, Redis | same, `Realtime__Bus=Redis` | | | | |

<details><summary>What I measured when I built the lab (WSL2, 2026-11-24)</summary>

About 150 bids in each 30 s run; latency is from the watchers' instance.

| Run | requests | bids_seen | latency p50 / p95 |
|---|---|---|---|
| poll, 200 watchers | 6,173 | 2,728 | 452 / 863 ms |
| poll, 2,000 watchers | 60,183 | 27,482 | 522 / 963 ms |
| long poll | 3,099 | 2,927 | 4.3 / 5.5 ms |
| SSE | 353 | 2,720 | 3.5 / 4.7 ms |
| WebSocket | 153 | 2,715 | 3.3 / 4.9 ms |
| SSE, two instances, local bus | | 0 | |
| SSE, two instances, Redis | | 2,741 | 3.7 / 6.6 ms |
| SSE, two instances, Redis, 2,000 watchers | | 27,206 | 4.2 / 7.1 ms |

Requests include the bids. Each SSE watcher is one request and each WebSocket watcher one upgrade. k6 has no streaming HTTP client, so in SSE mode it counts events when the server closes each stream at the end of the run.
</details>

## When you finish

1. Write 5 flashcards for the parts you got wrong or had to look up.
2. Ask Claude for a 10-minute mock: "Design live bidding for an auction site" (or "Design Facebook Live Comments"), Claude as interviewer. Cover the trade-off of each stage without notes.
3. Not covered here: authentication on long-lived connections, presence ("12 people watching"), and fan-out at millions of watchers (a tier of connection servers apart from the app). Lab 9 (capstone) uses SSE for upload progress.
