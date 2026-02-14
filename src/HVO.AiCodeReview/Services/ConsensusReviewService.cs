using System.Text.Json;
using AiCodeReview.Models;

namespace AiCodeReview.Services;

/// <summary>
/// Meta-provider that fans the same review request out to multiple
/// <see cref="ICodeReviewService"/> implementations and merges their results.
///
/// Use cases:
///   • Sanity-check: run gpt-4o AND gpt-4.1, only surface comments both agree on
///   • Model comparison: log per-model metrics side-by-side
///   • Hybrid: run a cheap/fast local model first, escalate only the flagged files to a cloud model
///
/// Comment agreement is determined by file path + overlapping line ranges.
/// Comments that meet the consensus threshold are kept; others are discarded
/// (unless threshold is 1, which means any single model's finding is enough).
/// </summary>
public class ConsensusReviewService : ICodeReviewService
{
    private readonly IReadOnlyList<(string Name, ICodeReviewService Service)> _providers;
    private readonly int _threshold;
    private readonly ILogger<ConsensusReviewService> _logger;

    /// <summary>
    /// Create a consensus service wrapping multiple providers.
    /// </summary>
    /// <param name="providers">Named provider instances.</param>
    /// <param name="threshold">Minimum providers that must flag a comment to keep it.</param>
    /// <param name="logger">Logger.</param>
    public ConsensusReviewService(
        IReadOnlyList<(string Name, ICodeReviewService Service)> providers,
        int threshold,
        ILogger<ConsensusReviewService> logger)
    {
        _providers = providers;
        _threshold = Math.Max(1, Math.Min(threshold, providers.Count));
        _logger = logger;

        _logger.LogInformation(
            "ConsensusReviewService initialised with {Count} providers (threshold={Threshold}): {Names}",
            providers.Count, _threshold,
            string.Join(", ", providers.Select(p => p.Name)));
    }

    public async Task<CodeReviewResult> ReviewAsync(
        PullRequestInfo pullRequest, List<FileChange> fileChanges)
    {
        var results = await FanOutAsync(
            (name, svc) => svc.ReviewAsync(pullRequest, fileChanges));

        return MergeResults(results);
    }

    public async Task<CodeReviewResult> ReviewFileAsync(
        PullRequestInfo pullRequest, FileChange file, int totalFilesInPr)
    {
        var results = await FanOutAsync(
            (name, svc) => svc.ReviewFileAsync(pullRequest, file, totalFilesInPr));

        return MergeResults(results);
    }

