using System.ComponentModel;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;
using DriveDuplicateFinder.Services;
using DriveDuplicateFinder.Utilities;

namespace DriveDuplicateFinder;

/// <summary>Local, paged preview only. This form has no Google Drive write dependency.</summary>
public sealed class PagedCleanupPlanPreviewForm : Form
{
    private const int PageSize = 100;
    private readonly RecoverableFullScanService _service;
    private readonly ScanInventoryIdentity _inventory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Label _summary = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label _pageLabel = new() { AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
    private readonly Label _warning = new()
    {
        AutoSize = true,
        Dock = DockStyle.Fill,
        ForeColor = Color.DarkRed,
        Text = "Previsualización local. No se ha comprobado el estado remoto actual en Google Drive y no se realizará ninguna operación."
    };
    private readonly DataGridView _grid = new()
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
    private readonly Button _previous = new() { Text = "Anterior", AutoSize = true };
    private readonly Button _next = new() { Text = "Siguiente", AutoSize = true };
    private int _offset;

    public PagedCleanupPlanPreviewForm(RecoverableFullScanService service, ScanInventoryIdentity inventory)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        Text = "Vista previa local del plan de limpieza";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(850, 480);
        ClientSize = new Size(1120, 680);

        _grid.Columns.AddRange(
        [
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlanRow.Group), HeaderText = "Grupo (tamaño / MD5)", FillWeight = 90 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlanRow.Decision), HeaderText = "Estado local", FillWeight = 65 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlanRow.Name), HeaderText = "Archivo", FillWeight = 100 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlanRow.Path), HeaderText = "Ruta", FillWeight = 180 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlanRow.Size), HeaderText = "Tamaño", FillWeight = 60 }
        ]);
        _previous.Click += async (_, _) => await LoadPageAsync(Math.Max(0, _offset - PageSize));
        _next.Click += async (_, _) => await LoadPageAsync(_offset + PageSize);
        FormClosing += (_, _) => _lifetime.Cancel();
        Disposed += (_, _) => _lifetime.Dispose();

        var navigation = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        navigation.Controls.AddRange([_previous, _next, _pageLabel]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_summary, 0, 0);
        layout.Controls.Add(_warning, 0, 1);
        layout.Controls.Add(_grid, 0, 2);
        layout.Controls.Add(navigation, 0, 3);
        Controls.Add(layout);
        Shown += async (_, _) => await InitializePlanAsync();
    }

    private async Task InitializePlanAsync()
    {
        try
        {
            _summary.Text = "Validando el inventario y preparando el plan local…";
            PagedCleanupPlanSummary planSummary = await _service.GetCleanupPlanSummaryAsync(_inventory, _lifetime.Token);
            _summary.Text = $"Grupos aptos: {planSummary.EligibleGroupCount:N0} | Archivos candidatos: {planSummary.CandidateFileCount:N0} | " +
                $"Recuperable potencial: {FileSizeFormatter.Format(planSummary.PotentiallyRecoverableBytes)} | Archivos incluidos en vista: {planSummary.FileEntryCount:N0} " +
                "(candidatos y conservados explícitamente).";
            await LoadPageAsync(0);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _summary.Text = "No se pudo validar el plan local; no se muestra ningún plan parcial.";
            _grid.DataSource = null;
            MessageBox.Show(this, exception.Message, "Vista previa bloqueada", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task LoadPageAsync(int offset)
    {
        try
        {
            _previous.Enabled = _next.Enabled = false;
            PagedCleanupPlanDisplayPage page = await _service.GetCleanupPlanDisplayPageAsync(_inventory, offset, PageSize, _lifetime.Token);
            _offset = page.Offset;
            _grid.DataSource = new BindingList<PlanRow>(page.Items.Select(item => new PlanRow(
                $"{item.GroupIdentity.SizeBytes:N0} B / {item.GroupIdentity.NormalizedChecksum}",
                item.Decision == DuplicateFileDecision.Keep ? "CONSERVAR" : "ENVIAR A PAPELERA (candidato local)",
                item.Name, item.Path, FileSizeFormatter.Format(item.SizeBytes))).ToList());
            _pageLabel.Text = page.Summary.FileEntryCount == 0
                ? "Sin grupos confirmados aptos."
                : $"Archivos {page.Offset + 1:N0}–{page.Offset + page.Items.Count:N0} de {page.Summary.FileEntryCount:N0}";
            _previous.Enabled = page.Offset > 0;
            _next.Enabled = page.HasMore;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _grid.DataSource = null;
            _summary.Text = "La vista previa se bloqueó porque el inventario o sus decisiones ya no son consistentes.";
            MessageBox.Show(this, exception.Message, "Vista previa bloqueada", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private sealed record PlanRow(string Group, string Decision, string Name, string Path, string Size);
}
