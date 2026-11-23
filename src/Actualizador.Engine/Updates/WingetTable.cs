using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    public sealed class WingetRow
    {
        public string Name = "", Id = "", Version = "", Available = "", Source = "";
        /// <summary>winget recortó el Id con "…": no se puede usar para instalar.</summary>
        public bool IdTruncated;
        /// <summary>Viene de la tabla de paquetes anclados / que requieren selección explícita.</summary>
        public bool RequiresExplicit;
        /// <summary>
        /// Encontrado en el catálogo por nombre y editor: winget no lo reconoce como instalado, así que se actualiza con
        /// «winget install» en vez de «winget upgrade» (que respondería «no hay ningún paquete instalado»).
        /// </summary>
        public bool InstallInsteadOfUpgrade;
        /// <summary>
        /// 2.0.6: el programa instalado es una copia sin instalador (portable, o de otra cuenta) encontrada por su acceso directo.
        /// La versión oficial se instala aparte con «winget install»: no se cierran ni se comparan los archivos de la copia vieja.
        /// </summary>
        public bool PortableCopy;
        /// <summary>2.0.6: encontrado por un nombre parecido (uno empieza con el otro) y el mismo editor, no por el nombre exacto.</summary>
        public bool ApproximateName;
        /// <summary>
        /// 2.0.6: para «winget install» de lo encontrado por nombre, el alcance de lo instalado («user» o «machine»; "" = el que
        /// elija winget). Sin esto, Actualizador elevado instalaba para todo el equipo un programa que estaba sólo para el usuario
        /// (Zoom, Discord) y quedaban dos copias.
        /// </summary>
        public string Scope = "";
        /// <summary>
        /// 2.0.20: paquete aprovisionado para todos los usuarios (de «winget upgrade --scope machine»): se actualiza con
        /// «--scope machine», no como los del usuario. Ver <see cref="WingetProvider.AddMachineRows"/>.
        /// </summary>
        public bool MachineScope;
        /// <summary>2.0.21: visto por winget como servicio (SYSTEM, como el agente de Vigía): se actualiza en ese mismo contexto.</summary>
        public bool SystemContext;
    }

    /// <summary>
    /// Interpreta las tablas que imprime winget (list / upgrade). winget no tiene salida JSON
    /// para estos comandos, los encabezados están traducidos y las columnas se alinean por
    /// ancho de visualización (los caracteres CJK ocupan dos celdas), así que:
    ///  - las columnas se ubican con la línea de encabezado que precede a la de guiones;
    ///  - las posiciones se calculan en celdas de pantalla, no en caracteres;
    ///  - cada fila se valida (Id sin espacios, versión presente, límites sobre espacios).
    /// </summary>
    public static class WingetTable
    {
        static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
        static readonly Regex Separator = new Regex(@"^-{8,}$", RegexOptions.Compiled);

        public static string CleanLine(string raw)
        {
            if (raw == null) return "";
            string l = Ansi.Replace(raw, "");
            int cr = l.LastIndexOf('\r');
            if (cr >= 0) l = cr == l.Length - 1 ? l.Substring(0, cr) : l.Substring(cr + 1);
            int cr2 = l.LastIndexOf('\r');
            if (cr2 >= 0) l = l.Substring(cr2 + 1);
            // Restos del indicador de progreso ("-\|/") al principio de la línea.
            l = Regex.Replace(l, @"^\s*[-\\|/]\s{2,}(?=\S)", "");
            return l.TrimEnd();
        }

        public static bool IsProgressLine(string l)
        {
            if (l.Length == 0) return false;
            int blocks = 0;
            foreach (char c in l) if (c == '█' || c == '▒' || c == '▓' || c == '░') blocks++;
            return blocks >= 3;
        }

        public static int CellWidth(string s, int index)
        {
            char c = s[index];
            if (char.IsLowSurrogate(c)) return 0;
            if (char.IsHighSurrogate(c)) return 2;
            if (c < 0x1100) return 1;
            if ((c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0x303E) || (c >= 0x3041 && c <= 0x33FF) ||
                (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xA000 && c <= 0xA4CF) ||
                (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) ||
                (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6))
                return 2;
            return 1;
        }

        /// <summary>Posición en celdas de pantalla donde empieza cada carácter (+1 al final).</summary>
        static int[] CellPositions(string s)
        {
            var pos = new int[s.Length + 1];
            int cell = 0;
            for (int i = 0; i < s.Length; i++) { pos[i] = cell; cell += CellWidth(s, i); }
            pos[s.Length] = cell;
            return pos;
        }

        static int CharIndexAtCell(int[] pos, int cell)
        {
            for (int i = 0; i < pos.Length; i++) if (pos[i] >= cell) return i;
            return pos.Length - 1;
        }

        public static List<WingetRow> Parse(string output)
        {
            var rows = new List<WingetRow>();
            var lines = new List<string>();
            foreach (string raw in (output ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string l = CleanLine(raw);
                if (!IsProgressLine(l)) lines.Add(l);
            }

            int tableIndex = 0;
            for (int i = 1; i < lines.Count; i++)
            {
                if (!Separator.IsMatch(lines[i].Trim())) continue;
                // Encabezado: la línea no vacía anterior.
                int h = i - 1;
                while (h >= 0 && lines[h].Trim().Length == 0) h--;
                if (h < 0) continue;
                string header = lines[h];
                int[] starts = HeaderStarts(header);
                if (starts.Length < 3) continue;
                bool explicitTable = tableIndex > 0;
                tableIndex++;

                var dataLines = new List<string>();
                for (int j = i + 1; j < lines.Count; j++)
                {
                    string line = lines[j];
                    if (line.Trim().Length == 0) break;
                    if (j + 1 < lines.Count && Separator.IsMatch(lines[j + 1].Trim())) break;   // empieza otra tabla
                    dataLines.Add(line);
                }
                bool[] soft = SoftBoundaries(header, starts);
                starts = MergeMultiWordHeaders(starts, ref soft, dataLines);
                if (starts.Length < 3) continue;
                var tableRows = new List<string[]>();
                foreach (string line in dataLines)
                {
                    string[] cells = Split(line, starts, soft);
                    if (cells == null) continue;
                    tableRows.Add(cells);
                }
                MapColumns(tableRows, starts.Length, explicitTable, rows);
            }
            return rows;
        }

        static int[] HeaderStarts(string header)
        {
            var list = new List<int>();
            int[] pos = CellPositions(header);
            for (int i = 0; i < header.Length; i++)
            {
                bool startsToken = !char.IsWhiteSpace(header[i]) && (i == 0 || char.IsWhiteSpace(header[i - 1]));
                if (startsToken) list.Add(pos[i]);
            }
            return list.ToArray();
        }

        /// <summary>
        /// Encabezados de más de una palabra (en coreano, «사용 가능» es «Disponible»): cada palabra parecía
        /// una columna, el límite caía en medio de los valores y ninguna fila se podía leer. Sólo un límite
        /// «blando» (la palabra va a un espacio de la anterior) puede ser la segunda palabra de un encabezado,
        /// y se une con la anterior si lo cruza la mayoría de las FILAS. Votan sólo las líneas que parecen
        /// filas: llegan a la última columna y no cruzan ningún límite firme. Los pies que winget imprime
        /// pegados a la tabla («1 upgrades available.», «1 package(s) have pins…») no votan: con una sola
        /// actualización, antes ganaban la votación y unían el nombre con el Id.
        /// </summary>
        public static int[] MergeMultiWordHeaders(int[] starts, ref bool[] soft, List<string> dataLines)
        {
            if (starts == null || starts.Length < 2 || dataLines == null || dataLines.Count == 0) return starts;
            if (soft == null || soft.Length != starts.Length) soft = new bool[starts.Length];
            var voters = new List<int[]>();       // por línea: posiciones de carácter de cada límite
            var votersText = new List<string>();
            foreach (string line in dataLines)
            {
                int[] pos = CellPositions(line);
                if (pos[line.Length] <= starts[starts.Length - 1]) continue;          // no llega a la última columna
                bool row = true;
                for (int k = 1; k < starts.Length && row; k++)
                    if (!soft[k] && Crosses(line, pos, starts[k])) row = false;
                if (!row) continue;
                voters.Add(pos);
                votersText.Add(line);
            }
            var keep = new List<int> { starts[0] };
            var keepSoft = new List<bool> { false };
            for (int k = 1; k < starts.Length; k++)
            {
                if (soft[k] && voters.Count > 0)
                {
                    int crossed = 0;
                    for (int v = 0; v < voters.Count; v++) if (Crosses(votersText[v], voters[v], starts[k])) crossed++;
                    if (crossed * 2 > voters.Count) continue;
                }
                keep.Add(starts[k]);
                keepSoft.Add(soft[k]);
            }
            soft = keepSoft.ToArray();
            return keep.ToArray();
        }

        /// <summary>¿La línea tiene un carácter justo antes del límite (el valor sigue de la columna anterior)?</summary>
        static bool Crosses(string line, int[] pos, int cell)
        {
            int a = CharIndexAtCell(pos, cell);
            return a > 0 && a < line.Length && !char.IsWhiteSpace(line[a - 1]);
        }

        /// <summary>
        /// Límites «blandos»: la palabra del encabezado está separada de la anterior por UN solo espacio,
        /// así que puede ser la segunda palabra del mismo encabezado («사용 가능»). Una fila que lo cruza
        /// no es inválida: ese tramo es parte de la columna anterior.
        /// </summary>
        static bool[] SoftBoundaries(string header, int[] starts)
        {
            var soft = new bool[starts.Length];
            int[] pos = CellPositions(header);
            for (int k = 1; k < starts.Length; k++)
            {
                int i = CharIndexAtCell(pos, starts[k]);
                soft[k] = i >= 2 && i <= header.Length && header[i - 1] == ' ' && !char.IsWhiteSpace(header[i - 2]);
            }
            return soft;
        }

        /// <summary>Corta la línea en columnas según las celdas del encabezado; null si no es una fila válida.</summary>
        static string[] Split(string line, int[] starts, bool[] soft)
        {
            int[] pos = CellPositions(line);
            var idx = new int[starts.Length];
            for (int c = 0; c < starts.Length; c++) idx[c] = Math.Min(CharIndexAtCell(pos, starts[c]), line.Length);
            var cells = new string[starts.Length];
            int open = 0;                         // columna que se está llenando
            for (int c = 1; c <= starts.Length; c++)
            {
                if (c < starts.Length)
                {
                    int a = idx[c];
                    // El límite de cada columna (salvo la primera) debe caer después de un espacio.
                    if (a < line.Length && a > 0 && !char.IsWhiteSpace(line[a - 1]))
                    {
                        if (soft == null || !soft[c]) return null;
                        cells[c] = "";            // segunda palabra del encabezado anterior: sigue la misma columna
                        continue;
                    }
                }
                int from = idx[open], to = c < starts.Length ? idx[c] : line.Length;
                if (to < from) to = from;
                cells[open] = line.Substring(from, to - from).Trim();
                open = c;
            }
            return cells;
        }

        // Una versión empieza con un dígito («<» o «>» delante en las desconocidas acotadas). Un origen
        // personalizado puede tener dígitos («repo2»), pero no empezar con uno.
        static readonly Regex LooksVersion = new Regex(@"^[<>]?\s*v?\d", RegexOptions.Compiled);

        static void MapColumns(List<string[]> table, int columns, bool explicitTable, List<WingetRow> output)
        {
            // Con 4 columnas la cuarta puede ser «Disponible» u «Origen»: se decide por el contenido.
            bool fourthIsAvailable = false;
            if (columns == 4)
                foreach (var r in table)
                    if (r[3].Length > 0 && LooksVersion.IsMatch(r[3]) && VersionUtil.Parse(r[3]) != null) { fourthIsAvailable = true; break; }

            foreach (var r in table)
            {
                var w = new WingetRow();
                w.Name = r[0];
                w.Id = r[1];
                w.Version = r[2];
                if (columns > 5)
                {
                    // Encabezados de varias palabras que no se pudieron unir (no hay filas que crucen el
                    // límite): el origen es la última columna y lo disponible, lo que haya en el medio.
                    w.Source = r[columns - 1];
                    for (int k = 3; k < columns - 1; k++) if (r[k].Length > 0) { w.Available = r[k]; break; }
                }
                else if (columns == 5) { w.Available = r[3]; w.Source = r[4]; }
                else if (columns == 4) { if (fourthIsAvailable) w.Available = r[3]; else w.Source = r[3]; }
                w.RequiresExplicit = explicitTable;
                if (w.Id.Length == 0 || w.Version.Length == 0) continue;
                // Los identificadores de repositorio («Google.Chrome») nunca tienen espacios: si
                // aparece uno, la fila está mal alineada. Pero los identificadores LOCALES que winget
                // arma con la clave del registro («ARP\Machine\X64\Picasa 3») sí los tienen, y
                // descartarlos hacía desaparecer justo los programas que winget no puede verificar.
                if (w.Id.IndexOf(' ') >= 0 && !IsLocalId(w.Id)) continue;
                // La versión puede tener espacios legítimos: AnyDesk se registra como «ad 9.7.15».
                // Descartar esas filas hacía que el programa no apareciera ni como disponible.
                if (w.Source.IndexOf(' ') >= 0) continue;
                w.IdTruncated = w.Id.EndsWith("…") || w.Id.EndsWith("...");
                output.Add(w);
            }
        }

        /// <summary>Identificador que winget arma localmente (programas fuera de sus repositorios).</summary>
        public static bool IsLocalId(string id)
        {
            return !string.IsNullOrEmpty(id) &&
                   (id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// ¿winget recortó el texto con «…» por falta de ancho? Una versión recortada («10.0.26100.1…»)
        /// se puede leer como número, pero no es la versión: no sirve para comparar.
        /// </summary>
        public static bool IsTruncated(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            string t = v.TrimEnd();
            return t.EndsWith("…") || t.EndsWith("...");
        }

        /// <summary>Versión desconocida según winget (en cualquier idioma).</summary>
        public static bool IsUnknownVersion(string v)
        {
            return string.IsNullOrEmpty(v) || VersionUtil.Parse(v) == null;
        }
    }
}
