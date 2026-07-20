using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Services;
using DriveDuplicateFinder.Utilities;

namespace DriveDuplicateFinder;

public sealed class ApplyRecommendationPreviewForm : Form
{
    private readonly ApplyRecommendationPreview _preview;
    private readonly CheckBox _replaceDecisionsCheckBox = new();
    private readonly Button _applyButton = new();

    public ApplyRecommendationPreviewForm(ApplyRecommendationPreview preview)
    {
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        InitializePreview();
    }

    private void InitializePreview()
    {
        Text = "Aplicar recomendación";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 540);
        ClientSize = new Size(960, 680);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;

        var header = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Vista previa de decisiones locales"
        };
        var notice = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DarkGreen,
            Text = "Esta acción solo cambia decisiones locales. No modifica Google Drive."
        };
        var warnings = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = _preview.Warnings.Count == 0 ? SystemColors.ControlText : Color.DarkGoldenrod,
            Text = string.Join(Environment.NewLine, _preview.Warnings)
        };

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateKeepPage());
        tabs.TabPages.Add(CreateCandidatesPage());
        tabs.TabPages.Add(CreateSkippedPage());

        string expectedStatus = _preview.ExpectedStatus == DuplicateGroupReviewStatus.ReadyForCleanup
            ? "Listo para limpieza"
            : "Parcialmente revisado";
        var summary = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = $"Archivos del grupo: {_preview.CandidateFiles.Count + _preview.SkippedFiles.Count + 1}. " +
                   $"Candidatos propuestos: {_preview.CandidateFiles.Count}. " +
                   $"Omitidos: {_preview.SkippedFiles.Count}. " +
                   $"Espacio candidato: {FileSizeFormatter.Format(_preview.CandidateBytes)}. " +
                   $"Estado esperado: {expectedStatus}."
        };

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true
        };
        var cancelButton = new Button { AutoSize = true, Text = "Cancelar", DialogResult = DialogResult.Cancel };
        _applyButton.AutoSize = true;
        _applyButton.Text = "Aplicar recomendación";
        _applyButton.Enabled = !_preview.ReplacesExistingDecisions;
        _applyButton.Click += (_, _) => DialogResult = DialogResult.OK;
        actions.Controls.Add(cancelButton);
        actions.Controls.Add(_applyButton);

        if (_preview.ReplacesExistingDecisions)
        {
            _replaceDecisionsCheckBox.AutoSize = true;
            _replaceDecisionsCheckBox.Dock = DockStyle.Fill;
            _replaceDecisionsCheckBox.Text = "Entiendo que se reemplazarán las decisiones actuales de este grupo.";
            _replaceDecisionsCheckBox.CheckedChanged += (_, _) => _applyButton.Enabled = _replaceDecisionsCheckBox.Checked;
        }

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 7
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(notice, 0, 1);
        layout.Controls.Add(warnings, 0, 2);
        layout.Controls.Add(tabs, 0, 3);
        layout.Controls.Add(summary, 0, 4);
        layout.Controls.Add(_replaceDecisionsCheckBox, 0, 5);
        layout.Controls.Add(actions, 0, 6);
        Controls.Add(layout);

        AcceptButton = _applyButton;
        CancelButton = cancelButton;
    }

    private TabPage CreateKeepPage()
    {
        DriveFileInfo file = _preview.KeepFile;
        var page = new TabPage("Se conservará");
        page.Controls.Add(CreateFileGrid(
        [
            new PreviewFileRow
            {
                Name = file.Name,
                Path = file.Path,
                Size = FileSizeFormatter.Format(file.Size),
                Date = FormatDate(file),
                Details = $"{_preview.Recommendation.Reason} Advertencias: {GetWarnings(file)}"
            }
        ]));
        return page;
    }

    private TabPage CreateCandidatesPage()
    {
        var page = new TabPage($"Candidatos ({_preview.CandidateFiles.Count})");
        page.Controls.Add(CreateFileGrid(_preview.CandidateFiles.Select(file => new PreviewFileRow
        {
            Name = file.Name,
            Path = file.Path,
            Size = FileSizeFormatter.Format(file.Size),
            Date = FormatDate(file),
            Details = GetWarnings(file)
        })));
        return page;
    }

    private TabPage CreateSkippedPage()
    {
        var page = new TabPage($"No se modificarán ({_preview.SkippedFiles.Count})");
        page.Controls.Add(CreateFileGrid(_preview.SkippedFiles.Select(item => new PreviewFileRow
        {
            Name = item.File.Name,
            Path = item.File.Path,
            Size = FileSizeFormatter.Format(item.File.Size),
            Date = FormatDate(item.File),
            Details = string.Join(" ", item.Reasons.Select(RecommendationApplicationService.GetSkipReasonText))
        })));
        return page;
    }

    private static DataGridView CreateFileGrid(IEnumerable<PreviewFileRow> rows)
    {
        var grid = new DataGridView
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
        grid.Columns.Add(CreateColumn(nameof(PreviewFileRow.Name), "Nombre", 150));
        grid.Columns.Add(CreateColumn(
            nameof(PreviewFileRow.Path),
            "Ubicación",
            220,
            DataGridViewAutoSizeColumnMode.Fill,
            fillWeight: 150));
        grid.Columns.Add(CreateColumn(
            nameof(PreviewFileRow.Size),
            "Tamaño",
            80,
            DataGridViewAutoSizeColumnMode.AllCells));
        grid.Columns.Add(CreateColumn(
            nameof(PreviewFileRow.Date),
            "Fecha",
            110,
            DataGridViewAutoSizeColumnMode.AllCells));
        grid.Columns.Add(CreateColumn(
            nameof(PreviewFileRow.Details),
            "Advertencias o motivo",
            230,
            DataGridViewAutoSizeColumnMode.Fill,
            fillWeight: 170));
        grid.DataSource = rows.ToList();
        return grid;
    }

    private static DataGridViewTextBoxColumn CreateColumn(
        string propertyName,
        string headerText,
        int width,
        DataGridViewAutoSizeColumnMode autoSizeMode = DataGridViewAutoSizeColumnMode.None,
        float fillWeight = 100F) => new()
    {
        Name = propertyName,
        DataPropertyName = propertyName,
        HeaderText = headerText,
        ReadOnly = true,
        Width = width,
        MinimumWidth = width,
        AutoSizeMode = autoSizeMode,
        FillWeight = fillWeight
    };

    internal static void VerifyLocalPreviewConstruction(ApplyRecommendationPreview preview)
    {
        using var form = new ApplyRecommendationPreviewForm(preview);
        DataGridView[] grids = form.Controls
            .OfType<TableLayoutPanel>()
            .SelectMany(layout => layout.Controls.OfType<TabControl>())
            .SelectMany(tabs => tabs.TabPages.Cast<TabPage>())
            .SelectMany(page => page.Controls.OfType<DataGridView>())
            .ToArray();

        if (grids.Length != 3)
        {
            throw new InvalidOperationException("La vista previa debe crear tres tablas de archivos.");
        }

        string[] expectedProperties =
        [
            nameof(PreviewFileRow.Name),
            nameof(PreviewFileRow.Path),
            nameof(PreviewFileRow.Size),
            nameof(PreviewFileRow.Date),
            nameof(PreviewFileRow.Details)
        ];
        foreach (DataGridView grid in grids)
        {
            if (grid.AutoGenerateColumns || !grid.ReadOnly || grid.AllowUserToAddRows || grid.AllowUserToDeleteRows ||
                grid.Columns.Count != expectedProperties.Length ||
                !grid.Columns.Cast<DataGridViewColumn>().Select(column => column.DataPropertyName).SequenceEqual(expectedProperties))
            {
                throw new InvalidOperationException("La tabla de vista previa no tiene las columnas explícitas esperadas.");
            }
        }
    }

    private static string FormatDate(DriveFileInfo file) =>
        (file.ModifiedTime ?? file.CreatedTime)?.ToLocalTime().ToString("g") ?? "No disponible";

    private static string GetWarnings(DriveFileInfo file)
    {
        string warnings = DuplicateReviewService.GetWarnings(file);
        return string.IsNullOrWhiteSpace(warnings) ? "Ninguna" : warnings;
    }

    private sealed class PreviewFileRow
    {
        public required string Name { get; init; }

        public required string Path { get; init; }

        public required string Size { get; init; }

        public required string Date { get; init; }

        public required string Details { get; init; }
    }
}
