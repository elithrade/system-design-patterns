-- profile page: WHERE author_id = ? ORDER BY created_at DESC LIMIT n
CREATE INDEX posts_author_created_idx ON posts (author_id, created_at DESC);

ALTER TABLE posts ADD COLUMN like_count bigint NOT NULL DEFAULT 0;

-- backfill the like_count column
UPDATE posts p
SET like_count = c.n
FROM (
    SELECT post_id, count(*) AS n
    FROM likes
    GROUP BY post_id
) c
WHERE c.post_id = p.id;
