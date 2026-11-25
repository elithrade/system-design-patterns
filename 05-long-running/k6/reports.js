// Year-in-review requests for lab 5: rendered inside the request, or queued and polled.
//
//   k6 run -e MODE=sync k6/reports.js       # through nginx: every request hits the 30 s timeout
//   k6 run -e MODE=queued k6/reports.js     # POST, then poll GET /jobs/{id} until done
//
// Settings (all optional, passed with -e):
//   BASE_URL   default http://localhost:5580 (nginx, 30 s timeout). http://localhost:5500 skips nginx.
//   MODE       sync:           GET /users/{id}/year-in-review (the baseline)
//              queued:         POST /users/{id}/year-in-review, poll the job (stage 1 on)
//              summary-sync:   GET /users/{id}/summary, a 200 ms job inline
//              summary-queued: POST /users/{id}/summary, poll the job
//              Default sync
//   USERS      reports to ask for, one per user, default 20
//   VUS        users clicking at the same time, default 10
//   CLICKS     POSTs per user in queued modes (impatient users click again), default 1
//   POLL_MS    queued modes: wait between polls, default 1000
//   GIVE_UP_S  queued modes: stop polling a job after this long, default 600
//
// It reports time to a result (job_seconds), and at the end what the app did: renders started,
// claims, completions. More renders than reports means some job ran twice.
import http from 'k6/http';
import { check, sleep } from 'k6';
import exec from 'k6/execution';
import { Trend, Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5580';
const MODE = __ENV.MODE || 'sync';
const USERS = Number(__ENV.USERS || 20);
const VUS = Number(__ENV.VUS || 10);
const CLICKS = Number(__ENV.CLICKS || 1);
const POLL_MS = Number(__ENV.POLL_MS || 1000);
const GIVE_UP_S = Number(__ENV.GIVE_UP_S || 600);

const jobSeconds = new Trend('job_seconds');
const outcomes = new Counter('outcomes');
const report = MODE.startsWith('summary') ? 'summary' : 'year-in-review';

export const options = {
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'max'],
  scenarios: {
    reports: { executor: 'shared-iterations', vus: VUS, iterations: USERS, maxDuration: '30m' },
  },
  thresholds: {
    // one line per outcome and step in the summary
    'outcomes{result:ok}': ['count>=0'],
    'outcomes{result:timeout}': ['count>=0'],
    'outcomes{result:failed}': ['count>=0'],
    'http_req_duration{name:get}': ['max>=0'],
    'http_req_duration{name:post}': ['max>=0'],
    'http_req_duration{name:poll}': ['max>=0'],
  },
};

export function setup() {
  return http.get(`${BASE_URL}/stats`).json();
}

export default function () {
  const user = exec.scenario.iterationInTest + 1;
  const started = Date.now();

  if (!MODE.endsWith('queued')) {
    const res = http.get(`${BASE_URL}/users/${user}/${report}`, { tags: { name: 'get' }, timeout: '120s' });
    const result = res.status === 200 ? 'ok' : res.status === 504 ? 'timeout' : 'failed';
    outcomes.add(1, { result });
    check(res, { 'got the report': (r) => r.status === 200 });
    if (result === 'ok') jobSeconds.add((Date.now() - started) / 1000);
    return;
  }

  let job = null;
  for (let i = 0; i < CLICKS; i++) {
    const res = http.post(`${BASE_URL}/users/${user}/${report}`, null, { tags: { name: 'post' } });
    check(res, { 'accepted': (r) => r.status === 202 });
    if (res.status !== 202) {
      outcomes.add(1, { result: 'failed' });
      return;
    }
    job = res.json();
  }

  while ((Date.now() - started) / 1000 < GIVE_UP_S) {
    sleep(POLL_MS / 1000);
    job = http.get(`${BASE_URL}/jobs/${job.id}`, { tags: { name: 'poll' } }).json();
    if (job.status === 'done') {
      outcomes.add(1, { result: 'ok' });
      jobSeconds.add((Date.now() - started) / 1000);
      return;
    }
    if (job.status === 'failed' || job.status === 'dead') {
      outcomes.add(1, { result: 'failed' });
      return;
    }
  }
  outcomes.add(1, { result: 'timeout' });
}

export function teardown(before) {
  const after = http.get(`${BASE_URL}/stats`).json();
  console.log(`app during this run: ${USERS} reports asked for, renders=${after.renders - before.renders}` +
    ` claims=${after.claims - before.claims} completions=${after.completions - before.completions}` +
    ` rejectedCompletions=${after.rejectedCompletions - before.rejectedCompletions}`);
}
