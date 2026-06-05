using System.Collections.Generic;

namespace AtlasCadCore.Adapter
{
    /// <summary>
    /// Optional adapter capability: relabel the spec-tree node identity so each
    /// component DISPLAYS its current Atlas revision (e.g. the dome shows
    /// "AH120276_0H_FRONT WHEEL DOME" once Atlas has bumped it to revision 0H),
    /// even though the on-disk filename keeps its original engineering-rev token.
    ///
    /// This is DISPLAY-ONLY. It changes the product's PartNumber (the label
    /// shown before the parentheses in the tree), NOT the filename and NOT the
    /// part-number resolution used by the walk/upload/check-in (which reads the
    /// filename and the PART_NUMBER user parameter, never the product label).
    /// CATIA resolves assembly links by the internal document UUID, so changing
    /// the displayed PartNumber can't break parent→child links.
    ///
    /// Only the CATIA adapter implements this; other CADs simply don't, and the
    /// shared checkout flow no-ops via an `is` check.
    /// </summary>
    public interface IRevisionDisplayAdapter
    {
        /// <summary>
        /// Walk the open assembly and, for every component whose filename is a
        /// key in <paramref name="currentPartNumberByFilename"/>, rewrite its
        /// displayed PartNumber so the revision token reflects the mapped
        /// current part_number. Returns the number of labels changed.
        /// Implementations must persist the change (so the checkout baseline
        /// hash captures it and check-in doesn't later flag a phantom edit) and
        /// must never throw into the caller — checkout must not be blocked by a
        /// cosmetic relabel failure.
        /// </summary>
        int ApplyRevisionDisplay(CadDocument doc,
                                 IDictionary<string, string> currentPartNumberByFilename);
    }
}
