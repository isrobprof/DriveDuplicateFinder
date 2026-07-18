using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Utilities;

namespace DriveDuplicateFinder;

public sealed class CleanupConfirmationForm : Form
{
    private readonly CheckBox _keepReviewed = new() { AutoSize = true, Text = "He revisado el archivo que se conservará." };
    private readonly CheckBox _candidatesReviewed = new() { AutoSize = true, Text = "He revisado las copias que se enviarán a la papelera." };
    private readonly Button _trashButton = new() { Text = "Enviar a la papelera", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };

    public CleanupConfirmationForm(CleanupPreflightResult preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        if (!preflight.IsSuccessful || preflight.KeepFile is null)
        {
            throw new ArgumentException("La confirmación requiere un preflight correcto.", nameof(preflight));
        }

        Text = "Confirmar envío de un grupo a la papelera";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 680);
        Size = new Size(1120, 760);

        var keepGrid = CreateGrid();
        keepGrid.DataSource = new[] { ToRow(preflight.KeepFile) };
        var candidateGrid = CreateGrid();
        candidateGrid.DataSource = preflight.CandidateFiles.Select(ToRow).ToList();

        var summary = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1040, 0),
            Text = $"Se conservará una copia. Se procesará únicamente este grupo y no habrá eliminación permanente. Candidatos: {preflight.CandidateFiles.Count:N0}. Espacio seleccionado: {FileSizeFormatter.Format(preflight.CandidateFiles.Sum(file => file.Size ?? 0))}."
        };
        _keepReviewed.CheckedChanged += (_, _) => UpdateConfirmationButton();
        _candidatesReviewed.CheckedChanged += (_, _) => UpdateConfirmationButton();

        var cancelButton = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_trashButton);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 34F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 66F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Archivo que se conservará", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(keepGrid, 0, 2);
        layout.Controls.Add(new Label { Text = "Archivos que se enviarán a la papelera", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 3);
        layout.Controls.Add(candidateGrid, 0, 4);
        layout.Controls.Add(summary, 0, 5);
        layout.Controls.Add(_keepReviewed, 0, 6);
        layout.Controls.Add(_candidatesReviewed, 0, 7);

        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.Controls.Add(layout, 0, 0);
        outer.Controls.Add(buttons, 0, 1);
        Controls.Add(outer);
        AcceptButton = _trashButton;
        CancelButton = cancelButton;
    }

    private void UpdateConfirmationButton() => _trashButton.Enabled = _keepReviewed.Checked && _candidatesReviewed.Checked;

    private static DataGridView CreateGrid()
    {
        return new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false
        };
    }

    private static ConfirmationFileRow ToRow(CleanupFileSnapshot file) => new()
    {
        Name = file.Name,
        Path = file.Path,
        Size = FileSizeFormatter.Format(file.Size ?? 0),
        Md5Checksum = file.Md5Checksum ?? "No disponible",
        ModifiedTime = file.ModifiedTime?.ToLocalTime().ToString("g") ?? "No disponible",
        Id = file.Id
    };

    private sealed class ConfirmationFileRow
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required string Size { get; init; }
        public required string Md5Checksum { get; init; }
        public required string ModifiedTime { get; init; }
        public required string Id { get; init; }
    }
}
