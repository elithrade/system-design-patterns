// Creators upload videos and wait for them to be published, for lab 9.
//
//   k6 run k6/publish.js                      # the baseline: upload through the API, transcode in the request
//   k6 run -e MODE=presigned k6/publish.js    # straight to MinIO, then confirm and poll (stage 1 on)
//
// The pipeline is the app's (Pipeline__Engine=Inline|Queue|Temporal).
//
// Settings (all optional, passed with -e):
//   BASE_URL   the app, default http://localhost:5900
//   MODE       direct:    PUT /videos/direct with the whole video (the baseline)
//              presigned: POST /videos, PUT to the URL, POST /videos/{id}/uploaded, then poll
//                         GET /videos/{id} until it is published or failed
//              Default direct
//   VIDEOS     videos in total, default 8
//   VUS        creators uploading at the same time, default 4 (each VU holds its own copy of the video)
//   SIZE_MB    size of each video, default 100
//   CORRUPT    fraction of videos whose bytes are not a video, default 0
//   POLL_MS    presigned: time between polls, default 500
//   WAIT_S     presigned: give up on a video after this long, default 300
//
// At the end it prints what happened to the videos and what the app did: bytes through the API
// process, its peak memory, transcodes started and resumed.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5900';
const MODE = __ENV.MODE || 'direct';
const VIDEOS = Number(__ENV.VIDEOS || 8);
const VUS = Number(__ENV.VUS || 4);
const SIZE_MB = Number(__ENV.SIZE_MB || 100);
const CORRUPT = Number(__ENV.CORRUPT || 0);
const POLL_MS = Number(__ENV.POLL_MS || 500);
const WAIT_S = Number(__ENV.WAIT_S || 300);

const SIZE = SIZE_MB * 1024 * 1024;
const good = new Uint8Array(SIZE).map((_, i) => i % 251);
const bad = good.slice();
bad.set([67, 79, 82, 82, 85, 80, 84]); // "CORRUPT"
const user = { 'X-User-Id': '1' };

const timeToFinal = new Trend('time_to_final', true);
const published = new Counter('videos_published');
const failed = new Counter('videos_failed');
const stuck = new Counter('videos_stuck');

export const options = {
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'max'],
  scenarios: {
    creators: { executor: 'shared-iterations', vus: VUS, iterations: VIDEOS, maxDuration: '60m' },
  },
  thresholds: {
    'http_req_duration{name:direct}': ['max>=0'],
    'http_req_duration{name:create}': ['max>=0'],
    'http_req_duration{name:put}': ['max>=0'],
    'http_req_duration{name:uploaded}': ['max>=0'],
    time_to_final: ['max>=0'],
  },
};

export function setup() {
  return http.get(`${BASE_URL}/stats`).json();
}

function finished(video) {
  if (video.status === 'published') published.add(1);
  else if (video.status === 'failed') failed.add(1);
  else stuck.add(1);
}

export default function () {
  const corrupt = Math.random() < CORRUPT;
  const body = (corrupt ? bad : good).buffer;
  const started = Date.now();

  if (MODE === 'direct') {
    const res = http.put(`${BASE_URL}/videos/direct?title=clip-${__VU}-${__ITER}`, body,
      { headers: { ...user, 'Content-Type': 'video/mp4' }, tags: { name: 'direct' }, timeout: '30m' });
    check(res, { 'uploaded': (r) => r.status === 201 });
    timeToFinal.add(Date.now() - started);
    if (res.status === 201) finished(res.json());
    else stuck.add(1);
    return;
  }

  const created = http.post(`${BASE_URL}/videos`, JSON.stringify({ title: `clip-${__VU}-${__ITER}`, contentType: 'video/mp4', size: SIZE }),
    { headers: { ...user, 'Content-Type': 'application/json' }, tags: { name: 'create' } });
  check(created, { 'created': (r) => r.status === 201 });
  const ticket = created.json();
  const put = http.put(ticket.uploadUrl, body, { headers: { 'Content-Type': 'video/mp4' }, tags: { name: 'put' }, timeout: '10m' });
  check(put, { 'put to MinIO': (r) => r.status === 200 });
  const confirm = http.post(`${BASE_URL}/videos/${ticket.id}/uploaded`, null, { headers: user, tags: { name: 'uploaded' }, timeout: '30m' });
  check(confirm, { 'confirmed': (r) => r.status === 202 });

  while (Date.now() - started < WAIT_S * 1000) {
    const video = http.get(`${BASE_URL}/videos/${ticket.id}`, { tags: { name: 'poll' } }).json();
    if (['published', 'failed', 'abandoned'].includes(video.status)) {
      timeToFinal.add(Date.now() - started);
      finished(video);
      return;
    }
    sleep(POLL_MS / 1000);
  }
  stuck.add(1);
}

export function teardown(before) {
  const after = http.get(`${BASE_URL}/stats`).json();
  console.log(`app during this run: bytesThroughApi=${after.bytesThroughApi - before.bytesThroughApi}` +
    ` peakWorkingSetMb=${after.peakWorkingSetMb} (was ${before.peakWorkingSetMb})` +
    ` transcodes=${after.transcodesStarted - before.transcodesStarted} resumed=${after.transcodesResumed - before.transcodesResumed}`);
}
