using AiCodeReview.Models;
using AiCodeReview.Services;

namespace AiCodeReview.Tests.Helpers;

/// <summary>
/// In-memory fake implementation of <see cref="IDevOpsService"/> that stores
/// all state locally.  Lets tests exercise the full orchestrator + dedup +
/// metadata pipeline without calling a real Azure DevOps (or GitHub) backend.
///
/// By default every method returns a sensible empty/default value. Override
/// individual factory delegates (e.g. <see cref="PullRequestFactory"/>) to
/// inject test-specific data.
/// </summary>
public class FakeDevOpsService : IDevOpsService
{
    // ── In-memory stores keyed by "project/repo/prId" ───────────────────
    private readonly Dictionary<string, PullRequestInfo> _pullRequests = new();
    private readonly Dictionary<string, List<FileChange>> _fileChanges = new();
    private readonly Dictionary<string, ReviewMetadata> _metadata = new();
    private readonly Dictionary<string, List<ReviewHistoryEntry>> _history = new();
    private readonly Dictionary<string, List<ExistingCommentThread>> _threads = new();
    private readonly Dictionary<string, List<PostedComment>> _postedComments = new();
    private readonly Dictionary<string, List<PostedInlineComment>> _postedInlineComments = new();
    private readonly Dictionary<string, bool> _tags = new();
    private readonly Dictionary<string, int> _votes = new();
    private readonly Dictionary<string, string> _descriptions = new();
    private readonly Dictionary<string, List<int>> _linkedWorkItems = new();
    private readonly Dictionary<int, WorkItemInfo> _workItems = new();
    private readonly Dictionary<int, List<WorkItemComment>> _workItemComments = new();
    private readonly Dictionary<string, int> _iterationCounts = new();

    private string Key(string project, string repo, int prId) => $"{project}/{repo}/{prId}";

    // ── Observability: what the orchestrator posted ──────────────────────

    /// <summary>All general comments posted via <see cref="PostCommentThreadAsync"/>.</summary>
    public IReadOnlyList<PostedComment> PostedComments(string project, string repo, int prId)
        => _postedComments.TryGetValue(Key(project, repo, prId), out var list) ? list : [];

    /// <summary>All inline comments posted via <see cref="PostInlineCommentThreadAsync"/>.</summary>
    public IReadOnlyList<PostedInlineComment> PostedInlineComments(string project, string repo, int prId)
        => _postedInlineComments.TryGetValue(Key(project, repo, prId), out var list) ? list : [];

    /// <summary>The last vote submitted via <see cref="AddReviewerAsync"/>.</summary>
    public int? LastVote(string project, string repo, int prId)
        => _votes.TryGetValue(Key(project, repo, prId), out var v) ? v : null;

    // ── Setup helpers ───────────────────────────────────────────────────

    /// <summary>Seed a PR so <see cref="GetPullRequestAsync"/> returns it.</summary>
    public void SeedPullRequest(string project, string repo, PullRequestInfo pr)
        => _pullRequests[Key(project, repo, pr.PullRequestId)] = pr;

    /// <summary>Seed file changes so <see cref="GetPullRequestChangesAsync"/> returns them.</summary>
    public void SeedFileChanges(string project, string repo, int prId, List<FileChange> changes)
        => _fileChanges[Key(project, repo, prId)] = changes;

    /// <summary>Seed existing threads for dedup testing.</summary>
    public void SeedExistingThreads(string project, string repo, int prId, List<ExistingCommentThread> threads)
        => _threads[Key(project, repo, prId)] = threads;

    /// <summary>Seed linked work item IDs.</summary>
    public void SeedLinkedWorkItems(string project, string repo, int prId, List<int> workItemIds)
        => _linkedWorkItems[Key(project, repo, prId)] = workItemIds;

    /// <summary>Seed a work item.</summary>
    public void SeedWorkItem(WorkItemInfo workItem)
        => _workItems[workItem.Id] = workItem;

    /// <summary>Seed work item comments.</summary>
    public void SeedWorkItemComments(int workItemId, List<WorkItemComment> comments)
        => _workItemComments[workItemId] = comments;

    /// <summary>Seed the iteration count for a PR.</summary>
    public void SeedIterationCount(string project, string repo, int prId, int count)
        => _iterationCounts[Key(project, repo, prId)] = count;

    // ── Optional factory overrides (like FakeCodeReviewService pattern) ─

