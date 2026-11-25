// A flash sale for lab 3: BUYERS buyers arrive at once for sale 1 (100 tickets, 100 seats).
// Reseed before every run, or the sale is already sold out:
//
//   docker compose exec postgres psql -U postgres -d sales -f /db/seed.sql -c "CALL seed(100, 2)"
//   k6 run k6/flash.js
//   k6 run -e MODE=best k6/flash.js
//
// Settings (all optional, passed with -e):
//   BASE_URL   the app, default http://localhost:5300
//   MODE       tickets: POST /sales/1/tickets (stages 1-2, and the two-database section)
//              best:    POST /sales/1/seats/best (stage 3)
//              hold:    hold a random seat, then buy it (stage 4)
//              Default tickets
//   BUYERS     distinct buyers, default 5000
//   CLICKS     requests each buyer sends at once (impatient double-clicks), default 1
//   VUS        concurrent virtual users, default 500
//   SEATS      seats in the sale, default 100
//
// At the end it prints the sale's audit (GET /sales/1): tickets against capacity, buyers over the
// limit, seats sold twice. Those numbers are the test: k6's own checks only count status codes.
import http from 'k6/http';
import exec from 'k6/execution';
import { Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5300';
const MODE = __ENV.MODE || 'tickets';
const BUYERS = Number(__ENV.BUYERS || 5000);
const CLICKS = Number(__ENV.CLICKS || 1);
const VUS = Number(__ENV.VUS || 500);
const SEATS = Number(__ENV.SEATS || 100);

const outcomes = {};
for (const o of ['Bought', 'Held', 'SoldOut', 'LimitReached', 'Taken', 'HeldByOther', 'NotHeld', 'Busy', 'NotFound', 'Error']) {
  outcomes[o] = new Counter(`outcome_${o}`);
}

export const options = {
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
  scenarios: {
    flash: {
      // everyone at once: BUYERS iterations shared by VUS users, as fast as they go
      executor: 'shared-iterations',
      vus: VUS,
      iterations: BUYERS,
      maxDuration: '5m',
    },
  },
  thresholds: {
    // one line per endpoint in the summary
    'http_req_duration{name:buy}': ['p(99)<60000'],
  },
};

// 409 and 503 are answers here, not failures
http.setResponseCallback(http.expectedStatuses(200, 201, 404, 409, 503));

function record(res) {
  let outcome = 'Error';
  try {
    outcome = res.json('outcome') || 'Error';
  } catch (_) { /* a 500 has no outcome */ }
  outcomes[outcome].add(1);
  return outcome;
}

export default function () {
  const buyer = exec.scenario.iterationInTest + 1;
  const params = { headers: { 'X-Buyer-Id': String(buyer) }, tags: { name: 'buy' } };

  if (MODE === 'hold') {
    const seat = 1 + Math.floor(Math.random() * SEATS);
    const held = http.post(`${BASE_URL}/seats/${seat}/hold`, null, { ...params, tags: { name: 'hold' } });
    if (record(held) === 'Held') {
      record(http.post(`${BASE_URL}/seats/${seat}/purchase`, null, params));
    }
    return;
  }

  const url = MODE === 'best' ? `${BASE_URL}/sales/1/seats/best` : `${BASE_URL}/sales/1/tickets`;
  const responses = http.batch(Array.from({ length: CLICKS }, () => ['POST', url, null, params]));
  responses.forEach(record);
}

export function teardown() {
  console.log(`sale 1 after the run: ${http.get(`${BASE_URL}/sales/1`).body}`);
  console.log(`app counters: ${http.get(`${BASE_URL}/stats`).body}`);
  const payments = http.get(`${BASE_URL}/payments/audit`);
  if (payments.status === 200) {
    console.log(`payments: ${payments.body}`);
  }
}
