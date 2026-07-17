using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Utilities;

namespace DriveDuplicateFinder;

public sealed class CleanupPlanForm : Form
{
    public CleanupPlanForm(IReadOnlyList<CleanupPlanRow> plan)
    {
        Text = "Vista previa del plan de limpieza";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1100, 600);
        MinimizeBox = false;

        var notice = new Label
        {
            Text = $"Esta vista no modifica Google Drive. Candidatos: {plan.Count:N0}. Espacio total: {FileSizeFormatter.Format(plan.Sum(row => row.Size))}.",
            AutoSize = true,
            Location = new Point(16, 16)
        };

        var grid = new DataGridView
        {
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            DataSource = plan,
            Dock = DockStyle.Bottom,
            Height = 540,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };

        Controls.Add(notice);
        Controls.Add(grid);
    }
}
