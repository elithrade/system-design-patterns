// Uploads for lab 4: the same file sent through the API, with a presigned PUT, or in parts.
//
//   k6 run -e MODE=direct k6/upload.js
//   k6 run -e MODE=presigned -e SIZE_MB=500 k6/upload.js
//
// Settings (all optional, passed with -e):
//   BASE_URL   the app, default http://localhost:5400
//   MODE       direct:    PUT /files/direct/{name} with the whole body (the baseline)
//              presigned: POST /files, PUT to the URL, POST /files/{id}/complete (stage 1)
//              multipart: POST /files, POST /files/{id}/multipart, PUT every part, complete (stage 2)
//              Default direct
//   SIZE_MB    size of each file, default 200
//   UPLOADS    files to upload in total, default 4
//   VUS        uploads at the same time, default 2 (each VU holds its own copy of the file)
//   COMPLETE   presigned and multipart: call complete (1) or leave it to bucket events (0), default 1
//
// At the end it prints what the app did during the run: bytes that went through the API process,
// and its peak memory.
import http from 'k6/http';
import { check } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5400';
const MODE = __ENV.MODE || 'direct';
const SIZE_MB = Number(__ENV.SIZE_MB || 200);
const UPLOADS = Number(__ENV.UPLOADS || 4);
const VUS = Number(__ENV.VUS || 2);
const COMPLETE = (__ENV.COMPLETE || '1') === '1';

const SIZE = SIZE_MB * 1024 * 1024;
const data = new Uint8Array(SIZE).map((_, i) => i % 251).buffer;
const user = { 'X-User-Id': '1' };
const json = { ...user, 'Content-Type': 'application/json' };

export const options = {
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'max'],
  scenarios: {
    uploads: { executor: 'shared-iterations', vus: VUS, iterations: UPLOADS, maxDuration: '30m' },
  },
  thresholds: {
    // one line per step in the summary
    'http_req_duration{name:direct}': ['max>=0'],
    'http_req_duration{name:create}': ['max>=0'],
    'http_req_duration{name:put}': ['max>=0'],
    'http_req_duration{name:part}': ['max>=0'],
    'http_req_duration{name:complete}': ['max>=0'],
    checks: ['rate==1'],
  },
};

export function setup() {
  return http.get(`${BASE_URL}/stats`).json();
}

function create(name) {
  const res = http.post(`${BASE_URL}/files`,
    JSON.stringify({ name, contentType: 'application/octet-stream', size: SIZE }),
    { headers: json, tags: { name: 'create' } });
  check(res, { 'created': (r) => r.status === 201 });
  return res.json();
}

function complete(url) {
  if (!COMPLETE) return;
  const res = http.post(url, null, { headers: user, tags: { name: 'complete' } });
  check(res, { 'completed': (r) => r.status === 200 });
}

export default function () {
  const name = `file-${__VU}-${__ITER}.bin`;

  if (MODE === 'direct') {
    const res = http.put(`${BASE_URL}/files/direct/${name}`, data,
      { headers: { ...user, 'Content-Type': 'application/octet-stream' }, tags: { name: 'direct' }, timeout: '10m' });
    check(res, { 'uploaded': (r) => r.status === 201 });
    return;
  }

  const ticket = create(name);
  if (MODE === 'presigned') {
    const put = http.put(ticket.uploadUrl, data,
      { headers: { 'Content-Type': 'application/octet-stream' }, tags: { name: 'put' }, timeout: '10m' });
    check(put, { 'put to MinIO': (r) => r.status === 200 });
    complete(`${BASE_URL}/files/${ticket.id}/complete`);
    return;
  }

  const plan = http.post(`${BASE_URL}/files/${ticket.id}/multipart`, null, { headers: user, tags: { name: 'create' } }).json();
  for (const part of plan.parts) {
    const start = (part.partNumber - 1) * plan.partSize;
    const res = http.put(part.url, data.slice(start, start + part.size), { tags: { name: 'part' }, timeout: '5m' });
    check(res, { 'part to MinIO': (r) => r.status === 200 });
  }
  complete(`${BASE_URL}/files/${ticket.id}/multipart/complete`);
}

export function teardown(before) {
  const after = http.get(`${BASE_URL}/stats`).json();
  console.log(`app during this run: bytesThroughApi=${after.bytesThroughApi - before.bytesThroughApi}` +
    ` peakWorkingSetMb=${after.peakWorkingSetMb} (was ${before.peakWorkingSetMb}) managedHeapMb=${after.managedHeapMb}`);
}