    /// <summary>Override to return custom PR info.</summary>
    public Func<string, string, int, PullRequestInfo>? PullRequestFactory { get; set; }

    /// <summary>Override to return custom file changes.</summary>
    public Func<string, string, int, PullRequestInfo, List<FileChange>>? FileChangesFactory { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    //  IDevOpsService implementation
    // ═══════════════════════════════════════════════════════════════════

    public Task<PullRequestInfo> GetPullRequestAsync(string project, string repository, int pullRequestId)
    {
        if (PullRequestFactory is not null)
            return Task.FromResult(PullRequestFactory(project, repository, pullRequestId));

        if (_pullRequests.TryGetValue(Key(project, repository, pullRequestId), out var pr))
            return Task.FromResult(pr);

        // Return a sensible default PR
        return Task.FromResult(new PullRequestInfo
        {
            PullRequestId = pullRequestId,
            Title = $"Test PR #{pullRequestId}",
            Description = "Fake PR for testing.",
            SourceBranch = "refs/heads/feature/test",
            TargetBranch = "refs/heads/main",
            CreatedBy = "Test User",
            CreatedDate = DateTime.UtcNow,
            Status = "active",
            LastMergeSourceCommit = $"abc{pullRequestId:D5}",
            LastMergeTargetCommit = $"def{pullRequestId:D5}",
            IsDraft = false,
        });
    }

    public Task<int> GetIterationCountAsync(string project, string repository, int pullRequestId)
    {
        _iterationCounts.TryGetValue(Key(project, repository, pullRequestId), out var count);
        return Task.FromResult(count == 0 ? 1 : count);
    }

    public Task<bool> HasReviewTagAsync(string project, string repository, int pullRequestId)
    {
        _tags.TryGetValue(Key(project, repository, pullRequestId), out var has);
        return Task.FromResult(has);
    }

    public Task AddReviewTagAsync(string project, string repository, int pullRequestId)
    {
        _tags[Key(project, repository, pullRequestId)] = true;
        return Task.CompletedTask;
    }

    public Task<ReviewMetadata> GetReviewMetadataAsync(string project, string repository, int pullRequestId)
    {
        _metadata.TryGetValue(Key(project, repository, pullRequestId), out var meta);
        return Task.FromResult(meta ?? new ReviewMetadata());
    }

    public Task SetReviewMetadataAsync(string project, string repository, int pullRequestId, ReviewMetadata metadata)
    {
        _metadata[Key(project, repository, pullRequestId)] = metadata;
        return Task.CompletedTask;
    }

    public Task<List<ReviewHistoryEntry>> GetReviewHistoryAsync(string project, string repository, int pullRequestId)
    {
        _history.TryGetValue(Key(project, repository, pullRequestId), out var list);
        return Task.FromResult(list ?? new List<ReviewHistoryEntry>());
    }

    public Task AppendReviewHistoryAsync(string project, string repository, int pullRequestId, ReviewHistoryEntry entry)
    {
        var key = Key(project, repository, pullRequestId);
        if (!_history.ContainsKey(key))
            _history[key] = new List<ReviewHistoryEntry>();
        _history[key].Add(entry);
        return Task.CompletedTask;
    }

    public Task<List<ExistingCommentThread>> GetExistingReviewThreadsAsync(
        string project, string repository, int pullRequestId, string? attributionTag = null)
    {
        _threads.TryGetValue(Key(project, repository, pullRequestId), out var list);
        return Task.FromResult(list ?? new List<ExistingCommentThread>());
    }

    public Task UpdateThreadStatusAsync(string project, string repository, int pullRequestId, int threadId, string status)
    {
        var key = Key(project, repository, pullRequestId);
        if (_threads.TryGetValue(key, out var list))
        {
            var thread = list.Find(t => t.ThreadId == threadId);
            if (thread is not null)
            {
                thread.Status = status switch
                {
                    "fixed" => 2,
                    "closed" => 4,
                    "active" => 1,
                    _ => thread.Status
                };
            }
        }
        return Task.CompletedTask;
    }

    public Task ReplyToThreadAsync(string project, string repository, int pullRequestId, int threadId, string content)
    {
        var key = Key(project, repository, pullRequestId);
        if (_threads.TryGetValue(key, out var list))
        {
            var thread = list.Find(t => t.ThreadId == threadId);
            thread?.Replies.Add(new ThreadReply
            {
                Author = "AI Reviewer",
                Content = content,
                CreatedDateUtc = DateTime.UtcNow,
            });
        }
        return Task.CompletedTask;
    }

    public Task<int> CountReviewSummaryCommentsAsync(string project, string repository, int pullRequestId)
    {
        var key = Key(project, repository, pullRequestId);
        var count = _postedComments.TryGetValue(key, out var list) ? list.Count : 0;
        return Task.FromResult(count);
    }

    public Task<List<FileChange>> GetPullRequestChangesAsync(
        string project, string repository, int pullRequestId, PullRequestInfo prInfo)
    {
        if (FileChangesFactory is not null)
            return Task.FromResult(FileChangesFactory(project, repository, pullRequestId, prInfo));

        if (_fileChanges.TryGetValue(Key(project, repository, pullRequestId), out var list))
            return Task.FromResult(list);

        // Return a single default file change
        return Task.FromResult(new List<FileChange>
        {
            new FileChange
            {
                FilePath = "/src/Example.cs",
                ChangeType = "edit",
                OriginalContent = "// original",
                ModifiedContent = "// modified\nvar x = 1;",
                UnifiedDiff = "@@ -1 +1,2 @@\n-// original\n+// modified\n+var x = 1;",
                ChangedLineRanges = new List<(int, int)> { (1, 2) },
            }
        });
    }

    public Task PostCommentThreadAsync(
        string project, string repository, int pullRequestId, string content, string status = "closed")
    {
        var key = Key(project, repository, pullRequestId);
        if (!_postedComments.ContainsKey(key))
            _postedComments[key] = new List<PostedComment>();
        _postedComments[key].Add(new PostedComment { Content = content, Status = status });

        // Also track as an existing thread so dedup picks it up
        if (!_threads.ContainsKey(key))
            _threads[key] = new List<ExistingCommentThread>();
        _threads[key].Add(new ExistingCommentThread
        {
            ThreadId = _threads[key].Count + 1000,
            Content = content,
            Status = status == "closed" ? 4 : 1,
            IsAiGenerated = true,
        });

        return Task.CompletedTask;
    }

    public Task PostInlineCommentThreadAsync(
        string project, string repository, int pullRequestId,
        string filePath, int startLine, int endLine,
        string content, string status = "closed")
    {
        var key = Key(project, repository, pullRequestId);
        if (!_postedInlineComments.ContainsKey(key))
            _postedInlineComments[key] = new List<PostedInlineComment>();
        _postedInlineComments[key].Add(new PostedInlineComment
        {
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            Content = content,
            Status = status,
        });

        // Also track as an existing thread for dedup
        if (!_threads.ContainsKey(key))
            _threads[key] = new List<ExistingCommentThread>();
        _threads[key].Add(new ExistingCommentThread
        {
            ThreadId = _threads[key].Count + 2000,
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            Content = content,
            Status = status == "closed" ? 4 : 1,
            IsAiGenerated = true,
        });

        return Task.CompletedTask;
    }

    public Task AddReviewerAsync(string project, string repository, int pullRequestId, int vote)
    {
        _votes[Key(project, repository, pullRequestId)] = vote;
        return Task.CompletedTask;
    }

    public Task UpdatePrDescriptionAsync(string project, string repository, int pullRequestId, string newDescription)
    {
        _descriptions[Key(project, repository, pullRequestId)] = newDescription;
        return Task.CompletedTask;
    }

    public Task<List<int>> GetLinkedWorkItemIdsAsync(string project, string repository, int pullRequestId)
    {
        _linkedWorkItems.TryGetValue(Key(project, repository, pullRequestId), out var list);
        return Task.FromResult(list ?? new List<int>());
    }

    public Task<WorkItemInfo?> GetWorkItemAsync(string project, int workItemId)
    {
        _workItems.TryGetValue(workItemId, out var item);
        return Task.FromResult(item);
    }

    public Task<List<WorkItemComment>> GetWorkItemCommentsAsync(string project, int workItemId)
    {
        _workItemComments.TryGetValue(workItemId, out var list);
        return Task.FromResult(list ?? new List<WorkItemComment>());
    }

    public virtual Task<string?> ResolveServiceIdentityAsync()
        => Task.FromResult<string?>("fake-identity-id");

    public void Dispose() { }
}

/// <summary>Record of a general comment posted to a PR.</summary>
public class PostedComment
{
    public string Content { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>Record of an inline comment posted to a specific file/line range.</summary>
public class PostedInlineComment
{
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
