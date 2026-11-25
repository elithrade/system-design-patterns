// Checkouts for lab 7, with client retries, then an audit of what the services hold.
//
//   k6 run k6/checkout.js                  # no idempotency keys: a retry is a new purchase
//   k6 run -e KEYS=1 k6/checkout.js        # one Idempotency-Key per purchase, reused by its retries
//
// Failures come from the app: start it with Faults__TransientRate=0.1 Faults__LostResponseRate=0.05
// (README.md, "Setup"). Reseed between runs so the audit only sees this run.
//
// Settings (all optional, passed with -e):
//   BASE_URL       default http://localhost:5700
//   CHECKOUTS      purchases in total, default 300
//   VUS            purchases at the same time, default 20
//   KEYS           1: send Idempotency-Key, the same on every retry of a purchase. Default 0
//   RETRIES        retries per purchase after a 5xx or a timeout, default 3
//   DECLINED       share of purchases with a card that is declined, default 0.05
//   UNDELIVERABLE  share with an address shipping refuses, default 0.05
//   SCARCE         share that buy 3 of 'scarce' (20 in stock), default 0.1
//
// At the end it prints purchases by final answer and the audit: orders by status, purchases charged
// twice, charges for orders that never shipped, stock held for orders that never shipped.
import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';
import { Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5700';
const CHECKOUTS = Number(__ENV.CHECKOUTS || 300);
const VUS = Number(__ENV.VUS || 20);
const KEYS = (__ENV.KEYS || '0') === '1';
const RETRIES = Number(__ENV.RETRIES || 3);
const DECLINED = Number(__ENV.DECLINED || 0.05);
const UNDELIVERABLE = Number(__ENV.UNDELIVERABLE || 0.05);
const SCARCE = Number(__ENV.SCARCE || 0.1);

const answers = new Counter('purchases');

export const options = {
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'max'],
  scenarios: {
    checkouts: { executor: 'shared-iterations', vus: VUS, iterations: CHECKOUTS, maxDuration: '10m' },
  },
  thresholds: {
    'purchases{answer:completed}': ['count>=0'],
    'purchases{answer:refused}': ['count>=0'],
    'purchases{answer:gave_up}': ['count>=0'],
    'http_req_duration{name:checkout}': ['max>=0'],
  },
};

export default function () {
  const n = exec.scenario.iterationInTest;
  const roll = Math.random();
  const body = {
    clientRef: `purchase-${n}-${Date.now()}`,
    customerId: 1 + (n % 100),
    sku: roll < SCARCE ? 'scarce' : n % 2 === 0 ? 'mug' : 'poster',
    quantity: roll < SCARCE ? 3 : 1,
    card: Math.random() < DECLINED ? '4000-0000-0000-0002' : '4242-4242-4242-4242',
    address: Math.random() < UNDELIVERABLE ? '1 Nowhere Lane' : '10 Main Street',
  };
  const headers = { 'Content-Type': 'application/json' };
  if (KEYS) headers['Idempotency-Key'] = body.clientRef;

  for (let attempt = 0; attempt <= RETRIES; attempt++) {
    const res = http.post(`${BASE_URL}/checkout`, JSON.stringify(body), { headers, tags: { name: 'checkout' }, timeout: '30s' });
    if (res.status === 201) {
      answers.add(1, { answer: 'completed' });
      return;
    }
    // 409 with a key in progress means "still running": wait and ask again
    const inProgress = res.status === 409 && res.body.includes('in progress');
    if (res.status === 409 && !inProgress) {
      answers.add(1, { answer: 'refused' });
      return;
    }
    if (res.status < 500 && !inProgress) {
      check(res, { 'expected status': () => false });
      return;
    }
  }
  answers.add(1, { answer: 'gave_up' });
}

export function teardown() {
  const a = http.get(`${BASE_URL}/audit`).json();
  console.log(`audit: orders=${JSON.stringify(a.orders)} chargedTwice=${a.chargedTwice} paidNotShipped=${a.paidNotShipped}` +
    ` shippedNotPaid=${a.shippedNotPaid} heldNotShipped=${a.heldNotShipped} stockMismatch=${a.stockMismatch}`);
}
