# Lab 4: large blobs

A file upload service: metadata in Postgres, bytes in MinIO (S3-compatible). The baseline takes the upload as the request body, holds all of it in memory, then writes it to MinIO, and serves downloads the same way in reverse. It works for a profile photo. Four 200 MB uploads pushed the API's memory to 1.6 GB, and a 2 GB file is more than a .NET `MemoryStream` can hold. The five stages take the API out of the byte path: clients upload and download straight from storage with presigned URLs, big files go in resumable parts, storage events replace the client's word, and a sweeper cleans up what clients abandon.

| Stage | Technique | What it costs you | Your code |
|---|---|---|---|
| 1 | Presigned PUT | the API no longer sees the bytes, so it can't check them on the way in | `Uploads/PresignedUploads.cs` |
| 2 | Multipart, resumable upload | more round trips; open uploads keep their parts until someone aborts | `Uploads/MultipartUploads.cs` |
| 3 | Bucket notifications update the metadata | a second way in to protect; at-least-once delivery | `Events/ObjectCreatedHandler.cs` |
| 4 | Orphan cleanup | a job that deletes data, so its rules must be right | `OrphanSweeper` in `Cleanup/OrphanSweeper.cs` |
| 5 | Signed downloads | a link anyone can use until it expires | `Downloads/DownloadLinks.cs` |

Everything else (endpoints, repository, S3 client wiring, the sweep loop, compose, tests, k6) is written. The stubs throw `NotImplementedException`, so stages 1-5 start red. One baseline test is green and must stay green.

## The API

| | |
|---|---|
| `PUT /files/direct/{name}` | the baseline upload: the body is the file (needs `X-User-Id`) |
| `GET /files/{id}/content` | the baseline download, through the API |
| `GET /files/{id}` | metadata: owner, name, size, `pending` / `ready` / `failed` |
| `POST /files` `{"name", "contentType", "size"}` | a pending file and a presigned PUT URL (stage 1) |
| `POST /files/{id}/complete` | "I finished uploading": checks MinIO, marks ready |
| `POST /files/{id}/multipart` | a presigned URL per part (stage 2); again to resume |
| `GET /files/{id}/multipart/parts` | the parts MinIO already has |
| `POST /files/{id}/multipart/complete`, `DELETE /files/{id}/multipart` | finish or abandon a multipart upload |
| `POST /events/minio` | where MinIO sends bucket events (stage 3) |
| `POST /admin/sweep` | run the orphan sweeper now (stage 4) |
| `GET /files/{id}/download` | 302 to a presigned GET, owner only (stage 5) |
| `GET /stats` | bytes that went through the API process, its peak memory |

There is no auth: `X-User-Id: 1` says who you are.

## Setup

```
cd ~/code/system-design-patterns/04-large-blobs
docker compose up -d --wait                       # postgres :55732, MinIO :59000 (console :59001)
dotnet run -c Release --project src/LargeBlobs    # applies migrations, creates the bucket, listens on :5400
```

The MinIO console at http://localhost:59001 (minio / minio-secret) shows the bucket, its objects, and the events it sends. `minio/minio` is no longer on Docker Hub, so compose uses Chainguard's build of MinIO, pinned by digest.

Change behavior with environment variables, no code edits: `Blobs__UploadUrlSeconds=60`, `Blobs__PartSizeMb=16`, `Blobs__PendingHours=1`, `Blobs__DownloadSeconds=30`. All settings are in `src/LargeBlobs/appsettings.json` and `Options.cs`.

Tests run on their own containers (Testcontainers), not the compose stack. Each test gets its own database and bucket.

```
dotnet test                                # everything, about 20 s (a few tests wait for URLs to expire)
dotnet test --filter-namespace "*Stage2"   # one stage
```

Load test (`k6/upload.js` lists every setting):

```
k6 run -e MODE=direct k6/upload.js                    # 4 files of 200 MB through the API
k6 run -e MODE=presigned k6/upload.js                 # straight to MinIO
k6 run -e MODE=multipart -e SIZE_MB=500 k6/upload.js  # in parts
```

