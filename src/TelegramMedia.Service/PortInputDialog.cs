using System.Windows.Forms;

namespace TelegramMedia.Service;

public class PortInputDialog : Form
{
    private readonly NumericUpDown _portInput;
    public int SelectedPort => (int)_portInput.Value;

    public PortInputDialog(int currentPort)
    {
        Text = "Set Dashboard Port";
        Size = new Size(340, 180);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;

        var label = new Label
        {
            Text = "Enter the port number for the web dashboard:",
            Location = new Point(20, 20),
            Size = new Size(280, 20)
        };

        _portInput = new NumericUpDown
        {
            Minimum = 1024,
            Maximum = 65535,
            Value = currentPort,
            Location = new Point(20, 48),
            Size = new Size(120, 28),
            Font = new Font("Segoe UI", 11)
        };

        var hint = new Label
        {
            Text = "Valid range: 1024 - 65535",
            Location = new Point(150, 52),
            Size = new Size(160, 20),
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8.5f)
        };

        var okButton = new Button
        {
            Text = "Save && Restart",
            DialogResult = DialogResult.OK,
            Location = new Point(20, 95),
            Size = new Size(130, 32)
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(160, 95),
            Size = new Size(90, 32)
        };

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[] { label, _portInput, hint, okButton, cancelButton });
    }
}
