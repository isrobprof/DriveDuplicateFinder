using System.Text;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Services;

namespace DriveDuplicateFinder;

/// <summary>Displays the result of a read-only metadata preflight; it has no cleanup actions.</summary>
public sealed class PagedGroupPreflightResultForm : Form
{
    public PagedGroupPreflightResultForm(
        PersistedCleanupGroupReview localReview,
        CleanupPreflightResult preflight,
        bool remoteFileReadsStarted)
    {
        ArgumentNullException.ThrowIfNull(localReview);
        ArgumentNullException.ThrowIfNull(preflight);

        Text = "Comprobación de un grupo — sin limpieza";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 520);
        Size = new Size(1040, 720);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var notice = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DarkRed,
            Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            Text = remoteFileReadsStarted
                ? "PRE-FLIGHT DE SOLO LECTURA. No se ha ejecutado ni autorizado ninguna limpieza."
                : "LECTURA DE MIEMBROS NO INICIADA: la validación local bloqueó el grupo. Solo se verificaron identidad y raíz de la cuenta."
        };
        var details = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9),
            Text = BuildDetails(localReview, preflight, remoteFileReadsStarted)
        };
        var close = new Button { Text = "Cerrar", AutoSize = true, Anchor = AnchorStyles.Right };
        close.Click += (_, _) => Close();
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        footer.Controls.Add(close);
        layout.Controls.Add(notice, 0, 0);
        layout.Controls.Add(details, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
    }

    private static string BuildDetails(
        PersistedCleanupGroupReview localReview,
        CleanupPreflightResult preflight,
        bool remoteFileReadsStarted)
    {
        var output = new StringBuilder();
        DuplicateGroupReview review = localReview.Review;
        output.AppendLine($"Grupo: {review.StableId}");
        output.AppendLine($"Tamaño: {review.Group.FileSize:N0} bytes | MD5: {review.Group.Md5Checksum}");
        output.AppendLine($"Inventario ScanId: {localReview.Identity.Inventory.ScanId}");
        output.AppendLine($"Miembros: {localReview.MemberCount:N0} | Conservados: {localReview.KeepCount:N0} | Candidatos explícitos: {localReview.CandidateCount:N0} | Sin decidir: {localReview.UndecidedCount:N0}");
        output.AppendLine($"Resultado: {(preflight.IsSuccessful ? "APROBADO SOLO PARA ESTA COMPROBACIÓN" : "BLOQUEADO / NO APROBADO")}");
        output.AppendLine($"Fallo: {preflight.FailureKind}");
        output.AppendLine("No se ejecutó limpieza; el preflight no habilita escrituras.");

        output.AppendLine();
        output.AppendLine("COPIAS MARCADAS CONSERVAR (decisión local):");
        foreach (DriveFileInfo file in review.Group.Files.Where(file =>
                     review.DecisionsByFileId.GetValueOrDefault(file.Id) == DuplicateFileDecision.Keep))
        {
            bool isRemoteAnchor = preflight.Review.DecisionsByFileId.TryGetValue(file.Id, out DuplicateFileDecision decision) &&
                decision == DuplicateFileDecision.Keep;
            output.AppendLine($"- {file.Name} [{file.Id}] | {(isRemoteAnchor && preflight.KeepFile is not null ? "ancla comprobada" : "no incluida en la lectura remota")}");
            output.AppendLine($"  {file.Path}");
        }

        output.AppendLine();
        output.AppendLine("CANDIDATOS EXPLÍCITOS (los SIN DECIDIR nunca se incluyen):");
        var remoteCandidates = preflight.CandidateFiles.ToDictionary(file => file.Id, StringComparer.Ordinal);
        foreach (DriveFileInfo file in review.Group.Files.Where(file =>
                     review.DecisionsByFileId.GetValueOrDefault(file.Id) == DuplicateFileDecision.CandidateForTrash))
        {
            string status = !remoteFileReadsStarted
                ? "no comprobado remotamente"
                : preflight.IsSuccessful
                    ? "comprobado; no se envió a la papelera"
                    : remoteCandidates.ContainsKey(file.Id)
                        ? "metadatos leídos, pero el preflight global quedó bloqueado"
                        : "no llegó a comprobarse";
            output.AppendLine($"- {file.Name} [{file.Id}] | {status}");
            output.AppendLine($"  {file.Path}");
            if (remoteCandidates.TryGetValue(file.Id, out CleanupFileSnapshot? snapshot))
                output.AppendLine($"  Remoto: tamaño={snapshot.Size?.ToString("N0") ?? "desconocido"}; MD5={snapshot.Md5Checksum ?? "desconocido"}; papelera={snapshot.Trashed?.ToString() ?? "desconocido"}");
        }

        output.AppendLine();
        output.AppendLine("BLOQUEOS Y MOTIVOS:");
        if (preflight.ValidationMessages.Count == 0)
            output.AppendLine("- Ninguno.");
        else
            foreach (string message in preflight.ValidationMessages)
                output.AppendLine($"- {message}");

        return output.ToString();
    }
}
