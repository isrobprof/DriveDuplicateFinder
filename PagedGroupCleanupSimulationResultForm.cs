using System.Text;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Services;

namespace DriveDuplicateFinder;

/// <summary>Shows coordinator outcomes as simulated results without implying changes to real Drive files.</summary>
public sealed class PagedGroupCleanupSimulationResultForm : Form
{
    public PagedGroupCleanupSimulationResultForm(DemoPagedCleanupSimulationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Text = "Resultado de simulación local — sin cambios en Drive";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        MinimumSize = new Size(860, 520);
        ClientSize = new Size(1100, 700);

        var warning = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DarkRed,
            Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            Text = "SIMULACIÓN OFFLINE. Todas las solicitudes fueron atendidas por un transporte local en memoria. No se accedió a Google Drive ni se modificó ningún archivo real. Este resultado no autoriza ni habilita una ejecución real."
        };
        var details = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = true,
            Font = new Font(FontFamily.GenericMonospace, 9),
            Text = BuildDetails(result)
        };
        var close = new Button { AutoSize = true, Anchor = AnchorStyles.Right, Text = "Cerrar" };
        close.Click += (_, _) => Close();
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        footer.Controls.Add(close);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(warning, 0, 0);
        layout.Controls.Add(details, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        CancelButton = close;
    }

    internal static string DescribeFileResult(CleanupFileResult fileResult)
    {
        if (string.Equals(fileResult.FailureReason, "OutcomeUncertain", StringComparison.Ordinal) ||
            string.Equals(fileResult.FailureStage, "Files.UpdateOutcomeUncertain", StringComparison.Ordinal))
            return "INCIERTO: el coordinador no pudo confirmar el resultado simulado; no se debe reintentar automáticamente.";

        return fileResult.Status switch
        {
            CleanupFileStatus.Trashed when fileResult.VerificationSucceeded => "SIMULADO y verificado por el transporte local; no es un cambio real en Drive.",
            CleanupFileStatus.Trashed => "Marcado como simulado; verificación no confirmada.",
            CleanupFileStatus.Failed => "Fallido en la simulación.",
            CleanupFileStatus.Cancelled => "Cancelado en la simulación.",
            CleanupFileStatus.NotProcessed => "No procesado en la simulación.",
            _ => "Pendiente en la simulación."
        };
    }

    internal static string BuildDetails(DemoPagedCleanupSimulationResult result)
    {
        var output = new StringBuilder();
        output.AppendLine("SIMULACIÓN OFFLINE: transporte local en memoria; ejecución real disponible: NO.");
        output.AppendLine("No se accedió a Google Drive. No se modificó ningún archivo real.");
        output.AppendLine($"Solicitudes atendidas por el transporte local: {result.LocalTransportRequestCount:N0}.");

        if (result.Preflight is not null)
        {
            output.AppendLine($"Preflight simulado: {(result.Preflight.IsStale ? "OBSOLETO" : result.Preflight.Preflight.IsSuccessful ? "APROBADO EN LA SIMULACIÓN" : "BLOQUEADO")}");
            output.AppendLine($"Lecturas de miembros iniciadas: {(result.Preflight.RemoteFileReadsStarted ? "sí, mediante el transporte local" : "no")}.");
            foreach (string message in result.Preflight.Preflight.ValidationMessages)
                output.AppendLine($"- Bloqueo preflight: {message}");
        }

        if (result.Plan is not null)
        {
            output.AppendLine($"Inventario: {result.Plan.Identity.Inventory.ScanId}");
            output.AppendLine($"Grupo: {result.Plan.Identity.SizeBytes:N0} bytes + MD5 {result.Plan.Identity.NormalizedChecksum}");
            output.AppendLine($"Keep protegidos: {result.Plan.KeepFiles.Count:N0}; candidatos explícitos: {result.Plan.CandidateFiles.Count:N0}; Undecided excluidos: {result.Plan.LocalReview.UndecidedCount:N0}.");
        }

        if (!string.IsNullOrWhiteSpace(result.BlockingReason))
            output.AppendLine($"Bloqueo: {result.BlockingReason}");

        if (result.ConfirmationDeclined)
            output.AppendLine("Confirmación final cancelada: no se ejecutó el coordinador y no se generaron PATCH simulados.");
        else if (result.Operation is null)
            output.AppendLine("No hubo resultado de operación porque la preparación se bloqueó, quedó obsoleta o fue cancelada.");
        else
        {
            CleanupOperationRecord record = result.Operation.Record;
            int confirmed = record.FileResults.Count(file => file.Status == CleanupFileStatus.Trashed && file.VerificationSucceeded);
            int uncertain = record.FileResults.Count(file => file.FailureReason == "OutcomeUncertain" || file.FailureStage == "Files.UpdateOutcomeUncertain");
            int failed = record.FileResults.Count(file => file.Status == CleanupFileStatus.Failed &&
                file.FailureReason != "OutcomeUncertain" && file.FailureStage != "Files.UpdateOutcomeUncertain");
            int cancelled = record.FileResults.Count(file => file.Status == CleanupFileStatus.Cancelled);
            int notProcessed = record.FileResults.Count(file => file.Status == CleanupFileStatus.NotProcessed);
            output.AppendLine($"Estado del coordinador en el transporte local: {record.Status}.");
            output.AppendLine($"Resultados simulados: confirmados={confirmed:N0}; fallidos={failed:N0}; inciertos={uncertain:N0}; cancelados={cancelled:N0}; no procesados={notProcessed:N0}.");
            output.AppendLine($"PATCH simulados capturados: {result.SimulatedPatchFileIds.Count:N0}.");
            output.AppendLine($"Solicitudes no-GET observadas por el transporte local: {result.ObservedWriteRequests.Count:N0}.");
            foreach (string request in result.ObservedWriteRequests)
                output.AppendLine($"- Transporte local: {request}");
            if (result.SimulatedPatchFileIds.Count > 0)
                output.AppendLine("IDs con PATCH simulado: " + string.Join(", ", result.SimulatedPatchFileIds));
            output.AppendLine("OutcomeUncertain nunca se cuenta como confirmado ni como seguro para reintentar.");
            foreach (CleanupFileResult file in record.FileResults)
            {
                output.AppendLine($"- {file.File.Name} [{file.File.Id}]: {DescribeFileResult(file)}");
                if (!string.IsNullOrWhiteSpace(file.Message))
                    output.AppendLine($"  Motivo: {file.Message}");
            }
        }

        output.AppendLine("Ninguna respuesta de esta simulación puede reutilizarse para habilitar escrituras reales.");
        return output.ToString();
    }
}
