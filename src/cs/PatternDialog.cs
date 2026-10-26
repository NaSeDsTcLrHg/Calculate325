/*
 * Calculate325 —— 自定义数字串窗口（新窗口）
 * ---------------------------------------------------------------------------
 * 左侧列出当前已有的自定义数字串，右侧输入框可新增 / 删除。
 * 关闭后主窗口的“数字串”菜单会自动重建（自定义项排在“我全都要！”上方）。
 */
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Calculate325
{
    sealed class PatternDialog : Form
    {
        readonly Skin skin;
        readonly ListBox list = new ListBox();
        readonly TextBox input = new TextBox();
        readonly Label hint = new Label();
        readonly ChamferButton btnAdd = new ChamferButton();
        readonly ChamferButton btnDel = new ChamferButton();
        readonly ChamferButton btnDone = new ChamferButton();

        /// <summary>关闭后主窗口据此决定选中项。</summary>
        public string ResultPattern;

        public PatternDialog(Skin s)
        {
            skin = s ?? Skins.Plain;
            Text = "自定义数字串";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(430, 300);
            AppIcon.Apply(this);
            ThemedCaption.Hook(this, skin);
            BackColor = skin.FormBack;
            ForeColor = skin.Ink;

            Label title = new Label();
            title.Text = "自定义数字串（只允许数字，1–8 位）";
            title.AutoSize = false;
            title.Bounds = new Rectangle(16, 12, 400, 20);
            title.ForeColor = skin.Ink;
            Controls.Add(title);

            list.Bounds = new Rectangle(16, 38, 250, 158);
            list.BorderStyle = BorderStyle.FixedSingle;
            list.BackColor = skin.FieldBack;
            list.ForeColor = skin.FieldFore;
            list.Font = new Font("Consolas", 10f);
            Controls.Add(list);

            input.Bounds = new Rectangle(16, 208, 250, 30);
            input.BorderStyle = BorderStyle.FixedSingle;
            input.BackColor = skin.FieldBack;
            input.ForeColor = skin.FieldFore;
            input.Font = new Font("Consolas", 11f);
            input.KeyDown += delegate(object s2, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoAdd(); }
            };
            Controls.Add(input);

            hint.Bounds = new Rectangle(16, 246, 398, 40);
            hint.AutoSize = false;
            hint.ForeColor = skin.Muted;
            hint.Text = "最多 8 位：当前求解引擎对 9 位以上基本无解。";
            Controls.Add(hint);

            btnAdd.Text = "添加";
            btnAdd.Bounds = new Rectangle(282, 38, 132, 34);
            Style(btnAdd);
            btnAdd.Click += delegate { DoAdd(); };
            Controls.Add(btnAdd);

            btnDel.Text = "删除选中";
            btnDel.Bounds = new Rectangle(282, 80, 132, 34);
            Style(btnDel);
            btnDel.Click += delegate
            {
                if (list.SelectedItem == null)
                {
                    Say("先在左侧选中要删除的数字串。", true);
                    return;
                }
                string p = list.SelectedItem.ToString();
                CustomPatterns.Remove(p);
                Refresh2();
                Say("已删除 " + p, false);
            };
            Controls.Add(btnDel);

            btnDone.Text = "完成";
            btnDone.Bounds = new Rectangle(282, 208, 132, 34);
            Style(btnDone);
            btnDone.Click += delegate { Close(); };
            Controls.Add(btnDone);

            list.SelectedIndexChanged += delegate
            {
                if (list.SelectedItem != null) input.Text = list.SelectedItem.ToString();
            };

            Refresh2();
            AcceptButton = null;
        }

        void Style(ChamferButton b)
        {
            b.ApplySkin(skin);
            b.Cut = 6;
        }

        void Say(string msg, bool warn)
        {
            hint.Text = msg;
            hint.ForeColor = warn ? skin.Warn : skin.Muted;
        }

        void DoAdd()
        {
            string err = CustomPatterns.Validate(input.Text);
            if (err != null)
            {
                Say(err, true);
                input.Focus();
                input.SelectAll();
                return;
            }
            string p = input.Text.Trim();
            CustomPatterns.Add(p);
            Refresh2(p);
            input.Clear();
            Say(p.Length >= 7
                ? ("已添加 " + p + "：位数较多，求解可能很慢。")
                : ("已添加 " + p), p.Length >= 7);
            input.Focus();
        }

        void Refresh2(string select = null)
        {
            list.Items.Clear();
            for (int i = 0; i < CustomPatterns.Items.Count; i++) list.Items.Add(CustomPatterns.Items[i]);
            if (select != null)
            {
                int idx = list.Items.IndexOf(select);
                if (idx >= 0) list.SelectedIndex = idx;
            }
            ResultPattern = CustomPatterns.Items.Count > 0 ? CustomPatterns.Items[CustomPatterns.Items.Count - 1] : null;
        }
    }
}
