// Read-heavy load for lab 1: profile pages, single posts, and a trickle of likes.
//
//   k6 run k6/reads.js
//   k6 run -e RATE=500 -e HOT_SHARE=0.5 k6/reads.js
//
// Settings (all optional, passed with -e):
//   BASE_URL     the app, default http://localhost:5100
//   RATE         requests per second, held constant, default 200
//   DURATION     how long to hold that rate, default 30s
//   POST_SHARE   share of reads that are GET /posts/{id}; the rest are GET /users/{id}/posts. Default 0.5
//   HOT_SHARE    share of GET /posts/{id} that go to the 10 celebrity posts (ids 1-10), default 0
//   LIKE_SHARE   share of requests that are likes, default 0.02
//   USERS        users in the seed, default 200000
//   POSTS        posts in the seed, default 2000000
//   P99_MS       the p99 each endpoint must stay under to pass, default 200
//
// At the end it prints what the app did during the run (GET /stats before and after): queries per
// database, post reads that reached the database, and Redis commands.
import http from 'k6/http';
import { check } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5100';
const RATE = Number(__ENV.RATE || 200);
const DURATION = __ENV.DURATION || '30s';
const POST_SHARE = Number(__ENV.POST_SHARE || 0.5);
const HOT_SHARE = Number(__ENV.HOT_SHARE || 0);
const LIKE_SHARE = Number(__ENV.LIKE_SHARE || 0.02);
const USERS = Number(__ENV.USERS || 200000);
const POSTS = Number(__ENV.POSTS || 2000000);
const P99_MS = Number(__ENV.P99_MS || 200);

export const options = {
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
  scenarios: {
    reads: {
      // RATE requests a second whatever the response times, so a slow app shows up as latency,
      // not as a lower request rate
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 50,
      maxVUs: 2000,
    },
  },
  // one threshold per endpoint, so the summary prints each endpoint's latency separately
  thresholds: {
    'http_req_failed{scenario:reads}': ['rate<0.01'],
    'http_req_duration{name:profile}': [`p(99)<${P99_MS}`],
    'http_req_duration{name:post}': [`p(99)<${P99_MS}`],
    'http_req_duration{name:like}': [`p(99)<${P99_MS}`],
  },
};

const randInt = (max) => 1 + Math.floor(Math.random() * max);

export function setup() {
  return http.get(`${BASE_URL}/stats`).json();
}

export default function () {
  const viewer = randInt(USERS);
  const params = (name) => ({ headers: { 'X-User-Id': String(viewer) }, tags: { name } });
  const r = Math.random();

  if (r < LIKE_SHARE) {
    const post = Math.random() < HOT_SHARE ? randInt(10) : randInt(POSTS);
    const res = http.post(`${BASE_URL}/posts/${post}/likes`, null, params('like'));
    check(res, { 'like 204': (x) => x.status === 204 });
  } else if (r < LIKE_SHARE + (1 - LIKE_SHARE) * POST_SHARE) {
    const post = Math.random() < HOT_SHARE ? randInt(10) : randInt(POSTS);
    const res = http.get(`${BASE_URL}/posts/${post}`, params('post'));
    check(res, { 'post 200': (x) => x.status === 200 });
  } else {
    const res = http.get(`${BASE_URL}/users/${randInt(USERS)}/posts`, params('profile'));
    check(res, { 'profile 200': (x) => x.status === 200 });
  }
}

export function teardown(before) {
  const after = http.get(`${BASE_URL}/stats`).json();
  const delta = Object.fromEntries(Object.keys(after).map((k) => [k, after[k] - before[k]]));
  console.log(`app during this run: ${JSON.stringify(delta)}`);
}
