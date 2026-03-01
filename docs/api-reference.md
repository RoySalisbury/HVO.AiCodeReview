# API Reference

## Table of Contents

- [POST /api/review](#post-apireview)
  - [DeltaInfo Object](#deltainfo-object)
- [GET /api/review/status/{sessionId}](#get-apireviewstatussessionid)
- [GET /api/review/queue](#get-apireviewqueue)
- [DELETE /api/review/{sessionId}](#delete-apireviewsessionid)
- [GET /api/review/metrics](#get-apireviewmetrics)
- [GET /api/review/health](#get-apireviewhealth)

---

## POST /api/review

Execute an AI code review for a pull request.

**Request:**

```http
POST /api/review
Content-Type: application/json

{
  "projectName": "OneVision",
  "repositoryName": "MyRepo",
  "pullRequestId": 12345,
  "reviewDepth": "Standard",
  "reviewStrategy": "FileByFile"
}
```

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `projectName` | string | Yes | — | Azure DevOps project name. |
| `repositoryName` | string | Yes | — | Repository name or GUID. |
| `pullRequestId` | int | Yes | — | Pull request ID (must be > 0). |
| `reviewDepth` | string | No | `"Standard"` | Review depth mode: `"Quick"`, `"Standard"`, or `"Deep"`. See [Review Depth Modes](architecture.md#review-depth-modes). |
| `reviewStrategy` | string | No | `"FileByFile"` | Pass 2 strategy: `"FileByFile"`, `"Vector"`, or `"Auto"`. See [Review Strategies](architecture.md#review-strategies). |
| `enableSecurityPass` | bool? | No | `null` | Enable/disable the dedicated security review pass for this request. `true` = always run, `false` = never run, `null` = use global `AiProvider:SecurityPassEnabled` setting. |

**Response — Reviewed (Full Review or Re-Review):**

```json
{
  "status": "Reviewed",
  "recommendation": "ApprovedWithSuggestions",
  "summary": "## Code Review #1 -- PR #12345\n\n### Summary\n5 files changed (3 edits, 2 adds, 0 deletes)...",
  "issueCount": 4,
  "errorCount": 0,
  "warningCount": 1,
  "infoCount": 3,
  "errorMessage": null,
  "reviewDepth": "Standard",
  "vote": 5,
  "promptTokens": 12500,
  "completionTokens": 3200,
  "totalTokens": 15700,
  "aiDurationMs": 5400,
  "estimatedCost": 0.0038
}
```

**Response — Skipped (no changes since last review):**

```json
{
  "status": "Skipped",
  "recommendation": null,
  "summary": "This PR has already been reviewed. No new changes detected since the last review.",
  "issueCount": 0,
  "errorCount": 0,
  "warningCount": 0,
  "infoCount": 0,
  "errorMessage": null,
  "vote": null
}
```

**Response — Rate Limited:**

```json
{
  "status": "RateLimited",
  "recommendation": null,
  "summary": "This PR was reviewed too recently. Please wait 115 seconds before requesting another review. (Cooldown: 5 min, last reviewed: 2026-02-14 00:50:18 UTC, next allowed: 2026-02-14 00:55:18 UTC)",
  "issueCount": 0,
  "errorCount": 0,
  "warningCount": 0,
  "infoCount": 0,
  "errorMessage": null,
  "vote": null
}
```

**Response — Error:**

```json
{
  "status": "Error",
  "recommendation": null,
  "summary": null,
  "issueCount": 0,
  "errorCount": 0,
  "warningCount": 0,
  "infoCount": 0,
  "errorMessage": "Detailed error message here.",
  "vote": null
}
```

**Status Codes:**

| Code | When |
|------|------|
| `200 OK` | Review completed, skipped, or rate-limited (queue disabled). |
| `202 Accepted` | Review queued for background processing (queue enabled). Response body includes `sessionId`; the status URL is returned in the `Location` header. |
| `400 Bad Request` | Invalid request body. |
| `500 Internal Server Error` | Unhandled exception (returned as `status: "Error"`). |
| `503 Service Unavailable` | Review queue is full (queue enabled). |

**Response — Queued (queue enabled):**

When the review queue is enabled (`ReviewQueue:Enabled = true`), the API returns `202 Accepted` immediately:

```json
{
  "sessionId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "status": "Queued",
  "summary": "Review has been queued for processing."
}
```

The `Location` header contains the status polling URL: `/api/review/status/{sessionId}`.

**Response Field Reference:**

| Field | Type | Description |
|-------|------|-------------|
| `status` | string | `"Reviewed"`, `"Skipped"`, `"RateLimited"`, or `"Error"`. |
| `recommendation` | string? | `"Approved"`, `"ApprovedWithSuggestions"`, `"NeedsWork"`, `"Rejected"`, or `null`. |
| `summary` | string? | The full Markdown summary posted to the PR thread. |
| `issueCount` | int | Total inline comments posted. |
| `errorCount` | int | Bug / Security severity comments. |
| `warningCount` | int | Concern / Performance severity comments. |
| `infoCount` | int | Suggestion / LGTM / Info severity comments. |
| `errorMessage` | string? | Error details if `status` is `"Error"`. |
| `reviewDepth` | string? | The review depth used: `"Quick"`, `"Standard"`, or `"Deep"`. `null` for non-reviewed responses. |
| `vote` | int? | Vote cast: `10` (Approved), `5` (Approved w/ Suggestions), `-5` (Waiting for Author), `-10` (Rejected), or `null`. |
| `promptTokens` | int? | Total prompt (input) tokens consumed across all AI calls. |
| `completionTokens` | int? | Total completion (output) tokens consumed. |
| `totalTokens` | int? | Sum of prompt + completion tokens. |
| `aiDurationMs` | long? | AI inference time in milliseconds (sum of all AI calls). |
| `estimatedCost` | decimal? | Estimated cost in USD based on model pricing and actual token usage. Requires pricing data in the model adapter. |
| `securityFindingCount` | int? | Number of security findings from the dedicated security pass. `null` when the security pass was not enabled. |
| `deltaInfo` | object? | Delta (incremental) review details. Present only for re-reviews where a subset of files were reviewed. See [DeltaInfo Object](#deltainfo-object). |

#### DeltaInfo Object

When a re-review detects that only a subset of files changed since the last review, the response includes a `deltaInfo` object:

| Field | Type | Description |
|-------|------|-------------|
| `isDeltaReview` | bool | Always `true` when this object is present. |
| `baseIteration` | int | The last-reviewed iteration (comparison base). |
| `currentIteration` | int | The current iteration being reviewed. |
| `totalFilesInPr` | int | Total files in the PR. |
| `deltaFilesReviewed` | int | Number of files that changed and were sent to AI. |
| `carriedForwardFiles` | int | Number of unchanged files with results carried forward. |
| `changedFilePaths` | string[] | Paths of files reviewed in this pass. |
| `carriedForwardFilePaths` | string[] | Paths of files carried forward from the prior review. |
| `estimatedTokenSavings` | int | Approximate tokens saved by not re-reviewing unchanged files. |

---

## GET /api/review/status/{sessionId}

Poll the status of a queued or completed review session. Only available when the review queue is enabled.

**Request:**

```http
GET /api/review/status/a1b2c3d4-e5f6-7890-abcd-ef1234567890
```

**Response — Queued/InProgress:**

```json
{
  "sessionId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "status": "InProgress"
}
```

**Response — Completed:**

```json
{
  "sessionId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "status": "Completed",
  "recommendation": "ApprovedWithSuggestions",
  "vote": 5
}
```

| Code | When |
|------|------|
| `200 OK` | Session found (any status). |
| `404 Not Found` | Session ID not found, or queue is not enabled. |

---

## GET /api/review/queue

List all active (queued and in-progress) review sessions with queue statistics.

**Request:**

```http
GET /api/review/queue
```

**Response:**

```json
{
  "enabled": true,
  "maxConcurrentReviews": 3,
  "maxQueueDepth": 50,
  "queuedCount": 2,
  "inProgressCount": 1,
  "sessions": [
    {
      "sessionId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "pullRequestId": 12345,
      "project": "OneVision",
      "repository": "MyRepo",
      "status": "InProgress",
      "requestedAtUtc": "2026-02-14T00:50:18.123Z"
    },
    {
      "sessionId": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
      "pullRequestId": 12346,
      "project": "OneVision",
      "repository": "OtherRepo",
      "status": "Queued",
      "requestedAtUtc": "2026-02-14T00:51:00.000Z"
    }
  ]
}
```

When the queue is disabled, returns `{ "enabled": false, "sessions": [] }`.

---

## DELETE /api/review/{sessionId}

Cancel a queued review session. Only sessions with status `Queued` can be cancelled — sessions that are already `InProgress` return `409 Conflict`.

**Request:**

```http
DELETE /api/review/a1b2c3d4-e5f6-7890-abcd-ef1234567890
```

**Response — Success:**

```json
{
  "status": "Cancelled",
  "sessionId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
}
```

| Code | When |
|------|------|
| `200 OK` | Session cancelled successfully. |
| `404 Not Found` | Session ID not found, or queue is not enabled. |
| `409 Conflict` | Session is already `InProgress`, `Completed`, or `Failed`. |

---

## GET /api/review/metrics

Get full review history and aggregated AI metrics for a pull request. All data comes from PR properties — not affected by tag deletion.

**Request:**

```http
GET /api/review/metrics?project=OneVision&repository=MyRepo&pullRequestId=12345
```

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project` | string | Yes | Azure DevOps project name. |
| `repository` | string | Yes | Repository name. |
| `pullRequestId` | int | Yes | Pull request ID. |

**Response:**

```json
{
  "pullRequestId": 12345,
  "reviewCount": 3,
  "lastReviewedAtUtc": "2026-02-14T00:50:18.123Z",
  "lastSourceCommit": "abc1234def5678",
  "lastIteration": 2,
  "wasDraft": false,
  "voteSubmitted": true,
  "history": [
    {
      "reviewNumber": 1,
      "reviewedAtUtc": "2026-02-13T22:15:00Z",
      "action": "Full Review",
      "verdict": "APPROVED WITH SUGGESTIONS",
      "sourceCommit": "abc1234",
      "iteration": 1,
      "isDraft": true,
      "inlineComments": 4,
      "filesChanged": 37,
      "vote": null,
      "modelName": "gpt-4o-2024-08-06",
      "promptTokens": 12500,
      "completionTokens": 3200,
      "totalTokens": 15700,
      "aiDurationMs": 5400,
      "totalDurationMs": 8200,
      "estimatedCost": 0.0511
    },
    {
      "reviewNumber": 2,
      "reviewedAtUtc": "2026-02-14T00:50:18Z",
      "action": "Skipped",
      "verdict": "No Changes",
      "sourceCommit": "abc1234",
      "iteration": 1,
      "isDraft": false,
      "inlineComments": 0,
      "filesChanged": 0,
      "vote": null,
      "modelName": null,
      "promptTokens": null,
      "completionTokens": null,
      "totalTokens": null,
      "aiDurationMs": null,
      "totalDurationMs": null
    }
  ],
  "totalPromptTokens": 12500,
  "totalCompletionTokens": 3200,
  "totalTokensUsed": 15700,
  "totalAiDurationMs": 5400,
  "totalDurationMs": 8200
}
```

---

## GET /api/review/health

Health check endpoint. Verifies the service is running and can reach Azure DevOps.

**Request:**

```http
GET /api/review/health
```

**Response (healthy):**

```json
{
  "status": "healthy",
  "timestamp": "2026-02-14T00:55:00.000Z",
  "azureDevOps": "connected"
}
```

**Response (healthy, queue enabled):**

```json
{
  "status": "healthy",
  "timestamp": "2026-02-14T00:55:00.000Z",
  "azureDevOps": "connected",
  "queue": {
    "enabled": true,
    "queued": 2,
    "inProgress": 1,
    "total": 5
  }
}
```

**Response (degraded):**

```json
{
  "status": "degraded",
  "timestamp": "2026-02-14T00:55:00.000Z",
  "azureDevOps": "unreachable: Connection refused"
}
```

| Code | Status | When |
|------|--------|------|
| `200` | healthy | Service running, Azure DevOps reachable |
| `503` | degraded | Service running, Azure DevOps unreachable |
