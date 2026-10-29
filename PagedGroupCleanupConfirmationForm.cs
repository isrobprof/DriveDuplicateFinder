using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Services;
using DriveDuplicateFinder.Utilities;

namespace DriveDuplicateFinder;

/// <summary>Explicit review of all Keep and candidate IDs before the coordinator's final gate.</summary>
public sealed class PagedGroupCleanupConfirmationForm : Form
{
    private readonly CheckBox _keepsReviewed = new()
    {
        AutoSize = true,
        Text = "He revisado todos los archivos CONSERVAR y sus identificadores."
    };
    private readonly CheckBox _candidatesReviewed = new()
    {
        AutoSize = true,
        Text = "He revisado todos los candidatos explícitos y sus identificadores."
    };
    private readonly Button _confirm = new()
    {
        AutoSize = true,
        DialogResult = DialogResult.OK,
        Enabled = false,
        Text = string.Empty
    };

    public PagedGroupCleanupConfirmationForm(PagedGroupCleanupPlan plan, bool offlineSimulation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Text = offlineSimulation
            ? "Confirmación explícita — simulación local"
            : "Confirmación final — ejecución paginada bloqueada";
        _confirm.Text = offlineSimulation
            ? "Confirmar simulación offline"
            : "Confirmar revisión (la escritura está bloqueada)";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        MinimumSize = new Size(900, 620);
        ClientSize = new Size(1120, 760);

        var warning = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DarkRed,
            Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            Text = offlineSimulation
                ? "MODO DEMOSTRACIÓN. Confirmar ejecutará el coordinador únicamente contra un transporte HTTP local en memoria. No se iniciará OAuth, no se accederá a Google Drive y no se modificará ningún archivo real."
                : "EJECUCIÓN REAL DESHABILITADA EN ESTA VERSIÓN. Esta revisión final puede continuar la revalidación, pero el coordinador rechazará toda sesión real antes de iniciar una escritura. No se enviará ningún archivo a la papelera."
        };
        var identity = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = $"Inventario: {plan.Identity.Inventory.ScanId}{Environment.NewLine}" +
                   $"Grupo: {plan.Identity.SizeBytes:N0} bytes + MD5 {plan.Identity.NormalizedChecksum}{Environment.NewLine}" +
                   $"Miembros: {plan.LocalReview.MemberCount:N0} | Keep: {plan.KeepFiles.Count:N0} | candidatos: {plan.CandidateFiles.Count:N0} | pendientes excluidos: {plan.LocalReview.UndecidedCount:N0} | potencial: {FileSizeFormatter.Format(new System.Numerics.BigInteger(plan.Identity.SizeBytes) * plan.CandidateFiles.Count)}"
        };
        var keepGrid = CreateGrid();
        keepGrid.DataSource = plan.KeepFiles.Select(ToRow).ToList();
        var candidateGrid = CreateGrid();
        candidateGrid.DataSource = plan.CandidateFiles.Select(ToRow).ToList();

        _keepsReviewed.CheckedChanged += (_, _) => UpdateConfirmationState();
        _candidatesReviewed.CheckedChanged += (_, _) => UpdateConfirmationState();
        var cancel = new Button { AutoSize = true, DialogResult = DialogResult.Cancel, Text = "Cancelar" };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_confirm);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 9
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(warning, 0, 0);
        layout.Controls.Add(identity, 0, 1);
        layout.Controls.Add(new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Text = "TODOS los archivos que se conservarán" }, 0, 2);
        layout.Controls.Add(keepGrid, 0, 3);
        layout.Controls.Add(new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Text = "ÚNICAMENTE estos candidatos explícitos" }, 0, 4);
        layout.Controls.Add(candidateGrid, 0, 5);
        layout.Controls.Add(_keepsReviewed, 0, 6);
        layout.Controls.Add(_candidatesReviewed, 0, 7);
        layout.Controls.Add(buttons, 0, 8);
        Controls.Add(layout);
        AcceptButton = _confirm;
        CancelButton = cancel;
    }

    private void UpdateConfirmationState() => _confirm.Enabled = _keepsReviewed.Checked && _candidatesReviewed.Checked;

    private static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Dock = DockStyle.Fill,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        grid.Columns.AddRange(
        [
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(ConfirmationRow.Id), HeaderText = "FileId", FillWeight = 90 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(ConfirmationRow.Name), HeaderText = "Archivo", FillWeight = 100 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(ConfirmationRow.Path), HeaderText = "Ruta", FillWeight = 170 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(ConfirmationRow.Size), HeaderText = "Tamaño", FillWeight = 65 }
        ]);
        return grid;
    }

    private static ConfirmationRow ToRow(CleanupFileSnapshot file) => new(
        file.Id,
        file.Name,
        file.Path,
        FileSizeFormatter.Format(file.Size ?? 0));

    private sealed record ConfirmationRow(string Id, string Name, string Path, string Size);
}
