using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace LootLedger
{
    public sealed class ChestShortcut
    {
        public int[] Keys { get; private set; }
        public string Text { get; private set; }

        public ChestShortcut(IEnumerable<int> keys)
        {
            Keys = keys.Select(Normalize).Distinct().OrderBy(Order).ThenBy(k => k).ToArray();
            if (Keys.Length == 0 || Keys.Any(k => k < 8 || k > 254))
                throw new ArgumentException("Press a keyboard key or combination.");
            Text = string.Join("+", Keys.Select(Name).ToArray());
        }

        public static int Normalize(int key)
        {
            if (key == 0xA0 || key == 0xA1) return 0x10;
            if (key == 0xA2 || key == 0xA3) return 0x11;
            if (key == 0xA4 || key == 0xA5) return 0x12;
            if (key == 0x5C) return 0x5B;
            return key;
        }

        private static int Order(int key)
        {
            return key == 0x11 ? 0 : key == 0x12 ? 1 : key == 0x10 ? 2 : key == 0x5B ? 3 : 4;
        }

        private static string Name(int key)
        {
            switch (key)
            {
                case 0x10: return "Shift";
                case 0x11: return "Ctrl";
                case 0x12: return "Alt";
                case 0x5B: return "Win";
                case 0x14: return "CapsLock";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0x0D: return "Enter";
                case 0x1B: return "Esc";
                default: return ((Keys)key).ToString();
            }
        }

        public static ChestShortcut Parse(string text)
        {
            var keys = new List<int>();
            foreach (string part in text.Split('+'))
            {
                string name = part.Trim();
                switch (name.ToUpperInvariant())
                {
                    case "CTRL": case "CONTROL": keys.Add(0x11); break;
                    case "ALT": keys.Add(0x12); break;
                    case "SHIFT": keys.Add(0x10); break;
                    case "WIN": case "WINDOWS": keys.Add(0x5B); break;
                    case "CAPSLOCK": case "CAPITAL": keys.Add(0x14); break;
                    case "PGDN": case "PAGEDOWN": case "NEXT": keys.Add(0x22); break;
                    case "PGUP": case "PAGEUP": case "PRIOR": keys.Add(0x21); break;
                    case "ESC": keys.Add(0x1B); break;
                    default:
                        var key = (Keys)new KeysConverter().ConvertFromInvariantString(name);
                        keys.Add((int)key);
                        break;
                }
            }
            return new ChestShortcut(keys);
        }
    }

    public sealed class LedgerMenuRenderer : ToolStripProfessionalRenderer
    {
        public static readonly Color Background = Color.FromArgb(23, 24, 31);
        public static readonly Color Gold = Color.FromArgb(255, 207, 135);
        public static readonly Color Ink = Color.FromArgb(242, 237, 246);
        public LedgerMenuRenderer() { RoundedEdges = false; }

        public static void Style(ToolStrip menu)
        {
            menu.Renderer = new LedgerMenuRenderer();
            menu.BackColor = Background;
            menu.ForeColor = Ink;
            menu.Font = new Font("Segoe UI", 10f);
            menu.Padding = new Padding(6, 6, 6, 6);
            menu.AutoSize = true;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Background);
        }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var pen = new Pen(Color.FromArgb(72, 65, 54)))
                e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            using (var brush = new SolidBrush(Color.FromArgb(54, 47, 39)))
                e.Graphics.FillRectangle(brush, new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2));
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Color.FromArgb(131, 128, 144) : e.Item.Selected ? Gold : e.Item.ForeColor;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using (var pen = new Pen(Gold, 2f))
                e.Graphics.DrawLines(pen, new[] { new Point(r.Left + 2, r.Top + r.Height / 2),
                    new Point(r.Left + 6, r.Bottom - 3), new Point(r.Right - 1, r.Top + 3) });
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Enabled ? Gold : Color.Gray;
            base.OnRenderArrow(e);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (var pen = new Pen(Color.FromArgb(49, 48, 59)))
                e.Graphics.DrawLine(pen, 12, e.Item.Height / 2, e.Item.Width - 12, e.Item.Height / 2);
        }
    }

    // Capture at the message-preview stage so Tab, Enter, Escape and Alt are
    // recorded as keys instead of navigating controls or invoking menu commands.
    public sealed class ChestHotkeyDialog : Form, IMessageFilter
    {
        private readonly HashSet<int> held = new HashSet<int>();
        private readonly HashSet<int> chord = new HashSet<int>();
        private readonly Label keyLabel;
        private readonly Label hint;
        private readonly Button save;
        public ChestShortcut Shortcut { get; private set; }
        public Action<ChestShortcut> SaveShortcut { get; set; }
        public string SaveError { get; private set; }

        public ChestHotkeyDialog(string current)
        {
            Text = "Chest hotkey";
            ClientSize = new Size(440, 256);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            BackColor = LedgerMenuRenderer.Background;
            ForeColor = LedgerMenuRenderer.Ink;
            Font = new Font("Segoe UI", 10f);
            KeyPreview = true;

            Controls.Add(new Label { Text = "Chest hotkey", Font = new Font("Segoe UI Semibold", 17f),
                ForeColor = LedgerMenuRenderer.Gold, Bounds = new Rectangle(24, 20, 390, 34) });
            Controls.Add(new Label { Text = "Press a key or hold a combination together.",
                Bounds = new Rectangle(24, 62, 392, 24) });
            keyLabel = new Label { Text = "Listening for keys...", TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 15f), ForeColor = LedgerMenuRenderer.Gold,
                BackColor = Color.FromArgb(39, 36, 35), Bounds = new Rectangle(24, 100, 392, 62) };
            Controls.Add(keyLabel);
            hint = new Label { Text = "Current: " + current + "   |   Press keys, then click Save",
                ForeColor = Color.FromArgb(178, 171, 187), Bounds = new Rectangle(24, 174, 392, 32) };
            Controls.Add(hint);
            var cancel = new Button { Text = "Cancel", FlatStyle = FlatStyle.Flat,
                Bounds = new Rectangle(230, 211, 88, 30), TabStop = false };
            cancel.FlatAppearance.BorderColor = Color.FromArgb(77, 70, 62);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
            save = new Button { Text = "Save", FlatStyle = FlatStyle.Flat,
                Bounds = new Rectangle(328, 211, 88, 30), TabStop = false, Enabled = false,
                BackColor = LedgerMenuRenderer.Gold, ForeColor = LedgerMenuRenderer.Background };
            save.FlatAppearance.BorderSize = 0;
            save.Click += delegate { SaveSelection(); };
            Controls.Add(save);
        }

        private void SaveSelection()
        {
            if (Shortcut == null) return;
            try
            {
                SaveError = null;
                // Persist and refresh the tray before closing. A failed write
                // leaves the selected shortcut available for another attempt.
                if (SaveShortcut != null) SaveShortcut(Shortcut);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception error)
            {
                SaveError = error.GetBaseException().Message;
                hint.Text = "Could not save. Please try Save again.";
                hint.ForeColor = Color.FromArgb(245, 164, 101);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Application.AddMessageFilter(this);
            Activate();
        }

        public bool PreFilterMessage(ref Message msg)
        {
            // Observe the dialog's keyboard messages before focused buttons
            // can consume navigation keys or system-key releases.
            if (ContainsFocus && (msg.Msg == 0x100 || msg.Msg == 0x101 || msg.Msg == 0x104 || msg.Msg == 0x105))
                return ProcessKeyPreview(ref msg);
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Application.RemoveMessageFilter(this);
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Color.FromArgb(72, 65, 54)))
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            RecordDown((int)(keyData & System.Windows.Forms.Keys.KeyCode));
            return true;
        }

        protected override bool ProcessKeyPreview(ref Message msg)
        {
            if (msg.Msg == 0x100 || msg.Msg == 0x104)
            {
                RecordDown(msg.WParam.ToInt32());
                return true;
            }
            if (msg.Msg == 0x101 || msg.Msg == 0x105)
            {
                held.Remove(ChestShortcut.Normalize(msg.WParam.ToInt32()));
                if (held.Count == 0 && chord.Count != 0)
                {
                    Shortcut = new ChestShortcut(chord);
                    save.Enabled = true;
                    hint.Text = "Click Save to apply, or press a different shortcut.";
                }
                return true;
            }
            return base.ProcessKeyPreview(ref msg);
        }

        private void RecordDown(int key)
        {
            key = ChestShortcut.Normalize(key);
            if (key < 8 || key > 254) return;
            if (held.Count == 0) chord.Clear();
            // Include modifiers already held when the dialog received focus.
            if ((ModifierKeys & System.Windows.Forms.Keys.Control) != 0) held.Add(0x11);
            if ((ModifierKeys & System.Windows.Forms.Keys.Alt) != 0) held.Add(0x12);
            if ((ModifierKeys & System.Windows.Forms.Keys.Shift) != 0) held.Add(0x10);
            held.Add(key);
            chord.UnionWith(held);
            Shortcut = new ChestShortcut(chord);
            keyLabel.Text = Shortcut.Text;
            save.Enabled = false;
            hint.ForeColor = Color.FromArgb(178, 171, 187);
            hint.Text = "Release the keys, then click Save.";
        }

        protected override void OnDeactivate(EventArgs e)
        {
            held.Clear();
            save.Enabled = Shortcut != null;
            keyLabel.Text = Shortcut != null ? Shortcut.Text : "Click here to resume";
            base.OnDeactivate(e);
        }

        protected override void OnActivated(EventArgs e)
        {
            keyLabel.Text = Shortcut != null ? Shortcut.Text : "Listening for keys...";
            base.OnActivated(e);
        }
    }
}
