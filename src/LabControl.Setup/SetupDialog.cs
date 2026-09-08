using System.Resources;
using LabControl.Shared;
using Forms = System.Windows.Forms;

namespace LabControl.Setup;

internal static class SetupDialog
{
    private static readonly ResourceManager Resources = new("LabControl.Setup.SetupStrings", typeof(SetupDialog).Assembly);
    internal static string Text(string name) => Resources.GetString(name) ?? throw new InvalidOperationException("Missing setup text.");

    private static System.Drawing.Icon LoadIcon()
    {
        using var stream = typeof(SetupDialog).Assembly.GetManifestResourceStream("LabControl.Setup.AppIcon")
            ?? throw new InvalidOperationException("Missing setup icon.");
        using var icon = new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)icon.Clone();
    }

    public static (int Number, bool CreateStudent)? Choose(int initialNumber, bool createStudent)
    {
        Forms.Application.EnableVisualStyles();
        using var icon = LoadIcon();
        using var form = new Forms.Form { Icon = icon, Text = Text("Title"), Width = 540, Height = 300,
            StartPosition = Forms.FormStartPosition.CenterScreen, FormBorderStyle = Forms.FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, AutoScaleMode = Forms.AutoScaleMode.Dpi };
        var numberLabel = new Forms.Label { Text = Text("Number"), Left = 20, Top = 23, Width = 170 };
        var number = new Forms.NumericUpDown { Left = 210, Top = 20, Width = 80, Minimum = 1,
            Maximum = Defaults.MaxStudentPcs, Value = Math.Clamp(initialNumber, 1, Defaults.MaxStudentPcs) };
        var account = new Forms.CheckBox { Text = Text("CreateStudent"), Left = 20, Top = 65,
            Width = 485, Height = 40, Checked = createStudent };
        form.Height = 390;
        var explanation = new Forms.Label { Text = Text("Changes"), Left = 20, Top = 110, Width = 485, Height = 165 };
        var install = new Forms.Button { Text = Text("Install"), Left = 250, Top = 300, Width = 145, DialogResult = Forms.DialogResult.OK };
        var cancel = new Forms.Button { Text = Text("Cancel"), Left = 405, Top = 300, Width = 100, DialogResult = Forms.DialogResult.Cancel };
        form.Controls.AddRange([numberLabel, number, account, explanation, install, cancel]);
        form.AcceptButton = install;
        form.CancelButton = cancel;
        return form.ShowDialog() == Forms.DialogResult.OK ? ((int)number.Value, account.Checked) : null;
    }

    public static bool Confirm(string text) => Forms.MessageBox.Show(Text(text), Text("Title"),
        Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Question, Forms.MessageBoxDefaultButton.Button2) == Forms.DialogResult.Yes;

    public static bool RebootCountdown()
    {
        Forms.Application.EnableVisualStyles();
        using var icon = LoadIcon();
        using var form = new Forms.Form { Icon = icon, Text = Text("Title"), Width = 510, Height = 210,
            StartPosition = Forms.FormStartPosition.CenterScreen, FormBorderStyle = Forms.FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, AutoScaleMode = Forms.AutoScaleMode.Dpi };
        var remaining = 30;
        var message = new Forms.Label { Left = 20, Top = 20, Width = 465, Height = 85, ForeColor = System.Drawing.Color.DarkGreen };
        void Refresh() => message.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("RebootCountdown"), remaining);
        Refresh();
        var now = new Forms.Button { Text = Text("RebootNow"), Left = 235, Top = 120, Width = 130, DialogResult = Forms.DialogResult.Yes };
        var later = new Forms.Button { Text = Text("RebootLater"), Left = 375, Top = 120, Width = 110, DialogResult = Forms.DialogResult.No };
        form.Controls.AddRange([message, now, later]);
        form.AcceptButton = now;
        form.CancelButton = later;
        using var timer = new Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining == 0) { timer.Stop(); form.DialogResult = Forms.DialogResult.Yes; form.Close(); }
            else Refresh();
        };
        form.Shown += (_, _) => timer.Start();
        return form.ShowDialog() == Forms.DialogResult.Yes;
    }
}
