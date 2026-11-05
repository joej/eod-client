// SPDX-License-Identifier: GPL-3.0-or-later

namespace DaocAssets.Tests;

/// <summary>
/// Tests over files from the owner's own client install (git-ignored private/*-fixtures, copied next
/// to the test assembly as live-nif/, live-mpk/, live-terrain/). Without them a test is reported as
/// skipped, naming what is missing, rather than failing or passing without checking anything.
/// DAOC_REQUIRE_FIXTURES=1 turns the skip off, so a missing fixture fails.
/// </summary>
internal static class LiveFixture
{
    /// <summary>The skip reason for <paramref name="paths"/> (files or folders relative to the test
    /// output), or null when all are present or fixtures are required.</summary>
    public static string? SkipReason(string[] paths)
    {
        if (Environment.GetEnvironmentVariable("DAOC_REQUIRE_FIXTURES") == "1")
            return null;
        string[] missing = paths
            .Where(p => !File.Exists(Path.Combine(AppContext.BaseDirectory, p))
                        && !Directory.Exists(Path.Combine(AppContext.BaseDirectory, p)))
            .ToArray();
        return missing.Length == 0 ? null : "private fixture missing: " + string.Join(", ", missing);
    }
}

/// <summary>A fact that needs the given private fixtures; see <see cref="LiveFixture"/>.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(params string[] paths) => Skip = LiveFixture.SkipReason(paths);
}

/// <summary>A theory that needs the given private fixtures; see <see cref="LiveFixture"/>.</summary>
public sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute(params string[] paths) => Skip = LiveFixture.SkipReason(paths);
}
