using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Utilities;

namespace DriveDuplicateFinder;

public sealed class RecommendationBatchPreviewForm : Form
{
    private readonly RecommendationBatchPreview _preview;
    private readonly Button _applyButton = new();
    private readonly CheckBox _replaceDecisionsCheckBox = new();
    private readonly TextBox _detailText = new();

    public RecommendationBatchPreviewForm(RecommendationBatchPreview preview)
    {
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        InitializePreview();
    }

    private void InitializePreview()
    {
        Text = "Aplicar recomendaciones seleccionadas";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(820, 600);
        ClientSize = new Size(1120, 760);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;

        var notice = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DarkGreen,
            Text = "Esta acción solo cambia decisiones locales. No modifica Google Drive ni envía archivos a la papelera."
        };
        var summary = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = $"Seleccionados: {_preview.SelectedGroupCount}. Aplicables: {_preview.ApplicableGroupCount}. Excluidos: {_preview.ExcludedGroupCount}. " +
                   $"Conservar: {_preview.KeepFileCount}. Candidatos: {_preview.CandidateFileCount}. Omitidos: {_preview.SkippedFileCount}. " +
                   $"Espacio candidato: {FileSizeFormatter.Format(_preview.CandidateBytes)}. Reemplazos: {_preview.GroupsReplacingExistingDecisions}. " +
                   $"Listos: {_preview.GroupsExpectedReadyForCleanup}. Parciales: {_preview.GroupsExpectedPartiallyReviewed}."
        };

        var applicableGrid = CreateApplicableGrid();
        applicableGrid.SelectionChanged += (_, _) => ShowDetails(applicableGrid.CurrentRow?.DataBoundItem as ApplicableRow);
        var excludedGrid = CreateExcludedGrid();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var applicablePage = new TabPage($"Se aplicarán ({_preview.ApplicableGroupCount})");
        applicablePage.Controls.Add(applicableGrid);
        var excludedPage = new TabPage($"Excluidos ({_preview.ExcludedGroupCount})");
        excludedPage.Controls.Add(excludedGrid);
        tabs.TabPages.Add(applicablePage);
        tabs.TabPages.Add(excludedPage);

        _detailText.Dock = DockStyle.Fill;
        _detailText.Multiline = true;
        _detailText.ReadOnly = true;
        _detailText.ScrollBars = ScrollBars.Vertical;
        _detailText.MinimumSize = new Size(0, 90);
        _detailText.Text = _preview.ApplicableGroups.Count == 0
            ? "No hay grupos aptos; no se modificarán decisiones locales."
            : "Selecciona un grupo aplicable para ver la propuesta validada.";

        _replaceDecisionsCheckBox.AutoSize = true;
        _replaceDecisionsCheckBox.Dock = DockStyle.Fill;
        _replaceDecisionsCheckBox.Text = "Entiendo que se reemplazarán las decisiones actuales de algunos grupos seleccionados.";
        _replaceDecisionsCheckBox.Visible = _preview.GroupsReplacingExistingDecisions > 0;
        _replaceDecisionsCheckBox.CheckedChanged += (_, _) => RefreshApplyButton();

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
        var cancelButton = new Button { AutoSize = true, Text = "Cancelar", DialogResult = DialogResult.Cancel };
        _applyButton.AutoSize = true;
        _applyButton.Text = "Aplicar recomendaciones";
        _applyButton.Click += (_, _) => DialogResult = DialogResult.OK;
        actions.Controls.Add(cancelButton);
        actions.Controls.Add(_applyButton);

        var layout = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(notice, 0, 0);
        layout.Controls.Add(summary, 0, 1);
        layout.Controls.Add(tabs, 0, 2);
        layout.Controls.Add(_detailText, 0, 3);
        layout.Controls.Add(_replaceDecisionsCheckBox, 0, 4);
        layout.Controls.Add(actions, 0, 5);
        Controls.Add(layout);
        RefreshApplyButton();
        AcceptButton = _applyButton;
        CancelButton = cancelButton;
    }

    private DataGridView CreateApplicableGrid()
    {
        var grid = CreateReadOnlyGrid();
        grid.Columns.AddRange(
        [
            Column(nameof(ApplicableRow.Group), "Grupo", 180, DataGridViewAutoSizeColumnMode.Fill, 115),
            Column(nameof(ApplicableRow.Keep), "Archivo recomendado", 160, DataGridViewAutoSizeColumnMode.Fill, 105),
            Column(nameof(ApplicableRow.Candidates), "Candidatos", 80),
            Column(nameof(ApplicableRow.Skipped), "Omitidos", 70),
            Column(nameof(ApplicableRow.Space), "Espacio", 90),
            Column(nameof(ApplicableRow.ExpectedStatus), "Estado esperado", 125),
            Column(nameof(ApplicableRow.Replaces), "Reemplaza decisiones", 115)
        ]);
        grid.DataSource = _preview.ApplicableGroups.Select(group => new ApplicableRow
        {
            Group = group.DisplayName,
            Keep = group.IndividualPreview.KeepFile.Name,
            Candidates = group.IndividualPreview.CandidateFiles.Count,
            Skipped = group.IndividualPreview.SkippedFiles.Count,
            Space = FileSizeFormatter.Format(group.IndividualPreview.CandidateBytes),
            ExpectedStatus = group.ExpectedStatusText,
            Replaces = group.IndividualPreview.ReplacesExistingDecisions ? "Sí" : "No",
            Source = group
        }).ToList();
        return grid;
    }

    private DataGridView CreateExcludedGrid()
    {
        var grid = CreateReadOnlyGrid();
        grid.Columns.AddRange(
        [
            Column(nameof(ExcludedRow.Group), "Grupo", 180, DataGridViewAutoSizeColumnMode.Fill, 100),
            Column(nameof(ExcludedRow.Reason), "Motivo", 150),
            Column(nameof(ExcludedRow.Detail), "Detalle", 300, DataGridViewAutoSizeColumnMode.Fill, 180)
        ]);
        grid.DataSource = _preview.ExcludedGroups.Select(group => new ExcludedRow
        {
            Group = group.DisplayName,
            Reason = group.Reason.ToString(),
            Detail = group.Detail
        }).ToList();
        return grid;
    }

    private static DataGridView CreateReadOnlyGrid() => new()
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

    private static DataGridViewTextBoxColumn Column(
        string propertyName,
        string header,
        int width,
        DataGridViewAutoSizeColumnMode autoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
        float fillWeight = 100) => new()
    {
        Name = propertyName,
        DataPropertyName = propertyName,
        HeaderText = header,
        ReadOnly = true,
        Width = width,
        MinimumWidth = Math.Min(width, 90),
        AutoSizeMode = autoSizeMode,
        FillWeight = fillWeight
    };

    private void ShowDetails(ApplicableRow? row)
    {
        if (row?.Source is not RecommendationBatchGroupPreview group)
        {
            return;
        }

        ApplyRecommendationPreview preview = group.IndividualPreview;
        _detailText.Text = $"Se conservará: {preview.KeepFile.Name}{Environment.NewLine}" +
                           $"Candidatos: {string.Join(", ", preview.CandidateFiles.Select(file => file.Name).DefaultIfEmpty("Ninguno"))}{Environment.NewLine}" +
                           $"Omitidos: {string.Join(", ", preview.SkippedFiles.Select(file => file.File.Name).DefaultIfEmpty("Ninguno"))}{Environment.NewLine}" +
                           $"Advertencias: {string.Join(" ", preview.Warnings.DefaultIfEmpty("Ninguna"))}";
    }

    private void RefreshApplyButton() =>
        _applyButton.Enabled = _preview.ApplicableGroupCount > 0 &&
            (_preview.GroupsReplacingExistingDecisions == 0 || _replaceDecisionsCheckBox.Checked);

    private sealed class ApplicableRow
    {
        public required string Group { get; init; }
        public required string Keep { get; init; }
        public int Candidates { get; init; }
        public int Skipped { get; init; }
        public required string Space { get; init; }
        public required string ExpectedStatus { get; init; }
        public required string Replaces { get; init; }
        public required RecommendationBatchGroupPreview Source { get; init; }
    }

    private sealed class ExcludedRow
    {
        public required string Group { get; init; }
        public required string Reason { get; init; }
        public required string Detail { get; init; }
    }
}
