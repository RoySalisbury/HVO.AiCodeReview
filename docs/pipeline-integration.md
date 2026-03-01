# Azure DevOps Pipeline Integration

## Table of Contents

- [Pipeline YAML](#pipeline-yaml)
- [Pipeline Variables](#pipeline-variables)
- [Optional: Fail Pipeline on "Needs Work"](#optional-fail-pipeline-on-needs-work)
- [Optional: Gate with Review Status](#optional-gate-with-review-status)
- [Scripts](#scripts)
- [Related Documentation](#related-documentation)

To automatically trigger a review on every pull request, add a step to your PR validation pipeline that calls the API.

## Pipeline YAML

```yaml
trigger: none

pr:
  branches:
    include:
      - main
      - develop

variables:
  # URL where the AI Code Review service is hosted
  aiReviewServiceUrl: 'https://your-ai-review-service.azurewebsites.net'

jobs:
  - job: AICodeReview
    displayName: 'AI Code Review'
    pool:
      vmImage: 'ubuntu-latest'
    steps:
      - task: Bash@3
        displayName: 'Request AI Code Review'
        inputs:
          targetType: inline
          script: |
            RESPONSE=$(curl -s -w "\n%{http_code}" -X POST \
              "$(aiReviewServiceUrl)/api/review" \
              -H "Content-Type: application/json" \
              -d '{
                "projectName": "$(System.TeamProject)",
                "repositoryName": "$(Build.Repository.Name)",
                "pullRequestId": $(System.PullRequest.PullRequestId)
              }')

            HTTP_CODE=$(echo "$RESPONSE" | tail -1)
            BODY=$(echo "$RESPONSE" | sed '$d')

            echo "HTTP Status: $HTTP_CODE"
            echo "Response: $BODY"

            # Extract status from JSON response
            STATUS=$(echo "$BODY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('status',''))")

            if [ "$STATUS" = "Error" ]; then
              echo "##vso[task.logissue type=error]AI Code Review failed"
              exit 1
            fi

            echo "AI Review Status: $STATUS"
```

## Pipeline Variables

The pipeline YAML above uses a mix of built-in and user-defined variables. The built-in variables are **automatically populated by Azure DevOps at runtime** — you do not need to set them. The only variable you need to configure is the service URL.

| Variable | Auto / Manual | Description |
|----------|:---:|-------------|
| `$(System.TeamProject)` | Auto | Azure DevOps project name. Populated automatically when the pipeline runs. |
| `$(Build.Repository.Name)` | Auto | Repository name. Populated automatically when the pipeline runs. |
| `$(System.PullRequest.PullRequestId)` | Auto | The PR ID that triggered this pipeline run. **Only available on PR-triggered pipelines** (`pr:` trigger). Azure DevOps injects this automatically — the pipeline step passes it in the API request body so the service knows which PR to review. |
| `$(aiReviewServiceUrl)` | Manual | The base URL where the AI Code Review service is hosted. Set this as a pipeline variable or in a variable group (e.g., `https://your-ai-review-service.azurewebsites.net`). |

## Optional: Fail Pipeline on "Needs Work"

```yaml
      - task: Bash@3
        displayName: 'Check AI Review Verdict'
        inputs:
          targetType: inline
          script: |
            RECOMMENDATION=$(echo "$BODY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('recommendation',''))")

            if [ "$RECOMMENDATION" = "NeedsWork" ] || [ "$RECOMMENDATION" = "Rejected" ]; then
              echo "##vso[task.logissue type=warning]AI Review verdict: $RECOMMENDATION"
              # Uncomment the next line to fail the build on NeedsWork/Rejected
              # exit 1
            fi
```

## Optional: Gate with Review Status

If you want the pipeline to pass even when the AI review finds issues (advisory mode), check the `status` field only:

```yaml
            if [ "$STATUS" = "Reviewed" ] || [ "$STATUS" = "Skipped" ] || [ "$STATUS" = "RateLimited" ]; then
              echo "AI Code Review completed successfully."
            else
              echo "##vso[task.logissue type=error]Unexpected status: $STATUS"
              exit 1
            fi
```

## Scripts

The `scripts/` folder contains pipeline integration helpers:

| File | Description |
|------|-------------|
| `ai-code-review.sh` | Bash script for Azure Pipelines. Calls the review API via `curl`/`jq`, sets pipeline output variables (`AI_REVIEW_STATUS`, `AI_REVIEW_RECOMMENDATION`, `AI_REVIEW_ISSUE_COUNT`), and fails the pipeline on "Rejected" or warns on "NeedsWork". |
| `azure-pipelines-template.yml` | Azure Pipelines YAML template with two options: inline Bash (Option A) or external script file (Option B). Conditionally runs on PR builds only. |

---

## Related Documentation

- [API Reference](api-reference.md) — Full endpoint documentation for the review API.
- [Configuration](configuration.md) — Service configuration and environment variables.
- [Architecture](architecture.md) — Review flow, depth modes, and decision logic.
- [Getting Started](getting-started.md) — Prerequisites and quick start guide.
