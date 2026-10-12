using System.ComponentModel;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Services;
using DriveDuplicateFinder.Utilities;

namespace DriveDuplicateFinder;

/// <summary>Displays one complete, confirmed group's local cleanup snapshot; it cannot execute cleanup.</summary>
public sealed class PagedGroupCleanupLocalPreviewForm : Form
{
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

    public PagedGroupCleanupLocalPreviewForm(PagedGroupCleanupLocalPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        Text = "Plan del grupo — solo vista previa local";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        MinimumSize = new Size(850, 420);
        int initialClientHeight = Math.Clamp(360 + Math.Min(preview.Entries.Count, 12) * 28, 420, 680);
        ClientSize = new Size(1120, initialClientHeight);

        var summary = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 4),
            Text = $"Grupo confirmado: {preview.MemberCount:N0} miembros | {preview.Identity.SizeBytes:N0} bytes por archivo | MD5 {preview.Identity.NormalizedChecksum}{Environment.NewLine}" +
                   $"Conservar: {preview.KeepCount:N0} | candidatos explícitos: {preview.CandidateCount:N0} | pendientes excluidos: {preview.UndecidedExcludedCount:N0} | potencial: {FileSizeFormatter.Format(preview.PotentiallyRecoverableBytes)}"
        };
        var warning = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DarkRed,
            Margin = new Padding(0, 0, 0, 6),
            Text = "PREPARACIÓN LOCAL NO EJECUTABLE. Se muestran todas las decisiones persistidas del grupo, no solo la página visible. No se consultó el estado remoto actual, no se hizo preflight remoto y no se modificará Google Drive."
        };
        _grid.Columns.AddRange(
        [
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PreviewRow.Decision), HeaderText = "Decisión", FillWeight = 70 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PreviewRow.Name), HeaderText = "Archivo", FillWeight = 110 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PreviewRow.Path), HeaderText = "Ruta", FillWeight = 190 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PreviewRow.Size), HeaderText = "Tamaño", FillWeight = 70 }
        ]);
        _grid.DataSource = new BindingList<PreviewRow>(preview.Entries.Select(entry => new PreviewRow(
            entry.Decision == DuplicateFileDecision.Keep ? "CONSERVAR" : "CANDIDATO EXPLÍCITO",
            entry.File.Name,
            entry.File.Path,
            FileSizeFormatter.Format(entry.File.Size))).ToList());

        var close = new Button
        {
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(3, 6, 3, 3),
            Text = "Cerrar"
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(summary, 0, 0);
        layout.SetColumnSpan(summary, 2);
        layout.Controls.Add(warning, 0, 1);
        layout.SetColumnSpan(warning, 2);
        layout.Controls.Add(_grid, 0, 2);
        layout.SetColumnSpan(_grid, 2);
        layout.Controls.Add(close, 1, 3);
        Controls.Add(layout);
        void ResizeWrappedText()
        {
            int width = Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal);
            summary.MaximumSize = new Size(width, 0);
            warning.MaximumSize = new Size(width, 0);
        }
        layout.SizeChanged += (_, _) => ResizeWrappedText();
        ResizeWrappedText();
        CancelButton = close;
    }

    private sealed record PreviewRow(string Decision, string Name, string Path, string Size);
}
