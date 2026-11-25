// Riders search for nearby drivers while drivers report their location, for lab 8.
//
//   k6 run k6/nearby.js                          # searches only, 20 per second
//   k6 run -e DRIVERS=100000 k6/nearby.js        # plus 100,000 drivers reporting every 3 s
//
// The engine is the app's (Proximity__Engine=Scan|Geohash|PostGis|Redis), and the data size is the
// seed's (README.md, "Setup"). With the Redis engine, POST /admin/load first.
//
// Settings (all optional, passed with -e):
//   BASE_URL   default http://localhost:5800
//   SEARCHES   searches per second, default 20 (0: none)
//   RADIUS     search radius in meters, default 2000
//   LIMIT      drivers per search, default 10
//   DRIVERS    drivers reporting every 3 s, ids 1..DRIVERS, default 0
//   BATCH      reports per POST /drivers/locations, default 500
//   DURATION   seconds, default 30
//
// Searches and reports are spread over the seeded box around New York.
import http from 'k6/http';
import { check } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5800';
const SEARCHES = Number(__ENV.SEARCHES ?? 20);
const RADIUS = Number(__ENV.RADIUS || 2000);
const LIMIT = Number(__ENV.LIMIT || 10);
const DRIVERS = Number(__ENV.DRIVERS || 0);
const BATCH = Number(__ENV.BATCH || 500);
const DURATION = Number(__ENV.DURATION || 30);

const scenarios = {};
if (SEARCHES > 0) {
  scenarios.riders = {
    executor: 'constant-arrival-rate', exec: 'search', rate: SEARCHES, timeUnit: '1s',
    duration: `${DURATION}s`, preAllocatedVUs: 20, maxVUs: 500,
  };
}
if (DRIVERS > 0) {
  // DRIVERS / 3 reports a second, BATCH at a time
  scenarios.drivers = {
    executor: 'constant-arrival-rate', exec: 'report', rate: Math.max(1, Math.round(DRIVERS / 3 / BATCH)), timeUnit: '1s',
    duration: `${DURATION}s`, preAllocatedVUs: 20, maxVUs: 500,
  };
}

export const options = {
  scenarios,
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
  thresholds: {
    'http_req_duration{name:search}': ['max>=0'],
    'http_req_duration{name:report}': ['max>=0'],
    'http_req_failed{name:search}': ['rate>=0'],
    'http_req_failed{name:report}': ['rate>=0'],
  },
};

const point = () => [40.58 + Math.random() * 0.29, -74.07 + Math.random() * 0.29];

export function search() {
  const [lat, lng] = point();
  const res = http.get(`${BASE_URL}/drivers/nearby?lat=${lat}&lng=${lng}&radiusM=${RADIUS}&limit=${LIMIT}`, { tags: { name: 'search' }, timeout: '30s' });
  check(res, { 'search ok': (r) => r.status === 200 });
}

export function report() {
  const batch = [];
  for (let i = 0; i < BATCH; i++) {
    const [lat, lng] = point();
    batch.push({ driverId: Math.floor(Math.random() * DRIVERS) + 1, lat, lng, available: Math.random() < 0.8 });
  }
  const res = http.post(`${BASE_URL}/drivers/locations`, JSON.stringify(batch),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'report' }, timeout: '30s' });
  check(res, { 'report ok': (r) => r.status === 204 });
}
