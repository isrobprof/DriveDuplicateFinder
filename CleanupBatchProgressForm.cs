using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Utilities;

namespace DriveDuplicateFinder;

public sealed class CleanupBatchProgressForm : Form
{
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(640, 0) };
    private readonly Label _group = new() { AutoSize = true };
    private readonly Label _file = new() { AutoSize = true, MaximumSize = new Size(640, 0) };
    private readonly Label _totals = new() { AutoSize = true };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0 };
    private readonly Button _stopButton = new() { AutoSize = true, Text = "Detener después del archivo actual" };

    public CleanupBatchProgressForm()
    {
        Text = "Limpieza por lotes en curso";
        StartPosition = FormStartPosition.CenterParent;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(700, 250);
        _stopButton.Click += (_, _) =>
        {
            StopRequested = true;
            _stopButton.Enabled = false;
            _status.Text = "Deteniendo después del archivo actual...";
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 6 };
        for (int index = 0; index < 5; index++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(_group, 0, 1);
        layout.Controls.Add(_file, 0, 2);
        layout.Controls.Add(_totals, 0, 3);
        layout.Controls.Add(_progress, 0, 4);
        layout.Controls.Add(_stopButton, 0, 5);
        Controls.Add(layout);
    }

    public bool StopRequested { get; private set; }

    public void Report(CleanupBatchProgress progress)
    {
        _status.Text = progress.Status;
        _group.Text = $"Grupo {progress.CurrentGroup:N0} de {progress.TotalGroups:N0}";
        _file.Text = progress.FileName is null
            ? ""
            : $"Archivo {progress.CurrentFile:N0} de {progress.TotalFiles:N0}: {progress.FileName}{Environment.NewLine}{progress.FilePath}";
        _totals.Text = $"Archivos enviados: {progress.TrashedFiles:N0}. Espacio enviado a la papelera: {FileSizeFormatter.Format(progress.TrashedBytes)}.";
        _progress.Maximum = Math.Max(1, progress.TotalFiles);
        _progress.Value = Math.Clamp(progress.CurrentFile, 0, _progress.Maximum);
    }

    public void Complete(string status)
    {
        _status.Text = status;
        _stopButton.Enabled = false;
    }
}