It prints the bytes that went through the API process and the process's peak memory. The peak is since the app started, so restart the app between runs you want to compare.

## How to run each stage

1. Answer the "before you code" questions in writing. Predict the numbers.
2. Run the k6 command for the stage on the current code and write the result in the table at the bottom.
3. Write the code until the stage's tests pass.
4. Run the same k6 command again. Compare with your prediction.
5. Try the mutation listed: break your code on purpose and check a test goes red.

---

## Stage 1: presigned uploads

**Before you code**

1. Run `k6 run -e MODE=direct k6/upload.js`. Where do the 200 MB sit while the request is in flight? What happens with 20 uploads at once, or one 2 GB file?
2. A presigned URL is a request MinIO will accept, signed with the app's secret, for one key, one method, until one time. What can a client do with it that you did not intend? What does signing the content type add?
3. After the client says "complete", what should the API check, and why a HEAD instead of a GET?

**Do**

- `Uploads/PresignedUploads.cs`. The class comment lists what the tests check.
- `dotnet test --filter-namespace "*Stage1"`. Two tests wait a few seconds for a URL to expire.
- Restart the app, then run `k6 run -e MODE=presigned k6/upload.js`. Compare `bytesThroughApi` and the peak memory with the direct run.

**Mutation:** leave `ContentType` out of the presigned request. Which test catches it? Then complete without checking MinIO.

**Afterwards:** the API never sees the bytes now. How would you scan an upload for malware, or check that a "photo" is an image? Where would that run, and what state would the file be in until it finishes?

---

## Stage 2: multipart and resumable

**Before you code**

1. A 2 GB upload fails at 1.9 GB over a phone connection. With one PUT, what does the retry cost? With 8 MB parts?
2. S3 caps a single PUT at 5 GB and a part at 5 GB, and needs parts of at least 5 MiB (except the last). How many parts does a 50 GB file need at 8 MB? What is the limit on parts?
3. The client crashes after part 7 of 20. What does it need to know to resume, and who should it ask?

**Do**

- `Uploads/MultipartUploads.cs`.
- `dotnet test --filter-namespace "*Stage2"`. The tests use 5 MiB parts.
- `k6 run -e MODE=multipart -e SIZE_MB=500 k6/upload.js`. Open the MinIO console during the run: where are the parts before you complete?

**Mutation:** start a new multipart upload every time `StartAsync` is called. Which test catches it? Then complete with whatever parts are there.

**Afterwards:** abort a multipart upload halfway and look at the bucket's size in the console. What is still stored, and who would ever delete it? That is stage 4.

---

## Stage 3: let storage tell you

**Before you code**

1. The client uploads, then crashes before calling complete. What state is the file in, and for how long?
2. MinIO delivers each event at least once. What must your handler do when the same event arrives twice?
3. Anyone can POST to `/events/minio`. What could they do with a forged event, and what does `WebhookToken` change?

**Do**

- `Events/ObjectCreatedHandler.cs`. `Events/S3Event.cs` has the event's shape.
- `dotnet test --filter-namespace "*Stage3"`. MinIO cannot reach the in-memory test server, so these tests POST the events MinIO would send.
- Now see the real thing. MinIO runs in a container and calls the app on the host, so the app must listen on an address the container can reach (this exposes it on your network while it runs):

  ```
  dotnet run -c Release --project src/LargeBlobs --urls http://0.0.0.0:5400
  k6 run -e MODE=presigned -e COMPLETE=0 k6/upload.js
  docker compose exec postgres psql -U postgres -d files -c "SELECT id, status FROM files ORDER BY id DESC LIMIT 4"
  ```

  The files are ready, and nobody called complete. Now take the app away while an upload lands:

  ```
  curl -s -X POST localhost:5400/files -H 'X-User-Id: 1' -H 'Content-Type: application/json' \
    -d '{"name": "a.txt", "contentType": "text/plain", "size": 5}'      # note the id and uploadUrl
  # stop the app (Ctrl+C), then upload with the URL: MinIO does not need the app for that
  curl -X PUT -H 'Content-Type: text/plain' --data-binary 'hello' '<uploadUrl>'
  # start the app again, then:
  curl -s localhost:5400/files/<id>
  ```

  MinIO queued the event while the app was down (`MINIO_NOTIFY_WEBHOOK_QUEUE_DIR_PRIMARY` in `compose.yaml`) and delivered it when the app came back.

