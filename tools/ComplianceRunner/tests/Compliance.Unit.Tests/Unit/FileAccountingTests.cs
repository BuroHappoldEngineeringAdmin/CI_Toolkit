using NUnit.Framework;

namespace Compliance.Tests
{
    /// <summary>
    /// The coverage denominator's arithmetic and its wording, tested without a BHoM install.
    ///
    /// The accounting was extracted from the two runners' entry points precisely so it could be
    /// tested here: those entry points compile against BHoM types, so with the counting inlined
    /// the only way to check a total would be a full CI run against a real dependency closure.
    ///
    /// The wording is asserted as well as the arithmetic. This text is the whole deliverable —
    /// it is what a reader sees when a check reports success having examined nothing — so a
    /// change to it should be a deliberate edit to a test, not a silent drift.
    /// </summary>
    [TestFixture]
    public class FileAccountingTests
    {
        [Test]
        public void EveryFileExamined_ReportsFullCoverageAndNoDrops()
        {
            var a = new FileAccounting(3);
            a.CountExamined(); a.CountExamined(); a.CountExamined();

            Assert.Multiple(() =>
            {
                Assert.That(a.Examined, Is.EqualTo(3));
                Assert.That(a.ExaminedNothing, Is.False);
                Assert.That(a.CoverageLine(), Is.EqualTo("Coverage: 3 of 3 file(s) examined; none dropped."));
            });
        }

        [Test]
        public void EachDropReasonIsCountedAndNamedSeparately()
        {
            var a = new FileAccounting(4);
            a.CountNotRelevant();
            a.CountNotOnDisk();
            a.CountNoResult();
            a.CountExamined();

            Assert.That(a.CoverageLine(), Is.EqualTo(
                "Coverage: 1 of 4 file(s) examined; 1 not relevant to this check, 1 not found on disk, 1 returned no result."));
        }

        [Test]
        public void ADropReasonThatDidNotOccurIsNotMentioned()
        {
            // Listing every reason at zero would bury the one that fired.
            var a = new FileAccounting(2);
            a.CountNotRelevant(); a.CountNotRelevant();

            Assert.Multiple(() =>
            {
                Assert.That(a.CoverageLine(), Does.Contain("2 not relevant to this check"));
                Assert.That(a.CoverageLine(), Does.Not.Contain("not found on disk"));
                Assert.That(a.CoverageLine(), Does.Not.Contain("returned no result"));
            });
        }

        // ── The case this exists for ──────────────────────────────────────────────────

        [Test]
        public void FilesHandedInAndNoneExamined_IsFlagged()
        {
            var a = new FileAccounting(3);
            a.CountNotRelevant(); a.CountNotRelevant(); a.CountNotRelevant();

            Assert.That(a.ExaminedNothing, Is.True);
        }

        [Test]
        public void TheWarningCarriesTheBreakdown_NotJustTheFact()
        {
            // A bare "nothing was examined" does not tell a reader whether the pull request
            // changed nothing relevant or the selection layer handed over files this check
            // will never accept. The breakdown is what separates those two.
            var a = new FileAccounting(3);
            a.CountNotRelevant(); a.CountNotRelevant(); a.CountNotRelevant();

            string w = a.ExaminedNothingWarning();

            Assert.Multiple(() =>
            {
                Assert.That(w, Does.Contain("3 file(s) were handed to this check and none was examined"));
                Assert.That(w, Does.Contain("3 not relevant to this check"));
                Assert.That(w, Does.Contain("reports success without having inspected anything"));
                Assert.That(w, Does.Contain("disagree"),
                    "the reader needs to be told what a drop-as-not-relevant implies, not left to infer it");
            });
        }

        [Test]
        public void NoFilesAtAll_IsNotFlagged()
        {
            // The calling workflow exits before invoking a runner when it has no files, so this
            // is not a state production reaches. Asserted so the flag means "selected but
            // unused" rather than "empty", which is the distinction that makes it worth raising.
            var a = new FileAccounting(0);

            Assert.That(a.ExaminedNothing, Is.False);
        }

        [Test]
        public void SomeExamined_IsNotFlagged()
        {
            var a = new FileAccounting(2);
            a.CountNotRelevant();
            a.CountExamined();

            Assert.That(a.ExaminedNothing, Is.False);
        }

        // ── Machine-readable shape ────────────────────────────────────────────────────

        [Test]
        public void ThePayloadCarriesEveryCounter()
        {
            var a = new FileAccounting(4);
            a.CountNotRelevant(); a.CountNotOnDisk(); a.CountNoResult(); a.CountExamined();

            var p = a.ToPayload();

            Assert.Multiple(() =>
            {
                Assert.That(p["handedIn"],    Is.EqualTo(4));
                Assert.That(p["examined"],    Is.EqualTo(1));
                Assert.That(p["notRelevant"], Is.EqualTo(1));
                Assert.That(p["notOnDisk"],   Is.EqualTo(1));
                Assert.That(p["noResult"],    Is.EqualTo(1));
            });
        }

        [Test]
        public void TheCountsReconcile()
        {
            // If they ever stop adding up, the denominator is lying and every number above it
            // is unreadable.
            var a = new FileAccounting(10);
            for (int i = 0; i < 2; i++) a.CountNotRelevant();
            for (int i = 0; i < 3; i++) a.CountNotOnDisk();
            a.CountNoResult();
            for (int i = 0; i < 4; i++) a.CountExamined();

            Assert.That(a.NotRelevant + a.NotOnDisk + a.NoResult + a.Examined,
                        Is.EqualTo(a.HandedIn));
        }
    }
}
