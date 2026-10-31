// SPDX-License-Identifier: GPL-3.0-or-later
//
// Guards docs/roadmap.md against the shape eveofdarkness.org's progress panel parses. The site's
// build (build.py read_roadmap) stops rather than guess when an entry is malformed, which turns a
// small roadmap typo into a broken site build discovered late. These tests reproduce that parser's
// rules so the same mistake fails HERE, in CI, next to the change that caused it.
//
// The regexes mirror the site's exactly (relayed from eveofdarkness build.py). If the site parser
// changes, change these to match — they are a copy of the contract, not an independent idea of it.

using System.Globalization;
using System.Text.RegularExpressions;

namespace DaocProtocol.Tests;

public class RoadmapFormatTests
{
    // Site parser rules, verbatim:
    //  heading: "### M<n> — Title", em dash U+2014 with one space each side, optional trailing ✅
    private static readonly Regex Heading = new(@"^### (M(\d+)) — (.+?)(?:\s+✅)?$");
    //  done line: **Done** <ISO date> · (shot <name> | shot: none<anything>)
    private static readonly Regex DoneLine = new(@"^\*\*Done\*\* (\d{4}-\d{2}-\d{2}) · shot(?:: none\b.*| (\S+))$");
    //  optional earlier-steps line
    private static readonly Regex EarlierSteps = new(@"^\(earlier steps: (.+)\)$");
    //  italic one-line summary
    private static readonly Regex Summary = new(@"^\*[^*].*\*$");
    //  MVP marker naming a milestone
    private static readonly Regex MvpMarker = new(@"MVP reached at M(\d+)");

    private const char Done = '✅';   // ✅ — not ✔ (U+2714) or ☑ (U+2611)

    private static string[] RoadmapLines()
    {
        string path = FindRepoFile(Path.Combine("docs", "roadmap.md"));
        return File.ReadAllLines(path);
    }

    /// <summary>Walk up from the test binary to the repo root (the dir holding docs/roadmap.md).</summary>
    private static string FindRepoFile(string relative)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not find {relative} walking up from {AppContext.BaseDirectory}.");
    }

    /// <summary>Lines outside ``` code fences — the site ignores fenced blocks, so the format example is safe.</summary>
    private static IEnumerable<(int Number, string Text)> UnfencedLines(string[] lines)
    {
        bool inFence = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```"))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
                yield return (i, lines[i]);
        }
    }

    private sealed record Milestone(int Number, string Title, bool IsDone, int LineIndex, string? DoneLineText, string? EarlierText);

    private static List<Milestone> ParseMilestones(string[] lines)
    {
        var unfenced = UnfencedLines(lines).ToList();
        // Map original line index -> position in the unfenced list, so we can look at the "next line".
        var byIndex = unfenced.Select((v, pos) => (v.Number, v.Text, pos)).ToList();
        var milestones = new List<Milestone>();
        for (int pos = 0; pos < byIndex.Count; pos++)
        {
            Match m = Heading.Match(byIndex[pos].Text);
            if (!m.Success)
                continue;

            int number = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            bool done = byIndex[pos].Text.TrimEnd().EndsWith(Done);
            string? doneLine = pos + 1 < byIndex.Count ? byIndex[pos + 1].Text : null;
            string? earlier = pos + 2 < byIndex.Count ? byIndex[pos + 2].Text : null;
            milestones.Add(new Milestone(number, m.Groups[3].Value, done, byIndex[pos].Number, doneLine, earlier));
        }

        return milestones;
    }

    [Fact]
    public void MilestonesAreNumberedWithoutGapsInOrder()
    {
        List<Milestone> ms = ParseMilestones(RoadmapLines());
        Assert.NotEmpty(ms);
        for (int i = 0; i < ms.Count; i++)
            Assert.True(ms[i].Number == i,
                $"Milestones must run M0, M1, … in order with no gaps; got M{ms[i].Number} at position {i}.");
    }

    [Fact]
    public void EveryDoneMilestoneHasAWellFormedDoneLineOnTheNextLine()
    {
        foreach (Milestone m in ParseMilestones(RoadmapLines()).Where(x => x.IsDone))
        {
            Assert.True(m.DoneLineText is not null && DoneLine.IsMatch(m.DoneLineText),
                $"M{m.Number} ✅ (roadmap line {m.LineIndex + 1}) needs a Done line directly under it: " +
                $"\"**Done** YYYY-MM-DD · shot <name>\" or \"· shot: none (<why>)\". Got: \"{m.DoneLineText}\".");
        }
    }

    [Fact]
    public void DoneLineShotNamesStartWithACaptureDate()
    {
        // The site takes a shot's capture date from name[:10], so a named shot must start YYYY-MM-DD.
        foreach (Milestone m in ParseMilestones(RoadmapLines()).Where(x => x.IsDone && x.DoneLineText is not null))
        {
            Match d = DoneLine.Match(m.DoneLineText!);
            if (!d.Success || d.Groups[2].Value.Length == 0)
                continue; // "shot: none" carries no name
            string name = d.Groups[2].Value;
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}_", name);
        }

        // Earlier-steps shot names (optional line) follow the same rule.
        foreach (Milestone m in ParseMilestones(RoadmapLines()).Where(x => x.IsDone && x.EarlierText is not null))
        {
            Match e = EarlierSteps.Match(m.EarlierText!);
            if (!e.Success)
                continue;
            foreach (string name in e.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries))
                Assert.Matches(@"^\d{4}-\d{2}-\d{2}_", name);
        }
    }

    [Fact]
    public void UnfinishedMilestonesHaveAnItalicSummaryUnderTheHeading()
    {
        foreach (Milestone m in ParseMilestones(RoadmapLines()).Where(x => !x.IsDone))
            Assert.True(m.DoneLineText is not null && Summary.IsMatch(m.DoneLineText.Trim()),
                $"Unfinished M{m.Number} needs its italic *one-line summary* directly under the heading " +
                $"(the site shows it for the next milestone). Got: \"{m.DoneLineText}\".");
    }

    [Fact]
    public void ThereIsAnMvpMarkerNamingAListedMilestone()
    {
        string[] lines = RoadmapLines();
        var numbers = ParseMilestones(lines).Select(m => m.Number).ToHashSet();
        Match? marker = UnfencedLines(lines).Select(l => MvpMarker.Match(l.Text)).FirstOrDefault(x => x.Success);
        Assert.True(marker is { Success: true }, "Roadmap must contain an \"MVP reached at M<n>\" marker.");
        int mvp = int.Parse(marker!.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Contains(mvp, numbers);
    }
}
