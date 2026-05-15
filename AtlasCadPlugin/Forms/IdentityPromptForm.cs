using System;
using System.Drawing;
using System.Windows.Forms;

namespace AtlasCadPlugin.Forms
{
    /// <summary>
    /// First-launch prompt: asks the user for their name (used as a stand-in
    /// for real auth in the demo). Also reused by the "Switch Identity" menu.
    /// </summary>
    public class IdentityPromptForm : Form
    {
        private TextBox _nameBox;
        private Button _okButton;

        public string EnteredName { get; private set; }

        public IdentityPromptForm(string current)
        {
            Text = "Atlas — Identify Yourself";
            Width = 400;
            Height = 180;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;

            var label = new Label
            {
                Text = "Your name or email (used to track who checked out which file):",
                Location = new Point(15, 15),
                Width = 360,
            };
            Controls.Add(label);

            _nameBox = new TextBox
            {
                Location = new Point(15, 50),
                Width = 360,
                Text = current ?? "",
            };
            Controls.Add(_nameBox);

            _okButton = new Button
            {
                Text = "OK",
                Location = new Point(290, 90),
                Width = 85,
                DialogResult = DialogResult.OK,
            };
            _okButton.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_nameBox.Text))
                {
                    MessageBox.Show("Please enter a name.", "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
                EnteredName = _nameBox.Text.Trim();
            };
            Controls.Add(_okButton);

            AcceptButton = _okButton;
        }
    }
}
