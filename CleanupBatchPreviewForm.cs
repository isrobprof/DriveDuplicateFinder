using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Services;
using DriveDuplicateFinder.Utilities;

namespace DriveDuplicateFinder;

public sealed class CleanupBatchPreviewForm : Form
{
    private readonly CleanupBatchPreview _preview;
    private readonly CheckBox _groupsReviewed = new() { AutoSize = true, Text = "He revisado los grupos y los archivos que se conservarán." };
    private readonly CheckBox _trashUnderstood = new() { AutoSize = true, Text = "Entiendo que los archivos candidatos se enviarán a la papelera de Google Drive." };
    private readonly Button _trashButton = new() { AutoSize = true, Text = "Enviar a la papelera", DialogResult = DialogResult.OK };
    private readonly TextBox _detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };

    public CleanupBatchPreviewForm(CleanupBatchPreview preview)
    {
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        InitializePreview();
    }

    private void InitializePreview()
    {
        Text = "Vista previa de limpieza por lotes";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 680);
        ClientSize = new Size(1220, 820);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;

        var notice = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(1160, 0),
            ForeColor = Color.DarkRed,
            Text = "Esta operación modificará Google Drive. Los archivos candidatos se enviarán a la papelera. No se eliminarán permanentemente."
        };
        var summary = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(1160, 0),
            Text = $"Grupos seleccionados: {_preview.SelectedGroupCount:N0}. Se procesarán: {_preview.ApplicableGroupCount:N0}. Excluidos: {_preview.ExcludedGroupCount:N0}. " +
                   $"Archivos candidatos: {_preview.CandidateFileCount:N0}. Espacio enviado a la papelera: {FileSizeFormatter.Format(_preview.CandidateBytes)}. " +
                   $"Límites: {CleanupBatchService.MaxCleanupBatchGroups:N0} grupos y {CleanupBatchService.MaxCleanupBatchCandidateFiles:N0} archivos candidatos."
        };
        var limit = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DarkRed, Text = _preview.LimitMessage ?? string.Empty, Visible = _preview.LimitsExceeded };

        var applicable = CreateApplicableGrid();
        applicable.SelectionChanged += (_, _) => ShowDetail((applicable.CurrentRow?.DataBoundItem as ApplicableRow)?.Plan);
        var excluded = CreateExcludedGrid();
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var applicablePage = new TabPage($"Se procesarán ({_preview.ApplicableGroupCount:N0})");
        applicablePage.Controls.Add(applicable);
        var excludedPage = new TabPage($"Excluidos ({_preview.ExcludedGroupCount:N0})");
        excludedPage.Controls.Add(excluded);
        tabs.TabPages.Add(applicablePage);
        tabs.TabPages.Add(excludedPage);

        _detail.Text = _preview.ApplicableGroups.Count == 0
            ? "No hay grupos aptos para limpiar. Google Drive no se modificará."
            : "Selecciona un grupo para ver el archivo que se conservará y los candidatos que se enviarán a la papelera.";
        _detail.MinimumSize = new Size(0, 150);
        _groupsReviewed.CheckedChanged += (_, _) => RefreshTrashButton();
        _trashUnderstood.CheckedChanged += (_, _) => RefreshTrashButton();

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
        var cancel = new Button { AutoSize = true, Text = "Cancelar", DialogResult = DialogResult.Cancel };
        actions.Controls.Add(cancel);
        actions.Controls.Add(_trashButton);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(notice, 0, 0);
        layout.Controls.Add(summary, 0, 1);
        layout.Controls.Add(limit, 0, 2);
        layout.Controls.Add(tabs, 0, 3);
        layout.Controls.Add(_detail, 0, 4);
        layout.Controls.Add(_groupsReviewed, 0, 5);
        layout.Controls.Add(_trashUnderstood, 0, 6);
        layout.Controls.Add(actions, 0, 7);
        Controls.Add(layout);
        AcceptButton = _trashButton;
        CancelButton = cancel;
        RefreshTrashButton();
    }

    private DataGridView CreateApplicableGrid()
    {
        var grid = CreateGrid();
        grid.Columns.AddRange(
        [
            Column(nameof(ApplicableRow.Group), "Grupo", 180, DataGridViewAutoSizeColumnMode.Fill),
            Column(nameof(ApplicableRow.Keep), "Se conservará", 180, DataGridViewAutoSizeColumnMode.Fill),
            Column(nameof(ApplicableRow.Candidates), "Candidatos", 85),
            Column(nameof(ApplicableRow.Space), "Espacio", 95),
            Column(nameof(ApplicableRow.Preflight), "Preflight", 105),
            Column(nameof(ApplicableRow.Warnings), "Advertencias", 180, DataGridViewAutoSizeColumnMode.Fill)
        ]);
        grid.DataSource = _preview.ApplicableGroups.Select(plan => new ApplicableRow
        {
            Group = plan.DisplayName,
            Keep = plan.KeepFile.Name,
            Candidates = plan.Candidates.Count,
            Space = FileSizeFormatter.Format(plan.Candidates.Sum(file => file.Size ?? 0)),
            Preflight = "Correcto",
            Warnings = string.Join(" ", plan.Warnings.DefaultIfEmpty("Ninguna")),
            Plan = plan
        }).ToList();
        return grid;
    }

    private DataGridView CreateExcludedGrid()
    {
        var grid = CreateGrid();
        grid.Columns.AddRange(
        [
            Column(nameof(ExcludedRow.Group), "Grupo", 180, DataGridViewAutoSizeColumnMode.Fill),
            Column(nameof(ExcludedRow.Reason), "Motivo", 150),
            Column(nameof(ExcludedRow.Detail), "Detalle", 420, DataGridViewAutoSizeColumnMode.Fill)
        ]);
        grid.DataSource = _preview.ExcludedGroups.Select(group => new ExcludedRow
        {
            Group = group.DisplayName,
            Reason = DescribeReason(group.Reason),
            Detail = group.Detail
        }).ToList();
        return grid;
    }

    private void ShowDetail(CleanupBatchGroupPlan? plan)
    {
        if (plan is null)
        {
            return;
        }

        _detail.Text = $"Se conservará:{Environment.NewLine}" + Describe(plan.KeepFile) +
                       $"{Environment.NewLine}{Environment.NewLine}Se enviarán a la papelera:{Environment.NewLine}" +
                       string.Join(Environment.NewLine + Environment.NewLine, plan.Candidates.Select(Describe));
    }

    private static string Describe(CleanupFileSnapshot file) =>
        $"Nombre: {file.Name}{Environment.NewLine}Ruta: {file.Path}{Environment.NewLine}Tamaño: {FileSizeFormatter.Format(file.Size ?? 0)}{Environment.NewLine}" +
        $"MD5: {file.Md5Checksum ?? "No disponible"}{Environment.NewLine}Fecha: {file.ModifiedTime?.ToLocalTime().ToString("g") ?? "No disponible"}{Environment.NewLine}" +
        $"Propiedad confirmada: {(file.OwnedByMe == true ? "Sí" : "No")}{Environment.NewLine}Permiso de papelera: {(file.CanTrash == true ? "Sí" : "No")}{Environment.NewLine}ID: {file.Id}";

    private void RefreshTrashButton() =>
        _trashButton.Enabled = _preview.CanExecute && _groupsReviewed.Checked && _trashUnderstood.Checked;

    private static DataGridView CreateGrid() => new()
    {
        AutoGenerateColumns = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };

    private static DataGridViewTextBoxColumn Column(string property, string header, int width, DataGridViewAutoSizeColumnMode mode = DataGridViewAutoSizeColumnMode.AllCells) => new()
    {
        DataPropertyName = property,
        HeaderText = header,
        Name = property,
        Width = width,
        MinimumWidth = Math.Min(width, 90),
        AutoSizeMode = mode,
        ReadOnly = true
    };

    private static string DescribeReason(CleanupBatchExclusionReason reason) => reason switch
    {
        CleanupBatchExclusionReason.GroupNotFound => "Grupo no disponible",
        CleanupBatchExclusionReason.ResultsAreStale => "Resultados obsoletos",
        CleanupBatchExclusionReason.NotReadyForCleanup => "No listo para limpieza",
        CleanupBatchExclusionReason.NoKeepFile => "Falta copia conservada",
        CleanupBatchExclusionReason.MultipleKeepFiles => "Varias copias conservadas",
        CleanupBatchExclusionReason.NoCandidates => "Sin candidatos",
        CleanupBatchExclusionReason.ReviewedWithoutCleanup => "Revisado sin limpieza",
        CleanupBatchExclusionReason.CandidateLimitExceeded => "Límite excedido",
        CleanupBatchExclusionReason.PreflightFailed => "Preflight bloqueado",
        CleanupBatchExclusionReason.MetadataChanged => "Metadatos cambiados",
        CleanupBatchExclusionReason.UnsafeCandidate => "Candidato inseguro",
        _ => "Estado no válido"
    };

    private sealed class ApplicableRow
    {
        public required string Group { get; init; }
        public required string Keep { get; init; }
        public int Candidates { get; init; }
        public required string Space { get; init; }
        public required string Preflight { get; init; }
        public required string Warnings { get; init; }
        public required CleanupBatchGroupPlan Plan { get; init; }
    }

    private sealed class ExcludedRow
    {
        public required string Group { get; init; }
        public required string Reason { get; init; }
        public required string Detail { get; init; }
    }
}
