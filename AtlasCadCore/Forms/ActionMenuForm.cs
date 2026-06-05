using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AtlasCadCore.Forms
{
    /// <summary>One labelled action in <see cref="ActionMenuForm"/>.</summary>
    public class MenuAction
    {
        public string Label;
        public Action OnClick;
        public MenuAction(string label, Action onClick) { Label = label; OnClick = onClick; }
    }

    /// <summary>
    /// A simple mouse-clickable action menu — one button per action, stacked
    /// vertically. Replaces the old VBScript InputBox where the user had to type
    /// a number. Picking an action auto-closes the menu (the menu hides
    /// immediately, the action runs, then the menu closes); re-run the macro to
    /// pick another. Close dismisses without running anything.
    /// </summary>
    public class ActionMenuForm : Form
    {
        public ActionMenuForm(string title, IList<MenuAction> actions)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false;
            ShowInTaskbar = true;

            const int margin = 16, btnW = 360, btnH = 38, gap = 8;
            int y = margin;

            foreach (var action in actions)
            {
                var captured = action;   // avoid closure-over-loop-variable
                var btn = new Button
                {
                    Text = captured.Label,
                    Location = new Point(margin, y),
                    Size = new Size(btnW, btnH),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(10, 0, 0, 0),
                    UseVisualStyleBackColor = true,
                    Font = new Font("Segoe UI", 9.5f),
                };
                btn.Click += (s, e) =>
                {
                    // Auto-close: hide the menu the instant an action is picked,
                    // run the action (it opens its own dialog), then close for
                    // good so ShowDialog returns.
                    Hide();
                    try { captured.OnClick?.Invoke(); }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Action failed: " + ex.Message, "Atlas",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    Close();
                };
                Controls.Add(btn);
                y += btnH + gap;
            }

            var closeBtn = new Button
            {
                Text = "Close",
                Location = new Point(margin, y + 4),
                Size = new Size(btnW, btnH),
                DialogResult = DialogResult.Cancel,
                Font = new Font("Segoe UI", 9.5f),
            };
            Controls.Add(closeBtn);
            CancelButton = closeBtn;

            ClientSize = new Size(btnW + 2 * margin, y + btnH + 4 + margin);
        }
    }
}
