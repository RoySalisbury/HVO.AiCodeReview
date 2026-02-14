<#
.SYNOPSIS
    Requests an AI code review from the AiCodeReview API service.
    Designed to run as a PowerShell step in an Azure DevOps pipeline.

.DESCRIPTION
    This script calls the centralized AI Code Review API to review a pull request.
    It uses built-in Azure DevOps pipeline variables to identify the project,
    repository, and PR. The API URL must be provided as a parameter or via the
    CODE_REVIEW_API_URL pipeline variable.

    Pipeline status output uses ##vso logging commands so Azure DevOps displays
    progress and results inline.

.PARAMETER ApiUrl
    Base URL of the AI Code Review API (e.g., https://your-service.azurewebsites.net).
    Defaults to the CODE_REVIEW_API_URL environment variable.

.PARAMETER ProjectName
    Azure DevOps project name. Defaults to $(System.TeamProject).

.PARAMETER RepositoryName
    Repository name. Defaults to $(Build.Repository.Name).

.PARAMETER PullRequestId
    Pull request ID. Defaults to $(System.PullRequest.PullRequestId).

.PARAMETER TimeoutSeconds
    How long to wait for the review to complete. Default: 600 (10 minutes).

.EXAMPLE
    # In an Azure DevOps pipeline step:
    pwsh -File pipeline/ai-code-review.ps1 -ApiUrl "https://myservice.azurewebsites.net"
#>
param(
    [string]$ApiUrl = $env:CODE_REVIEW_API_URL,
    [string]$ProjectName = $env:SYSTEM_TEAMPROJECT,
    [string]$RepositoryName = $env:BUILD_REPOSITORY_NAME,
    [string]$PullRequestId = $env:SYSTEM_PULLREQUEST_PULLREQUESTID,
    [int]$TimeoutSeconds = 600
)

# Validate required parameters
if ([string]::IsNullOrWhiteSpace($ApiUrl)) {
    Write-Host "##vso[task.logissue type=error]ApiUrl is required. Set CODE_REVIEW_API_URL variable or pass -ApiUrl."
    Write-Host "##vso[task.complete result=Failed;]Missing API URL"
    exit 1
}

if ([string]::IsNullOrWhiteSpace($PullRequestId)) {
    Write-Host "##vso[task.logissue type=warning]No PullRequestId found. This step should only run in PR builds."
    Write-Host "Skipping AI code review -- not a PR build."
    exit 0
}

# Start the review
Write-Host "##vso[task.setprogress value=5]Submitting code review request..."
Write-Host ""
Write-Host "================================================================"
Write-Host "  AI Code Review"
Write-Host "================================================================"
Write-Host "  Project:    $ProjectName"
Write-Host "  Repository: $RepositoryName"
Write-Host "  PR:         #$PullRequestId"
Write-Host "  API:        $ApiUrl"
Write-Host "================================================================"
Write-Host ""

$body = @{
    projectName    = $ProjectName
    repositoryName = $RepositoryName
    pullRequestId  = [int]$PullRequestId
} | ConvertTo-Json

try {
    Write-Host "Sending review request..."
    Write-Host "##vso[task.setprogress value=10]Analyzing pull request..."

    $response = Invoke-RestMethod -Uri "$ApiUrl/api/review" `
        -Method POST `
        -Body $body `
        -ContentType "application/json" `
        -TimeoutSec $TimeoutSeconds

    Write-Host "##vso[task.setprogress value=100]Review complete"
    Write-Host ""
    Write-Host "================================================================"
    Write-Host "  AI Code Review Results"
    Write-Host "================================================================"
    Write-Host "  Status:       $($response.status)"
    Write-Host "  Verdict:      $($response.recommendation)"
    Write-Host "  Total Issues: $($response.issueCount)"
    Write-Host "    Errors:     $($response.errorCount)"
    Write-Host "    Warnings:   $($response.warningCount)"
    Write-Host "    Info:       $($response.infoCount)"
    Write-Host "  Vote:         $($response.vote)"
    Write-Host "================================================================"
    Write-Host ""

    # Set output variables for downstream tasks
    Write-Host "##vso[task.setvariable variable=AI_REVIEW_STATUS]$($response.status)"
    Write-Host "##vso[task.setvariable variable=AI_REVIEW_RECOMMENDATION]$($response.recommendation)"
    Write-Host "##vso[task.setvariable variable=AI_REVIEW_ISSUE_COUNT]$($response.issueCount)"

    # Determine pipeline outcome based on recommendation
    switch ($response.recommendation) {
        "Rejected" {
            Write-Host "##vso[task.logissue type=error]AI Code Review: REJECTED -- Critical issues found"
            Write-Host "##vso[task.complete result=Failed;]AI Code Review REJECTED"
        }
        "NeedsWork" {
            Write-Host "##vso[task.logissue type=warning]AI Code Review: NEEDS WORK -- Issues found that should be addressed"
            # Warning only, doesn't fail the pipeline
        }
        "ApprovedWithSuggestions" {
            Write-Host "AI Code Review: APPROVED WITH SUGGESTIONS"
        }
        "Approved" {
            Write-Host "AI Code Review: APPROVED"
        }
        default {
            Write-Host "AI Code Review completed with status: $($response.status)"
        }
    }
}
catch {
    Write-Host "##vso[task.logissue type=error]AI Code Review failed: $($_.Exception.Message)"
    Write-Host "##vso[task.complete result=Failed;]AI Code Review Error"
    Write-Host ""
    Write-Host "Error details:"
    Write-Host $_.Exception.Message
    if ($_.Exception.StackTrace) {
        Write-Host "Stack trace:"
        Write-Host $_.Exception.StackTrace
    }
    if ($_.ErrorDetails.Message) {
        Write-Host $_.ErrorDetails.Message
    }
    exit 1
}