**Mutation:** accept every event type, not only `s3:ObjectCreated:*`. Which test catches it? Then mark the file ready without comparing sizes.

**Afterwards:** you now have two ways a file becomes ready: complete, and the event. They can race. Is that a problem here, and what makes it safe (look at `MarkReadyAsync`'s `WHERE`)?

---

## Stage 4: clean up after abandoned uploads

**Before you code**

1. List the ways bytes or rows get left behind in this app. Which ones cost money, and which ones only clutter?
2. The sweeper deletes a pending file that is 24 hours old. Could a slow, honest client still be uploading it? What should `PendingHours` be relative to `UploadUrlSeconds`?
3. An object exists with no row. Is it always garbage? Think about the order your code writes them in.

**Do**

- `OrphanSweeper` in `Cleanup/OrphanSweeper.cs`.
- `dotnet test --filter-namespace "*Stage4"`. The tests insert rows and objects directly and move the clock forward.
- Leave a few multipart uploads open (start them, upload a part, stop), restart the app with `Blobs__PendingHours=0`, and `curl -s -X POST localhost:5400/admin/sweep`.

**Mutation:** delete objects with no row whatever their age. Which test catches it, and what would it delete in production?

**Afterwards:** S3 has lifecycle rules that abort incomplete multipart uploads after N days without any code. Which of your sweeper's jobs could a lifecycle rule do, and which need the database?

---

## Stage 5: signed downloads

**Before you code**

1. The baseline streams every download through the API. What does a 2 GB download cost the API, and what does a redirect cost?
2. A presigned GET works for anyone who has the URL until it expires. Who might get hold of it (browser history, a shared screenshot, a proxy log), and how long should it live?
3. Without `Content-Disposition: attachment`, what does a browser do with an HTML file someone uploaded, served from your domain?

**Do**

- `Downloads/DownloadLinks.cs`.
- `dotnet test --filter-namespace "*Stage5"`.
- `curl -s -i -H 'X-User-Id: 1' localhost:5400/files/1/download`, then open the `Location` URL in a browser. Wait `DownloadSeconds` and open it again.

**Mutation:** drop the owner check. Which test catches it? Then drop the `ContentDisposition` override.

**Afterwards:** put a CDN in front of MinIO for downloads. What happens to a signed URL's expiry when the CDN caches the response? How do CDN signed URLs or signed cookies differ?

---

## Results

Record your own numbers. Same machine; restart the app before each run so the peak memory is that run's.

| Run | k6 command | bytesThroughApi | peak working set (MB) | upload p50 | notes |
|---|---|---|---|---|---|
| baseline | `-e MODE=direct` | | | | |
| stage 1 | `-e MODE=presigned` | | | | |
| stage 2 | `-e MODE=multipart -e SIZE_MB=500` | | | | |
| stage 3 | `-e MODE=presigned -e COMPLETE=0` | | | | files ready without complete? |

<details><summary>What the baseline did when I built the lab (WSL2, 2026-11-24)</summary>

Four 200 MB uploads, two at a time, through `PUT /files/direct`: 800 MB went through the API process, its peak working set went from 114 MB to 1,652 MB, and the managed heap held 761 MB after the run. Each upload took about 1 s on localhost, so the problem here is memory, not time. Presigned and multipart uploads of the same files sent 0 bytes through the API.
</details>

## When you finish

1. Write 5 flashcards for the parts you got wrong or had to look up.
2. Ask Claude for a 10-minute mock: "Design Dropbox's upload and download path", Claude as interviewer. Cover the trade-off of each stage without notes.
3. Not covered here: content-addressed storage and deduplication (Dropbox's block hashing), and CDNs for downloads. Lab 9 (capstone) builds on this lab's presigned upload.
