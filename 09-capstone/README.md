# Lab 9: capstone, a mini video platform

Creators upload videos, the platform makes three renditions of each (360p, 720p, a thumbnail), and a creator watches a progress bar until the video is published. The baseline does all of it in one request: the video comes through the API, which holds it in memory, writes it to MinIO, transcodes each rendition in turn, and answers when the video is published. Eight 100 MB uploads pushed the API to 1 GB of memory and held each request for 7 seconds. A 2 GB video is more than a .NET `MemoryStream` can hold, and its renditions would keep the request open for over 2 minutes. The four stages take the pieces of labs 4, 5, 6 and 7 and put them together: presigned upload, rendition jobs, a live progress bar across instances, and one workflow from upload to publish that survives crashes.

| Stage | Technique | From lab | Your code |
|---|---|---|---|
| 1 | Presigned upload, confirm once | 4 | `Uploads/VideoUploads.cs` |
| 2 | One job per rendition; publish when the last one finishes | 5 | `Jobs/TranscodeQueue.cs` |
| 3 | SSE progress bar; Postgres `LISTEN/NOTIFY` between instances | 6 | `Live/ProgressStream.cs`, `Bus/PgProgressBus.cs` |
| 4 | A Temporal workflow from create to publish | 7 | `Temporal/PublishWorkflow.cs` |

Everything else (the schema, the simulated transcoder, the job runner, the in-process bus, the Temporal activities, endpoints, tests, k6) is written. The stubs throw `NotImplementedException`, so stages 1-4 start red. Three baseline tests are green and must stay green.

The transcoder is a stand-in for ffmpeg. It spends `Transcode__MsPerMb` (20 ms) per MB of source times the rendition's cost (360p 1, 720p 2, thumb 0.1), reports progress every 10%, and writes a small text file as the rendition. A source whose bytes start with `CORRUPT` is "not a video", and that failure is permanent.

## The API

| | |
|---|---|
| `PUT /videos/direct?title=...` | the baseline: the body is the video; answers when it is published |
| `POST /videos` `{"title", "contentType", "size"}` | a pending video and a presigned PUT URL (stage 1) |
| `POST /videos/{id}/uploaded` | "I finished uploading": checks MinIO, then starts processing |
| `GET /videos/{id}` | the video's state: status, version, each rendition's percent |
| `GET /videos/{id}/progress` | Server-Sent Events: the state each time it changes, until it is final (stage 3) |
| `GET /videos/{id}/renditions/{name}` | 302 to a presigned GET, once published |
| `GET /stats` | bytes through the API, peak memory, transcodes started and resumed |

There is no auth: `X-User-Id: 1` says who you are.

## Setup

```
cd ~/code/system-design-patterns/09-capstone
docker compose up -d --wait                         # postgres :56232, MinIO :59100 (console :59101), Temporal :57333 (UI :58333)
dotnet run -c Release --project src/VideoPlatform   # applies migrations, creates the bucket, listens on :5900
```

Pick the pipeline with `Pipeline__Engine=Inline|Queue|Temporal` and the progress bus with `Pipeline__Bus=Local|Postgres`. Other settings: `Transcode__MsPerMb=20`, `Transcode__Workers=4`, `Transcode__LeaseSeconds=30`, `Transcode__MaxAttempts=3`, `Temporal__UploadWaitSeconds=3600`, `Temporal__HeartbeatTimeoutSeconds=10`. All are in `src/VideoPlatform/appsettings.json` and `Options.cs`. With the Temporal engine the app connects to Temporal at startup and fails if it is not running.

Tests run on their own containers (Testcontainers): Postgres and MinIO for every stage, plus a Temporal dev server for stage 4. Each test gets its own database, bucket and fake clock; stage 4 tests get their own task queue. Stages 2-4 create uploaded videos without your stage 1 code, so you can do the stages in any order.

```
dotnet test                                # everything
dotnet test --filter-namespace "*Stage2"   # one stage
```

Load test (`k6/publish.js` lists every setting): 8 videos of 100 MB, 4 creators at a time.

```
k6 run k6/publish.js                     # the baseline
k6 run -e MODE=presigned k6/publish.js   # upload to MinIO, confirm, poll until published
```

It prints how many videos were published, failed or stuck, the time from start to a final state, and what the app did.

## How to run each stage

