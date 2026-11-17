using SpaceInspector.Core.Models;

namespace SpaceInspector.Core.Interfaces;

public interface IFileFingerprintService
{
    Task<FileFingerprint> ComputeAsync(DuplicateCandidate file, DuplicateLevel level, bool includeProtected, CancellationToken cancellationToken);
    Task<bool> IsCurrentAsync(DuplicateCandidate file, CancellationToken cancellationToken);
}
public interface IDuplicateRepository
{
    Task<long> BeginDuplicatesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DuplicateCandidate>> GetDuplicateCandidatesAsync(long afterId, int count, bool fullHash, CancellationToken cancellationToken);
    Task StoreFingerprintAsync(long runId, DuplicateCandidate file, FileFingerprint fingerprint, bool fullHash, CancellationToken cancellationToken);
    Task<IReadOnlyList<DuplicateCandidate>> GetHashedFilesAsync(long runId, long afterId, CancellationToken cancellationToken);
    Task FinishDuplicatesAsync(DuplicateResult result, DuplicateLevel level, CancellationToken cancellationToken);
    Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DuplicateMember>> GetDuplicateMembersAsync(string groupId, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
}
public interface IDuplicateAnalysisService
{
    Task<DuplicateResult> AnalyzeAsync(DuplicateOptions options, IProgress<DuplicateProgress>? progress, CancellationToken cancellationToken);
}
