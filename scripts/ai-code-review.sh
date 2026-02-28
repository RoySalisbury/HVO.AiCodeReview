#!/usr/bin/env bash
# ===========================================================================
# AI Code Review Pipeline Script
# ===========================================================================
# Requests an AI code review from the AiCodeReview API service.
# Designed to run as a Bash step in an Azure DevOps pipeline.
#
# This script calls the centralized AI Code Review API to review a pull request.
# It uses built-in Azure DevOps pipeline variables to identify the project,
# repository, and PR. The API URL must be provided as a parameter or via the
# CODE_REVIEW_API_URL pipeline variable.
#
# Pipeline status output uses ##vso logging commands so Azure DevOps displays
# progress and results inline.
#
# Parameters (environment variables or positional):
#   API_URL           - Base URL of the AI Code Review API
#   PROJECT_NAME      - Azure DevOps project name  (default: $SYSTEM_TEAMPROJECT)
#   REPOSITORY_NAME   - Repository name             (default: $BUILD_REPOSITORY_NAME)
#   PULL_REQUEST_ID   - Pull request ID             (default: $SYSTEM_PULLREQUEST_PULLREQUESTID)
#   TIMEOUT_SECONDS   - Wait timeout                (default: 600)
#   REVIEW_STRATEGY   - FileByFile | Auto | Vector  (default: FileByFile)
#
# Usage:
#   # In an Azure DevOps pipeline step:
#   bash scripts/ai-code-review.sh
#
#   # With explicit parameters:
#   API_URL=https://myservice.azurewebsites.net bash scripts/ai-code-review.sh
# ===========================================================================
set -euo pipefail

# ---------------------------------------------------------------------------
# Parameter defaults from environment / pipeline variables
# ---------------------------------------------------------------------------
API_URL="${API_URL:-${CODE_REVIEW_API_URL:-}}"
PROJECT_NAME="${PROJECT_NAME:-${SYSTEM_TEAMPROJECT:-}}"
REPOSITORY_NAME="${REPOSITORY_NAME:-${BUILD_REPOSITORY_NAME:-}}"
PULL_REQUEST_ID="${PULL_REQUEST_ID:-${SYSTEM_PULLREQUEST_PULLREQUESTID:-}}"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-600}"
REVIEW_STRATEGY="${REVIEW_STRATEGY:-FileByFile}"

# ---------------------------------------------------------------------------
# Validate required parameters
# ---------------------------------------------------------------------------
if [[ -z "${API_URL}" ]]; then
    echo "##vso[task.logissue type=error]ApiUrl is required. Set CODE_REVIEW_API_URL variable or API_URL env var."
    echo "##vso[task.complete result=Failed;]Missing API URL"
    exit 1
fi

if [[ -z "${PULL_REQUEST_ID}" ]]; then
    echo "##vso[task.logissue type=warning]No PullRequestId found. This step should only run in PR builds."
    echo "Skipping AI code review -- not a PR build."
    exit 0
fi

# Validate review strategy
case "${REVIEW_STRATEGY}" in
    FileByFile|Auto|Vector) ;;
    *)
        echo "##vso[task.logissue type=error]Invalid REVIEW_STRATEGY '${REVIEW_STRATEGY}'. Must be FileByFile, Auto, or Vector."
        echo "##vso[task.complete result=Failed;]Invalid review strategy"
        exit 1
        ;;
esac

# ---------------------------------------------------------------------------
# Submit review request
# ---------------------------------------------------------------------------
echo "##vso[task.setprogress value=5]Submitting code review request..."
echo ""
echo "================================================================"
echo "  AI Code Review"
echo "================================================================"
echo "  Project:    ${PROJECT_NAME}"
echo "  Repository: ${REPOSITORY_NAME}"
echo "  PR:         #${PULL_REQUEST_ID}"
echo "  API:        ${API_URL}"
echo "  Strategy:   ${REVIEW_STRATEGY}"
echo "================================================================"
echo ""

BODY=$(cat <<EOF
{
    "projectName": "${PROJECT_NAME}",
    "repositoryName": "${REPOSITORY_NAME}",
    "pullRequestId": ${PULL_REQUEST_ID},
    "reviewStrategy": "${REVIEW_STRATEGY}"
}
EOF
)

echo "Sending review request..."
echo "##vso[task.setprogress value=10]Analyzing pull request..."

RESPONSE=$(curl -s -S --fail \
    --max-time "${TIMEOUT_SECONDS}" \
    -H "Content-Type: application/json" \
    -d "${BODY}" \
    "${API_URL}/api/review" 2>&1) || {
    echo "##vso[task.logissue type=error]AI Code Review failed: ${RESPONSE}"
    echo "##vso[task.complete result=Failed;]AI Code Review Error"
    echo ""
    echo "Error details:"
    echo "${RESPONSE}"
    exit 1
}

# ---------------------------------------------------------------------------
# Parse response (requires jq)
# ---------------------------------------------------------------------------
STATUS=$(echo "${RESPONSE}" | jq -r '.status // "unknown"')
RECOMMENDATION=$(echo "${RESPONSE}" | jq -r '.recommendation // "unknown"')
ISSUE_COUNT=$(echo "${RESPONSE}" | jq -r '.issueCount // 0')
ERROR_COUNT=$(echo "${RESPONSE}" | jq -r '.errorCount // 0')
WARNING_COUNT=$(echo "${RESPONSE}" | jq -r '.warningCount // 0')
INFO_COUNT=$(echo "${RESPONSE}" | jq -r '.infoCount // 0')
VOTE=$(echo "${RESPONSE}" | jq -r '.vote // "N/A"')

echo "##vso[task.setprogress value=100]Review complete"
echo ""
echo "================================================================"
echo "  AI Code Review Results"
echo "================================================================"
echo "  Status:       ${STATUS}"
echo "  Verdict:      ${RECOMMENDATION}"
echo "  Total Issues: ${ISSUE_COUNT}"
echo "    Errors:     ${ERROR_COUNT}"
echo "    Warnings:   ${WARNING_COUNT}"
echo "    Info:       ${INFO_COUNT}"
echo "  Vote:         ${VOTE}"
echo "================================================================"
echo ""

# Set output variables for downstream tasks
echo "##vso[task.setvariable variable=AI_REVIEW_STATUS]${STATUS}"
echo "##vso[task.setvariable variable=AI_REVIEW_RECOMMENDATION]${RECOMMENDATION}"
echo "##vso[task.setvariable variable=AI_REVIEW_ISSUE_COUNT]${ISSUE_COUNT}"

# Determine pipeline outcome based on recommendation
case "${RECOMMENDATION}" in
    Rejected)
        echo "##vso[task.logissue type=error]AI Code Review: REJECTED -- Critical issues found"
        echo "##vso[task.complete result=Failed;]AI Code Review REJECTED"
        ;;
    NeedsWork)
        echo "##vso[task.logissue type=warning]AI Code Review: NEEDS WORK -- Issues found that should be addressed"
        # Warning only, doesn't fail the pipeline
        ;;
    ApprovedWithSuggestions)
        echo "AI Code Review: APPROVED WITH SUGGESTIONS"
        ;;
    Approved)
        echo "AI Code Review: APPROVED"
        ;;
    *)
        echo "AI Code Review completed with status: ${STATUS}"
        ;;
esac
