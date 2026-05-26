using System;
using System.Drawing;
using System.Windows.Forms;
using AtlasCadCore.ApiClient;

namespace AtlasCadCore.Forms
{
    public class MissingPartChoiceForm : Form
    {
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
