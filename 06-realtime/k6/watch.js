// Watchers follow auctions while bidders bid, for lab 6.
//
//   k6 run -e MODE=poll k6/watch.js
//   k6 run -e MODE=sse -e WATCHERS=1000 k6/watch.js
//   k6 run -e MODE=sse -e BID_URL=http://localhost:5601 k6/watch.js   # bids on a second instance
//
// Settings (all optional, passed with -e):
//   BASE_URL      the instance watchers connect to, default http://localhost:5600
//   BID_URL       the instance bids go to, default BASE_URL
//   MODE          poll:     GET /auctions/{id}/bids?after=N every POLL_MS (the baseline)
//                 longpoll: GET ...&waitSeconds=25, again as soon as it answers (stage 1)
//                 sse:      one GET /auctions/{id}/stream for the whole run (stage 2)
//                 ws:       one WebSocket /auctions/{id}/ws for the whole run (stage 3)
//                 Default poll
//   WATCHERS      watchers connected at once, default 200
//   AUCTIONS      auctions they are spread over, default 10
//   BIDS_PER_SEC  bids per second across all auctions, default 5
//   DURATION      seconds, default 30
//   POLL_MS       poll mode: wait between polls, default 1000
//
// At the end it prints bids placed, bids the watchers saw, and from the watchers' instance:
// requests by kind and delivery latency (bid placed -> bid written to a watcher).
import http from 'k6/http';
import { check, sleep } from 'k6';
import exec from 'k6/execution';
import { Counter } from 'k6/metrics';
import { WebSocket } from 'k6/websockets';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5600';
const BID_URL = __ENV.BID_URL || BASE_URL;
const MODE = __ENV.MODE || 'poll';
const WATCHERS = Number(__ENV.WATCHERS || 200);
const AUCTIONS = Number(__ENV.AUCTIONS || 10);
const BIDS_PER_SEC = Number(__ENV.BIDS_PER_SEC || 5);
const DURATION = Number(__ENV.DURATION || 30);
const POLL_MS = Number(__ENV.POLL_MS || 1000);

const placed = new Counter('bids_placed');
const seen = new Counter('bids_seen');

export const options = {
  scenarios: {
    watchers: { executor: 'constant-vus', exec: 'watch', vus: WATCHERS, duration: `${DURATION}s`, gracefulStop: '5s' },
    bidders: {
      executor: 'constant-arrival-rate', exec: 'bid', rate: BIDS_PER_SEC, timeUnit: '1s',
      duration: `${DURATION}s`, preAllocatedVUs: 10, startTime: '2s',
    },
  },
  thresholds: {
    bids_placed: ['count>=0'],
    bids_seen: ['count>=0'],
  },
};

export function setup() {
  // where each auction is now, so pollers start from there instead of from its history
  const latest = {};
  for (let a = 1; a <= AUCTIONS; a++) {
    latest[a] = 0;
    for (;;) {
      const page = http.get(`${BASE_URL}/auctions/${a}/bids?after=${latest[a]}`).json();
      if (page.length === 0) break;
      latest[a] = page[page.length - 1].id;
    }
  }
  http.post(`${BASE_URL}/stats/reset`);
  return { started: Date.now(), latest };
}

// per watcher: the last bid id it has (-1 until it starts)
let after = -1;

export function bid() {
  const auction = 1 + Math.floor(Math.random() * AUCTIONS);
  // amounts only go up: milliseconds since 1970 beat any earlier bid
  const res = http.post(`${BID_URL}/auctions/${auction}/bids`, JSON.stringify({ bidderId: exec.vu.idInTest, amount: Date.now() }),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'bid' } });
  if (res.status === 201) placed.add(1);
}

export function watch(data) {
  const auction = 1 + (exec.vu.idInTest % AUCTIONS);
  const remaining = Math.floor((data.started + DURATION * 1000 - Date.now()) / 1000);

  if (MODE === 'poll' || MODE === 'longpoll') {
    if (after < 0) after = data.latest[auction];
    const wait = MODE === 'longpoll' ? '&waitSeconds=25' : '';
    const res = http.get(`${BASE_URL}/auctions/${auction}/bids?after=${after}${wait}`, { tags: { name: MODE }, timeout: '40s' });
    if (res.status === 200) {
      const bids = res.json();
      seen.add(bids.length);
      if (bids.length > 0) after = bids[bids.length - 1].id;
    }
    if (MODE === 'poll') sleep(POLL_MS / 1000);
    return;
  }

  if (remaining < 1) {
    sleep(1);
    return;
  }

  if (MODE === 'sse') {
    // k6 has no streaming HTTP client: the server closes the stream after `seconds`, and the
    // events are counted then. Latency comes from the server's stats.
    const res = http.get(`${BASE_URL}/auctions/${auction}/stream?seconds=${remaining}`,
      { headers: { Accept: 'text/event-stream' }, tags: { name: 'sse' }, timeout: `${remaining + 10}s` });
    check(res, { 'stream opened': (r) => r.status === 200 });
    seen.add((res.body.match(/^event: bid$/gm) || []).length);
    return;
  }

  const ws = new WebSocket(`${BASE_URL.replace('http', 'ws')}/auctions/${auction}/ws`);
  ws.onmessage = (e) => {
    if (JSON.parse(e.data).type === 'bid') seen.add(1);
  };
  ws.onerror = (e) => console.error(`socket: ${e.error}`);
  setTimeout(() => ws.close(), remaining * 1000);
}

export function teardown() {
  const s = http.get(`${BASE_URL}/stats`).json();
  console.log(`watchers' instance ${s.instance}: polls=${s.polls} longPolls=${s.longPolls} streams=${s.streams} sockets=${s.sockets}` +
    ` sent=${s.sent} dropped=${s.dropped} latency p50=${s.latencyP50Ms}ms p95=${s.latencyP95Ms}ms max=${s.latencyMaxMs}ms`);
}