1. Answer the "before you code" questions in writing. Predict the numbers.
2. Run the k6 command for the stage on the current code and write the result in the table at the bottom.
3. Write the code until the stage's tests pass.
4. Run the same k6 command again. Compare with your prediction.
5. Try the mutation listed: break your code on purpose and check a test goes red.

---

## Stage 1: presigned upload

**Before you code**

1. Run `k6 run k6/publish.js`. Where are the 400 MB of four uploads in flight? What does the API's peak memory say, and what happens with 2 GB videos?
2. You did this in lab 4. What is different about confirming a video? Processing starts after the confirm: what happens if two confirms race, or if the first one's answer is lost and the client retries?
3. The confirm succeeded, then the process died before it queued the renditions. The client retries the confirm and gets "already confirmed". What must the endpoint still do?

**Do**

- `Uploads/VideoUploads.cs`. The class comment lists what the tests check.
- `dotnet test --filter-namespace "*Stage1"`.
- Restart the app and run `k6 run -e MODE=presigned k6/publish.js`. Compare `bytesThroughApi`, the peak memory and the `uploaded` request's time with the baseline.

**Mutation:** presign without the content type. Which test catches it? Then confirm without comparing sizes, and then move the video to uploaded whether it was pending or already uploaded.

**Afterwards:** the inline engine still holds the confirm request until every rendition is made. Which part of the baseline's problem did stage 1 fix, and which is left?

---

## Stage 2: rendition jobs

**Before you code**

1. A video has three renditions. One job for the video, or one per rendition? What does each choice do to time to publish, retries, and progress?
2. Two workers finish a video's last two renditions at the same moment. Each marks its rendition done, then asks "are they all done now?". Under READ COMMITTED, what can each one see? Can both publish? Can neither?
3. A rendition fails for good because the source is corrupt. What should happen to the video, and to its other renditions still in the queue?
4. A worker is killed at 70% of a 20-minute 720p transcode. Where should the retry start?

**Do**

- `Jobs/TranscodeQueue.cs`. The runner that calls it is `Jobs/TranscodeRunner.cs`.
- `dotnet test --filter-namespace "*Stage2"`.
- Restart with `Pipeline__Engine=Queue` and run `k6 run -e MODE=presigned k6/publish.js`. Compare the `uploaded` request's time with stage 1. Then kill the app with `kill -9` mid-run, start it again, and watch the renditions table: what happens after `LeaseSeconds`?

**Mutation:** claim without a lock. Which test catches it? Then complete by video and name alone, then record progress without extending the lease, then use a constant backoff, then claim renditions whatever their video's status. Last, check whether the video is finished after committing the rendition, and publish without checking the video's status.

**Afterwards:** the trigger in `001_baseline.sql` locks the video row whenever a rendition changes. Read it. How does it serialize the two last completions, and what would you need without it?

---

## Stage 3: the progress bar

**Before you code**

1. Lab 6's bids were a log: every bid matters. What is progress? What does that change about a watcher that falls behind, and about the queue each subscriber keeps (`ProgressSubscription`)?
2. Each snapshot carries the video's `version`. A watcher gets version 12 from its read and then version 11 from the bus. Why can that happen, and what should it do?
3. The stream reads the state, then subscribes. The video is published in between. What does the watcher see, forever? What order fixes it?
4. The worker is on instance B, the watcher on instance A. Redis would do (lab 6), but both already talk to Postgres. What does `LISTEN/NOTIFY` promise, what does it drop, and why is one listening connection per instance enough?

**Do**

- `Live/ProgressStream.cs`, then `Bus/PgProgressBus.cs`.
- `dotnet test --filter-namespace "*Stage3"`.
- Run two instances on one database: one taking requests, one doing the work.

  ```
  Pipeline__Engine=Queue Pipeline__Bus=Postgres Transcode__RunWorkers=false dotnet run -c Release --project src/VideoPlatform
  Pipeline__Engine=Queue Pipeline__Bus=Postgres dotnet run -c Release --project src/VideoPlatform --urls http://localhost:5901
  ```

  Create a video on :5900, PUT the bytes, run `curl -N localhost:5900/videos/<id>/progress` in another terminal, then confirm on :5900. Do it again with `Pipeline__Bus=Local` on both.

**Mutation:** subscribe after reading the state. Which test catches it? Then send every snapshot without checking its version, then time the heartbeat on the real clock, then deliver a snapshot to this instance's subscribers directly as well as through Postgres, and last, open a listening connection per subscriber.

