// Exportación de la lista de actualizaciones (CSV para planillas, JSON para inventarios y scripts).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Actualizador.Core;

namespace Actualizador.Updates
{
    public enum UpdateExportFormat { Csv, Json }

    public static class UpdateExport
    {
        static readonly string[] Columns = { "Tipo", "Nombre", "Editor", "Versión instalada", "Versión disponible", "Estado", "Grupo", "Origen", "Identificador", "Detalle", "Resultado" };

        public static string DefaultFileName(DateTime now, UpdateExportFormat format)
        {
            return "Actualizador_actualizaciones_" + Fmt.SafeFileName(Environment.MachineName) + "_" +
                   now.ToString("yyyyMMdd_HHmmss", Fmt.Inv) + (format == UpdateExportFormat.Json ? ".json" : ".csv");
        }

        static string[] Cells(UpdateCandidate u)
        {
            return new[]
            {
                UpdateCandidate.KindText(u.Kind), u.Name ?? "", u.Publisher ?? "", u.InstalledVersion ?? "", u.AvailableVersion ?? "",
                UpdateCandidate.StatusText(u.Status), UpdateGroups.Of(u).ToString(), u.Source ?? "", u.Identifier ?? "",
                Redactor.Text(u.Detail ?? ""), Redactor.Text(u.Result ?? "")
            };
        }

        /// <summary>CSV con separador «;» (Excel en español) y BOM UTF-8. Las celdas que empiezan con = + - @ se neutralizan (inyección de fórmulas).</summary>
        public static string ToCsv(IList<UpdateCandidate> updates)
        {
            const string sep = ";";
            var sb = new StringBuilder();
            sb.Append(string.Join(sep, Array.ConvertAll(Columns, c => Fmt.Csv(c, sep)))).Append("\r\n");
            foreach (var u in updates)
            {
                var cells = Cells(u);
                for (int i = 0; i < cells.Length; i++) cells[i] = Fmt.Csv(NeutralizeFormula(cells[i]), sep);
                sb.Append(string.Join(sep, cells)).Append("\r\n");
            }
            return sb.ToString();
        }

        internal static string NeutralizeFormula(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            char c = s[0];
            return c == '=' || c == '+' || c == '-' || c == '@' || c == '\t' || c == '\r' ? "'" + s : s;
        }

        public static string ToJson(IList<UpdateCandidate> updates, OsInfo os)
        {
            var k = UpdateGroups.Count(updates);
            var root = new Dictionary<string, object>();
            root["programa"] = AppInfo.Name + " " + AppInfo.Version;
            root["equipo"] = Environment.MachineName;
            root["sistema"] = os != null ? os.FriendlyName : "";
            root["fecha"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", Fmt.Inv);
            var counts = new Dictionary<string, object>();
            counts["paraActualizar"] = k.ToUpdate;
            counts["fallidas"] = k.Failed;
            counts["total"] = updates.Count;
            root["resumen"] = counts;
            var list = new List<object>();
            foreach (var u in updates)
            {
                var d = new Dictionary<string, object>();
                var cells = Cells(u);
                d["tipo"] = u.Kind.ToString();
                d["nombre"] = cells[1];
                d["editor"] = cells[2];
                d["versionInstalada"] = cells[3];
                d["versionDisponible"] = cells[4];
                d["estado"] = u.Status.ToString();
                d["grupo"] = cells[6];
                d["origen"] = cells[7];
                d["identificador"] = cells[8];
                d["detalle"] = cells[9];
                d["resultado"] = cells[10];
                d["instalable"] = u.Installable;
                list.Add(d);
            }
            root["actualizaciones"] = list;
            return Json.Serialize(root, true);
        }

        public static void Write(string path, IList<UpdateCandidate> updates, OsInfo os, UpdateExportFormat format)
        {
            string text = format == UpdateExportFormat.Json ? ToJson(updates, os) : ToCsv(updates);
            File.WriteAllText(path, text, new UTF8Encoding(true));
        }
    }
}
