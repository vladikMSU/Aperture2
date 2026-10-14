using System;
using System.Drawing;
using System.Windows.Forms;

namespace Aperture.Excel
{
    internal sealed class SettingsDialog : Form
    {
        private readonly TextBox root = new TextBox();
        private readonly TextBox node = new TextBox();
        private readonly TextBox python = new TextBox();
        private readonly TextBox provider = new TextBox();
        private readonly TextBox model = new TextBox();
        internal PaneSettings Settings { get; private set; }

        internal SettingsDialog(PaneSettings current)
        {
            Text = "Aperture2 — настройки";
            Font = new Font("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(570, 370);
            AutoScaleMode = AutoScaleMode.Dpi;
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 8 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(table, 0, "Папка Aperture2", root, current.RepositoryRoot);
            AddRow(table, 1, "Node.js (необязательно)", node, current.NodePath);
            AddRow(table, 2, "Python (необязательно)", python, current.PythonPath);
            AddRow(table, 3, "Провайдер", provider, current.Provider);
            AddRow(table, 4, "Модель", model, current.Model);
            var hint = new Label { AutoSize = true, MaximumSize = new Size(520, 0), Text = "Для OpenRouter: /login openrouter в Pi или переменная OPENROUTER_API_KEY. API-ключи в настройках Aperture2 не сохраняются. Пустые провайдер и модель используют настройки Pi. Изменения — со следующей сессии." };
            table.Controls.Add(hint, 0, 5); table.SetColumnSpan(hint, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var save = new Button { Text = "Сохранить", AutoSize = true };
            var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
            save.Click += SaveClick;
            buttons.Controls.Add(save); buttons.Controls.Add(cancel);
            table.Controls.Add(buttons, 0, 7); table.SetColumnSpan(buttons, 2);
            Controls.Add(table); AcceptButton = save; CancelButton = cancel;
        }

        private static void AddRow(TableLayoutPanel table, int row, string title, TextBox box, string value)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            table.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            box.Text = value ?? ""; box.Dock = DockStyle.Fill; table.Controls.Add(box, 1, row);
        }

        private void SaveClick(object sender, EventArgs args)
        {
            Settings = new PaneSettings { RepositoryRoot = root.Text.Trim(), NodePath = node.Text.Trim(), PythonPath = python.Text.Trim(), Provider = provider.Text.Trim(), Model = model.Text.Trim() };
            try { Settings.Save(); DialogResult = DialogResult.OK; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось сохранить", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}
