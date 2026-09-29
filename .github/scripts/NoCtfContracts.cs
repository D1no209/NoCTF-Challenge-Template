using System.Text.Json.Nodes;

internal sealed record CreateChallengeTemplateRequest(
    Guid Id,
    string Mode,
    string Visibility,
    string Title,
    string? Description,
    string Direction,
    JsonObject Definition);

internal sealed record ChallengeTemplateContentPatchRequest(
    string Mode,
    string Visibility,
    string Title,
    string? Description,
    string Direction,
    JsonObject Definition);

internal sealed record PatchChallengeTemplateRequest(
    ChallengeTemplateContentPatchRequest Content);

internal sealed record CompetitionChallengeRulesPatchRequest(
    JsonObject Configuration);

internal sealed record ChallengeTemplateResponse(
    Guid Id,
    Guid OwnerId,
    IReadOnlyList<Guid> ManagerIds,
    string Mode,
    string Visibility,
    string Title,
    string? Description,
    string Direction,
    JsonObject Definition,
    DateTimeOffset? DeletedAt,
    int ActiveCompetitionReferenceCount);

internal sealed record CompetitionChallengeResponse(
    Guid Id,
    Guid ChallengeId,
    string? CustomTitle,
    int Order,
    bool IsPublished,
    DateTimeOffset? DeletedAt);

internal sealed record AdminCompetitionChallengeResponse(
    CompetitionChallengeResponse Challenge,
    string Mode,
    string CompetitionStatus,
    JsonObject Rules);
