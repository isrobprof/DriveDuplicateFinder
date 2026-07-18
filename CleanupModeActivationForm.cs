namespace DriveDuplicateFinder;

public sealed class CleanupModeActivationForm : Form
{
    private readonly CheckBox _acknowledgement = new()
    {
        AutoSize = true,
            Text = "Entiendo que la aplicación recibirá permiso para administrar mis archivos de Google Drive y que solo enviará a la papelera los candidatos que confirme."
    };

    private readonly Button _continueButton = new()
    {
        Text = "Continuar con autorización",
        AutoSize = true,
        Enabled = false,
        DialogResult = DialogResult.OK
    };

    public CleanupModeActivationForm()
    {
        Text = "Activar modo limpieza";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 250);

        var information = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            Text = "El análisis normal es de solo lectura. El modo limpieza solicitará permiso para ver y administrar todos los archivos de Google Drive.\r\n\r\nLa aplicación solo implementa el envío a la papelera de archivos que selecciones y confirmes. No implementa borrado permanente, vaciado de papelera, descarga ni modificación del contenido."
        };

        _acknowledgement.CheckedChanged += (_, _) => _continueButton.Enabled = _acknowledgement.Checked;
        var cancelButton = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_continueButton);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(information, 0, 0);
        layout.Controls.Add(_acknowledgement, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = _continueButton;
        CancelButton = cancelButton;
    }
}
