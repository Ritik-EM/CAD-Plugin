using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace AtlasCadPlugin.Forms
{
    /// <summary>
    /// Modeless progress dialog for long-running operations (assembly walk,
    /// file hashing, multipart upload). The caller drives it via
    /// `SetPhase(label, current, total)` from any thread; the dialog
    /// marshals back to the UI thread internally.
    ///
    /// `Cancellation` is exposed as a CancellationToken; the Cancel button
    /// flips it so cooperative work can stop.
    /// </summary>
    public class ProgressForm : Form
    {
        private Label _phaseLabel;
        private ProgressBar _bar;
        private Button _cancelButton;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public CancellationToken Cancellation => _cts.Token;

        public ProgressForm(string title)
        {
            Text = title;
            Width = 460;
            Height = 160;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;

            _phaseLabel = new Label
            {
                Location = new Point(15, 20),
                Width = 420,
                Height = 25,
                Text = "Starting…",
            };
            Controls.Add(_phaseLabel);

            _bar = new ProgressBar
            {
                Location = new Point(15, 50),
                Width = 420,
                Height = 20,
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
            };
            Controls.Add(_bar);

            _cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(350, 85),
                Width = 85,
            };
            _cancelButton.Click += (s, e) =>
            {
                _cancelButton.Enabled = false;
                _phaseLabel.Text = "Cancelling…";
                _cts.Cancel();
            };
            Controls.Add(_cancelButton);
        }

        public void SetPhase(string label, int current = 0, int total = 0)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetPhase(label, current, total)));
                return;
            }
            _phaseLabel.Text = label;
            if (total > 0)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Maximum = total;
                _bar.Value = Math.Min(current, total);
            }
            else
            {
                _bar.Style = ProgressBarStyle.Marquee;
                _bar.MarqueeAnimationSpeed = 30;
            }
            Application.DoEvents();
        }

        public void Done()
        {
            if (InvokeRequired) { BeginInvoke(new Action(Done)); return; }
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = _bar.Maximum;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _cts?.Dispose();
            base.Dispose(disposing);
        }
    }
}
