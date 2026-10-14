using System;
using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Hosting;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #13, ticket #42 review — the sketch-delete ordering defect.
    /// The equations keyed to a sketch are LIVE references while it
    /// survives, so the purge that unblocks a refused delete (the
    /// ticket-#19 finding) must run only on a proven refusal, and a delete
    /// that still refuses must put them back — the earlier ordering swept
    /// them first and lost them when the delete then failed. These tests
    /// pin the pure sequencing <see cref="OwnedSketchDeletePlan"/> runs
    /// (the add-in's OwnedSketchCleanup supplies the COM verbs).
    /// </summary>
    public class OwnedSketchDeletePlanTests
    {
        /// <summary>A scripted host: records the call order, answers by script.</summary>
        private sealed class Script
        {
            public readonly List<string> Calls = new List<string>();
            public readonly List<string> Restored = new List<string>();
            public bool SelectSucceeds = true;
            public bool PlainDeleteSucceeds;
            public bool ChildrenDeleteSucceeds;

            /// <summary>
            /// When set, the with-children delete succeeds only once the
            /// purge has run — the ticket-#19 blocker shape.
            /// </summary>
            public bool DeleteNeedsPurge;

            public IReadOnlyList<string> PurgedTexts = new string[0];

            public OwnedSketchDeleteResult Run() =>
                OwnedSketchDeletePlan.Run(
                    select: () => { Calls.Add("select"); return SelectSucceeds; },
                    deletePlain: () => { Calls.Add("deletePlain"); return PlainDeleteSucceeds; },
                    deleteWithChildren: () =>
                    {
                        Calls.Add("deleteChildren");
                        return ChildrenDeleteSucceeds ||
                            (DeleteNeedsPurge && Calls.Contains("purge"));
                    },
                    purgeReferences: () => { Calls.Add("purge"); return PurgedTexts; },
                    restore: texts =>
                    {
                        Calls.Add("restore");
                        Restored.AddRange(texts);
                        return texts.Count;
                    },
                    sweepOrphans: () => Calls.Add("sweep"));
        }

        [Fact]
        public void A_plain_delete_never_touches_the_equations()
        {
            var s = new Script { PlainDeleteSucceeds = true };

            var result = s.Run();

            Assert.Equal(OwnedSketchDeleteOutcome.Deleted, result.Outcome);
            Assert.Equal(new[] { "select", "deletePlain", "sweep" }, s.Calls);
        }

        [Fact]
        public void A_plain_refusal_retries_with_children_before_any_purge()
        {
            var s = new Script { ChildrenDeleteSucceeds = true };

            var result = s.Run();

            Assert.Equal(OwnedSketchDeleteOutcome.Deleted, result.Outcome);
            Assert.Equal(
                new[] { "select", "deletePlain", "select", "deleteChildren", "sweep" },
                s.Calls);
        }

        [Fact]
        public void The_equation_purge_waits_for_a_proven_refusal()
        {
            // The ticket-#19 blocker: the delete only goes through once the
            // equations keyed to the feature have been swept.
            var s = new Script
            {
                DeleteNeedsPurge = true,
                PurgedTexts = new[] { "\"D5@Sketch3\" = \"D1@Sketch3\"/21" },
            };

            var result = s.Run();

            Assert.Equal(OwnedSketchDeleteOutcome.Deleted, result.Outcome);
            Assert.Equal(1, result.Purged);
            Assert.Equal(0, result.Restored);
            Assert.Equal(
                new[] { "select", "deletePlain", "select", "deleteChildren",
                        "purge", "select", "deleteChildren", "sweep" },
                s.Calls);
        }

        [Fact]
        public void A_delete_that_still_refuses_restores_the_equations()
        {
            var texts = new[]
            {
                "\"D5@Sketch3\" = \"D1@Sketch3\"/21",
                "\"D6@Sketch3\" = \"D1@Sketch3\"/21",
            };
            var s = new Script { PurgedTexts = texts };

            var result = s.Run();

            Assert.Equal(OwnedSketchDeleteOutcome.DeleteRefusedAfterPurge, result.Outcome);
            Assert.Equal(2, result.Purged);
            Assert.Equal(2, result.Restored);
            Assert.Equal(texts, s.Restored);
            Assert.Equal(
                new[] { "select", "deletePlain", "select", "deleteChildren",
                        "purge", "select", "deleteChildren", "restore" },
                s.Calls);
        }

        [Fact]
        public void A_refusal_without_equations_purges_and_restores_nothing()
        {
            var s = new Script();

            var result = s.Run();

            Assert.Equal(OwnedSketchDeleteOutcome.DeleteRefused, result.Outcome);
            Assert.Equal(
                new[] { "select", "deletePlain", "select", "deleteChildren", "purge" },
                s.Calls);
        }

        [Fact]
        public void A_selection_failure_deletes_and_sweeps_nothing()
        {
            var s = new Script { SelectSucceeds = false };

            var result = s.Run();

            Assert.Equal(OwnedSketchDeleteOutcome.SelectFailed, result.Outcome);
            Assert.Equal(new[] { "select" }, s.Calls);
        }

        [Fact]
        public void The_orphan_sweep_never_runs_on_a_surviving_sketch()
        {
            // Whatever the refuse shape is, "sweep" must never appear in
            // the calls — the orphan sweep is post-delete hygiene only.
            var s = new Script { PurgedTexts = new[] { "\"D1@S\" = 5" } };

            s.Run();

            Assert.DoesNotContain("sweep", s.Calls);
        }
    }
}
