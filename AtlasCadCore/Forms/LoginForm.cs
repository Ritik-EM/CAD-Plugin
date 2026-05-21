using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtlasCadCore.Auth;
using AtlasCadCore.Utility;

namespace AtlasCadCore.Forms
{
    public class LoginForm : Form
    {
        private readonly AuthService _auth;
        private TextBox _userBox;
        private TextBox _passwordBox;
        private Button _loginButton;
        private Button _cancelButton;
        private Label _statusLabel;

        public StoredToken Token { get; private set; }

        public LoginForm(AuthService auth, string presetUser)
        {
            _auth = auth;
            Text = $"Atlas — Sign In  ({PluginVersion.Display})";
            Width = 420;
            Height = 270;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;

            int y = 20;
            Controls.Add(new Label { Text = "Username", Location = new Point(15, y), Width = 80 });
            _userBox = new TextBox { Location = new Point(105, y - 3), Width = 285, Text = presetUser ?? "" };
            Controls.Add(_userBox);

            y += 38;
            Controls.Add(new Label { Text = "Password", Location = new Point(15, y), Width = 80 });
            _passwordBox = new TextBox { Location = new Point(105, y - 3), Width = 285, UseSystemPasswordChar = true };
            Controls.Add(_passwordBox);

            y += 42;
            _statusLabel = new Label
            {
                Location = new Point(15, y), Width = 380, Height = 36, ForeColor = Color.Firebrick,
            };
            Controls.Add(_statusLabel);

            y += 42;
            _cancelButton = new Button
            {
                Text = "Cancel", Location = new Point(210, y), Width = 85, DialogResult = DialogResult.Cancel,
            };
            Controls.Add(_cancelButton);

            _loginButton = new Button { Text = "Sign In", Location = new Point(305, y), Width = 85 };
            _loginButton.Click += async (s, e) => await TryLogin();
            Controls.Add(_loginButton);

            // Version footer — bottom-left, small grey text so it's discoverable
            // without competing with the sign-in CTA. Anchored Bottom so it
            // stays in the corner even if the form is resized in a future tweak.
            var versionLabel = new Label
            {
                Text = $"Atlas CAD Plugin {PluginVersion.Display}",
                Location = new Point(15, y + 36),
                Width = 200,
                AutoSize = true,
                ForeColor = Color.DimGray,
                Font = new Font(Font.FontFamily, 8f),
            };
            Controls.Add(versionLabel);

            AcceptButton = _loginButton;
            CancelButton = _cancelButton;

            Shown += (s, e) =>
            {
                if (string.IsNullOrEmpty(_userBox.Text)) _userBox.Focus();
                else _passwordBox.Focus();
            };
        }

        private async Task TryLogin()
        {
            if (string.IsNullOrWhiteSpace(_userBox.Text) || string.IsNullOrWhiteSpace(_passwordBox.Text))
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = "Enter username and password.";
                return;
            }

            _statusLabel.ForeColor = Color.DimGray;
            _statusLabel.Text = "Signing in…";
            _loginButton.Enabled = false;
            _cancelButton.Enabled = false;
            try
            {
                Token = await _auth.LoginAsync(_userBox.Text.Trim(), _passwordBox.Text);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (AuthException ex)
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = ex.Message;
            }
            catch (Exception ex)
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = "Unexpected error: " + ex.Message;
            }
            finally
            {
                _loginButton.Enabled = true;
                _cancelButton.Enabled = true;
            }
        }
    }
}
