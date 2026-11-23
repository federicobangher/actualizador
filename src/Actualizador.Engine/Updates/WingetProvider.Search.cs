// Programas que winget no reconoce como suyos, buscados en su catálogo por nombre y editor.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    public sealed partial class WingetProvider
    {
        /// <summary>Como mucho tantas búsquedas por vez (cada una tarda 1 o 2 segundos).</summary>
        public const int MaxSearches = 150;
        static readonly TimeSpan SearchBudget = TimeSpan.FromMinutes(6);

        const RegexOptions RX = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
        static readonly Regex Marks = new Regex(@"[™®©]", RX);
        static readonly Regex ArchParens = new Regex(@"\((?:x64|x86|x86_64|amd64|arm64|64[- ]?bits?|32[- ]?bits?|64|32)\)", RX);
        static readonly Regex ArchWords = new Regex(@"(?<![\w.])(?:x64|x86|amd64|arm64|64[- ]?bits?|32[- ]?bits?|win64|win32)(?![\w.])", RX);
        static readonly Regex VersionTokens = new Regex(@"(?<![\w.])v?\d+(?:[.,]\d+)+(?:[a-z]{1,5}\d*)?(?![\w.])", RX);
        static readonly Regex Dashes = new Regex(@"\s+[-–]\s*$|^\s*[-–]\s+", RX);
        /// <summary>
        /// Idioma del instalador («es-AR», «en-US», «es-es», «pt-BR»): Firefox, Thunderbird, LibreOffice u Office lo agregan al nombre
        /// («Mozilla Thunderbird (x64 es-AR)») y el catálogo de winget no. Sólo códigos de idioma conocidos: «Wi-Fi» no es un idioma.
        /// </summary>
        static readonly Regex Locale = new Regex(@"(?<![\w-])(?:en|es|pt|fr|de|it|nl|ru|ja|zh|ko|pl|tr|sv|da|fi|nb|nn|no|cs|hu|el|he|ar|uk|ro|sk|sl|bg|hr|sr|lt|lv|et|th|vi|id|ms|ca|eu|gl|fa|hi)-(?:[a-z]{2}|[a-z]{4}|\d{3})(?![\w-])", RX);
        /// <summary>Paréntesis que sólo dicen para quién o cómo se instaló («(User)», «(Machine)», «(usuario)», «(remove only)»).</summary>
        static readonly Regex ScopeParens = new Regex(@"\(\s*(?:user|machine|per[- ]user|per[- ]machine|current user|all users|usuario|equipo|todos los usuarios|remove only|solo quitar|sólo quitar)\s*\)", RX);

        /// <summary>
        /// El nombre para buscar en winget: sin ™®©, sin la arquitectura y sin números de versión («7-Zip 26.00 (x64)» → «7-Zip»;
        /// «Microsoft Visual C++ 2012 Redistributable (x64) - 11.0.50727» → «Microsoft Visual C++ 2012 Redistributable»).
        /// </summary>
        public static string SearchName(string programName)
        {
            string n = Marks.Replace(programName ?? "", "");
            n = Locale.Replace(n, " ");
            n = ScopeParens.Replace(n, " ");
            n = ArchParens.Replace(n, " ");
            n = VersionTokens.Replace(n, " ");
            n = ArchWords.Replace(n, " ");
            n = Regex.Replace(n, @"\(\s*[,;]?\s*\)", " ");
            n = Regex.Replace(n, @"\s+", " ").Trim();
            for (int i = 0; i < 3; i++) n = Dashes.Replace(n, "").Trim();
            return n;
        }

        /// <summary>Sólo letras y dígitos, en minúsculas.</summary>
        public static string NameKey(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>
        /// ¿El editor del programa es el del paquete? Se compara con la primera parte del identificador de winget, que es la
        /// del editor («GOMLab.GOMPlayer» con «GOM &amp; Company»; «AnyDeskSoftwareGmbH.AnyDesk» con «AnyDesk Software GmbH»):
        /// una empieza con la otra, con al menos 3 letras. Sin editor registrado no se acepta.
        /// </summary>
        public static bool PublisherMatches(string publisher, string wingetId)
        {
            if (string.IsNullOrEmpty(publisher) || string.IsNullOrEmpty(wingetId)) return false;
            int dot = wingetId.IndexOf('.');
            if (dot <= 0) return false;
            string idPub = NameKey(wingetId.Substring(0, dot));
            string pub = NameKey(WingetRules.NormalizeCompany(publisher));
            string full = NameKey(publisher);
            if (idPub.Length < 3 || pub.Length < 3) return false;
            return idPub.StartsWith(pub) || pub.StartsWith(idPub) || full.StartsWith(idPub) || idPub.StartsWith(full);
        }

        /// <summary>
        /// La fila de «winget search» que corresponde al programa: el mismo nombre (sin versión ni arquitectura) y el mismo
        /// editor. Si hay más de un paquete posible, ninguna: mejor «no verificable» que instalar el que no es.
        /// </summary>
        public static WingetRow PickSearchMatch(List<WingetRow> rows, InstalledProgram p)
        {
            bool approximate;
            return PickSearchMatch(rows, p, out approximate);
        }

        /// <summary>
        /// Igual, y si ningún paquete tiene el nombre exacto prueba con uno PARECIDO (2.0.6): uno empieza con el otro («Opera Stable»
        /// y «Opera»; «Revo Uninstaller Free» y «Revo Uninstaller»), con al menos 5 letras en común, el MISMO editor (sin editor
        /// registrado no se adivina), sin que la diferencia sea otra edición del producto (Viewer, CLI, Beta, Portable, SDK…) y un
        /// único paquete posible. Lo encontrado así queda entre las opcionales (<paramref name="approximate"/> = true).
        /// </summary>
        public static WingetRow PickSearchMatch(List<WingetRow> rows, InstalledProgram p, out bool approximate)
        {
            approximate = false;
            if (rows == null || p == null) return null;
            bool sameNameSeen;
            var exact = PickExact(rows, p, out sameNameSeen);
            if (exact != null || sameNameSeen) return exact;       // con el nombre exacto ambiguo u otro editor, no se busca un parecido
            var near = PickApproximate(rows, p);
            approximate = near != null;
            return near;
        }

        static WingetRow PickExact(List<WingetRow> rows, InstalledProgram p, out bool sameNameSeen)
        {
            sameNameSeen = false;
            string want = NameKey(SearchName(p.Name));
            if (want.Length < 3) return null;
            // Sin editor registrado (2.0.4; antes no se buscaban y quedaban «sin verificar» para siempre): sólo con el nombre
            // exacto, de al menos 6 letras, y un único paquete con ese nombre en todo el catálogo.
            bool noPublisher = string.IsNullOrWhiteSpace(p.Publisher);
            if (noPublisher && want.Length < 6) return null;
            WingetRow found = null;
            foreach (var r in rows)
            {
                if (r == null || r.IdTruncated || WingetTable.IsLocalId(r.Id) || !IsSafeId(r.Id)) continue;
                if (r.Source.Length > 0 && !r.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)) continue;
                if (NameKey(SearchName(r.Name)) != want) continue;
                sameNameSeen = true;
                if (noPublisher && found != null && !found.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase)) return null;   // dos paquetes con ese nombre
                if (VersionUtil.Parse(r.Version) == null || WingetTable.IsTruncated(r.Version)) continue;
                if (!noPublisher && !PublisherMatches(p.Publisher, r.Id)) continue;
                if (found != null && !found.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase)) return null;   // ambiguo
                found = r;
            }
            return found;
        }

        /// <summary>Palabras que hacen de un nombre OTRO producto del mismo editor: el parecido no vale si la diferencia tiene una.</summary>
        static readonly Regex VariantWords = new Regex(@"(?<![a-z])(?:viewer|server|client|cli|sdk|runtime|portable|beta|alpha|preview|insider|insiders|nightly|canary|dev|developer|" +
                                                       @"rc|lts|esr|enterprise|plugin|plug-in|addon|add-on|extension|driver|drivers|helppack|help pack|language pack|langpack|" +
                                                       @"tools|toolkit|lite|pro|professional|ultimate|premium|studio|workstation|player|editor|launcher|updater|service|agent|" +
                                                       @"x86|x64|arm64|32|64)(?![a-z])", RX);

        static WingetRow PickApproximate(List<WingetRow> rows, InstalledProgram p)
        {
            if (string.IsNullOrWhiteSpace(p.Publisher)) return null;
            string wantName = SearchName(p.Name);
            string want = NameKey(wantName);
            if (want.Length < 5) return null;
            WingetRow found = null;
            foreach (var r in rows)
            {
                if (r == null || r.IdTruncated || WingetTable.IsLocalId(r.Id) || !IsSafeId(r.Id)) continue;
                if (r.Source.Length > 0 && !r.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)) continue;
                if (VersionUtil.Parse(r.Version) == null || WingetTable.IsTruncated(r.Version)) continue;
                string rowName = SearchName(r.Name);
                string have = NameKey(rowName);
                if (have.Length < 5 || have == want) continue;
                string shorter = have.Length < want.Length ? have : want;
                string longerName = have.Length < want.Length ? wantName : rowName;
                string longer = NameKey(longerName);
                if (!longer.StartsWith(shorter, StringComparison.Ordinal)) continue;
                // Lo que sobra del nombre largo (en palabras): no puede ser otra edición del producto.
                string extra = ExtraWords(longerName, shorter);
                if (extra == null || VariantWords.IsMatch(extra)) continue;
                if (!PublisherMatches(p.Publisher, r.Id)) continue;
                if (found != null && !found.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase)) return null;   // más de uno: ninguno
                found = r;
            }
            return found;
        }

        /// <summary>Las palabras del nombre largo que siguen a las que forman la clave corta, o null si el corte cae en medio de una palabra.</summary>
        static string ExtraWords(string longerName, string shorterKey)
        {
            var words = Regex.Split(longerName ?? "", @"\s+");
            string acc = "";
            for (int i = 0; i < words.Length; i++)
            {
                acc += NameKey(words[i]);
                if (acc == shorterKey) return string.Join(" ", words, i + 1, words.Length - i - 1).Trim();
                if (acc.Length > shorterKey.Length) return null;
            }
            return null;
        }

        /// <summary>
        /// Aplica a la fila lo que se encontró en el catálogo: más nueva → disponible (se instala con «winget install» del
        /// paquete encontrado, encima de lo instalado); si no, al día según ese paquete.
        /// </summary>
        public static void ApplySearchMatch(UpdateCandidate c, InstalledProgram p, WingetRow m)
        {
            ApplySearchMatch(c, p, m, false);
        }

        /// <summary>
        /// Igual; <paramref name="approximate"/>: se encontró por un nombre parecido, no el exacto (queda opcional). Una copia sin
        /// instalador (portable, encontrada por su acceso directo) también queda opcional: se instala la versión oficial aparte.
        /// </summary>
        public static void ApplySearchMatch(UpdateCandidate c, InstalledProgram p, WingetRow m, bool approximate)
        {
            var w = new WingetRow();
            w.Name = p.Name;
            w.Id = m.Id;
            w.Version = p.Version ?? "";
            w.Available = m.Version;
            w.Source = "winget";
            w.InstallInsteadOfUpgrade = true;
            w.PortableCopy = p.Unregistered;
            w.Scope = ScopeOf(p);
            w.ApproximateName = approximate;
            c.ProviderTag = w;
            c.Identifier = m.Id;
            c.Source = "winget (winget)";
            c.InstalledVersion = p.Version;
            c.AvailableVersion = m.Version;
            bool noPublisher = string.IsNullOrWhiteSpace(p.Publisher);
            string how = approximate ? "nombre parecido («" + m.Name + "») y mismo editor"
                       : noPublisher ? "mismo nombre; el programa no registra su editor" : "mismo nombre y editor";
            if (VersionUtil.IsNewer(m.Version, p.Version))
            {
                c.Status = UpdateStatus.Disponible;
                c.Installable = true;
                // Sin editor, con un nombre sólo parecido o sobre una copia portable no hay con qué confirmar que la instalación
                // reemplaza a lo que hay: queda entre las opcionales, para revisarla.
                c.Recommended = !noPublisher && !approximate && !p.Unregistered;
                if (p.Unregistered)
                    c.Detail = "Es una copia sin instalador (portable o de otra cuenta" + (string.IsNullOrEmpty(p.InstallLocation) ? "" : ", en " + p.InstallLocation) +
                               "). El catálogo de winget tiene " + m.Id + " (" + how + ") con la " + m.Version + ". Si la marcás, se instala la versión " +
                               "oficial con «winget install» (queda registrada en Aplicaciones); la copia vieja sigue en su carpeta y la podés borrar.";
                else
                    c.Detail = "winget no lo reconocía como instalado, pero su catálogo lo tiene como " + m.Id + " (" + how + ") con la " + m.Version +
                               ". Se instala la versión nueva con «winget install» encima de la instalada." +
                               (noPublisher || approximate ? " Revisá que sea el mismo programa antes de marcarla." : "");
            }
            else
            {
                c.Status = UpdateStatus.AlDia;
                c.Installable = false;
                c.Recommended = false;
                c.Detail = "Coincide con la última versión de " + m.Id + " en el catálogo de winget (encontrado por " +
                           (approximate ? "nombre parecido y editor" : noPublisher ? "nombre" : "nombre y editor") +
                           (p.Unregistered ? "; es una copia sin instalador" : ": winget no lo reconocía como instalado") + ").";
            }
        }

        /// <summary>El alcance de lo instalado para «winget install --scope»: «user» (HKCU), «machine» (HKLM) o "" (no se sabe o es portable).</summary>
        public static string ScopeOf(InstalledProgram p)
        {
            if (p == null || p.Unregistered) return "";
            string sc = p.Scope ?? "", rk = p.RegistryKey ?? "";
            if (sc == "Usuario actual" || rk.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)) return "user";
            if (sc == "Equipo" || rk.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)) return "machine";
            return "";
        }

        /// <summary>Búsquedas en paralelo.</summary>
        /// <summary>2.0.21: 4 a la vez; en modo liviano, de a una (cada búsqueda es un proceso de winget).</summary>
        public static int SearchWorkers { get { return Performance.Workers(4, 1); } }
        /// <summary>Cuánto vale una búsqueda guardada: el catálogo de winget cambia unas veces por día.</summary>
        public static readonly TimeSpan SearchCacheLife = TimeSpan.FromHours(72);   // 2.0.21: 3 días (antes 12 h): lo que no está en winget rara vez aparece de un día al otro

        /// <summary>
        /// Lo que winget dejó «no verificable» porque no lo reconoce como instalado (no figura en sus repositorios o no lo
        /// informa): muchas veces el paquete SÍ está en su catálogo y sólo falla la correspondencia con el registro. Se busca
        /// por nombre en el repositorio winget y, si coinciden nombre y editor, se compara con esa versión. No se buscan los
        /// que ya tienen explicación (juegos de Steam, antivirus, actualizadores propios, controladores) ni los portables.
        /// Las búsquedas corren de a <see cref="SearchWorkers"/> y se guardan 12 horas (la búsqueda siguiente y las
        /// vueltas de «Actualizar todo» no las repiten).
        /// </summary>
        int SearchUnrecognized(UpdateContext ctx, List<UpdateCandidate> output, List<InstalledProgram> programs, List<string> notes)
        {
            var started = DateTime.UtcNow;
            var jobs = new List<KeyValuePair<UpdateCandidate, InstalledProgram>>();
            var queries = new List<string>();
            foreach (var c in output.ToArray())
            {
                if (c == null || c.Kind != UpdateKind.Programa || c.Status != UpdateStatus.NoVerificable) continue;
                string id = c.Identifier ?? "";
                if (!(id.StartsWith("registro:", StringComparison.Ordinal) || WingetTable.IsLocalId(id))) continue;
                var p = FindProgram(programs, c.Name, VersionUtil.Parse(c.InstalledVersion) != null ? c.InstalledVersion : null);
                // 2.0.6: también las copias sin instalador (portables, encontradas por su acceso directo): antes quedaban
                // «sin verificar» siempre, aunque el programa estuviera en el catálogo (VLC o GOM copiados a una carpeta).
                if (p == null || !ProgramHints.AllowsCatalogSearch(p) || VersionUtil.Parse(p.Version) == null) continue;
                if (ProgramRules.IsSelf(p.Name, p.Publisher, "")) continue;
                string q = SearchName(p.Name);
                if (NameKey(q).Length < 3) continue;
                jobs.Add(new KeyValuePair<UpdateCandidate, InstalledProgram>(c, p));
                AddQuery(queries, q);
            }
            if (jobs.Count == 0) return 0;

            // Las guardadas no se repiten (salvo en las pruebas, que simulan winget).
            var disk = QueryForTests == null ? QueryCache.Open("winget-busquedas", SearchCacheLife) : null;
            var results = new Dictionary<string, List<WingetRow>>(StringComparer.OrdinalIgnoreCase);
            int fromCache = 0, asked = 0, left = 0;
            RunSearches(ctx, queries, results, disk, started, ref fromCache, ref asked, ref left);

            // 2.0.6: «winget search --name» busca el texto DENTRO del nombre del paquete: «Opera Stable» no encuentra «Opera».
            // Lo que no trajo ningún resultado se busca otra vez sin la última palabra (una sola vez, con al menos 5 letras).
            var fallbacks = new List<string>();
            foreach (var job in jobs)
            {
                List<WingetRow> rows;
                if (results.TryGetValue(SearchName(job.Value.Name), out rows) && rows.Count > 0) continue;
                string fb = ShorterQuery(SearchName(job.Value.Name));
                if (fb != null && !results.ContainsKey(fb)) AddQuery(fallbacks, fb);
            }
            if (fallbacks.Count > 0 && !ctx.CancelRequested) RunSearches(ctx, fallbacks, results, disk, started, ref fromCache, ref asked, ref left);
            if (disk != null) disk.Save();

            int matched = 0, newer = 0, near = 0;
            foreach (var job in jobs)
            {
                var rows = new List<WingetRow>();
                List<WingetRow> found;
                string q = SearchName(job.Value.Name);
                if (results.TryGetValue(q, out found)) rows.AddRange(found);
                string fb = rows.Count == 0 ? ShorterQuery(q) : null;
                if (fb != null && results.TryGetValue(fb, out found)) rows.AddRange(found);
                bool approximate;
                var m = PickSearchMatch(rows, job.Value, out approximate);
                if (m == null) continue;
                matched++;
                if (approximate) near++;
                ApplySearchMatch(job.Key, job.Value, m, approximate);
                if (job.Key.Status == UpdateStatus.Disponible) newer++;
                Log.Info("winget: «" + job.Value.Name + "» corresponde a " + m.Id + " " + m.Version + " (buscado por " + (approximate ? "nombre parecido" : "nombre") + " y editor)");
            }
            Log.Info("winget: " + asked + " búsquedas por nombre, " + fromCache + " guardadas, en " + Fmt.Duration(DateTime.UtcNow - started));
            if (matched > 0)
                notes.Add(Fmt.Plural(matched, "programa que winget no reconocía se encontró", "programas que winget no reconocía se encontraron") +
                          " en su catálogo por nombre y editor" + (newer > 0 ? " (" + Fmt.Plural(newer, "con versión nueva", "con versión nueva") + ")" : "") +
                          (near > 0 ? "; " + Fmt.Plural(near, "por un nombre parecido (queda entre las opcionales)", "por un nombre parecido (quedan entre las opcionales)") : "") + ".");
            if (left > 0)
                notes.Add(Fmt.Plural(left, "programa quedó", "programas quedaron") + " sin buscar en el catálogo de winget esta vez (límite de tiempo): volvé a buscar para completarlos.");
            return matched;
        }

        static void AddQuery(List<string> list, string q)
        {
            if (!list.Exists(delegate(string x) { return x.Equals(q, StringComparison.OrdinalIgnoreCase); })) list.Add(q);
        }

        /// <summary>La búsqueda sin la última palabra («Opera Stable» → «Opera»), o null si queda muy corta o no hay qué sacar.</summary>
        public static string ShorterQuery(string q)
        {
            string t = (q ?? "").Trim();
            int sp = t.LastIndexOf(' ');
            if (sp <= 0) return null;
            string s = t.Substring(0, sp).Trim().TrimEnd('-', '–', ',', ':').Trim();
            return NameKey(s).Length >= 5 ? s : null;
        }

        /// <summary>
        /// Corre las búsquedas que falten (las guardadas se toman del disco), de a <see cref="SearchWorkers"/>, con el límite de
        /// cantidad y de tiempo de toda la búsqueda. Lo que no entra se suma a <paramref name="left"/>.
        /// </summary>
        void RunSearches(UpdateContext ctx, List<string> queries, Dictionary<string, List<WingetRow>> results, QueryCache disk, DateTime started,
                         ref int fromCache, ref int asked, ref int left)
        {
            var pending = new List<string>();
            foreach (string q in queries)
            {
                string raw;
                if (disk != null && disk.TryGet(q, out raw)) { var rows = WingetTable.Parse(raw); FillSource(rows, "winget"); results[q] = rows; fromCache++; }
                else pending.Add(q);
            }
            int room = Math.Max(0, MaxSearches - asked);
            if (pending.Count > room) { left += pending.Count - room; pending.RemoveRange(room, pending.Count - room); }
            if (pending.Count == 0) return;
            asked += pending.Count;
            ctx.Report("winget: buscando en su catálogo " + Fmt.Plural(pending.Count, "programa que no reconoce", "programas que no reconoce") + "…");
            int next = -1, done = 0, outOfTime = 0;
            var threads = new List<System.Threading.Thread>();
            for (int t = 0; t < Math.Min(QueryForTests != null ? 1 : SearchWorkers, pending.Count); t++)
            {
                var th = new System.Threading.Thread(delegate()
                {
                    ProcessRunner.LowPriorityForThisThread = true;
                    while (true)
                    {
                        int i = System.Threading.Interlocked.Increment(ref next);
                        if (i >= pending.Count || ctx.CancelRequested) return;
                        if (DateTime.UtcNow - started > SearchBudget) { System.Threading.Interlocked.Increment(ref outOfTime); continue; }
                        string q = pending[i];
                        try
                        {
                            var r = RunQuery("search --name " + ProcessRunner.QuoteArg(q) + " --source winget" + CommonArgs, 60000);
                            var rows = WingetTable.Parse(r.Out);
                            FillSource(rows, "winget");
                            lock (results) results[q] = rows;
                            // Sólo lo que winget contestó de verdad (con resultados o «no hay»): un error se vuelve a intentar la próxima vez.
                            if (disk != null && !r.TimedOut && (rows.Count > 0 || IsNoResults(r.ExitCode))) lock (disk) disk.Put(q, r.Out ?? "");
                        }
                        catch (Exception ex) { Log.Warn("winget search «" + q + "»: " + ex.Message); }
                        int n = System.Threading.Interlocked.Increment(ref done);
                        if (n % 10 == 0) ctx.Report("winget: " + n + " de " + pending.Count + " programas buscados en su catálogo…");
                    }
                });
                th.IsBackground = true;
                th.Name = "winget-search-" + t;
                threads.Add(th);
                th.Start();
            }
            foreach (var th in threads) while (!th.Join(500)) if (ctx.CancelRequested) break;
            ctx.ThrowIfCancelled();
            left += outOfTime;
        }
    }
}
