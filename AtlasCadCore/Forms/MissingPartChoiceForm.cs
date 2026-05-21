using System;
using System.Drawing;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
    /// <summary>
    /// Small popup shown for each missing part during Upload when the
    /// pad-with-"00" pre-pass couldn't find a match in atlas. User picks
    /// one of three paths:
    ///   • Pick Existing — open PartMasterPickerDialog and attach the
    ///     uploaded file to whichever existing atlas part_number they pick
    ///   • Create New — proceed to AssignPartMetadataForm for this row
    ///     (the outer Upload flow handles that)
    ///   • Skip — don't upload this part at all
    /// </summary>
    public class MissingPartChoiceForm : Form
    {
        public enum ChoiceKind { Skip, UseExisting, CreateNew }

        public ChoiceKind Choice { get; private set; } = ChoiceKind.Skip;
        public string PickedExistingPartNumber { get; private set; }

        public MissingPartChoiceForm(string detectedCode, string filename, AtlasApiClient api)
        {
            Text = "Atlas — Part Not Found";
            Size = new Size(560, 290);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;

            var msg = new Label
            {
                Location = new Point(20, 20),
                Width = 510,
                Height = 90,
                Text =
                    $"Atlas couldn't find a part for:\n\n" +
                    $"   Filename:   {filename}\n" +
                    $"   Detected:   {detectedCode ?? "(none)"}\n\n" +
                    "Choose what to do with the upload:",
            };
            Controls.Add(msg);

            int by = 130;
            var pickExisting = new Button
            {
                Text = "Pick Existing Part…",
                Location = new Point(20, by),
                Width = 180, Height = 32,
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

            var createNew = new Button
            {
                Text = "Create New Part",
                Location = new Point(210, by),
                Width = 160, Height = 32,
            };
            createNew.Click += (s, e) =>
            {
                Choice = ChoiceKind.CreateNew;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(createNew);

            var skip = new Button
            {
                Text = "Skip",
                Location = new Point(380, by),
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
                Height = 50,
                ForeColor = Color.DimGray,
                Text =
                    "Pick Existing: attach the file to a part_number you choose.\n" +
                    "Create New: enter metadata; atlas mints a fresh part_number.\n" +
                    "Skip: leave this file out of the upload.",
            };
            Controls.Add(hint);

            CancelButton = skip;
        }
    }
}
