using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Neaslator.Tests.Messaging;

/// <summary>
/// A publish through the scoped <c>IPublishEndpoint</c> must be followed by a save, or it is never sent.
/// </summary>
/// <remarks>
/// <para>
/// <b>What breaks in production.</b> <c>UseBusOutbox()</c> makes the scoped <c>IPublishEndpoint</c>
/// <em>stage</em> a message and hold it until <c>SaveChangesAsync</c> writes it to the outbox table. A
/// publish with no save after it is staged into a scope that then disposes — never sent. Staging cannot
/// fail, so there is no exception, no log, no dead letter, and the consume reports success.
/// </para>
/// <para>
/// <b>Why this test was written before the outbox was switched on.</b> Turning on <c>UseBusOutbox</c>
/// changes the meaning of every existing publish site in the service. Both of this service's sites were
/// wrong for it at the time: <c>StartTranslationConsumer</c> published
/// <c>MenuTranslationCompletedEvent</c> after its last save, and <c>MenuTranslationRequestedConsumer</c>
/// published <c>StartTranslationCommand</c> while owning no DbContext at all — so translation would
/// simply never have started, silently. Enabling the outbox without auditing this is how subscription
/// ended up with four such sites, one of which meant a customer per batch was invoiced and never
/// charged.
/// </para>
/// <para>
/// <b>Not flagged:</b> <c>ConsumeContext.Publish</c>. There the inbox filter owns the transaction and
/// its save flushes the staged message, so ordering genuinely does not matter — that is the correct
/// instrument for a consumer with no unit of work of its own, and it is what
/// <c>MenuTranslationRequestedConsumer</c> now uses.
/// </para>
/// </remarks>
public class OutboxPublishOrderingTests
{
    private static readonly Regex ScopedPublish = new(
        @"\b(_publisher|_publishEndpoint|publishEndpoint)\s*\.\s*Publish\s*[<(]", RegexOptions.Compiled);

    private static readonly Regex Save = new(@"\bSaveChangesAsync\s*\(", RegexOptions.Compiled);

    /// <summary>How far after a publish a flushing save may sit and still count.</summary>
    /// <remarks>
    /// Generous on purpose. A publish is usually a multi-line object initializer, and the save that
    /// flushes it is often separated further by the closing brace of an activity scope and by the
    /// comment explaining why it is there — in <c>StartTranslationConsumer</c> the two are 28 lines
    /// apart for exactly those reasons, and 25 produced a false failure on the very code this test was
    /// written to protect. A window that cries wolf is a check people learn to override, which is worse
    /// than no check. Being in the same method is what actually matters, and 60 lines approximates that
    /// without needing to parse C#.
    /// </remarks>
    private const int WindowLines = 60;

    [Fact]
    public void Every_scoped_publish_is_followed_by_a_save_that_flushes_it()
    {
        var offences = new List<string>();
        int filesScanned = 0;
        int publishesSeen = 0;

        foreach (var file in SourceFiles())
        {
            filesScanned++;
            var lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                if (!ScopedPublish.IsMatch(lines[i]))
                {
                    continue;
                }

                publishesSeen++;

                bool flushed = false;
                for (int j = i + 1; j < Math.Min(lines.Length, i + WindowLines); j++)
                {
                    if (Save.IsMatch(lines[j]))
                    {
                        flushed = true;
                        break;
                    }
                }

                if (!flushed)
                {
                    offences.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()[..Math.Min(70, lines[i].Trim().Length)]}");
                }
            }
        }

        // Guards the guard. An unresolved glob and a clean codebase produce identical empty results, and
        // sweeps over this estate have reported "clean" from a broken matcher more than once.
        filesScanned.Should().BeGreaterThan(20, "the source glob must actually resolve the project");
        publishesSeen.Should().BeGreaterThan(
            0, "no scoped publish was found at all, so this test proves nothing");

        offences.Should().BeEmpty(
            "UseBusOutbox stages a scoped publish until SaveChangesAsync writes it to the outbox. These "
            + "sites publish with no save after them, so the message is staged into a disposing scope "
            + "and never sent — silently, with a successful consume. Either add a save to flush it, or "
            + "publish through ConsumeContext, whose save the inbox owns:\n  "
            + string.Join("\n  ", offences));
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = SrcRoot();
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));
    }

    private static string SrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Neaslator");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find src/Neaslator above " + AppContext.BaseDirectory);
    }
}
