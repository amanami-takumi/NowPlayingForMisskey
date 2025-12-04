using System;
using System.Drawing;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    internal sealed class MisskeySettingsDialog : Form
    {
        private readonly TextBox _instanceTextBox;
        private readonly TextBox _tokenTextBox;
        private readonly NumericUpDown _frequencyUpDown;
        private readonly TextBox _hashtagsTextBox;
        private readonly CheckBox _attachArtworkCheckBox;

        public PluginSettings ResultSettings { get; private set; }

        public MisskeySettingsDialog(PluginSettings initialSettings)
        {
            Text = "Misskey 投稿設定";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(420, 300);
            var instanceLabel = new Label
            {
                AutoSize = true,
                Location = new Point(15, 20),
                Text = "インスタンスURL"
            };
            _instanceTextBox = new TextBox
            {
                Location = new Point(18, 40),
                Width = 380
            };

            var tokenLabel = new Label
            {
                AutoSize = true,
                Location = new Point(15, 75),
                Text = "アクセストークン"
            };
            _tokenTextBox = new TextBox
            {
                Location = new Point(18, 95),
                Width = 380,
                UseSystemPasswordChar = true
            };

            var frequencyLabel = new Label
            {
                AutoSize = true,
                Location = new Point(15, 130),
                Text = "投稿頻度（n曲に1回）"
            };
            _frequencyUpDown = new NumericUpDown
            {
                Location = new Point(18, 150),
                Minimum = 1,
                Maximum = 50,
                Width = 120
            };

            var hashtagLabel = new Label
            {
                AutoSize = true,
                Location = new Point(15, 185),
                Text = "カスタムハッシュタグ（任意）"
            };
            _hashtagsTextBox = new TextBox
            {
                Location = new Point(18, 205),
                Width = 380
            };

            _attachArtworkCheckBox = new CheckBox
            {
                AutoSize = true,
                Location = new Point(18, 235),
                Text = "アルバムアートを添付する",
                Checked = true
            };
            var okButton = new Button
            {
                Text = "保存",
                DialogResult = DialogResult.OK,
                Location = new Point(232, 250),
                Size = new Size(80, 30)
            };
            okButton.Click += HandleOkButtonClicked;

            var cancelButton = new Button
            {
                Text = "キャンセル",
                DialogResult = DialogResult.Cancel,
                Location = new Point(318, 250),
                Size = new Size(80, 30)
            };

            Controls.AddRange(new Control[]
            {
                instanceLabel,
                _instanceTextBox,
                tokenLabel,
                _tokenTextBox,
                frequencyLabel,
                _frequencyUpDown,
                hashtagLabel,
                _hashtagsTextBox,
                _attachArtworkCheckBox,
                okButton,
                cancelButton
            });

            AcceptButton = okButton;
            CancelButton = cancelButton;

            if (initialSettings != null)
            {
                _instanceTextBox.Text = initialSettings.InstanceUrl;
                _tokenTextBox.Text = initialSettings.AccessToken;
                _frequencyUpDown.Value = Math.Max(1, Math.Min(initialSettings.PostEvery, (int)_frequencyUpDown.Maximum));
                _hashtagsTextBox.Text = initialSettings.CustomHashtags ?? string.Empty;
                _attachArtworkCheckBox.Checked = initialSettings.AttachAlbumArt;
            }
            else
            {
                _frequencyUpDown.Value = 1;
                _attachArtworkCheckBox.Checked = true;
            }
        }

        private void HandleOkButtonClicked(object sender, EventArgs e)
        {
            var normalizedInstance = NormalizeInstanceUrl(_instanceTextBox.Text);
            if (string.IsNullOrWhiteSpace(normalizedInstance))
            {
                ShowValidationError("インスタンスURLを入力してください。");
                _instanceTextBox.Focus();
                return;
            }

            if (!Uri.TryCreate(normalizedInstance, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ShowValidationError("有効なインスタンスURLを入力してください。");
                _instanceTextBox.Focus();
                return;
            }

            var token = _tokenTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                ShowValidationError("アクセストークンを入力してください。");
                _tokenTextBox.Focus();
                return;
            }

            ResultSettings = new PluginSettings
            {
                InstanceUrl = normalizedInstance,
                AccessToken = token,
                PostEvery = (int)_frequencyUpDown.Value,
                CustomHashtags = _hashtagsTextBox.Text?.Trim(),
                AttachAlbumArt = _attachArtworkCheckBox.Checked
            };

            DialogResult = DialogResult.OK;
            Close();
        }

        private static string NormalizeInstanceUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim().TrimEnd('/');
            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "https://" + trimmed;
            }

            return trimmed;
        }

        private void ShowValidationError(string message)
        {
            MessageBox.Show(this, message, "Misskey 投稿設定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
