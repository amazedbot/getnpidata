namespace Npi.Loader.Nppes;

/// <summary>A file the planner chose, with the URL to download it from.</summary>
public sealed record PlannedFile(NppesFile File, Uri Url);

/// <summary>A file the planner left out, and why.</summary>
public sealed record SkippedFile(string FileName, string Reason);

public sealed record LoadPlan(IReadOnlyList<PlannedFile> Files, IReadOnlyList<SkippedFile> Skipped);

/// <summary>
/// Decides what <c>run</c> processes (CLAUDE.md §7 Stage 1.1–1.2): the newest monthly, then the
/// weeklies in chronological order, then the newest deactivation report. Only
/// <c>downlog.status = 'Completed'</c> counts as done, so failed or interrupted files are retried.
/// </summary>
public static class LoadPlanner
{
    /// <param name="links">Zip links found on the NPPES page.</param>
    /// <param name="completed">File names whose downlog status is Completed.</param>
    public static LoadPlan Plan(IEnumerable<NppesLink> links, IReadOnlySet<string> completed)
    {
        var skipped = new List<SkippedFile>();
        var files = new List<PlannedFile>();
        foreach (var link in links)
        {
            var file = NppesFileClassifier.Classify(link.FileName);
            if (file is null)
            {
                skipped.Add(new SkippedFile(link.FileName, "not a recognised V2 NPPES file name"));
            }
            else
            {
                files.Add(new PlannedFile(file, link.Url));
            }
        }

        var completedFiles = completed
            .Select(NppesFileClassifier.Classify)
            .OfType<NppesFile>()
            .ToList();

        var plan = new List<PlannedFile>();
        plan.AddRange(NewestOnly(NppesFileKind.Monthly, files, completed, completedFiles, skipped));
        foreach (var weekly in files.Where(f => f.File.Kind == NppesFileKind.Weekly).OrderBy(f => f.File.FileDate))
        {
            if (completed.Contains(weekly.File.FileName))
            {
                skipped.Add(new SkippedFile(weekly.File.FileName, "already completed"));
            }
            else
            {
                plan.Add(weekly);
            }
        }

        plan.AddRange(NewestOnly(NppesFileKind.Deactivation, files, completed, completedFiles, skipped));
        return new LoadPlan(plan, skipped);
    }

    // Monthly files and deactivation reports each replace everything before them, so only the
    // newest one on the page matters, and only if nothing newer has already been loaded.
    private static IEnumerable<PlannedFile> NewestOnly(
        NppesFileKind kind, List<PlannedFile> files, IReadOnlySet<string> completed, List<NppesFile> completedFiles, List<SkippedFile> skipped)
    {
        var ofKind = files.Where(f => f.File.Kind == kind).OrderByDescending(f => f.File.FileDate).ToList();
        if (ofKind.Count == 0)
        {
            yield break;
        }

        foreach (var older in ofKind.Skip(1))
        {
            skipped.Add(new SkippedFile(older.File.FileName, $"superseded by {ofKind[0].File.FileName}"));
        }

        var newest = ofKind[0];
        var newestCompleted = completedFiles.Where(f => f.Kind == kind).MaxBy(f => f.FileDate);
        if (completed.Contains(newest.File.FileName))
        {
            skipped.Add(new SkippedFile(newest.File.FileName, "already completed"));
        }
        else if (newestCompleted is not null && newestCompleted.FileDate > newest.File.FileDate)
        {
            skipped.Add(new SkippedFile(newest.File.FileName, $"older than the completed {newestCompleted.FileName}"));
        }
        else
        {
            yield return newest;
        }
    }
}