**Afterwards:** a notification payload is limited to 8,000 bytes and is lost if nobody listens at that moment. What in this design makes both acceptable? When would they not be?

---

## Stage 4: one workflow from upload to publish

**Before you code**

1. A creator gets an upload URL and never uploads. With stages 1-3, what cleans up the video? What would you have to build (lab 4's sweeper)? What does a durable timer in a workflow replace?
2. Your stage 2 queue has leases, attempts, backoff and a fan-in check. Which of these does Temporal do, and which part is still yours?
3. A transcode heartbeats its percent. The worker freezes at 40%. What notices, after how long, and where does the retry start? What decides the heartbeat timeout?
4. The 720p rendition fails for good after 360p finished. What must happen to the 360p file?

**Do**

- `Temporal/PublishWorkflow.cs`. The activities in `Temporal/PublishActivities.cs` are written.
- `dotnet test --filter-namespace "*Stage4"`.
- Restart with `Pipeline__Engine=Temporal`, run `k6 run -e MODE=presigned k6/publish.js`, and follow a workflow in the UI (http://localhost:58333). Kill the app mid-run and start it again. Then create a video, never upload it, and watch it with `Temporal__UploadWaitSeconds=30`.

**Mutation:** leave out `NonRetryableErrorTypes`. Which test catches it? Then leave out `HeartbeatTimeout`, and then wait for the upload with no timeout.

**Afterwards:** when one rendition fails for good, the others may still be running when your workflow cleans up. Can one write its file after the cleanup? How would you cancel them and wait before deleting? Compare your stage 2-3 code with this workflow for a pipeline that grows to 10 steps (captions, moderation, CDN warm-up).

---

## Results

Record your own numbers. Same machine, 8 videos of 100 MB, 4 at a time; restart the app before each run.

| Run | engine | k6 command | bytesThroughApi | peak working set | `uploaded` p50 | time to final p50 / p95 | notes |
|---|---|---|---|---|---|---|---|
| baseline | Inline | `k6 run k6/publish.js` | | | | | |
| stage 1 | Inline | `-e MODE=presigned` | | | | | |
| stage 2 | Queue | `-e MODE=presigned` | | | | | |
| stage 2 | Queue | same, `kill -9` mid-run | | | | | all published? |
| stage 4 | Temporal | `-e MODE=presigned` | | | | | |
| stage 4 | Temporal | same, `kill -9` mid-run | | | | | all published? |

<details><summary>What I measured when I built the lab (WSL2, 2026-11-25)</summary>

| Run | bytesThroughApi | peak working set | request p50 | time to final p50 / p95 |
|---|---|---|---|---|
| Inline, direct | 800 MB | 115 → 1,035 MB | 7.03 s (`direct`) | 7.11 / 7.21 s |
| Inline, presigned | 0 | 117 → 157 MB | 6.49 s (`uploaded`) | 6.97 / 7.15 s |
| Queue, presigned | 0 | 125 → 158 MB | 14.5 ms (`uploaded`) | 6.28 / 8.34 s |
| Temporal, presigned | 0 | 136 → 194 MB | 10.8 ms (`uploaded`) | 4.98 / 5.02 s |

The queue's 4 workers share 24 jobs. The workflow runs a video's three renditions at once, so its time is the 720p's.

`kill -9` with 4 videos processing:

- Queue: 4 renditions were running at the kill. 32 s after the restart all 4 videos were published; the 4 interrupted renditions waited for their 30 s leases and resumed from their recorded percent.
- Temporal: 8 renditions were running at the kill. 14 s after the restart all 4 videos were published; the 8 retries (after the 10 s heartbeat timeout) resumed from their last heartbeat.

Two instances on `Pipeline__Bus=Postgres`, a 20 MB video confirmed on :5900 and transcoded by :5901's workers: the stream on :5900 got 36 events and ended at `published`.
</details>

## When you finish

1. Write 5 flashcards for the parts you got wrong or had to look up.
2. Ask Claude for a 15-minute mock: "Design YouTube's upload and processing pipeline", Claude as interviewer. Name which lab each part of your design comes from and what it costs.
3. Not covered here: adaptive streaming (HLS/DASH segments and manifests), a CDN in front of the renditions, and moderation or copyright checks as extra workflow steps.
