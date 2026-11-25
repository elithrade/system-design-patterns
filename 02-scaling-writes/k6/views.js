// Write-heavy load for lab 2: views, most of them on one viral video, plus a few reads.
//
//   k6 run k6/views.js
//   k6 run -e RATE=3000 -e HOT_SHARE=0.9 k6/views.js
//
// Settings (all optional, passed with -e):
//   BASE_URL     the app, default http://localhost:5200
//   RATE         requests per second, held constant, default 1000
//   DURATION     how long to hold that rate, default 30s
//   HOT_SHARE    share of views that go to video 1, default 0.8
//   READ_SHARE   share of requests that are GET /videos/{id}, default 0.05
//   VIDEOS       videos in the seed, default 100000
//   P99_MS       the p99 each endpoint must stay under to pass, default 200
//
// A 202 (queued) and a 503 (shed) both count as handled: k6 reports them separately as
// `accepted` and `shed`. At the end it prints what the app did during the run (GET /stats before
// and after): statements per database, views accepted and shed, and the queue depth.
import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5200';
const RATE = Number(__ENV.RATE || 1000);
const DURATION = __ENV.DURATION || '30s';
const HOT_SHARE = Number(__ENV.HOT_SHARE || 0.8);
const READ_SHARE = Number(__ENV.READ_SHARE || 0.05);
const VIDEOS = Number(__ENV.VIDEOS || 100000);
const P99_MS = Number(__ENV.P99_MS || 200);

const written = new Counter('written');
const accepted = new Counter('accepted');
const shed = new Counter('shed');

export const options = {
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
  scenarios: {
    views: {
      // RATE requests a second whatever the response times, so a slow app shows up as latency,
      // not as a lower request rate
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 100,
      maxVUs: 3000,
    },
  },
  thresholds: {
    'http_req_failed{scenario:views}': ['rate<0.01'],
    'http_req_duration{name:view}': [`p(99)<${P99_MS}`],
    'http_req_duration{name:read}': [`p(99)<${P99_MS}`],
  },
};

// 503 is an answer here, not a failure: shedding is what stage 4 is for
http.setResponseCallback(http.expectedStatuses(200, 202, 204, 503));

const randInt = (max) => 1 + Math.floor(Math.random() * max);
const pickVideo = () => (Math.random() < HOT_SHARE ? 1 : randInt(VIDEOS));

export function setup() {
  return http.get(`${BASE_URL}/stats`).json();
}

export default function () {
  if (Math.random() < READ_SHARE) {
    const res = http.get(`${BASE_URL}/videos/${pickVideo()}`, { tags: { name: 'read' } });
    check(res, { 'read 200': (x) => x.status === 200 });
    return;
  }
  const res = http.post(`${BASE_URL}/videos/${pickVideo()}/views`, null, { tags: { name: 'view' } });
  check(res, { 'view handled': (x) => [202, 204, 503].includes(x.status) });
  if (res.status === 204) written.add(1);
  if (res.status === 202) accepted.add(1);
  if (res.status === 503) shed.add(1);
}

export function teardown(before) {
  const after = http.get(`${BASE_URL}/stats`).json();
  const delta = Object.fromEntries(Object.keys(after).map((k) => [
    k,
    Array.isArray(after[k]) ? after[k].map((v, i) => v - before[k][i]) : k === 'queueDepth' ? after[k] : after[k] - before[k],
  ]));
  console.log(`app during this run: ${JSON.stringify(delta)}`);
}
