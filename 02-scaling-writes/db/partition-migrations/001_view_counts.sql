-- Applied to each partition database (stage 3). A partition holds counts only for the videos that
-- hash to it; titles stay in the main database.
CREATE TABLE view_counts (
    video_id bigint PRIMARY KEY,
    views    bigint NOT NULL
);
