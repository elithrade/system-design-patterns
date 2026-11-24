namespace ScalingReads;

// One post as the API returns it. The parameter names and types match the query columns, so Dapper
// can build it from a row (author_name -> AuthorName, like_count -> LikeCount).
public sealed record Post(
    long Id,
    long AuthorId,
    string AuthorName,
    string Caption,
    DateTime CreatedAt,
    long LikeCount);
