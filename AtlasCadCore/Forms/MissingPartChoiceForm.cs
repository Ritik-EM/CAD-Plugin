using System;
using System.Drawing;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// Small popup shown for each missing part during Upload when the
    /// pad-with-"00" pre-pass couldn't find a match in atlas. User picks
    /// one of two paths:
    ///   • Pick Existing — open PartMasterPickerDialog and attach the
    ///     uploaded file to whichever existing atlas part_number they pick
    ///   • Skip — don't upload this part. The outer Upload flow will list
    ///     the skipped parts in its summary so the user knows what to
    ///     release on atlas-ui before re-running Upload.
    ///
    /// "Create New" was intentionally removed — atlas-ui is the single
    /// source of truth for releasing new part_numbers (metadata +
    /// reviewer workflow). The plugin is attach-only.
    /// </summary>
    public class MissingPartChoiceForm : Form
    {
        // ChoiceKind kept (rather than just bool UseExisting) so call sites
        // can distinguish "user actively skipped" from "user closed without
        // choosing" if we ever need to. Both currently route to the same
        // "needs to be released on atlas-ui" bucket in the upload flow.
        public enum ChoiceKind { Skip, UseExisting }

        public ChoiceKind Choice { get; private set; } = ChoiceKind.Skip;
        public string PickedExistingPartNumber { get; private set; }

        public MissingPartChoiceForm(string detectedCode, string filename, AtlasApiClient api)
        {
            Text = "Atlas — Part Not Found";
            Size = new Size(560, 280);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;

            var msg = new Label
            {
                Location = new Point(20, 20),
                Width = 510,
                Height = 110,
                Text =
                    $"This part isn't released on atlas yet:\n\n" +
                    $"   Filename:   {filename}\n" +
                    $"   Detected:   {detectedCode ?? "(none)"}\n\n" +
                    "You can attach the file to an EXISTING atlas part_number, " +
                    "or Skip and release this part_number on atlas-ui first.",
            };
            Controls.Add(msg);

            int by = 150;
            var pickExisting = new Button
            {
                Text = "Pick Existing Part…",
                Location = new Point(20, by),
                Width = 200, Height = 32,
            };
            pickExisting.Click += (s, e) =>
            {
                using (var dlg = new PartMasterPickerDialog(api, initialSearch: detectedCode))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    Choice = ChoiceKind.UseExisting;
                    PickedExistingPartNumber = dlg.SelectedPartNumber;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };
            Controls.Add(pickExisting);

            var skip = new Button
            {
                Text = "Skip",
                Location = new Point(430, by),
                Width = 100, Height = 32,
                DialogResult = DialogResult.Cancel,
            };
            skip.Click += (s, e) =>
            {
                Choice = ChoiceKind.Skip;
            };
            Controls.Add(skip);

            var hint = new Label
            {
                Location = new Point(20, by + 50),
                Width = 510,
                AutoSize = false,
                Height = 40,
                ForeColor = Color.DimGray,
                Text =
                    "Pick Existing: attach the file to a part_number you choose.\n" +
                    "Skip: leave this file out of the upload — release it on atlas-ui first.",
            };
            Controls.Add(hint);

            CancelButton = skip;
        }
    }
}