    public async Task<List<ThreadVerificationResult>> VerifyThreadResolutionsAsync(
        List<ThreadVerificationCandidate> candidates)
    {
        if (candidates.Count == 0)
            return new List<ThreadVerificationResult>();

        // For verification, use majority vote per thread
        var allResults = await FanOutAsync(
            (name, svc) => svc.VerifyThreadResolutionsAsync(candidates));

        var merged = new List<ThreadVerificationResult>();
        var threadIds = candidates.Select(c => c.ThreadId).Distinct();

        foreach (var tid in threadIds)
        {
            int fixedVotes = 0, totalVotes = 0;
            var reasonings = new List<string>();

            foreach (var (name, results) in allResults)
            {
                var match = results.FirstOrDefault(r => r.ThreadId == tid);
                if (match != null)
                {
                    totalVotes++;
                    if (match.IsFixed) fixedVotes++;
                    if (!string.IsNullOrEmpty(match.Reasoning))
                        reasonings.Add($"[{name}] {match.Reasoning}");
                }
            }

            // Conservative: require majority to mark as fixed
            bool isFixed = fixedVotes > totalVotes / 2;

            merged.Add(new ThreadVerificationResult
            {
                ThreadId = tid,
                IsFixed = isFixed,
                Reasoning = $"Consensus: {fixedVotes}/{totalVotes} providers say fixed. " +
                            string.Join(" | ", reasonings),
            });
        }

        return merged;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Fan-out + merge internals
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<List<(string Name, T Result)>> FanOutAsync<T>(
        Func<string, ICodeReviewService, Task<T>> work)
    {
        var tasks = _providers.Select(async p =>
        {
            try
            {
                var result = await work(p.Name, p.Service);
                return (p.Name, Result: result, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Provider '{Provider}' failed", p.Name);
                return (p.Name, Result: default(T)!, Error: (Exception?)ex);
            }
        }).ToList();

        var outcomes = await Task.WhenAll(tasks);

        var successes = outcomes
            .Where(o => o.Error == null)
            .Select(o => (o.Name, o.Result))
            .ToList();

        if (successes.Count == 0)
        {
            throw new AggregateException(
                "All AI providers failed during consensus review.",
                outcomes.Where(o => o.Error != null).Select(o => o.Error!));
        }

        _logger.LogInformation("Consensus fan-out: {Successes}/{Total} providers succeeded",
            successes.Count, _providers.Count);

        return successes;
    }

    private CodeReviewResult MergeResults(List<(string Name, CodeReviewResult Result)> results)
    {
        // ── Inline comments: keep those that meet the consensus threshold ──
        var allComments = results
            .SelectMany(r => r.Result.InlineComments
                .Select(c => new { Provider = r.Name, Comment = c }))
            .ToList();

        var consensusComments = new List<InlineComment>();
        var used = new HashSet<int>(); // indices already matched

        for (int i = 0; i < allComments.Count; i++)
        {
            if (used.Contains(i)) continue;

            var anchor = allComments[i];
            var agreeing = new List<string> { anchor.Provider };
            used.Add(i);

            // Find other providers that flagged the same region
            for (int j = i + 1; j < allComments.Count; j++)
            {
                if (used.Contains(j)) continue;

                var other = allComments[j];
                if (other.Provider == anchor.Provider) continue; // same provider, skip

                if (CommentsOverlap(anchor.Comment, other.Comment))
                {
                    agreeing.Add(other.Provider);
                    used.Add(j);
                }
            }

            if (agreeing.Count >= _threshold)
            {
                // Annotate the comment with which models flagged it
                var merged = anchor.Comment;
                merged.Comment = $"[{string.Join("+", agreeing)}] {merged.Comment}";
                consensusComments.Add(merged);
            }
            else
            {
                _logger.LogDebug(
                    "Consensus filtered: {File}:{Start}-{End} flagged by {Providers} (need {Threshold})",
                    anchor.Comment.FilePath, anchor.Comment.StartLine, anchor.Comment.EndLine,
                    string.Join(", ", agreeing), _threshold);
            }
        }

        // ── Summary: use the most detailed/harshest verdict ──
        var summaries = results.Select(r => r.Result.Summary).ToList();
        var mergedSummary = summaries.OrderByDescending(VerdictSeverity).First();
        mergedSummary.Description = $"[Consensus from {results.Count} providers] {mergedSummary.Description}";

        // ── Vote: use the lowest (most critical) ──
        var lowestVote = results.Min(r => r.Result.RecommendedVote);

        // ── File reviews: union (deduplicate by path) ──
        var fileReviews = results
            .SelectMany(r => r.Result.FileReviews)
            .GroupBy(fr => fr.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(fr => VerdictSeverity(fr.Verdict)).First())
            .ToList();

        // ── Metrics: sum across providers ──
        int? promptTokens = results.Sum(r => r.Result.PromptTokens);
        int? completionTokens = results.Sum(r => r.Result.CompletionTokens);
        int? totalTokens = results.Sum(r => r.Result.TotalTokens);
        long? maxDuration = results.Max(r => r.Result.AiDurationMs);

        return new CodeReviewResult
        {
            Summary = mergedSummary,
            FileReviews = fileReviews,
            InlineComments = consensusComments,
            RecommendedVote = lowestVote,
            ModelName = string.Join("+", results.Select(r => r.Result.ModelName ?? r.Name)),
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens,
            AiDurationMs = maxDuration,
        };
    }

    /// <summary>
    /// Two inline comments "overlap" if they target the same file and their
    /// line ranges intersect (within a 3-line tolerance for minor offsets).
    /// </summary>
    private static bool CommentsOverlap(InlineComment a, InlineComment b)
    {
        if (!string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase))
            return false;

        const int tolerance = 3;
        return a.StartLine <= b.EndLine + tolerance
            && b.StartLine <= a.EndLine + tolerance;
    }

    private static int VerdictSeverity(ReviewSummary s) => VerdictSeverity(s.Verdict);

    private static int VerdictSeverity(string verdict) => verdict.ToUpperInvariant() switch
    {
        "REJECTED" => 4,
        "NEEDS WORK" => 3,
        "APPROVED WITH SUGGESTIONS" => 2,
        "CONCERN" => 2,
        "OBSERVATION" => 1,
        "APPROVED" => 0,
        _ => 0,
    };
}
